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
/// A THIRD, opt-in patch (coop.ini <c>client_temp_death_suppress</c>, default OFF, NEEDS-LIVE
/// — death-handling.md §5 item 4 and its 2026-09-02 follow-up) early-returns
/// `TempDeathUpdate` itself on a CLIENT instance: game-over, auto-revive and the
/// gather-teleport are all host-authoritative decisions there, so a client running the
/// function at all is at best redundant and at worst racing the host's own mirror.
///
/// All three patches are verified-bytes-or-refuse and default to INERT: the suppressions only
/// engage while the agent (or, for the third, coop.ini) has set their flags, so a plugin
/// running without a session behaves exactly like stock KOTOR. See
/// docs/research/death-handling.md.
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

    /// <summary>
    /// `CServerExoAppInternal::TempDeathUpdate` itself (docs/research/death-handling.md §3) —
    /// the function <see cref="RunDeathSequenceCall"/> lives inside. See
    /// <see cref="InstallClientTempDeathGuard"/> for why a CLIENT wants the whole function
    /// neutralised rather than just its one call site.
    /// </summary>
    private const nint TempDeathUpdate = 0x004B6DA0;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>Hit points to stand a revived player up with when the agent names none.</summary>
    private const int DefaultReviveHitPoints = 10;

    /// <summary>
    /// coop.ini: arms <see cref="InstallClientTempDeathGuard"/>'s early-return, and ONLY on a
    /// client instance (<see cref="SharedBlockView.DamageDealtEnabled"/> — see that guard's own
    /// doc comment for why that flag, not a new one, is the client discriminator here). Default
    /// OFF — death-handling.md §5 item 4 is reasoned but NEEDS-LIVE, so nobody's behaviour
    /// changes until they opt in. Read once; coop.ini is not re-read mid-session.
    /// </summary>
    private static readonly bool ClientTempDeathSuppressConfigured =
        PluginConfig.ReadBool("client_temp_death_suppress", false);

    /// <summary>
    /// Four ints in UNMANAGED memory, because the patch stubs reference them by absolute
    /// address and must keep working for the life of the process: a managed static has no
    /// address stable enough to bake into machine code.
    ///   [0] suppress control swap   [1] suppress game-over   [2] game-over was attempted
    ///   [3] suppress TempDeathUpdate entirely (client-only, opt-in)
    /// </summary>
    private static int* _flags;

    private const int FlagSuppressSwap = 0;
    private const int FlagSuppressGameOver = 1;
    private const int FlagGameOverWanted = 2;
    private const int FlagSuppressClientTempDeath = 3;

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

    /// <summary>
    /// The session ended: disarm both byte patches immediately.
    ///
    /// ⚠ THIS IS THE FIX FOR THE UNCLICKABLE COMPANION. The two suppressions are LEVELS the
    /// agent raises in the shared block and <see cref="OnFrame"/> mirrors into the stubs every
    /// frame — and a client raises the swap suppression once, at the first death pump, and
    /// never lowers it. Nothing lowered it on teardown either, so after a disconnect the guard
    /// stayed armed for the life of the process: <c>ChangeCharacter</c> returned immediately,
    /// which is Tab, EVERY party portrait and the engine's own death auto-swap all inert. The
    /// player got Trask back in the party and could not click him.
    ///
    /// The block is now cleared on teardown too (KotorGameBridge.SessionScopedBlockLevels), so
    /// this is the second of two locks on the same door: OnFrame would lower the stubs anyway
    /// on its next in-world frame, but only if the agent got as far as clearing the block. A
    /// player whose agent died without tearing down still gets their portraits back here.
    /// </summary>
    public void ReleaseSession()
    {
        if (_flags == null)
        {
            return;
        }

        _flags[FlagSuppressSwap] = 0;
        _flags[FlagSuppressGameOver] = 0;
        _flags[FlagGameOverWanted] = 0;
        _flags[FlagSuppressClientTempDeath] = 0;

        PluginHost.Log("death: suppressions lifted — Tab and the party portraits answer again");
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

        // Client-only, opt-in (see InstallClientTempDeathGuard). block.DamageDealtEnabled is
        // set true ONLY on the instance mirroring somebody else's enemies — the same flag
        // CheatAuthority already treats as the client discriminator — so this is armed only
        // once this instance is actually a connected client, never at the main menu or on a
        // host, even if the ini key is on.
        _flags[FlagSuppressClientTempDeath] =
            ClientTempDeathSuppressConfigured && block.DamageDealtEnabled ? 1 : 0;

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
        //
        // GetEffectiveMaxHitPoints, NOT GetMaxHitPoints: this target is always a player
        // character (a peer's revive request names a downed PLAYER), whose plain stored-field
        // max reads zero forever — see CheatController.Player.cs's TopUp and MaxHitPointsRule's
        // own class doc comment for the full story.
        int max = Odyssey.GetEffectiveMaxHitPoints(target);
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

        // Best-effort, unlike the two above: it stays OFF by coop.ini default and is
        // NEEDS-LIVE (death-handling.md §5 item 4), so a refusal here must not take the
        // already-shipped swap/game-over suppressions down with it — it only ever matters once
        // a player has explicitly opted in.
        string clientTempDeath = InstallClientTempDeathGuard(out string clientTempDeathDiagnostic)
            ? clientTempDeathDiagnostic
            : $"client temp-death guard NOT installed: {clientTempDeathDiagnostic}";

        _installed = true;
        _installDiagnostic = diagnostic = $"{swap}; {gameOver}; {clientTempDeath}";
        return true;
    }

    /// <summary>
    /// Prefixes ChangeCharacter with a test of the suppression flag: when set, it returns to
    /// its caller immediately. The function is callee-clean with two stack arguments
    /// (`ret 8`, read from its epilogue), so the early exit must clean both — and it does so
    /// before the prologue has touched esp, where the frame is still exactly [ret][arg][arg].
    /// </summary>
    private static bool InstallSwapGuard(out string diagnostic)
    {
        byte* site = (byte*)ChangeCharacter;

        // The 6 prologue bytes we relocate: sub esp,0xC; push esi; mov esi,ecx. Verified
        // against the binary; refuse anything else rather than patch a function we do not
        // recognise. (None of them is position-dependent, so they relocate verbatim.)
        ReadOnlySpan<byte> prologue = [0x83, 0xEC, 0x0C, 0x56, 0x8B, 0xF1];
        for (int i = 0; i < prologue.Length; i++)
        {
            if (site[i] != prologue[i])
            {
                diagnostic = $"ChangeCharacter prologue mismatch at +{i} ({site[i]:X2})";
                return false;
            }
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

        s[n++] = 0x74; s[n++] = 0x03;                        // jz -> run the original
        s[n++] = 0xC2; s[n++] = 0x08; s[n++] = 0x00;         // ret 8  (suppressed)

        s[n++] = 0x83; s[n++] = 0xEC; s[n++] = 0x0C;         // sub esp,0xC  (relocated)
        s[n++] = 0x56;                                       // push esi     (relocated)
        s[n++] = 0x8B; s[n++] = 0xF1;                        // mov esi,ecx  (relocated)
        s[n++] = 0xE9;                                       // jmp ChangeCharacter+6
        *(int*)(s + n) = (int)((ChangeCharacter + 6) - (stub + n + 4));

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

    /// <summary>
    /// death-handling.md §5 item 4: on a CLIENT, all three of TempDeathUpdate's jobs are
    /// host-authoritative decisions — game-over (the host's swallow already decides the
    /// wipe), auto-revive (a client-local revive would stand a companion up while the host's
    /// own mirror still shows it down) and the gather-teleport (party-sync owns positions) —
    /// so rather than stack a third narrow patch on top of <see cref="InstallGameOverGuard"/>,
    /// this early-returns the WHOLE function on a client, the same shape
    /// <see cref="InstallSwapGuard"/> already uses for <c>ChangeCharacter</c>.
    ///
    /// Prefixes TempDeathUpdate with a test of the suppression flag: when set, it returns to
    /// its caller immediately. TempDeathUpdate is a plain `__thiscall` with NO stack
    /// arguments — its only caller (server MainLoop, 0x004BAC4A) does `mov ecx, esi; call
    /// TempDeathUpdate` with nothing pushed first, and its own epilogue is a bare `ret` (no
    /// operand) — so the early exit needs no stack cleanup at all, unlike
    /// <see cref="InstallSwapGuard"/>'s `ret 8`. VERIFIED against the binary (tools/disasm.py
    /// 0x4B6DA0 / 0x4BAC30 / 0x4B71C0), not merely inferred from the calling convention.
    ///
    /// The single relocated instruction is the function's own SEH prologue opener
    /// (`mov eax, fs:[0]`, 6 bytes — MSVC's C++ exception-frame setup, unrelated to anything
    /// this function actually does before we skip it entirely). death-handling.md's own risk
    /// register already rates a TempDeathUpdate early-return LOW: "function only reads party
    /// state and applies effects; skipping it leaves no dangling state" — every field it would
    /// have touched (the combat-clear timers at +0x100D0/+0x100D4, the revive/gather-teleport
    /// writes) simply stays put for as many frames as the client suppresses it, and none of
    /// them are read by anything else.
    /// </summary>
    private static bool InstallClientTempDeathGuard(out string diagnostic)
    {
        byte* site = (byte*)TempDeathUpdate;

        // mov eax, fs:[0] — the one instruction relocated whole; refuse anything else rather
        // than patch a function we do not recognise.
        ReadOnlySpan<byte> prologue = [0x64, 0xA1, 0x00, 0x00, 0x00, 0x00];
        for (int i = 0; i < prologue.Length; i++)
        {
            if (site[i] != prologue[i])
            {
                diagnostic = $"TempDeathUpdate prologue mismatch at +{i} ({site[i]:X2})";
                return false;
            }
        }

        nint stub = NativeMethods.VirtualAlloc(0, 48, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;

        s[n++] = 0x83;                                       // cmp dword ptr [suppressClientTempDeath], 0
        s[n++] = 0x3D;
        *(int*)(s + n) = (int)(nint)(_flags + FlagSuppressClientTempDeath);
        n += 4;
        s[n++] = 0x00;

        s[n++] = 0x74; s[n++] = 0x01;                        // jz -> run the original
        s[n++] = 0xC3;                                       // ret (suppressed - no operand, no stack args)

        s[n++] = 0x64; s[n++] = 0xA1;                        // mov eax, fs:[0]  (relocated)
        *(int*)(s + n) = 0;
        n += 4;
        s[n++] = 0xE9;                                       // jmp TempDeathUpdate+6
        *(int*)(s + n) = (int)((TempDeathUpdate + 6) - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(TempDeathUpdate, (nuint)5, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;                                      // jmp stub
        *(int*)(site + 1) = (int)(stub - (TempDeathUpdate + 5));
        NativeMethods.VirtualProtect(TempDeathUpdate, (nuint)5, old, out _);

        diagnostic = $"client temp-death guard at 0x{TempDeathUpdate:X} (stub 0x{stub:X}, "
            + $"configured={ClientTempDeathSuppressConfigured})";
        return true;
    }
}
