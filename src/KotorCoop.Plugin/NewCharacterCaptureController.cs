namespace KotorCoop.Plugin;

/// <summary>
/// Gets the character a first-time joiner just made in the game's OWN character-creation
/// screens into a form world adoption can carry — by letting the new game run to its first
/// module and then asking the engine to SAVE.
///
/// WHY THIS EXISTS. World adoption (docs/research/world-adoption.md) lets a joiner play the
/// host's world with their own character, but it lifts that character out of the joiner's own
/// SAVE, and a first-time joiner has none. Before this, that player had nothing to join with:
/// <c>WorldAdoption.TryAdoptAsync</c> fails at its very first step ("the save's
/// <c>Mod_PlayerList</c> is empty") and, correctly, refuses to touch anything.
///
/// ⚠ THE FIRST DESIGN OF THIS CONTROLLER WAS BUILT ON A WRONG FACT, AND THE CORRECTION IS THE
/// WHOLE OF THIS FILE'S CURRENT SHAPE. docs/research/character-login-chain.md §2 listed
/// <c>CSWCCreature::SaveCharGenCreature 0x6123E0</c> as a writer of <c>TEMP:pifo</c>, so this
/// controller watched <c>Temp\pifo.ifo</c> for the character — first with a 250 ms poll, then
/// (when the poll never saw the file) with <see cref="CharGenSaveHook"/> spliced onto the call
/// itself. The hook fired, and the file was ZERO BYTES; it was still zero after a 400 ms spin
/// on the game thread, and <c>Temp\</c> was empty by world-up. The reason, disassembled
/// afterwards: <c>SaveCharGenCreature</c> never writes a pifo at all. It does not reference the
/// string — <c>tools/xrefs.py 0x745318</c> lists five sites and none is inside it — and its own
/// tail writes <c>TEMP:temp</c> with restype <c>0x7DF</c> ("bic") through
/// <c>CResGFF::WriteGFFFile</c> at 0x613434, i.e. <c>Temp\temp.bic</c>, a BIC character file.
/// The three real <c>TEMP:pifo</c> writers (<c>StorePlayerCharacters</c>,
/// <c>DoPCAutosave</c>, <c>LoadPCAutoSave</c>) are none of them on the chargen path.
///
/// So there is no file to catch, and the character is obtained the only way KOTOR makes one
/// portable — the same way <c>join_mode = save</c> always has:
/// <list type="number">
/// <item>The player finishes character creation; the new game loads its first module (the
/// Endar Spire).</item>
/// <item>This controller notices a world is up and asks the engine to save
/// (<see cref="Odyssey.RequestQuickSave"/> — retail's own quicksave).</item>
/// <item>It watches <c>saves\</c> for the directory that appears, names it in the shared block,
/// and the AGENT reads the PC out of it with <c>PlayerIfo.TryExtractPlayer</c> — byte-for-byte
/// the extraction the verified save-join path uses.</item>
/// </list>
///
/// WHAT THE PLAYER DOES: character creation, and nothing else. The Endar Spire intro is cut
/// short by the adoption load that follows, which is a full server teardown and rebuild.
///
/// EVERY STEP IS LATCHED ONE-SHOT AND LOGGED, per this codebase's per-frame-write rule: the
/// save is requested at most <see cref="MaxSaveRequests"/> times (a module's opening cutscene
/// can legitimately shut <c>CSWSPlayer::GetIsAllowedToSave</c>, and a refused save is silent),
/// spaced by <see cref="SaveRetryMs"/>, and the whole phase gives up rather than asking
/// forever.
///
/// Runs from <c>PluginHost.MenuFrame</c> — the MainLoop hook, which fires at the main menu AND
/// in-world, so one schedule covers both halves of this flow. The SwapBuffers hook would miss
/// the first half entirely (dormant behind the menu movie), which is the same reason
/// <see cref="MenuLoadController"/> lives there.
/// </summary>
internal sealed class NewCharacterCaptureController(SharedBlockView block)
{
    /// <summary>How long a world must have been up before the save is asked for. The module
    /// load itself is over by the time a player creature answers, but the opening script pass
    /// (spawns, party assembly, the first cutscene arming) is not, and asking mid-way is how a
    /// save gets refused for reasons nothing reports.</summary>
    private const int WorldSettleMs = 4000;

    /// <summary>Extra time, ON TOP OF <see cref="WorldSettleMs"/>, spent retrying the
    /// starting-gear equip repair (<see cref="Odyssey.EquipUnequippedStartingGear"/>) before the
    /// quicksave is requested. LIVE-MEASURED 2026-09-10 (docs/research/world-adoption.md's
    /// addendum): whether the starting WEAPON exists anywhere this repair can find it at the
    /// single instant <see cref="WorldSettleMs"/> expires is a race, not a constant — <c>OnFrame</c>
    /// is main-thread-driven and does not tick at all while a cinematic holds the game (this
    /// project's own "main thread was gone for N ms" warning, seen at exactly this join stage
    /// every round so far), so <see cref="WorldSettleMs"/>'s own deadline is frequently blown
    /// straight through the instant a stall ends rather than reached by ordinary polling — the
    /// repair got exactly one try, at whatever moment that happened to be. Retrying every poll
    /// for this much longer, now that frames are confirmed flowing again, gives the engine's own
    /// item-creation script room to finish even when it is still running as the cinematic ends.
    /// Idempotent per slot (EquipUnequippedStartingGear never re-touches an already-filled one),
    /// so retrying costs a few wasted reads per tick, never a double-equip.
    ///
    /// ⚠ THIS DOES NOT CLOSE THE GAP FOR THE BODY/CLOTHING SLOT. Across every live pass this
    /// round (six joins, one class package, varying this constant from 0 to 15000): the starting
    /// WEAPON was found and equipped in every run that reached this window at all, while the
    /// starting CLOTHES item — confirmed genuinely possessed by the same creature by the time the
    /// `items` console verb ran moments later — was never found by this repair regardless of how
    /// long it kept retrying. Widening this window stopped changing the outcome well before 6000,
    /// which rules out "just needs more time" as the explanation for clothing specifically; see
    /// <see cref="Odyssey.EquipUnequippedStartingGear"/>'s own doc comment for what is and is not
    /// understood about why. NEEDS-LIVE: the actual mechanism that creates/links a fresh
    /// character's starting clothing item is still unidentified.</summary>
    private const int EquipRepairExtraMs = 6000;

    /// <summary>Gap between save requests. Long enough that a refusal caused by an opening
    /// cutscene has a chance to stop being true.</summary>
    private const int SaveRetryMs = 8000;

    /// <summary>How many times the save is asked for before the join is failed. Five requests
    /// over <see cref="SaveRetryMs"/> spans ~40 s, which is longer than the Endar Spire's
    /// opening exchange.</summary>
    private const int MaxSaveRequests = 5;

    /// <summary>Request serial already acted on, so one arm arms exactly once.</summary>
    private uint _armedSerial;

    /// <summary>True between arming and either a capture or a failure.</summary>
    private bool _armed;

    private long _nextPollMs;

    /// <summary>Throttles the "still waiting" line so a player who takes ten minutes over their
    /// appearance does not produce ten minutes of log.</summary>
    private long _nextWaitingLogMs;

    /// <summary><c>Environment.TickCount64</c> when a player creature was first seen, or 0 while
    /// there is no world. Reset to 0 if the world goes away again, so a chargen that was
    /// cancelled back to the menu does not leave a half-started save phase behind.</summary>
    private long _worldUpAtMs;

    /// <summary>Deadline for the extra starting-gear repair retry window
    /// (<see cref="EquipRepairExtraMs"/>), or 0 until <see cref="WorldSettleMs"/> first expires
    /// and the window is armed.</summary>
    private long _equipRepairDeadlineMs;

    /// <summary>UTC ticks at the same moment, for comparing against file timestamps. A save
    /// older than this belongs to a world that is not the one the player just created.</summary>
    private long _worldUpAtTicksUtc;

    private int _saveRequests;

    private long _lastSaveRequestMs;

    /// <summary>The save directory currently being watched for stability, or null.</summary>
    private string? _candidate;

    /// <summary>The candidate's <c>SAVEGAME.sav</c> length on the previous poll, or -1. The
    /// stability half of <see cref="NewCharacterCaptureRule.ShouldCapture"/>.</summary>
    private long _previousBytes = -1;

    public void OnFrame()
    {
        uint serial = block.JoinChargenRequest;

        if (serial != block.JoinChargenApplied)
        {
            Arm(serial);
            return;
        }

        if (!_armed)
        {
            return;
        }

        long nowMs = Environment.TickCount64;
        if (nowMs < _nextPollMs)
        {
            return;
        }

        _nextPollMs = nowMs + NewCharacterCaptureRule.PollIntervalMs;

        try
        {
            Step(nowMs);
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private void Arm(uint serial)
    {
        _armedSerial = serial;
        _armed = true;
        _nextPollMs = 0;
        _nextWaitingLogMs = Environment.TickCount64 + 15000;
        _worldUpAtMs = 0;
        _worldUpAtTicksUtc = 0;
        _equipRepairDeadlineMs = 0;
        _saveRequests = 0;
        _lastSaveRequestMs = 0;
        _candidate = null;
        _previousBytes = -1;

        block.JoinChargenBytes = 0;
        block.PublishJoinChargenSaveDirectory(string.Empty);
        block.JoinChargenState = SharedBlock.JoinChargenStateArmed;
        block.JoinChargenApplied = serial;

        // The chargen-finished marker goes in HERE rather than at plugin startup, so an install
        // that never uses join_mode = newcharacter carries no engine patch at all. Main thread:
        // MenuFrame is the game's own MainLoop.
        string hook = CharGenSaveHook.Install(out string hookDiagnostic)
            ? hookDiagnostic
            : $"NO CHARGEN MARKER - {hookDiagnostic} (harmless: the save phase keys on a world "
                + "coming up, not on the marker)";

        // ASCII only: the plugin log is written as bytes, so an em-dash arrives as '?'.
        PluginHost.Log(
            $"newchar: armed (request {serial}) - make a character; when the new game's first "
            + $"module is up this will save it and hand the save to the agent; {hook}");
    }

    private void Step(long nowMs)
    {
        if (_saveRequests == 0)
        {
            WaitForWorld(nowMs);
            return;
        }

        WatchForSave(nowMs);
    }

    /// <summary>Phase 1: the player is in character creation, or the first module is loading.
    /// </summary>
    private void WaitForWorld(long nowMs)
    {
        if (!Odyssey.HasPlayer)
        {
            if (_worldUpAtMs != 0)
            {
                // The world went away again (a cancelled chargen, a quit to menu). Start over
                // rather than carrying a settle timer across two different worlds.
                PluginHost.Log("newchar: the world went away again; back to waiting for a character");
                _worldUpAtMs = 0;
                _worldUpAtTicksUtc = 0;
            }

            if (nowMs >= _nextWaitingLogMs)
            {
                _nextWaitingLogMs = nowMs + 15000;
                PluginHost.Log(
                    "newchar: still waiting for the new game's first module "
                    + $"(chargen finished {CharGenSaveHook.Sequence} time(s) so far)");
            }

            return;
        }

        if (_worldUpAtMs == 0)
        {
            _worldUpAtMs = nowMs;
            _worldUpAtTicksUtc = DateTime.UtcNow.Ticks;
            PluginHost.Log(
                "newchar: the new game's first module is up with a player creature; "
                + $"saving in {WorldSettleMs} ms so the character can be carried");
            return;
        }

        if (nowMs - _worldUpAtMs < WorldSettleMs)
        {
            return;
        }

        // ⚠ LIVE-CONFIRMED 2026-09-10 (docs/research/world-adoption.md's addendum): the class
        // package chargen just granted sits POSSESSED but UNEQUIPPED in the fresh character's
        // own inventory around this point — the body/right-weapon CSWInventory slot dwords read
        // zero even 90+ seconds after the character went on to join a host's world, while the
        // items themselves were confirmed present the whole time. ActionEquipItem (whatever
        // chargen's own template application uses internally) only QUEUES an equip onto the
        // creature's combat round (see Odyssey.EquipItemFn's own doc comment); nothing in this
        // window drains that queue. Retried every poll for EquipRepairExtraMs (see its own doc
        // comment for why one attempt right at WorldSettleMs's expiry is not reliable — the
        // items are not always there yet, even by this point) before the quicksave below
        // captures this character for carrying, so the phantom a host sees is dressed instead of
        // bare. A no-op for every slot that already holds an item, so an already-equipped
        // character is never touched.
        if (_equipRepairDeadlineMs == 0)
        {
            _equipRepairDeadlineMs = nowMs + EquipRepairExtraMs;
        }

        int repaired = Odyssey.EquipUnequippedStartingGear(Odyssey.GetPlayerObjectId());
        if (repaired > 0)
        {
            PluginHost.Log(
                $"newchar: equipped {repaired} starting item(s) that chargen left unequipped, "
                + "before capturing the character");
        }

        if (nowMs < _equipRepairDeadlineMs)
        {
            return;
        }

        RequestSave(nowMs);
    }

    private void RequestSave(long nowMs)
    {
        _saveRequests++;
        _lastSaveRequestMs = nowMs;

        if (!Odyssey.RequestQuickSave())
        {
            PluginHost.Log($"newchar: save request {_saveRequests} refused - no world loaded");
            return;
        }

        block.JoinChargenState = SharedBlock.JoinChargenStateSaving;
        PluginHost.Log(
            $"newchar: asked the engine to save (request {_saveRequests}/{MaxSaveRequests}); "
            + "watching saves\\ for the directory");
    }

    /// <summary>Phase 2: the save was asked for; watch <c>saves\</c> for what the engine wrote.
    /// </summary>
    private void WatchForSave(long nowMs)
    {
        string savesDirectory = Path.Combine(NativeMethods.GetGameDirectory(), "saves");

        if (!TryFindFreshSave(savesDirectory, out string name, out long bytes, out long writtenTicks))
        {
            _candidate = null;
            _previousBytes = -1;

            if (nowMs - _lastSaveRequestMs >= SaveRetryMs)
            {
                if (_saveRequests >= MaxSaveRequests)
                {
                    // ⚠ A REFUSED SAVE IS SILENT. CSWSPlayer::GetIsAllowedToSave is a plain
                    // [player+0x3C] flag and the engine answers a refusal with a status message
                    // nothing here listens to, so "no directory appeared" is the only signal
                    // there is. Failing (rather than asking forever) is what turns it into a
                    // sentence the player can act on.
                    Fail(
                        $"the engine wrote no save after {MaxSaveRequests} requests - it may be "
                        + "refusing to save here (a cutscene, or no disk space)");
                    return;
                }

                RequestSave(nowMs);
            }

            return;
        }

        if (!string.Equals(name, _candidate, StringComparison.OrdinalIgnoreCase))
        {
            _candidate = name;
            _previousBytes = -1;
        }

        if (!NewCharacterCaptureRule.ShouldCapture(
                fileExists: true, bytes, _previousBytes, writtenTicks, _worldUpAtTicksUtc))
        {
            _previousBytes = bytes;
            return;
        }

        _armed = false;

        // NAME FIRST, STATE LAST — the same ordering every request region here uses, mirrored
        // for a reply: the agent must never read "captured" and then find the name still empty.
        block.PublishJoinChargenSaveDirectory(name);
        block.JoinChargenBytes = (uint)Math.Min(bytes, uint.MaxValue);
        block.JoinChargenState = SharedBlock.JoinChargenStateCaptured;

        PluginHost.Log(
            $"newchar: the engine saved the new character into saves\\{name} "
            + $"({bytes} bytes of SAVEGAME.sav) - the agent can now adopt the host's world");
    }

    /// <summary>
    /// The newest save directory whose <c>SAVEGAME.sav</c> was written after this world came up.
    ///
    /// ⚠ THE FRESHNESS CUTOFF IS WORLD-UP, NOT ARM. An install with saves on disk has old
    /// <c>SAVEGAME.sav</c> files; carrying a character out of one of those would silently join
    /// with somebody the player did not create — the same class of failure the stale-pifo rule
    /// was written for. Keying on world-up (rather than on the arm) also correctly excludes a
    /// save made during a chargen that was cancelled and restarted.
    ///
    /// ANY fresh save counts, not only the one this controller asked for: if the engine's own
    /// autosave-on-entering-module beat the quicksave to it, that save holds the same character
    /// and is just as good. The request exists to guarantee one exists, not to be the only one.
    /// </summary>
    private bool TryFindFreshSave(
        string savesDirectory, out string name, out long bytes, out long writtenTicks)
    {
        name = string.Empty;
        bytes = 0;
        writtenTicks = 0;

        if (!Directory.Exists(savesDirectory))
        {
            return false;
        }

        foreach (string directory in Directory.EnumerateDirectories(savesDirectory))
        {
            string savegame = Path.Combine(directory, "SAVEGAME.sav");
            FileInfo info = new(savegame);

            if (!info.Exists)
            {
                continue;
            }

            long ticks = info.LastWriteTimeUtc.Ticks;
            if (ticks < _worldUpAtTicksUtc || ticks <= writtenTicks)
            {
                continue;
            }

            string candidate = Path.GetFileName(directory);
            if (candidate.Length >= SharedBlock.JoinChargenSaveDirLength)
            {
                // It cannot be named across the block, so it cannot be handed over. Not a
                // failure: the engine's own names are far shorter than this, and a directory
                // this long was not written by the save path.
                continue;
            }

            name = candidate;
            bytes = info.Length;
            writtenTicks = ticks;
        }

        return name.Length > 0;
    }

    private void Fail(string why)
    {
        _armed = false;
        block.JoinChargenState = SharedBlock.JoinChargenStateFailed;
        PluginHost.Log($"newchar: capture failed for request {_armedSerial} - {why}");
    }
}
