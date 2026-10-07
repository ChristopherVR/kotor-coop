using System.ComponentModel;
using System.Diagnostics;

namespace KotorCoop.Agent;

/// <summary>
/// Restarting this agent as Administrator through the normal Windows UAC prompt — for a game
/// folder Windows protects (Program Files) and for the one-off firewall rule. Only ever the
/// agent's own exe with arguments it was given or a fixed verb; nothing is built from user text.
/// </summary>
internal static class Elevation
{
    internal enum Outcome
    {
        Succeeded,
        Failed,
        /// <summary>The player pressed No on the UAC prompt.</summary>
        Declined,
    }

    /// <summary>Runs this agent again, elevated, with <paramref name="arguments"/>. When
    /// <paramref name="wait"/> the call returns after it exits, with its exit code as the outcome.</summary>
    public static Outcome Run(IReadOnlyList<string> arguments, bool wait, bool hidden)
    {
        string? self = Environment.ProcessPath;
        if (self is null || !OperatingSystem.IsWindows())
        {
            return Outcome.Failed;
        }

        var start = new ProcessStartInfo(self)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
        };

        // `dotnet KotorCoop.Agent.dll ...` (a dev run): the entry assembly has to come along.
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        }

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(start);
            if (process is null)
            {
                return Outcome.Failed;
            }

            if (!wait)
            {
                return Outcome.Succeeded;
            }

            return process.WaitForExit(120_000) && process.ExitCode == 0 ? Outcome.Succeeded : Outcome.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return Outcome.Declined;
        }
        catch (Exception)
        {
            return Outcome.Failed;
        }
    }

    /// <summary>Restarts the current command line (same arguments) elevated, in its own window.</summary>
    public static Outcome RelaunchSelf() =>
        Run(Environment.GetCommandLineArgs().Skip(1).ToArray(), wait: false, hidden: false);
}
