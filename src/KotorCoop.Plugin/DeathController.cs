namespace KotorCoop.Plugin;

/// <summary>
/// Makes dying work in co-op.
///
/// KOTOR's own rules are actually a good fit and are kept: a party member (the PC included)
/// never truly dies — at zero hit points they go "temp dead", and the engine stands them
/// back up by itself once no hostile has been visible nearby for a few seconds. What breaks
/// a two-instance session is two engine reflexes around that state, and this class suppresses
/// exactly those two, each with a narrow byte patch:
///
///   1. **Control auto-swap.** When the controlled PC goes down the engine hands control to
///      the next living party member. On a client that fights the control lock (input is
///      pinned to the player's own PC) and hands them a companion the host owns.
///   2. **Game-over.** When every party member is down the engine fades out and offers the
///      main menu. In co-op that strands one player at the menu while the other plays on —
///      so it is swallowed, the agent is told, and the peers decide together whether this is
///      a real party wipe (both players down) or just one player waiting for a rescue.
///
/// Everything else is left to the engine, including its own bleed-out recovery. On top of
/// it, a peer who reaches a downed player can revive them early: the agent bumps a serial
/// and the local instance applies the engine's Resurrection effect (the only path that
/// restores the AI flags and stands the body up), then tops the hit points up a tick later.
///
/// Both patches are verified-bytes-or-refuse and default to INERT: the suppressions only
/// engage while the agent has set their flags, so a plugin running without a session behaves
/// exactly like stock KOTOR. See docs/research/death-handling.md.
/// </summary>
internal sealed unsafe class DeathController(SharedBlockView block)
{
    /// <summary>
    /// `CClientExoAppInternal::ChangeCharacter` — the single funnel for Tab, the portraits
    /// and the death auto-swap. Suppressing it here covers all three.
    /// </summary>
    private const nint ChangeCharacter = 0x005F6B60;

    /// <summary>
    /// The single call site that raises game-over, inside `TempDeathUpdate` once every party
    /// member is down. Patching the CALL (not the callee) leaves the rest of TempDeathUpdate
    /// — including the engine's auto-revive — running normally.
    /// </summary>
    private const nint RunDeathSequenceCall = 0x004B6E99;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>Hit points to stand a revived player up with when the agent names none.</summary>
    private const int DefaultReviveHitPoints = 10;

    /// <summary>
    /// Three ints in UNMANAGED memory, because the patch stubs reference them by absolute
    /// address and must keep working for the life of the process: a managed static has no
    /// address stable enough to bake into machine code.
    ///   [0] suppress control swap   [1] suppress game-over   [2] game-over was attempted
    /// </summary>
    private static int* _flags;

    private const int FlagSuppressSwap = 0;
    private const int FlagSuppressGameOver = 1;
    private const int FlagGameOverWanted = 2;

    private static bool _installed;
    private static string _installDiagnostic = "not attempted";

    private uint _appliedReviveSerial;
    private uint _downedSerial;
    private bool _wasDowned;

    /// <summary>Pending hit-point top-up after a Resurrection, applied a tick later so the
    /// effect handler has run (a same-frame write can land while the target still reads
    /// dead, and the engine drops it).</summary>
    private int _pendingHitPoints;
    private uint _pendingHitPointTarget;

    public static string InstallDiagnostic => _installDiagnostic;

    /// <summary>Drops per-world state; the patches themselves are process-lifetime.</summary>
    public void Invalidate()
    {
        _wasDowned = false;
        _pendingHitPoints = 0;
        _pendingHitPointTarget = 0;

        if (_flags != null)
        {
            _flags[FlagGameOverWanted] = 0;
        }
    }

    public void OnFrame()
    {
        if (_flags == null)
        {
            return; // the patches refused; stock behaviour stands
        }

        // Mirror the agent's switches into the words the stubs read.
        _flags[FlagSuppressSwap] = block.SuppressControlSwap ? 1 : 0;
        _flags[FlagSuppressGameOver] = block.SuppressGameOver ? 1 : 0;

        // The engine asked for game-over and the stub swallowed it: tell the agent once per
        // occurrence. It decides whether this is a genuine session-wide wipe (every player
        // down) and, if so, clears SuppressGameOver so the next attempt goes through.
        if (_flags[FlagGameOverWanted] != 0)
        {
            _flags[FlagGameOverWanted] = 0;
            block.GameOverSerial++;
            PluginHost.Log("death: game-over suppressed; the session decides");
        }

        nint player = Odyssey.GetPlayerCreaturePointer();
        if (player == 0)
        {
            return;
        }

        // Publish the downed state as a level plus an edge, so the agent can both see the
        // current state and count individual knock-downs.
        bool downed = Odyssey.IsDowned(player);
        if (downed != _wasDowned)
        {
            _wasDowned = downed;
            block.LocalDowned = downed ? 1u : 0u;

            if (downed)
            {
                block.LocalDownedSerial = ++_downedSerial;
                PluginHost.Log("death: the local player is down");
            }
            else
            {
                PluginHost.Log("death: the local player is back up");
            }
        }

        ApplyPendingHitPoints();
        ApplyReviveRequest(player);
    }

    /// <summary>The top-up owed to a creature revived on an earlier tick.</summary>
    private void ApplyPendingHitPoints()
    {
        if (_pendingHitPoints <= 0)
        {
            return;
        }

        nint target = Odyssey.ResolveServerObject(_pendingHitPointTarget);
        int hitPoints = _pendingHitPoints;

        _pendingHitPoints = 0;
        _pendingHitPointTarget = 0;

        if (target == 0)
        {
            return;
        }

        // Never above the creature's own maximum; the agent's number is a request.
        ushort max = Odyssey.GetMaxHitPoints(target);
        if (max > 0 && hitPoints > max)
        {
            hitPoints = max;
        }

        Odyssey.SetCurrentHitPoints(target, hitPoints);
        PluginHost.Log($"death: revived player topped up to {hitPoints} hp");
    }

    /// <summary>A peer reached this player: stand them up.</summary>
    private void ApplyReviveRequest(nint player)
    {
        uint serial = block.ReviveSerial;
        if (serial == _appliedReviveSerial)
        {
            return;
        }

        _appliedReviveSerial = serial;

        uint playerId = Odyssey.GetPlayerObjectId();
        if (playerId == 0)
        {
            block.ReviveStatus = 1;
            block.ReviveApplied = serial;
            return;
        }

        // The Resurrection handler refuses a creature that is not raiseable; the engine's own
        // auto-revive sets this same flag raw before applying the effect.
        Odyssey.SetRaiseable(player);

        if (!Odyssey.ReviveCreature(playerId, out int failure))
        {
            block.ReviveStatus = failure == 0 ? 2u : (uint)failure;
            block.ReviveApplied = serial;
            PluginHost.Log($"death: revive refused (failure {failure})");
            return;
        }

        uint requested = block.ReviveHitPoints;
        _pendingHitPoints = requested > 0 ? (int)requested : DefaultReviveHitPoints;
        _pendingHitPointTarget = playerId;

        block.ReviveStatus = 0;
        block.ReviveApplied = serial;
        PluginHost.Log("death: revive applied; the player stands up");
    }

    // ---- the two patches ---------------------------------------------------------

    /// <summary>
    /// Installs both suppressions. Each verifies the exact original bytes first and refuses
    /// rather than patching something it does not recognise, and each remains inert until the
    /// agent sets its flag. MAIN THREAD ONLY, once, at startup.
    /// </summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = _installDiagnostic;
            return true;
        }

        // The stubs bake these addresses in, so they must outlive any managed relocation.
        _flags = (int*)NativeMethods.VirtualAlloc(0, 16, MemCommitReserve, 0x04 /* PAGE_READWRITE */);
        if (_flags == null)
        {
            _installDiagnostic = diagnostic = "could not allocate the suppression flags";
            return false;
        }

        _flags[FlagSuppressSwap] = 0;
        _flags[FlagSuppressGameOver] = 0;
        _flags[FlagGameOverWanted] = 0;

        if (!InstallSwapGuard(out string swap))
        {
            _installDiagnostic = diagnostic = $"control-swap guard refused: {swap}";
            return false;
        }

        if (!InstallGameOverGuard(out string gameOver))
        {
            _installDiagnostic = diagnostic = $"game-over guard refused: {gameOver}";
            return false;
        }

        _installed = true;
        _installDiagnostic = diagnostic = $"{swap}; {gameOver}";
        return true;
    }

    /// <summary>
    /// Prefixes ChangeCharacter with a test of the suppression flag: when set, it returns to
    /// its caller immediately (the function is thiscall with one argument and callee-clean —
    /// `ret 4` — so the early exit must clean the argument too).
    /// </summary>
    private static bool InstallSwapGuard(out string diagnostic)
    {
        byte* site = (byte*)ChangeCharacter;

        // The prologue we relocate: push ebp; mov ebp,esp — the canonical frame setup. Refuse
        // anything else rather than patch a function we do not recognise.
        if (site[0] != 0x55 || site[1] != 0x8B || site[2] != 0xEC)
        {
            diagnostic = $"ChangeCharacter prologue is {site[0]:X2} {site[1]:X2} {site[2]:X2}";
            return false;
        }

        nint stub = NativeMethods.VirtualAlloc(0, 48, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;

        s[n++] = 0x83;                                       // cmp dword ptr [suppressSwap], 0
        s[n++] = 0x3D;
        *(int*)(s + n) = (int)(nint)(_flags + FlagSuppressSwap);
        n += 4;
        s[n++] = 0x00;

        s[n++] = 0x74; s[n++] = 0x04;                        // jz -> run the original
        s[n++] = 0xC2; s[n++] = 0x04; s[n++] = 0x00;         // ret 4  (suppressed)
        s[n++] = 0x90;                                       // pad, keeps the jz target clean

        s[n++] = 0x55;                                       // push ebp     (relocated)
        s[n++] = 0x8B; s[n++] = 0xEC;                        // mov ebp,esp  (relocated)
        s[n++] = 0xE9;                                       // jmp ChangeCharacter+3
        *(int*)(s + n) = (int)((ChangeCharacter + 3) - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(ChangeCharacter, (nuint)5, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;                                      // jmp stub
        *(int*)(site + 1) = (int)(stub - (ChangeCharacter + 5));
        NativeMethods.VirtualProtect(ChangeCharacter, (nuint)5, old, out _);

        diagnostic = $"control-swap guard at 0x{ChangeCharacter:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>
    /// Replaces the one CALL that raises game-over with a call to our own stub: it records
    /// the request for the frame callback and, unless suppression is on, forwards to the
    /// engine's routine so a genuine party wipe still ends the game normally.
    /// </summary>
    private static bool InstallGameOverGuard(out string diagnostic)
    {
        byte* site = (byte*)RunDeathSequenceCall;
        if (site[0] != 0xE8)
        {
            diagnostic = $"expected a call at 0x{RunDeathSequenceCall:X}, found {site[0]:X2}";
            return false;
        }

        // Where the original call went; the stub forwards there when not suppressing.
        nint target = RunDeathSequenceCall + 5 + *(int*)(site + 1);

        nint stub = NativeMethods.VirtualAlloc(0, 48, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;

        // Record the attempt (a plain store; the frame callback consumes it).
        s[n++] = 0xC7; s[n++] = 0x05;                        // mov dword ptr [gameOverWanted], 1
        *(int*)(s + n) = (int)(nint)(_flags + FlagGameOverWanted);
        n += 4;
        *(int*)(s + n) = 1;
        n += 4;

        s[n++] = 0x83;                                       // cmp dword ptr [suppressGameOver], 0
        s[n++] = 0x3D;
        *(int*)(s + n) = (int)(nint)(_flags + FlagSuppressGameOver);
        n += 4;
        s[n++] = 0x00;

        s[n++] = 0x74; s[n++] = 0x01;                        // jz -> forward to the engine
        s[n++] = 0xC3;                                       // ret (suppressed)
        s[n++] = 0xE9;                                       // jmp RunDeathSequence
        *(int*)(s + n) = (int)(target - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(RunDeathSequenceCall + 1, (nuint)4, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        *(int*)(site + 1) = (int)(stub - (RunDeathSequenceCall + 5));
        NativeMethods.VirtualProtect(RunDeathSequenceCall + 1, (nuint)4, old, out _);

        diagnostic = $"game-over guard at 0x{RunDeathSequenceCall:X} (stub 0x{stub:X})";
        return true;
    }
}
