using System.Diagnostics;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// The `agent adopt` verb: adopts a host's world into this install and loads it carrying this
/// install's own character — the joining half of world adoption.
///
/// Run against a game sitting at the MAIN MENU: the load is fired through the plugin, the
/// same route `load` uses, but with the character login left on TEMP:pifo so the world's own
/// (the host's) character is not the one that comes up.
/// </summary>
internal static class AdoptCommand
{
    public static async Task<int> RunAsync(AgentOptions options)
    {
        if (options.HostEndPoint is null)
        {
            Console.Error.WriteLine("Specify the host with --join <ip[:port]>.");
            return 1;
        }

        using ProcessMemory memory = CommandSupport.OpenTarget(options, writable: true);
        using Process game = Process.GetProcessById(memory.ProcessId);

        string? gameDirectory;
        try
        {
            gameDirectory = Path.GetDirectoryName(game.MainModule?.FileName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cannot resolve the game directory: {ex.Message}");
            return 1;
        }

        if (gameDirectory is null)
        {
            Console.Error.WriteLine("cannot resolve the game directory");
            return 1;
        }

        // Which of OUR saves the character comes from. The world is the host's; this save
        // contributes nothing else.
        int slot = options.LoadSave > 0 ? options.LoadSave - 1 : 0;
        if (!PluginMenuLoader.TryResolveSaveDirectory(gameDirectory, slot, out string ownSave, out string which))
        {
            Console.Error.WriteLine(which);
            return 1;
        }

        // Roadmap item 14 phase 1, "resume tomorrow": a character captured at the end of a
        // PREVIOUS session (ClientCharacterPersistence, written on a save-identity change or
        // a clean disconnect) beats this install's own save when it is present, valid, and
        // newer — see docs/research/session-save.md §2 and WorldAdoption.TryAdoptAsync's
        // sidecar overload for the exact preference rule.
        string savesDirectory = Path.Combine(gameDirectory, "saves");
        (GffStruct Player, SidecarMetadata Metadata)? sidecar = null;
        if (ClientCharacterPersistence.TryReadSidecar(
            savesDirectory, out GffStruct sidecarPlayer, out SidecarMetadata sidecarMeta, out string sidecarDiag))
        {
            sidecar = (sidecarPlayer, sidecarMeta);
        }
        else
        {
            Console.WriteLine($"session-saved character: {sidecarDiag}");
        }

        Console.WriteLine($"carrying the character from '{which}' unless a newer session-saved one applies");

        AdoptedWorld? adopted = await WorldAdoption.TryAdoptAsync(
            gameDirectory,
            ownSave,
            sidecar,
            options.HostEndPoint,
            SessionPassword.Hash(options.Password),
            expectedSaveIdentity: 0,
            Console.WriteLine,
            CancellationToken.None).ConfigureAwait(false);

        if (adopted is null)
        {
            return 1;
        }

        // ⚠ adoptedWorld: true is the whole point. It leaves DoingStartNewModule SET so the
        // login reads Temp\pifo.ifo; clearing it (what every other load does) would read the
        // staged world's own module.ifo, which holds the HOST's character.
        if (!PluginMenuLoader.TryRequestLoad(memory, out nint block, out string request, adoptedWorld: true))
        {
            Console.Error.WriteLine(request);
            return 1;
        }

        Console.WriteLine(request);

        GameLifecycle lifecycle = new(memory);
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        string pifoPath = Path.Combine(gameDirectory, "Temp", "pifo.ifo");
        int reasserted = 0;

        while (DateTime.UtcNow < deadline)
        {
            // ⚠ MEASURED, AND THE WHOLE REASON THIS LOOP IS NOT JUST A WAIT.
            // LoadPCAutoSave copies <export>\pifo over TEMP:pifo (CExoResMan::FileCopy at
            // 0x6CA634) and runs that copy even when the staged save contains no pifo — which
            // TRUNCATES ours to zero bytes. Live: the file was written, was 0 bytes by the time
            // LoadPCAutoSave returned, LoadCharacterFromIFO found nothing usable, and the
            // engine manufactured a blank creature (the faction-repair canary fired). The
            // login reads the file ~0.6 s later, so re-writing it here lands inside the window;
            // a non-empty file is left alone so we never race the read.
            try
            {
                FileInfo pifo = new(pifoPath);
                if (!pifo.Exists || pifo.Length == 0)
                {
                    // ⚠ Re-writes THE PLAYER THIS ADOPTION ACTUALLY CHOSE
                    // (adopted.Value.CarriedPlayer), never re-derived from ownSave. Re-deriving
                    // here would silently discard a session-saved-character preference on
                    // every truncation the loader causes — the exact bug a naive re-read of
                    // ownSave would reintroduce.
                    if (PlayerIfo.TryWritePifo(gameDirectory, adopted.Value.CarriedPlayer, out string reassert))
                    {
                        reasserted++;
                        Console.WriteLine($"re-wrote the carried character after the loader cleared it ({reassert})");
                    }
                }
            }
            catch (IOException)
            {
                // The engine has the file open; the next pass picks it up.
            }

            if (lifecycle.GetPhase() == GamePhase.InWorld)
            {
                // Which character actually came up. A missing pifo does NOT fail the load —
                // the engine's 0xF login branch ignores LoadCharacterFromIFO's return value —
                // it produces a blank level-1 stranger, so this is the field that says so.
                PluginMenuLoader.TryReadLoginFlavour(memory, block, out uint flavour);

                Console.WriteLine(flavour switch
                {
                    // ⚠ The flavour says which BRANCH the login took, not that the character
                    // loaded — a missing pifo produces a blank level-1 stranger and reports
                    // the same 2. The re-assert count is the honest companion signal, and the
                    // oracle is the PC itself.
                    2 => $"in world: '{adopted.Value.Module}', carrying our own character (TEMP:pifo)"
                        + (reasserted > 0 ? $"; pifo re-written {reasserted}x during the load" : string.Empty),
                    1 => $"⚠ in world: '{adopted.Value.Module}', but the login took the SAVED character "
                        + "(the host's) — the adopted-world flag did not reach the plugin",
                    _ => $"⚠ in world: '{adopted.Value.Module}', but the plugin reported no login flavour",
                });

                return flavour == 2 ? 0 : 1;
            }

            if (PluginMenuLoader.TryReadStatus(memory, block, out _, out int status) && status != 0)
            {
                Console.Error.WriteLine($"the plugin refused the load (failure {status})");
                return 1;
            }

            game.Refresh();
            if (game.HasExited)
            {
                Console.Error.WriteLine("the game exited while loading");
                return 1;
            }

            // Fast enough to land inside the ~0.6 s between the loader clearing the pifo and
            // the login reading it.
            Thread.Sleep(100);
        }

        Console.Error.WriteLine("the plugin accepted the load but no world appeared within 120s");
        return 1;
    }
}
