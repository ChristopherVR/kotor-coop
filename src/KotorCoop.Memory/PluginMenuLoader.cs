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

    /// <summary>
    /// Reads which module a save is standing in, from SAVEGAME.sav's ERF key list: the
    /// entry typed 2055 (.sav) IS the module (e.g. END_M01AA). The engine's LoadModule
    /// takes this name as its argument — an empty name is the silent stall.
    /// </summary>
    public static bool TryReadSaveModuleName(string saveDirectory, out string moduleName)
    {
        moduleName = string.Empty;
        string erfPath = Path.Combine(saveDirectory, "SAVEGAME.sav");

        try
        {
            using FileStream stream = File.OpenRead(erfPath);
            using BinaryReader reader = new(stream);

            byte[] magic = reader.ReadBytes(8);
            if (magic.Length < 8)
            {
                return false;
            }

            stream.Position = 0x10;
            int entryCount = reader.ReadInt32();
            stream.Position = 0x18;
            int keyListOffset = reader.ReadInt32();

            stream.Position = keyListOffset;
            for (int i = 0; i < entryCount && i < 4096; i++)
            {
                byte[] resref = reader.ReadBytes(16);
                reader.ReadUInt32();
                ushort resType = reader.ReadUInt16();
                reader.ReadUInt16();

                // 2057 is what the retail GOG build writes for the module .sav inside
                // SAVEGAME.sav (measured); 2055 is what older restype tables claim.
                if (resType is 2057 or 2055)
                {
                    int length = Array.IndexOf(resref, (byte)0);
                    moduleName = System.Text.Encoding.ASCII
                        .GetString(resref, 0, length < 0 ? resref.Length : length);
                    return moduleName.Length > 0;
                }
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
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

        DirectoryInfo source = new(sourcePath);
        string savesDirectory = Path.Combine(gameDirectory, "saves");
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
        bool inSession = false)
    {
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

        // Continue from whatever serial the block holds — the plugin outlives agents.
        if (!memory.TryReadUInt32(block + BlockMenuLoadSerial, out uint serial)
            || !memory.TryWrite(block + BlockMenuLoadSerial, serial + 1))
        {
            diagnostic = "could not write the load request";
            return false;
        }

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

    /// <summary>Reads back the plugin's ack and failure code for the last request.</summary>
    public static bool TryReadStatus(ProcessMemory memory, nint block, out uint applied, out int status)
    {
        applied = 0;
        status = 0;
        return memory.TryReadUInt32(block + BlockMenuLoadApplied, out applied)
            && memory.TryReadInt32(block + BlockMenuLoadStatus, out status);
    }

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
    /// character creation (<c>CSWCCreature::SaveCharGenCreature</c> 0x6123E0, the chargen
    /// entry in docs/research/character-login-chain.md §2's exhaustive writer list) and copies
    /// it to the carried-character stash the adoption load restores from. The serial IS the
    /// whole request — there is nothing else to say — so unlike <see cref="TryRequestLoad"/>
    /// there is no "write the payload first" ordering to respect here.
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
    /// ⚠ READ THE BYTES, NOT JUST THE STATE. A zero-byte pifo is not "no file" — it is the
    /// exact wreckage a source-less <c>FileCopy</c> leaves (world-adoption.md §7) and the
    /// engine's login turns it into a blank level-1 stranger rather than an error. The plugin
    /// already refuses to report a short capture; the size is here so the agent can say so too.
    /// </summary>
    public static bool TryReadNewCharacterCapture(
        ProcessMemory memory, nint block, out uint state, out uint bytes)
    {
        bytes = 0;
        return memory.TryReadUInt32(block + BlockJoinChargenState, out state)
            && memory.TryReadUInt32(block + BlockJoinChargenBytes, out bytes);
    }
}
