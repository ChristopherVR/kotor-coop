using KotorCoop.Memory.GameFiles;

namespace KotorCoop.Agent;

/// <summary>
/// The `agent install-data-mods` verb: runs the no-RE 2DA merge (repute.2da coop_player
/// faction, appearance.2da coop_p2 body) against the install named by --game-path, which may
/// be the game directory or the exe inside it — the same flag launch already takes. Explicitly
/// on demand only: nothing else calls the installer, and the plugin does not use the new
/// faction row yet (that switch is gated on a live pass).
/// </summary>
internal static class InstallDataModsCommand
{
    public static int Run(AgentOptions options)
    {
        string path = options.GamePath;
        string? gameDirectory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);

        if (gameDirectory is null || !Directory.Exists(gameDirectory))
        {
            Console.Error.WriteLine($"game directory not found: {path} (use --game-path <dir or exe>)");
            return 1;
        }

        DataModInstallResult result = DataModInstaller.Install(gameDirectory);

        Console.WriteLine($"override:        {result.OverrideDirectory}");
        Console.WriteLine(
            $"repute.2da:      '{DataModInstaller.FactionLabel}' faction is row {result.FactionRowIndex}"
            + (result.ReputeWritten ? " (merged and written)" : " (already present, unchanged)"));
        Console.WriteLine(
            $"appearance.2da:  '{DataModInstaller.AppearanceLabel}' body is row {result.AppearanceRowIndex}"
            + (result.AppearanceWritten ? " (merged and written)" : " (already present, unchanged)"));
        return 0;
    }
}
