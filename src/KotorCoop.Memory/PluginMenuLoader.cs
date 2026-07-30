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
    private const int BlockMenuLoadSerial = 0x9A4;
    private const int BlockMenuLoadApplied = 0x9A8;
    private const int BlockMenuLoadStatus = 0x9AC;
    private const int BlockHeartbeat = 0x4C;

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
        string autosave = Path.Combine(savesDirectory, "000000 - AUTOSAVE");

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
    public static bool TryRequestLoad(ProcessMemory memory, out nint block, out string diagnostic)
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

        // Continue from whatever serial the block holds — the plugin outlives agents.
        if (!memory.TryReadUInt32(block + BlockMenuLoadSerial, out uint serial)
            || !memory.TryWrite(block + BlockMenuLoadSerial, serial + 1))
        {
            diagnostic = "could not write the load request";
            return false;
        }

        diagnostic = $"load request {serial + 1} sent to the plugin";
        return true;
    }

    /// <summary>Reads back the plugin's ack and failure code for the last request.</summary>
    public static bool TryReadStatus(ProcessMemory memory, nint block, out uint applied, out int status)
    {
        applied = 0;
        status = 0;
        return memory.TryReadUInt32(block + BlockMenuLoadApplied, out applied)
            && memory.TryReadInt32(block + BlockMenuLoadStatus, out status);
    }
}
