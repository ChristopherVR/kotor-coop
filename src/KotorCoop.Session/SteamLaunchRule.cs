namespace KotorCoop.Session;

/// <summary>
/// Converts the retail game's startup arguments to one co-op agent startup action. Steam
/// appends the published connect value verbatim; explicit wrappers also support launchers.
/// Shared by source with the plugin so validation cannot drift from the agent's codec.
/// </summary>
public static class SteamLaunchRule
{
    public static string[] AgentArguments(IReadOnlyList<string> gameArguments, bool firstLaunch)
    {
        if (!firstLaunch)
        {
            return ["--menu"]; // Never replay an old invite after an agent crash/restart.
        }

        string? candidate = null;
        for (int i = 1; i < gameArguments.Count; i++) // argv[0] is the game executable.
        {
            string arg = gameArguments[i];
            if (arg is "+connect" or "--steam-join")
            {
                if (++i >= gameArguments.Count || candidate is not null)
                {
                    return ["--menu"];
                }
                candidate = gameArguments[i];
            }
            else if (arg.StartsWith(SteamConnectStringRule.Prefix, StringComparison.Ordinal))
            {
                if (candidate is not null)
                {
                    return ["--menu"];
                }
                candidate = arg;
            }
        }

        // No quotes/backslashes/control characters may reach the child's command line.
        // Endpoint/password validation is repeated by the agent before any network action.
        if (candidate is null || candidate.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)
                || c is '"' or '\\')
            || !SteamConnectStringRule.TryParse(candidate, out _, out _, out _))
        {
            return ["--menu"];
        }
        return ["--steam-join", candidate];
    }
}
