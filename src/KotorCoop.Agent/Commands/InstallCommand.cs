namespace KotorCoop.Agent;

/// <summary>
/// The `install`, `uninstall` and (no-argument) `setup` verbs, all over
/// <see cref="PackageInstaller"/>.
///
/// `setup` is what happens when a player double-clicks KotorCoop.Agent.exe in an extracted
/// package: find the game, install into it, offer to launch. Run from inside a game
/// directory that already has the mod, it just launches the game. Every prompt has a
/// sensible default and is skipped entirely when stdin is not a console, so scripts can
/// call it too.
/// </summary>
internal static class InstallCommand
{
    public static int Install(AgentOptions options)
    {
        string? game = ResolveGameDirectory(options, interactive: false);
        if (game is null)
        {
            return 1;
        }

        if (options.Check)
        {
            // Read-only report: nothing else below applies.
            return PackageInstaller.Check(game, Cli.Log) ? 0 : 1;
        }

        string package = options.PackageDirectory ?? AppContext.BaseDirectory;
        bool ok;
        try
        {
            ok = PackageInstaller.Install(new PackageInstaller.Options(package, game, options.Chain, options.ChainLoad, options.Force), Cli.Log);
        }
        catch (PackageInstaller.RefusedException ex)
        {
            Cli.Error(ex.Message.Replace("  chain ", "  --chain ").Replace("  force ", "  --force "));
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine("The game loads DirectInput from its own directory before the system one, so this is");
        Console.WriteLine("picked up on next launch. Nothing else was modified; remove it with: KotorCoop.Agent.exe uninstall");
        return ok ? 0 : 1;
    }

    public static int Uninstall(AgentOptions options)
    {
        string? game = ResolveGameDirectory(options, interactive: false);
        if (game is null)
        {
            return 1;
        }

        PackageInstaller.Uninstall(game, Cli.Log);
        return 0;
    }

    public static int Setup()
    {
        string here = AppContext.BaseDirectory.TrimEnd('\\');
        bool interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;

        Cli.Heading("kotor-coop setup");
        Console.WriteLine();

        try
        {
            if (PackageInstaller.IsGameDirectory(here))
            {
                // Extracted straight into the game folder, or already installed: nothing to
                // copy, just play.
                Console.WriteLine($"This is the game directory ({here}) and kotor-coop is in place.");
                return OfferLaunch(here, interactive);
            }

            if (!PackageInstaller.IsPackageDirectory(here))
            {
                Cli.Error($"{here} is neither an extracted kotor-coop package nor a game directory.");
                Cli.Error("Extract the whole zip somewhere and run KotorCoop.Agent.exe from there.");
                return Pause(interactive, 1);
            }

            string? game = ChooseGameDirectory(interactive);
            if (game is null)
            {
                return Pause(interactive, 1);
            }

            bool chain = false;
            bool force = false;
            string proxyPath = Path.Combine(game, PackageInstaller.PluginFileName);
            if (PackageInstaller.Identify(proxyPath) == PackageInstaller.DinputKind.Foreign)
            {
                ForeignDinputIdentity.Identity identity = PackageInstaller.IdentifyForeign(proxyPath);
                Console.WriteLine();
                if (identity.Kind == ForeignDinputIdentity.Kind.SystemFile)
                {
                    // Not a mod - nothing to ask about, PackageInstaller.Install below just
                    // replaces it and says so.
                    Console.WriteLine($"The dinput8.dll here is {identity.FriendlyName} - not a mod, so it will simply be replaced.");
                }
                else
                {
                    Console.WriteLine($"Another mod already uses dinput8.dll in that folder: {identity.FriendlyName}.");
                    chain = Ask("Keep it and load both? (the alternative overwrites it)", defaultYes: true, interactive);
                    force = !chain;
                }
            }

            Console.WriteLine();
            bool ok = PackageInstaller.Install(new PackageInstaller.Options(here, game, chain, null, force), Cli.Log);
            Console.WriteLine();
            if (ok)
            {
                Cli.Success("Installed. From now on just start the game: the Co-op button is on the main menu.");
            }
            else
            {
                Cli.Warn("Installed, but the check above found a problem (a FAIL line). Run");
                Cli.Warn("KotorCoop.Agent.exe install --check for the details, or try setup again.");
            }

            return OfferLaunch(game, interactive);
        }
        catch (Exception ex)
        {
            Cli.Error($"error: {ex.Message}");
            return Pause(interactive, 1);
        }
    }

    private static string? ChooseGameDirectory(bool interactive)
    {
        IReadOnlyList<PackageInstaller.Candidate> found = PackageInstaller.FindGameDirectories();
        List<PackageInstaller.Candidate> supported = found.Where(c => c.Supported).ToList();

        foreach (PackageInstaller.Candidate c in found)
        {
            Cli.Log($"  {(c.Supported ? "found" : "skipped")}: {c.Directory}   [{c.Source}]");
        }

        if (supported.Count == 1 && !interactive)
        {
            return supported[0].Directory;
        }

        if (!interactive)
        {
            Cli.Error(supported.Count == 0
                ? "No GOG KOTOR install found. Run: KotorCoop.Agent.exe install --game-path <folder with swkotor.exe>"
                : "More than one install found. Run: KotorCoop.Agent.exe install --game-path <one of the above>");
            return null;
        }

        Console.WriteLine();
        if (supported.Count > 0)
        {
            for (int i = 0; i < supported.Count; i++)
            {
                Cli.Choice(i + 1, supported[i].Directory);
            }

            Console.WriteLine();
            Cli.Prompt("Install into which? [1], a number, or type another folder: ");
        }
        else
        {
            Cli.Prompt("No GOG KOTOR install was found. Type the folder that contains swkotor.exe: ");
        }

        string answer = (Console.ReadLine() ?? string.Empty).Trim().Trim('"');
        if (answer.Length == 0 && supported.Count > 0)
        {
            return supported[0].Directory;
        }

        if (int.TryParse(answer, out int index) && index >= 1 && index <= supported.Count)
        {
            return supported[index - 1].Directory;
        }

        if (answer.Length > 0 && PackageInstaller.IsGameDirectory(answer))
        {
            return Path.GetFullPath(answer);
        }

        Cli.Error(answer.Length == 0 ? "Nothing chosen." : $"No swkotor.exe in {answer}.");
        return null;
    }

    private static int OfferLaunch(string game, bool interactive)
    {
        // A script that ran setup non-interactively gets an install, not a surprise game window.
        if (!Ask("Launch KOTOR now?", defaultYes: interactive, interactive))
        {
            return Pause(interactive, 0);
        }

        string exe = Path.Combine(game, PackageInstaller.GameExecutable);
        bool ok = GameLauncher.LaunchForSetup(exe, out string message);
        if (ok)
        {
            Cli.Success(message);
        }
        else
        {
            Cli.Error(message);
        }

        return ok ? 0 : 1;
    }

    private static bool Ask(string question, bool defaultYes, bool interactive)
    {
        if (!interactive)
        {
            return defaultYes;
        }

        Cli.Prompt($"{question} [{(defaultYes ? "Y/n" : "y/N")}] ");
        string answer = (Console.ReadLine() ?? string.Empty).Trim();
        return answer.Length == 0 ? defaultYes : answer.StartsWith('y') || answer.StartsWith('Y');
    }

    /// <summary>A double-clicked console app closes its window on exit; keep the text up.</summary>
    private static int Pause(bool interactive, int code)
    {
        if (interactive)
        {
            Console.WriteLine();
            Console.Write("Press Enter to close.");
            Console.ReadLine();
        }

        return code;
    }

    private static string? ResolveGameDirectory(AgentOptions options, bool interactive)
    {
        if (options.HasGamePath)
        {
            string path = options.GamePath;
            string? directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (directory is null || !Directory.Exists(directory))
            {
                Cli.Error($"game directory not found: {path}");
                return null;
            }

            return directory;
        }

        List<PackageInstaller.Candidate> supported = PackageInstaller.FindGameDirectories().Where(c => c.Supported).ToList();
        if (supported.Count == 1)
        {
            Console.WriteLine($"game directory: {supported[0].Directory} [{supported[0].Source}]");
            return supported[0].Directory;
        }

        Cli.Error(supported.Count == 0
            ? "no GOG KOTOR install found; pass --game-path <folder with swkotor.exe>"
            : "more than one KOTOR install found; pass --game-path:\n  " + string.Join("\n  ", supported.Select(c => c.Directory)));
        _ = interactive;
        return null;
    }
}
