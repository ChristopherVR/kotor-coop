using KotorCoop.Memory.GameFiles;

namespace KotorCoop.Memory;

/// <summary>
/// Drives a game from the main menu into a save through the in-process plugin, with no
/// input injection. Menu clicking is intermittently and inexplicably dead (Handoff trap
/// 25); this path cannot miss, because the plugin replays the GUI's own load code on the
/// game's thread.
///
/// The mechanism leans on a filesystem fact: a KOTOR save is just a directory of five
/// files, and the engine's autosave-load path (`CSWGuiSaveLoad::LoadPCAutoSave`) builds
/// its target name from the literal "AUTOSAVE". So "load save N" becomes: copy save N's
/// directory over `000000 - AUTOSAVE`, then ask the plugin to fire the autosave load.
///
/// This class is deliberately standalone rather than part of KotorGameBridge: the bridge
/// anchors on the player creature, which does not exist at the main menu, while this must
/// work exactly there.
/// </summary>
public static class PluginMenuLoader
{
    /// <summary>
    /// The save slot the plugin's load path fires against.
    ///
    /// ⚠ Id 1, not 0: <c>LoadPCAutoSave</c> formats its target as
    /// <c>Format("%06d - %s", 1, "AUTOSAVE")</c>, and staging "000000 - AUTOSAVE" produced a
    /// load that reached SetLoadingModule(1) and then silently bailed with no module target.
    /// </summary>
    public const string AutosaveSlot = "000001 - AUTOSAVE";

    private const int BlockMenuLoadManualIntent = 0x8634;
    private const int BlockMenuLoadSerial = 0x9A4;
    private const int BlockMenuLoadApplied = 0x9A8;
    private const int BlockMenuLoadStatus = 0x9AC;
    private const int BlockMenuLoadModule = 0x9B0;
    private const int BlockMenuLoadModuleLength = 16;
    private const int BlockHeartbeat = 0x4C;

    // SharedBlock.OffsetMenuLoadInSession (block version 52). See TryRequestLoad's own
    // <paramref name="inSession"/> remark and docs/research/session-save.md's dated entry.
    private const int BlockMenuLoadInSession = 0x3F24;

    // SharedBlock.OffsetAdoptWorldLogin / OffsetAdoptWorldApplied (block version 35).
    private const int BlockAdoptWorldLogin = 0x3320;
    private const int BlockAdoptWorldApplied = 0x3324;

    // SharedBlock's new-character (coop.ini join_mode = newcharacter) capture region,
    // 0x4780..0x47A0 — SharedBlock.OffsetJoinChargenRequest and friends.
    private const int BlockJoinChargenRequest = 0x4780;
    private const int BlockJoinChargenState = 0x4788;
    private const int BlockJoinChargenBytes = 0x478C;

    /// <summary>SharedBlock.OffsetJoinChargenSaveDir - the NAME of the save directory the
    /// plugin had the engine write, relative to `saves\`, NUL-padded ASCII. Pinned equal to the
    /// plugin's own constant by SharedBlockNewRegionOffsetMirrorTests.</summary>
    private const int BlockJoinChargenSaveDir = 0x47C0;

    private const int BlockJoinChargenSaveDirLength = 32;

    // SharedBlock's chargen-drive verb region (`newgame`, `chargen quick`, `chargen accept`).
    private const int BlockChargenDriveRequest = 0x4790;
    private const int BlockChargenDriveKind = 0x4794;
    private const int BlockChargenDriveApplied = 0x4798;
    private const int BlockChargenDriveStatus = 0x479C;

    /// <summary>
    /// Reads which module a save is standing in. The engine's LoadModule takes this name as
    /// its argument — an empty name is the silent stall.
    ///
    /// savenfo.res's <c>LASTMODULE</c> is the answer when it is readable: it is the field
    /// retail's own save list shows and loads from. A single SAVEGAME.sav module member is
    /// the fallback when the metadata is absent. Picking the first of multiple members is
    /// ambiguous, including one the engine wrote AT a module transition
    /// (the autosave a <c>warp</c> triggers, live 2026-09-02): the nested module .sav there
    /// is still the module being LEFT (END_M01AA), while LASTMODULE already names the one
    /// being entered (tar_m10aa). Loading that save through the ERF answer put the client in
    /// END_M01AA against a host in tar_m10aa, whose follow-warp autosaved, which read as an
    /// own load, which rejoined into END_M01AA again — a rejoin loop with no exit.
    /// </summary>
    public static bool TryReadSaveModuleName(string saveDirectory, out string moduleName)
    {
        moduleName = string.Empty;

        if (TryReadLastModule(saveDirectory, out moduleName))
        {
            return true;
        }

        try
        {
            ErfArchive outer = ErfArchive.Open(Path.Combine(saveDirectory, "SAVEGAME.sav"));
            if (SavedModuleSelection.TrySelect(saveDirectory, outer, out ArchiveResource module))
            {
                moduleName = module.Resref;
                return moduleName.Length > 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }

        return false;
    }
    /// <summary>Reads only <c>savenfo.res:LASTMODULE</c>, without falling back to the first
    /// nested module member. Callers selecting a module state must use this distinction:
    /// enumeration order is not an identity, especially for transition autosaves.</summary>
    internal static bool TryReadSaveLastModuleName(string saveDirectory, out string moduleName) =>
        TryReadLastModule(saveDirectory, out moduleName);

    /// <summary>savenfo.res's <c>LASTMODULE</c> (a "NFO " GFF), or false when the file is
    /// missing, not a GFF (the test fixtures write a placeholder byte), or has no such
    /// field — never throws, so the ERF fallback above always gets its turn.</summary>
    private static bool TryReadLastModule(string saveDirectory, out string moduleName)
    {
        moduleName = string.Empty;
        string nfoPath = Path.Combine(saveDirectory, "savenfo.res");

        try
        {
            if (!File.Exists(nfoPath))
            {
                return false;
            }

            Gff nfo = Gff.Parse(File.ReadAllBytes(nfoPath));
            return nfo.Root.TryGetString("LASTMODULE", out moduleName)
                && moduleName.Length > 0
                && moduleName.Length < BlockMenuLoadModuleLength;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            moduleName = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// The save directory holding slot <paramref name="slotIndex"/> (0-based, in the same
    /// most-recent-first order the in-game list shows).
    ///
    /// The autosave is excluded from the ordering rather than counted: the GUI pins it on
    /// top, so counting it would shift every real slot by one — the off-by-one that made
    /// "-Save 2" silently load through a different path than "-Save 1".
    /// </summary>
    public static bool TryResolveSaveDirectory(
        string gameDirectory, int slotIndex, out string saveDirectory, out string diagnostic)
    {
        saveDirectory = string.Empty;

        string savesDirectory = Path.Combine(gameDirectory, "saves");
        if (!Directory.Exists(savesDirectory))
        {
            diagnostic = $"no saves directory at {savesDirectory}";
            return false;
        }

        List<DirectoryInfo> saves =
        [
            .. new DirectoryInfo(savesDirectory)
                .GetDirectories()
                .Where(d => !d.Name.EndsWith("AUTOSAVE", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.LastWriteTimeUtc),
        ];

        if (slotIndex < 0 || slotIndex >= saves.Count)
        {
            diagnostic = $"save slot {slotIndex} does not exist ({saves.Count} save(s) found)";
            return false;
        }

        saveDirectory = saves[slotIndex].FullName;
        diagnostic = saves[slotIndex].Name;
        return true;
    }

    /// <summary>
    /// Copies the save directory holding slot <paramref name="slotIndex"/> (0-based, in
    /// the same most-recent-first order the in-game list shows) over the autosave slot.
    /// </summary>
    public static bool TryStageSaveAsAutosave(string gameDirectory, int slotIndex, out string diagnostic)
    {
        if (!TryResolveSaveDirectory(gameDirectory, slotIndex, out string sourcePath, out diagnostic))
        {
            return false;
        }

        return TryStageSaveAsAutosaveFromDirectory(gameDirectory, sourcePath, out diagnostic);
    }

    /// <summary>Stages the already-resolved folder. Callers that need source identity must
    /// resolve once and pass that exact path through staging rather than resolving a mutable
    /// recency-ordered slot a second time.</summary>
    public static bool TryStageSaveAsAutosaveFromDirectory(
        string gameDirectory,
        string sourcePath,
        out string diagnostic)
    {
        string savesDirectory = Path.Combine(gameDirectory, "saves");
        string fullSaves = Path.GetFullPath(savesDirectory);
        string fullSource = Path.GetFullPath(sourcePath);
        if (!fullSource.StartsWith(fullSaves + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(fullSource))
        {
            diagnostic = $"selected save folder is not inside {fullSaves}";
            return false;
        }

        DirectoryInfo source = new(fullSource);
        string autosave = Path.Combine(savesDirectory, AutosaveSlot);

        try
        {
            if (Directory.Exists(autosave))
            {
                Directory.Delete(autosave, recursive: true);
            }

            Directory.CreateDirectory(autosave);
            foreach (FileInfo file in source.GetFiles())
            {
                file.CopyTo(Path.Combine(autosave, file.Name));
            }

            // File.Copy may preserve the source archive timestamp. The staged autosave is a
            // newly activated baseline, so stamp the archive after the complete copy. The live
            // resolver compares the conventional quicksave against this boundary.
            File.SetLastWriteTimeUtc(
                Path.Combine(autosave, "SAVEGAME.sav"), DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            diagnostic = $"could not stage '{source.Name}' as the autosave: {ex.Message}";
            return false;
        }

        diagnostic = $"staged '{source.Name}' as the autosave";
        return true;
    }

    /// <summary>
    /// Finds the plugin's shared block (works at the main menu; no player needed) and
    /// bumps the menu-load serial. Returns the block address for status polling.
    /// </summary>
    /// <param name="adoptedWorld">True when the staged save came from the host rather than
    /// this install (world adoption), so the character must be read from Temp\pifo.ifo
    /// instead of the staged world's own module.ifo — which holds the HOST's character. See
    /// the adopted-world login region in <c>SharedBlock</c> and
    /// docs/research/character-login-chain.md.</param>
    /// <param name="inSession">True to request the MID-SESSION (pause-menu-equivalent) replay
    /// instead of the main-menu-only one: <c>MenuLoadController</c> then runs retail's own
    /// extra <c>CAppManager::DestroyServer</c> + <c>CClientExoApp::UnloadModule</c> pre-step
    /// (<c>CSWGuiSaveLoad::LoadGame</c>'s <c>bFromMainMenu==0</c> branch) instead of
    /// acking-and-mooting the instant <c>Odyssey.HasServer</c> is true. See
    /// <c>SharedBlock.OffsetMenuLoadInSession</c> (block version 52) and
    /// docs/research/session-save.md's dated entry on the <c>loadgame</c> verb.</param>
    public static bool TryRequestLoad(
        ProcessMemory memory, out nint block, out string diagnostic, bool adoptedWorld = false,
        bool inSession = false, bool manualIntent = false)
        => TryRequestLoad(memory, out block, out diagnostic, out _, adoptedWorld, inSession, manualIntent);

    public static bool TryRequestLoad(
        ProcessMemory memory, out nint block, out string diagnostic, out uint requestId,
        bool adoptedWorld = false, bool inSession = false, bool manualIntent = false)
    {
        requestId = 0;
        if (!TryFindBlock(memory, out block, out diagnostic))
        {
            return false;
        }

        // The module the staged save stands in; the engine's LoadModule needs the name.
        string savesDirectory = string.Empty;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(memory.ProcessId);
            savesDirectory = Path.Combine(
                Path.GetDirectoryName(process.MainModule?.FileName) ?? string.Empty,
                "saves", AutosaveSlot);
        }
        catch (Exception)
        {
        }

        if (!TryReadSaveModuleName(savesDirectory, out string moduleName))
        {
            diagnostic = $"could not read the module name from {savesDirectory}";
            return false;
        }

        if (!memory.TryWriteAsciiString(block + BlockMenuLoadModule, moduleName, BlockMenuLoadModuleLength))
        {
            diagnostic = "could not write the module name";
            return false;
        }

        // Which character the load brings up, written BEFORE the serial for the same reason
        // the module name is: the plugin acts on the serial, so everything it will read must
        // already be there.
        if (!memory.TryWrite(block + BlockAdoptWorldLogin, adoptedWorld ? 1u : 0u))
        {
            diagnostic = "could not write the login flavour";
            return false;
        }

        // Same rule, same reason: whether this is the mid-session replay must already be
        // there when the plugin reads the bumped serial. Always written (0 or 1), not only
        // when true, so a STALE flag from an earlier mid-session request can never leak into
        // a later plain one.
        if (!memory.TryWrite(block + BlockMenuLoadInSession, inSession ? 1u : 0u))
        {
            diagnostic = "could not write the in-session flag";
            return false;
        }

        if (!memory.TryWrite(block + BlockMenuLoadManualIntent, manualIntent ? 1u : 0u))
        {
            diagnostic = "could not write the manual request intent";
            return false;
        }

        // Continue from whatever serial the block holds — the plugin outlives agents.
        if (!memory.TryReadUInt32(block + BlockMenuLoadSerial, out uint serial)
            || !memory.TryWrite(block + BlockMenuLoadSerial, serial + 1))
        {
            diagnostic = "could not write the load request";
            return false;
        }

        requestId = unchecked(serial + 1);
        diagnostic = $"load request {serial + 1} sent to the plugin (module {moduleName}"
            + (adoptedWorld ? ", carried character" : string.Empty)
            + (inSession ? ", mid-session)" : ")");
        return true;
    }

    /// <summary>
    /// Which character the last load actually brought up: 1 the world's own saved character,
    /// 2 the character carried in through Temp\pifo.ifo, 0 nothing loaded yet.
    ///
    /// Worth checking rather than assuming. A missing pifo does not fail the load — the
    /// engine's 0xF login branch ignores LoadCharacterFromIFO's return value — it produces a
    /// blank level-1 stranger, and this is the field that says so before the player notices.
    /// </summary>
    public static bool TryReadLoginFlavour(ProcessMemory memory, nint block, out uint flavour) =>
        memory.TryReadUInt32(block + BlockAdoptWorldApplied, out flavour);

    /// <summary>
    /// True while a load this agent requested has not been acked by the plugin — for a
    /// successful load the ack comes only when "the world is up and on screen", so the whole
    /// teardown → load → present window reads as in flight. False without a block, so a
    /// plugin-less attach is never held.
    /// </summary>
    public static bool TryReadLoadInFlight(ProcessMemory memory, nint block)
    {
        return block != 0
            && memory.TryReadUInt32(block + BlockMenuLoadSerial, out uint serial)
            && memory.TryReadUInt32(block + BlockMenuLoadApplied, out uint applied)
            && IsLoadInFlight(serial, applied);
    }

    /// <summary>The rule behind <see cref="TryReadLoadInFlight"/>: the plugin's ack lags the
    /// request for as long as the load is being serviced; equal means idle (or never asked).</summary>
    public static bool IsLoadInFlight(uint requestedSerial, uint appliedSerial) =>
        requestedSerial != appliedSerial;

    /// <summary>
    /// Reads back the plugin's ack and failure code for the last request.  The applied field
    /// is cumulative across requests, so a fresh request can briefly expose the previous
    /// nonzero applied value while the engine is still on the old world.  Return a result only
    /// when the applied serial acknowledges the CURRENT request; otherwise expose zero so
    /// callers cannot mistake a stale success for a new settled load.
    /// </summary>
    public static bool TryReadStatus(ProcessMemory memory, nint block, out uint applied, out int status)
    {
        applied = 0;
        status = 0;
        if (!memory.TryReadUInt32(block + BlockMenuLoadSerial, out uint requested)
            || !memory.TryReadUInt32(block + BlockMenuLoadApplied, out uint observedApplied)
            || !memory.TryReadInt32(block + BlockMenuLoadStatus, out int observedStatus))
        {
            return false;
        }

        // A request can be issued on the dispatcher task while this poll runs. Re-read
        // the serial after the payload so a previous applied value cannot pair with the
        // request serial captured before the new request was written.
        if (!memory.TryReadUInt32(block + BlockMenuLoadSerial, out uint requestedAfter)
            || !IsLoadAcknowledged(requested, requestedAfter, observedApplied))
        {
            return true;
        }

        applied = observedApplied;
        status = observedStatus;
        return true;
    }

    /// <summary>True only when a nonzero request serial has been acknowledged by the plugin.
    /// Equality is required because the applied field retains the previous request's serial
    /// while a new load is still being serviced.</summary>
    public static bool IsLoadAcknowledged(uint requestedSerial, uint appliedSerial) =>
        requestedSerial != 0 && requestedSerial == appliedSerial;

    /// <summary>Snapshot-safe form used when request and status fields are read separately:
    /// both request reads must agree before the applied serial can acknowledge the request.</summary>
    public static bool IsLoadAcknowledged(
        uint requestedSerialBefore,
        uint requestedSerialAfter,
        uint appliedSerial) =>
        requestedSerialBefore == requestedSerialAfter
        && IsLoadAcknowledged(requestedSerialAfter, appliedSerial);

    /// <summary>
    /// Finds the plugin's shared block, proving it live before handing it back.
    ///
    /// Two things make this more than a scan, and both were learned the hard way. The
    /// lifecycle can report "main menu" seconds before the plugin has allocated its block —
    /// the phase check reads engine state, the block appears when the game's startup calls
    /// <c>DirectInput8Create</c> — so a single scan races it. And a STALE block in freed
    /// memory still matches the signature, so a candidate is accepted only once its heartbeat
    /// has been seen to ADVANCE.
    ///
    /// Extracted from <see cref="TryRequestLoad"/> so <see cref="TryArmNewCharacterCapture"/>
    /// finds the same block the same way, rather than growing a second, subtly different copy
    /// of this.
    /// </summary>
    public static bool TryFindBlock(ProcessMemory memory, out nint block, out string diagnostic)
    {
        block = 0;

        PatternScanner scanner = new(memory);
        BytePattern signature = BytePattern.Parse(KotorGameBridge.PluginSignature);

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (block == 0 && DateTime.UtcNow < deadline)
        {
            foreach (nint candidate in scanner.ScanAll(signature, ScanScope.PrivateCommitted, limit: 4))
            {
                if (!memory.TryReadUInt32(candidate + BlockHeartbeat, out uint first))
                {
                    continue;
                }

                Thread.Sleep(60);

                if (memory.TryReadUInt32(candidate + BlockHeartbeat, out uint second) && second != first)
                {
                    block = candidate;
                    break;
                }
            }

            if (block == 0)
            {
                Thread.Sleep(2000);
            }
        }

        if (block == 0)
        {
            diagnostic = "plugin shared block not found (is dinput8.dll installed and the version matched?)";
            return false;
        }

        diagnostic = $"plugin shared block at 0x{block:X}";
        return true;
    }

    /// <summary>
    /// Arms the plugin's new-character capture — coop.ini <c>join_mode = newcharacter</c>.
    ///
    /// The plugin then watches for <c>Temp\pifo.ifo</c> to be written by the game's own
    /// character creation, lets the new game reach its first module, has the engine SAVE there,
    /// and names that save back. (It used to watch <c>Temp\pifo.ifo</c>, on the belief that
    /// retail chargen writes one; it does not - see
    /// <c>KotorCoop.Plugin.NewCharacterCaptureController</c>'s header for the correction.) The
    /// serial IS the whole request — there is nothing else to say — so unlike
    /// <see cref="TryRequestLoad"/> there is no "write the payload first" ordering to respect
    /// here.
    /// </summary>
    public static bool TryArmNewCharacterCapture(
        ProcessMemory memory, out nint block, out string diagnostic)
    {
        if (!TryFindBlock(memory, out block, out diagnostic))
        {
            return false;
        }

        // Continue from whatever serial the block holds — the plugin outlives agents.
        if (!memory.TryReadUInt32(block + BlockJoinChargenRequest, out uint serial)
            || !memory.TryWrite(block + BlockJoinChargenRequest, serial + 1))
        {
            diagnostic = "could not write the new-character capture request";
            return false;
        }

        diagnostic = $"new-character capture armed (request {serial + 1})";
        return true;
    }

    /// <summary>
    /// Reads back what the armed capture has done: <paramref name="state"/> is one of
    /// <c>SharedBlock.JoinChargenState*</c> (0 idle, 1 armed, 2 captured, 3 failed) and
    /// <paramref name="bytes"/> is the size of what was captured.
    ///
    /// ⚠ READ THE BYTES, NOT JUST THE STATE. A zero-byte file is not "no file" — it is a write
    /// that has been opened and not filled, and every reader downstream of here turns a
    /// characterless input into a blank level-1 stranger rather than an error
    /// (world-adoption.md §7). The plugin already refuses to report a short capture; the size is
    /// here so the agent can say so too.
    ///
    /// <paramref name="saveDirectory"/> names the save the plugin had the engine write,
    /// relative to <c>saves\</c>, and is meaningful only in the captured state.
    /// </summary>
    public static bool TryReadNewCharacterCapture(
        ProcessMemory memory, nint block, out uint state, out uint bytes, out string saveDirectory)
    {
        bytes = 0;
        saveDirectory = string.Empty;

        if (!memory.TryReadUInt32(block + BlockJoinChargenState, out state)
            || !memory.TryReadUInt32(block + BlockJoinChargenBytes, out bytes))
        {
            return false;
        }

        // Best-effort: the name is only meaningful once the state says captured, and a failed
        // string read must not turn a good poll into a dead one.
        memory.TryReadAsciiString(
            block + BlockJoinChargenSaveDir, BlockJoinChargenSaveDirLength, out saveDirectory);

        return true;
    }

    /// <summary>
    /// Runs one of the character-creation drive verbs in the plugin — <c>newgame</c>,
    /// <c>chargen quick</c>, <c>chargen accept</c>. The plugin calls the engine's OWN button
    /// handler for that step (see <c>KotorCoop.Plugin.CharGenDriveController</c>); nothing here
    /// synthesises input.
    ///
    /// Waits for the plugin's ack so the caller learns whether the step was refused — these are
    /// single engine calls made on the next MainLoop frame, not module loads, so the wait is
    /// bounded and short.
    /// </summary>
    /// <param name="kind">One of <c>SharedBlock.ChargenDrive*</c>, mirrored agent-side in
    /// <see cref="ChargenDriveKinds"/>.</param>
    /// <param name="classIndex">For <c>quick</c>: which of the six class portraits, 0-5. The
    /// engine derives the class from the CONTROL'S ADDRESS, so the plugin turns this back into
    /// the genuine embedded control rather than passing a number on.</param>
    public static bool TryDriveChargen(
        ProcessMemory memory, uint kind, int classIndex, out string diagnostic)
    {
        if (!TryFindBlock(memory, out nint block, out diagnostic))
        {
            return false;
        }

        // Kind (with the class index in the high half) BEFORE the serial: the plugin acts on
        // the serial, so everything it will read must already be there.
        uint packed = (kind & 0xFFFF) | ((uint)classIndex << 16);
        if (!memory.TryWrite(block + BlockChargenDriveKind, packed)
            || !memory.TryReadUInt32(block + BlockChargenDriveRequest, out uint serial)
            || !memory.TryWrite(block + BlockChargenDriveRequest, serial + 1))
        {
            diagnostic = "could not write the chargen request";
            return false;
        }

        serial++;

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (memory.TryReadUInt32(block + BlockChargenDriveApplied, out uint applied)
                && applied == serial)
            {
                memory.TryReadUInt32(block + BlockChargenDriveStatus, out uint status);
                diagnostic = status == 0
                    ? $"chargen: {ChargenDriveKinds.Describe(kind, classIndex)} done"
                    : $"chargen: {ChargenDriveKinds.Describe(kind, classIndex)} refused "
                        + $"(failure {status}; the reason is in kotorcoop-plugin.log)";
                return status == 0;
            }

            Thread.Sleep(50);
        }

        diagnostic = "chargen: the plugin did not answer (is the game at the main menu with the "
            + "plugin loaded?)";
        return false;
    }
}

/// <summary>
/// The plugin's <c>SharedBlock.ChargenDrive*</c> constants, mirrored on this side of the
/// x86/x64 boundary the same way <c>CheatKinds</c> mirrors the cheat kinds — the two assemblies
/// cannot share a type.
/// </summary>
public static class ChargenDriveKinds
{
    /// <summary>Open the character-creation screens from the main menu.</summary>
    public const uint NewGame = 1;

    /// <summary>Pick a class and apply the optimised "Quick Character" template.</summary>
    public const uint Quick = 2;

    /// <summary>Press Play — writes the character to <c>TEMP:pifo</c> and starts the module.
    /// </summary>
    public const uint Accept = 3;

    public static string Describe(uint kind, int classIndex) => kind switch
    {
        NewGame => "newgame",
        Quick => $"quick (class {classIndex})",
        Accept => "accept",
        _ => $"kind {kind}",
    };
}
