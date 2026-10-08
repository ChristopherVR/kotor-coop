using System.ComponentModel;
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

    /// <summary>
    /// Whether a running game is this install's. Only a copy of the SAME executable means there is
    /// nothing to launch: a second install running alongside (a renamed-mutex instance 2, a tester's
    /// other copy) left a crashed host unable to restart while its joiner was still up. A process
    /// whose path cannot be read counts as this install, which keeps the old, cautious answer.
    /// </summary>
    private static bool IsThisInstall(Process process, string gamePath)
    {
        try
        {
            string? running = process.MainModule?.FileName;
            return running is null
                || string.Equals(Path.GetFullPath(running), Path.GetFullPath(gamePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return true;
        }
    }

    public static int Launch(AgentOptions options)
    {
        string processName = Path.GetFileNameWithoutExtension(options.GamePath);

        if (options.Instance > 1)
        {
            return LaunchAdditionalInstance(options);
        }

        if (Process.GetProcessesByName(processName).Any(p => options.ViaSteam || IsThisInstall(p, options.GamePath)))
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

        Console.WriteLine($"launching instance {options.Instance}: {options.GamePath}");

        if (!TryStartAsInstance(options.GamePath, options.Instance, out Process? game, out string diagnostic))
        {
            Console.Error.WriteLine(diagnostic);
            return 1;
        }

        Console.WriteLine(diagnostic);

        int result = AwaitAutoLoad(options, game!);
        game!.Dispose();
        return result;
    }

    /// <summary>
    /// Launches the game for the interactive setup flow (<see cref="InstallCommand.Setup"/>),
    /// which never asks the player about instance numbers up front. If another copy of the
    /// same executable is already running - typically the first player's game, still up while
    /// the second player runs setup against their own copy - this becomes an extra instance
    /// (its own mutex name, see <see cref="InstanceLauncher"/>) automatically instead of trying
    /// a plain launch that the retail single-instance guard would silently kill.
    /// </summary>
    public static bool LaunchForSetup(string gamePath, out string message)
    {
        string processName = Path.GetFileNameWithoutExtension(gamePath);
        int running = Process.GetProcessesByName(processName).Length;

        if (running == 0)
        {
            try
            {
                Process.Start(new ProcessStartInfo(gamePath)
                {
                    WorkingDirectory = Path.GetDirectoryName(gamePath),
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                message = $"launch failed: {ex.Message}";
                return false;
            }

            message = $"started {gamePath}";
            return true;
        }

        // AgentOptions clamps --instance the same way; "kcoop9" is the last name that still
        // fits the 7-byte "swkotor" slot InstanceLauncher patches in place.
        int instance = Math.Min(running + 1, 9);
        bool ok = TryStartAsInstance(gamePath, instance, out Process? game, out string diagnostic);

        if (!ok)
        {
            message = $"another copy of {processName} is already running, so this one was started "
                + $"as a separate instance - which failed: {diagnostic} This only works against the "
                + "DRM-free (GOG) build; a Steam-wrapped copy blocks a second instance on its own.";
            return false;
        }

        message = $"another copy of {processName} was already running, so this one was started as "
            + $"instance {instance} (pid {game!.Id}) alongside it.";
        game.Dispose();
        return true;
    }

    /// <summary>
    /// Starts <paramref name="gamePath"/> as instance <paramref name="instance"/> (2+; instance
    /// 1 is always the plain launch above and never comes through here) via
    /// <see cref="InstanceLauncher"/> for a private mutex name, and waits 3s to catch the guard
    /// still firing, which shows up as an immediate exit rather than an error return - worth
    /// reporting plainly instead of leaving a silent dead window.
    /// </summary>
    private static bool TryStartAsInstance(string gamePath, int instance, out Process? game, out string diagnostic)
    {
        // "swkotor" is 7 characters and the replacement is written in place, so the name has
        // to stay within that. "kcoopN" is unique per instance and safely shorter.
        game = InstanceLauncher.Launch(gamePath, $"kcoop{instance}", out diagnostic);

        if (game is null)
        {
            return false;
        }

        Thread.Sleep(3000);
        game.Refresh();

        if (game.HasExited)
        {
            // InstanceLauncher uses native CreateProcess then GetProcessById. After an
            // early exit this observer may know HasExited without retaining a handle
            // from which ExitCode can be recovered. A missing code must not hide the
            // actual launch failure behind "Process was not started by this object".
            diagnostic = $"instance {instance} exited immediately; mutex 'kcoop{instance}' may already be in use.";
            try
            {
                diagnostic = $"instance {instance} exited immediately with code {game.ExitCode}; mutex 'kcoop{instance}' may already be in use.";
            }
            catch (InvalidOperationException)
            {
                // The process observer no longer has an exit-code handle.
            }
            catch (Win32Exception)
            {
                // Its exit code is inaccessible; the early-exit verdict still holds.
            }
            game.Dispose();
            game = null;
            return false;
        }

        diagnostic = $"instance {instance} is running as pid {game.Id}.";
        return true;
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
            // Writable: the plugin-driven load route writes a request into the shared block.
            using ProcessMemory memory = ProcessMemory.Attach(game.Id, writable: true);

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
