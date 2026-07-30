using System.Diagnostics;
using KotorCoop.Memory;

namespace KotorCoop.Agent;

/// <summary>
/// Starts the game and waits for it. Nothing here injects or modifies the executable:
/// the agent is entirely external, so launching is just Process.Start.
/// </summary>
public static class GameLauncher
{
    private const string BackupSuffix = ".kotorcoop-backup";

    public static int Launch(AgentOptions options)
    {
        string processName = Path.GetFileNameWithoutExtension(options.GamePath);

        if (options.Instance > 1)
        {
            return LaunchAdditionalInstance(options);
        }

        if (Process.GetProcessesByName(processName).Length > 0)
        {
            Console.WriteLine($"{processName} is already running; nothing to launch.");
            return 0;
        }

        if (!options.ViaSteam && !File.Exists(options.GamePath))
        {
            Console.Error.WriteLine($"Game executable not found: {options.GamePath}");
            Console.Error.WriteLine("Pass --game-path <path to swkotor.exe>.");
            return 1;
        }

        if (options.Windowed)
        {
            string directory = Path.GetDirectoryName(options.GamePath) ?? ".";
            if (!TrySetWindowed(directory))
            {
                Console.Error.WriteLine("Could not switch the game to windowed mode; launching anyway.");
            }
        }

        try
        {
            if (options.ViaSteam)
            {
                Console.WriteLine($"launching via steam://rungameid/{AgentOptions.SteamAppId} ...");
                Process.Start(new ProcessStartInfo($"steam://rungameid/{AgentOptions.SteamAppId}")
                {
                    UseShellExecute = true,
                });
            }
            else
            {
                Console.WriteLine($"launching {options.GamePath} ...");

                // The working directory matters: KOTOR resolves every path in its [Alias]
                // section relative to the executable's directory.
                Process.Start(new ProcessStartInfo(options.GamePath)
                {
                    WorkingDirectory = Path.GetDirectoryName(options.GamePath),
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"launch failed: {ex.Message}");
            Console.Error.WriteLine("If the game is Steam-wrapped, make sure Steam is running, or try --via-steam.");
            return 1;
        }

        Process? game = WaitForProcess(processName, TimeSpan.FromSeconds(90));

        if (game is null)
        {
            Console.Error.WriteLine($"{processName} did not appear within 90s.");
            return 1;
        }

        Console.WriteLine($"{processName} is running as pid {game.Id}.");

        if (options.LoadSave > 0)
        {
            int loadResult = AwaitAutoLoad(options, game);
            game.Dispose();
            return loadResult;
        }

        Console.WriteLine();
        Console.WriteLine("Next: load a save and take control of your character, then run");
        Console.WriteLine($"  dotnet run --project src/KotorCoop.Agent -- track --process {processName}");
        game.Dispose();
        return 0;
    }

    /// <summary>
    /// Starts an extra copy of the game alongside one that is already running, by giving
    /// it its own single-instance mutex name. Unlike instance 1 this deliberately does not
    /// bail out when the process is already present - that is the entire point.
    /// </summary>
    private static int LaunchAdditionalInstance(AgentOptions options)
    {
        if (options.Windowed)
        {
            string directory = Path.GetDirectoryName(options.GamePath) ?? ".";
            if (!TrySetWindowed(directory))
            {
                Console.Error.WriteLine("Could not switch the game to windowed mode; launching anyway.");
            }
        }

        // "swkotor" is 7 characters and the replacement is written in place, so the name
        // has to stay within that. "kcoopN" is unique per instance and safely shorter.
        string mutexName = $"kcoop{options.Instance}";

        Console.WriteLine($"launching instance {options.Instance}: {options.GamePath}");
        Process? game = InstanceLauncher.Launch(options.GamePath, mutexName, out string diagnostic);

        if (game is null)
        {
            Console.Error.WriteLine($"launch failed: {diagnostic}");
            return 1;
        }

        Console.WriteLine(diagnostic);

        // A guard that still fires shows up as an immediate exit -1, which is worth
        // reporting plainly rather than leaving the caller to wonder where the window is.
        Thread.Sleep(3000);
        game.Refresh();

        if (game.HasExited)
        {
            Console.Error.WriteLine(
                $"instance {options.Instance} exited immediately with code {game.ExitCode}.");
            game.Dispose();
            return 1;
        }

        Console.WriteLine($"instance {options.Instance} is running as pid {game.Id}.");

        int result = AwaitAutoLoad(options, game);
        game.Dispose();
        return result;
    }

    /// <summary>
    /// Takes the freshly launched game into a save, if one was asked for. Failing is
    /// reported but not fatal: the game is up, and a human can still load by hand.
    /// </summary>
    private static int AwaitAutoLoad(AgentOptions options, Process game)
    {
        if (options.LoadSave <= 0)
        {
            return 0;
        }

        Console.WriteLine($"loading save slot {options.LoadSave} ...");

        try
        {
            using ProcessMemory memory = ProcessMemory.Attach(game.Id, writable: false);

            bool loaded = SaveLoader.TryLoadSave(
                game, memory, options.LoadSave - 1, TimeSpan.FromSeconds(180), out string diagnostic);

            Console.WriteLine(diagnostic);
            return loaded ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"auto-load failed: {ex.Message}");
            return 1;
        }
    }

    public static Process? WaitForProcess(string processName, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            Process[] matches = Process.GetProcessesByName(processName);

            if (matches.Length > 0)
            {
                for (int i = 1; i < matches.Length; i++)
                {
                    matches[i].Dispose();
                }

                return matches[0];
            }

            Thread.Sleep(500);
        }

        return null;
    }

    /// <summary>
    /// Sets FullScreen=0 in swkotor.ini, keeping a one-time backup of the original.
    /// Watching a console readout while the game holds an exclusive fullscreen surface
    /// is not workable, so this is close to required for testing.
    /// </summary>
    public static bool TrySetWindowed(string gameDirectory)
    {
        string iniPath = Path.Combine(gameDirectory, "swkotor.ini");

        if (!File.Exists(iniPath))
        {
            Console.Error.WriteLine($"swkotor.ini not found at {iniPath}.");
            return false;
        }

        try
        {
            string backupPath = iniPath + BackupSuffix;
            if (!File.Exists(backupPath))
            {
                File.Copy(iniPath, backupPath);
                Console.WriteLine($"backed up swkotor.ini to {Path.GetFileName(backupPath)}");
            }

            List<string> lines = [.. File.ReadAllLines(iniPath)];
            bool changed = false;

            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("FullScreen=", StringComparison.OrdinalIgnoreCase)
                    && lines[i].Trim() != "FullScreen=0")
                {
                    lines[i] = "FullScreen=0";
                    changed = true;
                }
            }

            // FullScreen=0 alone is silently ignored by KOTOR 1: without AllowWindowedMode
            // the game still takes an exclusive fullscreen display mode. That is not a
            // cosmetic difference. In exclusive fullscreen, GDI screen capture returns a
            // stale frame that never updates, and DirectInput running at an exclusive
            // cooperative level does not observe injected input, so neither screenshots
            // nor automation work at all.
            bool hasAllowWindowed = lines.Any(l =>
                l.StartsWith("AllowWindowedMode=", StringComparison.OrdinalIgnoreCase));

            if (!hasAllowWindowed)
            {
                int graphicsSection = lines.FindIndex(l =>
                    l.Trim().Equals("[Graphics Options]", StringComparison.OrdinalIgnoreCase));

                if (graphicsSection >= 0)
                {
                    lines.Insert(graphicsSection + 1, "AllowWindowedMode=1");
                }
                else
                {
                    lines.Add(string.Empty);
                    lines.Add("[Graphics Options]");
                    lines.Add("AllowWindowedMode=1");
                }

                changed = true;
                Console.WriteLine("added AllowWindowedMode=1 to swkotor.ini");
            }
            else
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].StartsWith("AllowWindowedMode=", StringComparison.OrdinalIgnoreCase)
                        && lines[i].Trim() != "AllowWindowedMode=1")
                    {
                        lines[i] = "AllowWindowedMode=1";
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                File.WriteAllLines(iniPath, lines);
                Console.WriteLine("swkotor.ini set to windowed (FullScreen=0, AllowWindowedMode=1)");
            }
            else
            {
                Console.WriteLine("swkotor.ini is already windowed");
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not edit swkotor.ini: {ex.Message}");
            return false;
        }
    }

    public static void RestoreIni(string gameDirectory)
    {
        string iniPath = Path.Combine(gameDirectory, "swkotor.ini");
        string backupPath = iniPath + BackupSuffix;

        if (File.Exists(backupPath))
        {
            File.Copy(backupPath, iniPath, overwrite: true);
            Console.WriteLine("restored swkotor.ini from backup");
        }
    }
}
