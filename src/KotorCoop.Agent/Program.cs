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

        try
        {
            return options.Command switch
            {
                AgentCommand.Launch => GameLauncher.Launch(options),
                AgentCommand.Load => LoadCommand.Run(options),
                AgentCommand.Profiles => ProfilesCommand.Run(),
                AgentCommand.InstallDataMods => InstallDataModsCommand.Run(options),
                AgentCommand.Info => InfoCommand.Run(options),
                AgentCommand.Scan => ScanCommand.Run(options),
                AgentCommand.Track => TrackCommand.Run(options),
                AgentCommand.AutoTrack => AutoTrackCommand.Run(options),
                AgentCommand.ServeWorld => await ServeWorldCommand.RunAsync(options).ConfigureAwait(false),
                AgentCommand.Adopt => await AdoptCommand.RunAsync(options).ConfigureAwait(false),
                AgentCommand.CraftSave => CraftSaveCommand.Run(options),
                AgentCommand.Run => await RunCommand.RunAsync(options).ConfigureAwait(false),
                AgentCommand.Install => InstallCommand.Install(options),
                AgentCommand.Uninstall => InstallCommand.Uninstall(options),
                AgentCommand.Setup => InstallCommand.Setup(),
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
