namespace KotorCoop.Agent;

/// <summary>
/// The CLI entry point: parse options, select the verb, run it. Every verb's actual work lives
/// in its own <c>Commands/&lt;Verb&gt;Command.cs</c> file (`run` additionally delegates its
/// session wiring to <see cref="AgentComposition"/>) — this file only routes.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        AgentOptions options = AgentOptions.Parse(args);

        // Launched by the plugin with its log path: write the log ourselves so it can rotate
        // mid-session (RotatingLogWriter). Falls back to the inherited stdout if it cannot open.
        if (!string.IsNullOrEmpty(options.ConsoleLog))
        {
            try
            {
                RotatingLogWriter log = new(options.ConsoleLog, options.ConsoleLogMb, options.ConsoleLogKeep);
                Console.SetOut(log);
                Console.SetError(log);
                RotatingLogWriter.ReleaseInheritedStdHandles();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"could not open the rotating log '{options.ConsoleLog}': {ex.Message}");
            }
        }

        // Linux/Proton: NEEDS-LIVE, no Wine box exists to confirm this against — see
        // KotorCoop.Memory.WineDetection's own doc comment for the detection method. Printed
        // once, before dispatch, so a Linux player sees it whichever verb they ran (including
        // the no-argument setup — see InstallCommand.Setup).
        if (KotorCoop.Memory.WineDetection.IsWine)
        {
            string? host = KotorCoop.Memory.WineDetection.HostDescription;
            Console.WriteLine(host is null
                ? $"running under Wine {KotorCoop.Memory.WineDetection.Version}"
                : $"running under Wine {KotorCoop.Memory.WineDetection.Version} ({host})");
        }

        try
        {
            return options.Command switch
            {
                AgentCommand.Launch => GameLauncher.Launch(options),
                AgentCommand.Load => LoadCommand.Run(options),
                AgentCommand.Profiles => ProfilesCommand.Run(),
                AgentCommand.InstallDataMods => InstallDataModsCommand.Run(options),
                AgentCommand.Info => InfoCommand.Run(options),
                AgentCommand.Mods => ModsCommand.Run(options),
                AgentCommand.Scan => ScanCommand.Run(options),
                AgentCommand.Track => TrackCommand.Run(options),
                AgentCommand.AutoTrack => AutoTrackCommand.Run(options),
                AgentCommand.ServeWorld => await ServeWorldCommand.RunAsync(options).ConfigureAwait(false),
                AgentCommand.Adopt => await AdoptCommand.RunAsync(options).ConfigureAwait(false),
                AgentCommand.CraftSave => CraftSaveCommand.Run(options),
                AgentCommand.Run => await RunCommand.RunAsync(options).ConfigureAwait(false),
                AgentCommand.Install => InstallCommand.Install(options),
                AgentCommand.Uninstall => InstallCommand.Uninstall(options),
                AgentCommand.Firewall => InstallCommand.Firewall(options),
                AgentCommand.Logs => LogsCommand.Run(options),
                AgentCommand.Support => LogsCommand.RunInteractive(options),
                AgentCommand.Setup => InstallCommand.Setup(options),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine(AgentOptions.HelpText);
        return 0;
    }
}
