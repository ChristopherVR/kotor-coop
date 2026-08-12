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

    // SharedBlock.OffsetAdoptWorldLogin / OffsetAdoptWorldApplied (block version 35).
    private const int BlockAdoptWorldLogin = 0x3320;
    private const int BlockAdoptWorldApplied = 0x3324;

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
    /// Copies the save directory holding slot <paramref name="slotIndex"/> (0-based, in
    /// the same most-recent-first order the in-game list shows) over the autosave slot.
    /// </summary>
    public static bool TryStageSaveAsAutosave(string gameDirectory, int slotIndex, out string diagnostic)
    {
        string savesDirectory = Path.Combine(gameDirectory, "saves");
        if (!Directory.Exists(savesDirectory))
        {
            diagnostic = $"no saves directory at {savesDirectory}";
            return false;
        }

        // The GUI lists saves newest-first, with the autosave pinned on top when it
        // exists. Slot indices here refer to real saves, so the autosave is excluded
        // from the ordering rather than counted.
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

        DirectoryInfo source = saves[slotIndex];

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
    public static bool TryRequestLoad(
        ProcessMemory memory, out nint block, out string diagnostic, bool adoptedWorld = false)
    {
        block = 0;

        PatternScanner scanner = new(memory);
        BytePattern signature = BytePattern.Parse(KotorGameBridge.PluginSignature);

        // The lifecycle can report "main menu" seconds before the plugin has allocated
        // its block (the phase check reads engine state, the block appears when the
        // game's startup calls DirectInput8Create), so a single scan races it. Retry
        // briefly; the plugin side then waits for the real menu on its own.
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (block == 0 && DateTime.UtcNow < deadline)
        {
            foreach (nint candidate in scanner.ScanAll(signature, ScanScope.PrivateCommitted, limit: 4))
            {
                // A live plugin's heartbeat advances; a stale block in freed memory does not.
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

        // Continue from whatever serial the block holds — the plugin outlives agents.
        if (!memory.TryReadUInt32(block + BlockMenuLoadSerial, out uint serial)
            || !memory.TryWrite(block + BlockMenuLoadSerial, serial + 1))
        {
            diagnostic = "could not write the load request";
            return false;
        }

        diagnostic = $"load request {serial + 1} sent to the plugin (module {moduleName}"
            + (adoptedWorld ? ", carried character)" : ")");
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
}
