using KotorCoop.Memory;

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
/// ⚠ NO EQUIP REPAIR HAPPENS HERE ANY MORE — ROUND 4, 2026-09-10e. Rounds 1-3 ran the starting-
/// gear equip repair (<see cref="Odyssey.EquipUnequippedStartingGear"/>) from right here, right
/// before the save, for up to 90 (then round 4's own diagnostic, 150) seconds. Round 4 pushed
/// that ceiling to 150 SECONDS and captured a live join end to end: <c>Odyssey.
/// GetPlayerObjectId()</c> read the engine's <c>0x7FFFFFFF</c> value on every single poll of the
/// whole window, AND — this is the round 4 correction — that same accessor reads the IDENTICAL
/// <c>0x7FFFFFFF</c> for the HOST's own long-established, fully-equipped character, nothing to do
/// with chargen. It is not a "no real player yet" placeholder that eventually resolves (rounds
/// 1-3's framing, and this controller's own now-removed <c>EquipRepairMaxWallClockMs</c>/
/// <c>EquipRepairMaxAttempts</c>); it is the engine's permanent, reserved self-id for whichever
/// creature a client currently drives, on every client, forever. The actual reason the repair
/// never found anything here: round 4's same capture showed both candidate repositories
/// <c>EquipUnequippedStartingGear</c> checks read <c>count=0</c> for the ENTIRE original chargen-
/// module window — chargen's own starting items are simply never linked into either repository
/// while still inside the isolated, hand-rolled in-process chargen replay
/// (<see cref="CharGenDriveController"/>), no matter which id is used to address them or how long
/// the wait. So this controller now saves promptly (<see cref="WorldSettleMs"/> alone, no repair
/// loop eating up to two and a half extra minutes of a player's first join for nothing) and
/// <see cref="PostAdoptEquipRepairController"/> runs the SAME repair exactly once, after the
/// adopted-world load that follows — a genuine, ordinary engine module load, which round 4
/// live-verified DOES link the items into a checked repository. See that class's own doc comment
/// for the full mechanism, and docs/research/world-adoption.md's "Round 2026-09-10e" for the live
/// evidence trail.
///
/// Runs from <c>PluginHost.MenuFrame</c> — the MainLoop hook, which fires at the main menu AND
/// in-world, so one schedule covers both halves of this flow. The SwapBuffers hook would miss
/// the first half entirely (dormant behind the menu movie), which is the same reason
/// <see cref="MenuLoadController"/> lives there.
/// </summary>
internal sealed class NewCharacterCaptureController(
    SharedBlockView block, PostAdoptEquipRepairController postAdoptEquipRepair)
{
    /// <summary>How long a world must have been up before the save is asked for. The module
    /// load itself is over by the time a player creature answers, but the opening script pass
    /// (spawns, party assembly, the first cutscene arming) is not, and asking mid-way is how a
    /// save gets refused for reasons nothing reports.</summary>
    private const int WorldSettleMs = 4000;

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

    /// <summary>Tick of the last capture, or 0. With <see cref="_menuLoadAppliedAtCapture"/>,
    /// what <see cref="HoldsFollowWarp"/> keys on between the capture and the adoption load.</summary>
    private long _capturedAtMs;

    private uint _menuLoadAppliedAtCapture;

    /// <summary>
    /// True while a session follow warp must wait for this capture
    /// (<see cref="KotorCoop.Protocol.NewCharacterWarpHoldRule"/>): from the arm until the
    /// adoption load that follows the capture. MAIN THREAD ONLY.
    /// </summary>
    public bool HoldsFollowWarp(long nowMs) => KotorCoop.Protocol.NewCharacterWarpHoldRule.Holds(
        _armed, _capturedAtMs, _menuLoadAppliedAtCapture, block.MenuLoadApplied, nowMs);

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
        _saveRequests = 0;
        _lastSaveRequestMs = 0;
        _candidate = null;
        _previousBytes = -1;
        _capturedAtMs = 0;

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

        // ⚠ NO EQUIP REPAIR HERE ANY MORE — see this class's own doc comment ("ROUND 4, 2026-
        // 09-10e"). Round 4 live-confirmed the starting-gear repair can never find anything
        // inside this isolated chargen module (both candidate repositories read count=0 for the
        // WHOLE window, regardless of id or how long the wait), so this controller now saves
        // promptly rather than burning up to two and a half extra minutes of the player's first
        // join on a repair that cannot succeed here. PostAdoptEquipRepairController runs the
        // same repair once, after the adopted-world load that follows.
        RequestSave(nowMs);
    }

    private void RequestSave(long nowMs)
    {
        _saveRequests++;
        _lastSaveRequestMs = nowMs;

        // Hold the player's own quicksave aside BEFORE asking the engine to save: DoQuickSave
        // is hard-coded to slot 0, "QUICKSAVE", so that is where this capture will land too.
        // CoopSaveRedirect.CompleteRedirect (below, once the save is confirmed landed and
        // stable) moves the fresh capture to the dedicated co-op slot and restores whatever was
        // held back. See docs/research/session-save.md's 2026-09-25 entry.
        string savesDirectory = Path.Combine(NativeMethods.GetGameDirectory(), "saves");
        if (!CoopSaveRedirect.BeginHoldback(savesDirectory, out string holdbackDiagnostic))
        {
            PluginHost.Log($"newchar: {holdbackDiagnostic}");
        }

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

        // Relocate the fresh save to the dedicated co-op slot BEFORE reading anything back off
        // it, and restore whatever quicksave was held back. Every downstream reader
        // (PlayerIfo.TryExtractPlayer on this same directory, via the agent) works unchanged —
        // it is handed the directory NAME below, whichever one that ends up being.
        if (string.Equals(name, CoopSaveRedirect.QuickSaveFolder, StringComparison.OrdinalIgnoreCase)
            && CoopSaveRedirect.CompleteRedirect(savesDirectory, out string redirected, out string redirectDiagnostic))
        {
            name = Path.GetFileName(redirected);
            PluginHost.Log($"newchar: {redirectDiagnostic}");
        }

        // Arm the post-adopt repair BEFORE publishing the capture — the agent (JoinFlow.cs's
        // CreateCharacterThenAdoptAsync) polls block.JoinChargenState and can request the
        // adopted-world load (bumping block.MenuLoadSerial) essentially the instant it sees
        // JoinChargenStateCaptured below, so PostAdoptEquipRepairController must already be
        // watching block.MenuLoadApplied's baseline before that happens. See its own doc
        // comment for the whole mechanism and why this repair no longer runs here.
        postAdoptEquipRepair.Arm();

        // Keep holding the follow warp until the adoption load replaces this world; the agent
        // requests that load as soon as it reads the state below, so take the baseline first.
        _capturedAtMs = nowMs;
        _menuLoadAppliedAtCapture = block.MenuLoadApplied;

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
