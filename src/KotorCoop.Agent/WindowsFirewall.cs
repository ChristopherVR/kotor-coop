using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// The one inbound Windows Firewall rule a HOST needs, and the installer's handling of it.
///
/// The agent starts windowless behind a fullscreen game, so the "allow this app through the
/// firewall?" prompt Windows raises on its first listen lands where nobody sees it — and an
/// unanswered prompt is a silent deny: the host's status line says everything is fine while no
/// joiner's datagram ever arrives (docs/review-seamless-2026-09-18.md §2). So the installer
/// adds the rule up front when it can (elevated), and otherwise prints the exact command to
/// run once as Administrator; <c>install --check</c> reports whether the rule exists;
/// <c>uninstall</c> removes it.
///
/// One installation-specific rule name, two <c>netsh</c> entries: UDP on the session port plus the
/// LAN browser's discovery port (<see cref="ServerDiscovery.DiscoveryPort"/>), and TCP on the
/// session port (the world-transfer listener shares the number — <c>WorldTransfer</c>'s own
/// doc). Both carry the same name; deletion also filters by program, including legacy rules,
/// so installing or removing one copy cannot delete another copy's rules. Program-scoped so a
/// player who later changes <c>port</c> only needs to re-run <c>install</c>, and so the rule
/// never opens the port for anything but this exe.
///
/// Every decision that does not need a process — the commands, the port lookup, the
/// exit-code reading — is a pure static so it is unit-tested; the process calls are confined
/// to <see cref="RunNetsh"/> and never run under tests (<see cref="PackageInstaller.FirewallEnabled"/>).
/// </summary>
public static class WindowsFirewall
{
    public const string RuleName = "KotorCoop Agent";
    public const string AgentExecutableName = "KotorCoop.Agent.exe";

    public static string AgentExecutable(string gameDirectory) => Path.Combine(gameDirectory, AgentExecutableName);

    /// <summary>Stable per-installation identity, independent of Windows path case.</summary>
    public static string RuleNameFor(string agentExePath)
    {
        string canonical = Path.GetFullPath(agentExePath).Replace('/', '\\').ToUpperInvariant();
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
        return $"{RuleName} {suffix}";
    }

    /// <summary>The two <c>netsh</c> commands that create the rule, as a player would type them.</summary>
    public static IReadOnlyList<string> AddCommands(string agentExePath, int port) =>
    [
        $"netsh advfirewall firewall add rule name=\"{RuleNameFor(agentExePath)}\" dir=in action=allow program=\"{agentExePath}\" "
        + $"protocol=UDP localport={port},{ServerDiscovery.DiscoveryPort} enable=yes profile=any",
        $"netsh advfirewall firewall add rule name=\"{RuleNameFor(agentExePath)}\" dir=in action=allow program=\"{agentExePath}\" "
        + $"protocol=TCP localport={port} enable=yes profile=any",
    ];

    public static IReadOnlyList<string> DeleteCommands(string agentExePath) =>
    [
        $"netsh advfirewall firewall delete rule name=\"{RuleNameFor(agentExePath)}\" dir=in program=\"{agentExePath}\"",
        // Upgrade/removal of the former shared name must also be limited to this executable.
        $"netsh advfirewall firewall delete rule name=\"{RuleName}\" dir=in program=\"{agentExePath}\"",
    ];

    public static string ShowCommand(string agentExePath) =>
        $"netsh advfirewall firewall show rule name=\"{RuleNameFor(agentExePath)}\"";

    /// <summary>
    /// The session port a <c>coop.ini</c> names (<c>port = N</c>, comments and blanks ignored),
    /// or <see cref="ProtocolConstants.DefaultPort"/> when the file is absent or has no usable
    /// line — the same fallback <c>CoopConfig</c> itself applies.
    /// </summary>
    public static int SessionPort(string? coopIniText)
    {
        if (coopIniText is null)
        {
            return ProtocolConstants.DefaultPort;
        }

        foreach (string rawLine in coopIniText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0 || !string.Equals(line[..equals].Trim(), "port", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = line[(equals + 1)..].Trim();
            int comment = value.IndexOfAny([';', '#']);
            if (comment >= 0)
            {
                value = value[..comment].Trim();
            }

            return int.TryParse(value, out int port) && port is > 0 and <= ushort.MaxValue
                ? port
                : ProtocolConstants.DefaultPort;
        }

        return ProtocolConstants.DefaultPort;
    }

    /// <summary><c>netsh advfirewall firewall show rule</c> exits 0 when at least one rule
    /// matched and 1 when none did ("No rules match the specified criteria.") — the exit code
    /// is read rather than the text because the text is localised.</summary>
    public static bool RuleListed(int showExitCode) => showExitCode == 0;

    /// <summary>The port the installer opens: the game directory's <c>coop.ini</c> if it has
    /// one by now (the installer writes it before this runs), else the code default.</summary>
    public static int SessionPortFor(string gameDirectory)
    {
        string ini = Path.Combine(gameDirectory, "coop.ini");
        try
        {
            return SessionPort(File.Exists(ini) ? File.ReadAllText(ini) : null);
        }
        catch (IOException)
        {
            return ProtocolConstants.DefaultPort;
        }
    }

    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Whether the rule exists right now, or null when <c>netsh</c> could not be
    /// asked (not Windows, Wine's stub, no netsh on PATH).</summary>
    public static bool? Exists(string agentExePath)
    {
        if (!OperatingSystem.IsWindows() || KotorCoop.Memory.WineDetection.IsWine)
        {
            return null;
        }

        int? exit = RunNetsh(ShowCommand(agentExePath)["netsh ".Length..]);
        return exit is null ? null : RuleListed(exit.Value);
    }

    /// <summary>
    /// The install-time step: adds the rule when elevated (deleting any previous copy first, so
    /// a re-run after a <c>port</c> change replaces rather than accumulates), otherwise prints
    /// the exact commands. Silent on non-Windows and under Wine — a Linux host opens the port in
    /// its distribution's own firewall (tools/install.sh's header and the package README say so).
    /// </summary>
    public static void Install(string gameDirectory, Action<string> log)
    {
        if (!OperatingSystem.IsWindows() || KotorCoop.Memory.WineDetection.IsWine)
        {
            return;
        }

        string exe = AgentExecutable(gameDirectory);
        int port = SessionPortFor(gameDirectory);
        IReadOnlyList<string> commands = AddCommands(exe, port);

        if (!IsElevated())
        {
            log($"Windows Firewall: not running as Administrator, so the inbound rule '{RuleName}' was not added.");
            log("  The agent runs behind the game, so Windows' own firewall prompt is easy to miss; to let");
            log("  friends reach a game you host, run these once from an Administrator prompt:");
            foreach (string command in commands)
            {
                log($"    {command}");
            }

            return;
        }

        foreach (string command in DeleteCommands(exe))
        {
            RunNetsh(command["netsh ".Length..]);
        }
        bool added = true;
        foreach (string command in commands)
        {
            int? exit = RunNetsh(command["netsh ".Length..]);
            added &= exit == 0;
        }

        log(added
            ? $"Windows Firewall: inbound rule '{RuleName}' added for {exe} (UDP {port},{ServerDiscovery.DiscoveryPort}; TCP {port})"
            : $"Windows Firewall: could not add the inbound rule '{RuleName}' - run this as Administrator: {commands[0]}");
    }

    /// <summary>The <c>install --check</c> step: a PASS line when the rule exists, a NOTE (not a
    /// FAIL — the install itself is fine, and a LAN-only host never needs it) with the exact
    /// commands when it does not, nothing when <c>netsh</c> cannot be asked.</summary>
    public static void Report(string gameDirectory, Action<string> log)
    {
        bool? exists = Exists(AgentExecutable(gameDirectory));
        if (exists is null)
        {
            return;
        }

        if (exists.Value)
        {
            log($"PASS: Windows Firewall inbound rule for {AgentExecutable(gameDirectory)} exists (port/profile settings not verified)");
            return;
        }

        int port = SessionPortFor(gameDirectory);
        log($"NOTE: no Windows Firewall inbound rule '{RuleName}' (UDP {port},{ServerDiscovery.DiscoveryPort} + TCP {port}) - "
            + "friends outside this PC may not reach a game you host; add it as Administrator with:");
        foreach (string command in AddCommands(AgentExecutable(gameDirectory), port))
        {
            log($"    {command}");
        }
    }

    /// <summary>The <c>uninstall</c> step: deletes the rule when elevated, prints the command
    /// otherwise; nothing to say when there is no rule.</summary>
    public static void Remove(string gameDirectory, Action<string> log)
    {
        if (!OperatingSystem.IsWindows() || KotorCoop.Memory.WineDetection.IsWine)
        {
            return;
        }

        string exe = AgentExecutable(gameDirectory);
        IReadOnlyList<string> commands = DeleteCommands(exe);
        if (!IsElevated())
        {
            log($"Windows Firewall: rules for {exe} left in place if present (not running as Administrator) - remove them with:");
            foreach (string command in commands)
            {
                log($"    {command}");
            }
            return;
        }

        bool removed = false;
        foreach (string command in commands)
        {
            removed |= RunNetsh(command["netsh ".Length..]) == 0;
        }
        log(removed
            ? $"removed Windows Firewall rules for {exe}"
            : $"Windows Firewall: no matching rule removed for {exe}; other installations were left alone");
    }

    /// <summary>Runs <c>netsh</c> with <paramref name="arguments"/> and returns its exit code,
    /// or null when it could not be started or did not finish within a few seconds.</summary>
    private static int? RunNetsh(string arguments)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return null;
            }

            // Drain both streams so a chatty netsh cannot block on a full pipe.
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(10_000))
            {
                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                }

                return null;
            }

            return process.ExitCode;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
