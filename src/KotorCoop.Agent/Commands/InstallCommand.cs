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
        string? game = ResolveGameDirectory(options, interactive: false, options.Rescan);
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
        ReportVersion(package, game);

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
        if (ok)
        {
            OfferDefaults(package, game, interactive: false, options.UpdateDefaults);
        }
        return ok ? 0 : 1;
    }

    public static int Uninstall(AgentOptions options)
    {
        string? game = ResolveGameDirectory(options, interactive: false, options.Rescan);
        if (game is null)
        {
            return 1;
        }

        PackageInstaller.Uninstall(game, Cli.Log);
        return 0;
    }

    public static int Setup(AgentOptions options)
    {
        string here = AppContext.BaseDirectory.TrimEnd('\\');
        bool interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;

        Cli.Banner();
        Cli.Heading("kotor-coop setup");
        Console.WriteLine();

        try
        {
            if (PackageInstaller.IsGameDirectory(here))
            {
                // Extracted straight into the game folder, or already installed: verify
                // rather than assume, so a broken/partial install - most importantly a
                // foreign dinput8.dll still sitting in the slot - is reported instead of
                // silently offering a launch that will not actually be co-op.
                if (PackageInstaller.Check(here, Cli.Log))
                {
                    OfferDefaults(here, here, interactive, options.UpdateDefaults);
                    return OfferLaunch(here, interactive);
                }

                Console.WriteLine();
                Cli.Warn("kotor-coop is not fully installed here (see the FAIL line above).");
                if (PackageInstaller.Identify(Path.Combine(here, PackageInstaller.PluginFileName)) == PackageInstaller.DinputKind.Foreign)
                {
                    Cli.Warn("Another mod already owns dinput8.dll here. Self-repair from inside an already-");
                    Cli.Warn("extracted install can't chain it (there's no separate copy of our plugin left to");
                    Cli.Warn("install from) - extract a fresh copy of the kotor-coop zip into a DIFFERENT folder");
                    Cli.Warn("and run its KotorCoop.Agent.exe (or install.ps1) again, pointing it at:");
                    Cli.Warn($"  {here}");
                }

                return Pause(interactive, 1);
            }

            if (!PackageInstaller.IsPackageDirectory(here))
            {
                Cli.Error($"{here} is neither an extracted kotor-coop package nor a game directory.");
                Cli.Error("Extract the whole zip somewhere and run KotorCoop.Agent.exe from there.");
                return Pause(interactive, 1);
            }

            string? game = ChooseGameDirectory(interactive, options.Rescan);
            if (game is null)
            {
                return Pause(interactive, 1);
            }

            ReportVersion(here, game);

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
                return Pause(interactive, 1);
            }

            OfferDefaults(here, game, interactive, options.UpdateDefaults);
            return OfferLaunch(game, interactive);
        }
        catch (Exception ex)
        {
            Cli.Error($"error: {ex.Message}");
            return Pause(interactive, 1);
        }
    }

    /// <summary>
    /// Compares the version already installed in <paramref name="game"/> against the one
    /// this running package (<paramref name="package"/>) carries, and says so before any
    /// files are touched — the player asked "does it even check what's already there?", and
    /// silently overwriting an existing install (same version or not) without saying so is
    /// exactly what prompted that question.
    /// </summary>
    private static void ReportVersion(string package, string game)
    {
        string? installed = PackageInstaller.ReadVersion(game);
        if (installed is null)
        {
            // Nothing there yet (or it's not a KotorCoop.Agent.exe at all) - a fresh install,
            // nothing to compare against.
            return;
        }

        string? incoming = PackageInstaller.ReadVersion(package);
        if (incoming is not null && string.Equals(installed, incoming, StringComparison.OrdinalIgnoreCase))
        {
            Cli.Dim($"kotor-coop {installed} is already installed here and up to date.");
        }
        else
        {
            Cli.Heading($"kotor-coop {installed} is installed here; this package is "
                + $"{incoming ?? "an unversioned dev build"} - upgrading.");
        }
    }

    internal static string? ChooseGameDirectory(bool interactive, bool rescan, string action = "Install into")
    {
        // Cli.Spinner is a no-op flash when InstallScanCache already has the answer - it only
        // matters when there's no cache yet, or --rescan is forcing the full sweep back on.
        IReadOnlyList<PackageInstaller.Candidate> found =
            Cli.Spinner("scanning for a KOTOR install...", () => PackageInstaller.FindGameDirectories(rescan));
        List<PackageInstaller.Candidate> supported = found.Where(c => c.Supported).ToList();

        foreach (PackageInstaller.Candidate c in found)
        {
            string installed = c.InstalledVersion is null ? string.Empty : $" - kotor-coop {c.InstalledVersion} installed";
            Cli.Log($"  {(c.Supported ? "found" : "skipped")}: {c.Directory}   [{c.Source}]{installed}");
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
        if (supported.Count == 0)
        {
            Cli.FlushInput();
            Cli.Prompt("No GOG KOTOR install was found. Type the folder that contains swkotor.exe: ");
            string typed = (Console.ReadLine() ?? string.Empty).Trim().Trim('"');
            if (typed.Length > 0 && PackageInstaller.IsGameDirectory(typed))
            {
                return Path.GetFullPath(typed);
            }

            Cli.Error(typed.Length == 0 ? "Nothing chosen." : $"No swkotor.exe in {typed}.");
            return null;
        }

        for (int i = 0; i < supported.Count; i++)
        {
            Cli.Choice(i + 1, supported[i].Directory);
        }

        Console.WriteLine();

        // More than one candidate is exactly the case where silently defaulting on a bare
        // Enter is dangerous - it installs into whichever one happens to be listed first,
        // not necessarily the one the player meant, and there's no undo once files are
        // copied. A single candidate still accepts a bare Enter (matches the non-interactive
        // default a few lines up, which picks the lone candidate outright).
        //
        // The prompt (and any warning above it) is redrawn in the same spot on every retry -
        // mashing Enter through an ambiguous choice must not fill the screen with repeated
        // copies of the same question.
        var region = new Cli.RedrawRegion();
        string? warning = null;

        while (true)
        {
            Cli.FlushInput();
            region.Rewrite(() =>
            {
                if (warning is not null)
                {
                    Cli.Warn(warning);
                }

                Cli.Prompt(supported.Count == 1
                    ? $"{action} which? [1], a number, or type another folder: "
                    : $"{action} which? 1-{supported.Count}, or type another folder: ");
            });

            string answer = (Console.ReadLine() ?? string.Empty).Trim().Trim('"');

            if (answer.Length == 0)
            {
                if (supported.Count == 1)
                {
                    return supported[0].Directory;
                }

                warning = $"Type a number from 1 to {supported.Count}, or a folder path - "
                    + "pressing Enter alone doesn't pick one when more than one install was found.";
                continue;
            }

            if (int.TryParse(answer, out int index) && index >= 1 && index <= supported.Count)
            {
                return supported[index - 1].Directory;
            }

            if (PackageInstaller.IsGameDirectory(answer))
            {
                return Path.GetFullPath(answer);
            }

            warning = $"No swkotor.exe in {answer}, and not a valid number (1-{supported.Count}). Try again.";
        }
    }

    private static void OfferDefaults(string package, string game, bool interactive, bool update)
    {
        string template = Path.Combine(package, "KotorCoop.DEFAULTS.ini");
        if (!File.Exists(template))
        {
            template = Path.Combine(package, "coop.ini");
        }
        string player = Path.Combine(game, "coop.ini");
        if (Path.GetFullPath(template).Equals(Path.GetFullPath(player), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        IReadOnlyList<SettingsMigration.Change> changes = SettingsMigration.Find(player, template);
        if (changes.Count == 0)
        {
            return;
        }
        Cli.Warn("These settings match older defaults; they may also be deliberate custom choices:");
        foreach (SettingsMigration.Change change in changes)
        {
            Console.WriteLine($"  {change.Key}: {change.OldValue} -> {change.NewValue}");
        }
        if (update || Ask("Apply these defaults and back up coop.ini first?", false, interactive))
        {
            string backup = SettingsMigration.Apply(player, changes);
            Cli.Success($"Updated settings. Previous settings: {backup}");
        }
        else
        {
            Cli.Dim("Settings preserved. Compare KotorCoop.DEFAULTS.ini or use install --update-defaults.");
        }
    }

    private static int OfferLaunch(string game, bool interactive)
    {
        // Defaults to no either way: a non-interactive script gets an install, not a surprise
        // game window, and an interactive player has to type y - launching is the one action
        // in this whole flow that can't be undone by re-running setup, so a stray/impatient
        // Enter must never trigger it (unlike every other prompt here, where the safe default
        // is "do the obviously-intended thing").
        if (!Ask("Launch KOTOR now?", defaultYes: false, interactive))
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

        Cli.FlushInput();
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

    internal static string? ResolveGameDirectory(AgentOptions options, bool interactive, bool rescan)
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

        List<PackageInstaller.Candidate> supported = Cli
            .Spinner("scanning for a KOTOR install...", () => PackageInstaller.FindGameDirectories(rescan))
            .Where(c => c.Supported).ToList();
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
