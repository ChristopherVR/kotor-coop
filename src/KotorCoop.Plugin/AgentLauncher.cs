using System.Runtime.InteropServices;
using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Starts the co-op agent for this game instance, so installing the mod is the whole
/// installation.
///
/// Without this the player has to open a terminal and run the agent by hand with the right
/// process id — fine for development, wrong for a mod. The plugin already knows the one
/// thing that was awkward to supply (its own pid), so it launches
/// <c>KotorCoop.Agent.exe</c> itself in menu mode: the agent attaches, discovers the LAN,
/// feeds the in-game server browser, and waits for the player to pick a server.
///
/// Deliberately conservative:
///   - opt-out through <c>coop.ini</c> (<c>auto_start_agent = 0</c>), and skipped entirely
///     when no agent executable is found beside the game;
///   - launched automatically at most once per game process, detached, so nothing here
///     can block or outlive the frame loop — but if THAT launch's agent later dies
///     (heartbeat gone stale; see <see cref="AgentLiveness"/>), <see cref="TryRelaunch"/>
///     will bring it back, at most once a minute, and never touches an agent it did not
///     start itself;
///   - a failure is logged AND surfaced on screen (<see cref="StartupFailure"/>, read by
///     <see cref="AgentLivenessController"/>) — the game must still start normally with or
///     without the agent, and a player who prefers to run it by hand still can (an
///     already-running agent simply takes the session and this one exits on the duplicate
///     attach).
/// </summary>
internal static unsafe class AgentLauncher
{
    /// <summary>Names tried in the game directory, then in a <c>kotorcoop</c> subfolder.</summary>
    private static readonly string[] CandidateNames = ["KotorCoop.Agent.exe"];

    private static readonly string[] CandidateSubdirectories = ["", "kotorcoop", "coop"];

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Cb;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow;
        public short Reserved2Length;
        public nint Reserved2;
        public nint StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    /// <summary>
    /// No console at all. A console window — even one asked to open minimised and inactive —
    /// takes the foreground away from the game as it appears, and KOTOR discards every mouse
    /// message while it is not the active window, so the first clicks after launch are eaten.
    /// That was measured: the click-based save loader started failing the moment the plugin
    /// began launching the agent itself. The agent's output goes to a log beside the game
    /// instead, and the player's real status readout is the in-game overlay and ~ console.
    /// </summary>
    private const uint CreateNoWindow = 0x08000000;

    /// <summary>
    /// Born suspended so <see cref="AgentJob"/> can put it in the game-lifetime job before
    /// its first instruction; <see cref="ResumeThread"/> then lets it go.
    /// </summary>
    private const uint CreateSuspended = 0x00000004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint thread);

    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWriteDelete = 0x00000007; // DELETE share lets the agent rotate the file it also holds
    private const uint CreateAlways = 2;
    private const uint FileAttributeNormal = 0x00000080;
    private const int UseStdHandles = 0x00000100;
    private static readonly nint InvalidHandle = -1;

    private static bool _attempted;

    /// <summary>
    /// Non-null only once this launcher has actually stood up a process (a real
    /// CreateProcess success), and then it is the directory that succeeded — never merely
    /// "attempted". This is both the relaunch target and the "did I start this agent, or is
    /// the player running it by hand" flag <see cref="TryRelaunch"/> checks: an agent
    /// started outside this launcher (or one whose auto-start is opted out in coop.ini) is
    /// never taken over.
    /// </summary>
    private static string? _startedGameDirectory;

    private static long _lastRelaunchAttemptMs;

    /// <summary>
    /// One line for <see cref="AgentLivenessController"/> to put on screen when the agent
    /// never started at all — set by the two failure branches below, cleared the moment a
    /// launch (first or relaunch) actually succeeds so a stale message cannot outlive the
    /// problem it described.
    /// </summary>
    public static string? StartupFailure { get; private set; }

    /// <summary>
    /// Launches the agent for this process, once. Safe to call from plugin startup on the
    /// game thread: CreateProcess returns immediately and both handles are closed. A later
    /// death (heartbeat gone stale past <see cref="AgentLiveness.DeadAfterMs"/>) is handled
    /// separately by <see cref="TryRelaunch"/>, driven every frame from
    /// <see cref="AgentLivenessController"/>.
    /// </summary>
    public static void EnsureRunning(string gameDirectory)
    {
        if (_attempted)
        {
            return;
        }

        _attempted = true;

        if (!WantsAutoStart(gameDirectory))
        {
            PluginHost.Log("agent: auto-start disabled in coop.ini");
            return;
        }

        Launch(gameDirectory);
    }

    /// <summary>
    /// Called every frame once <see cref="AgentLiveness"/> judges the agent Dead. Relaunches
    /// at most once per <see cref="AgentLiveness.RelaunchCooldownMs"/>, and only when THIS
    /// launcher was the one that started the process being watched — logs each attempt it
    /// actually makes (silent on every other frame, which is most of them).
    /// </summary>
    public static void TryRelaunch(long lastHeartbeatMs, long nowMs)
    {
        if (_startedGameDirectory is not { } gameDirectory)
        {
            return;
        }

        if (!AgentLiveness.ShouldRelaunch(lastHeartbeatMs, nowMs, startedByLauncher: true, _lastRelaunchAttemptMs))
        {
            return;
        }

        _lastRelaunchAttemptMs = nowMs;
        PluginHost.Log(
            $"agent: heartbeat stale for {AgentLiveness.DeadAfterMs / 1000}s+; attempting a relaunch");
        Launch(gameDirectory);
    }

    /// <summary>
    /// Does the actual CreateProcess work — shared by the first automatic start and every
    /// later relaunch attempt, so the two can never drift apart in behaviour.
    /// </summary>
    private static void Launch(string gameDirectory)
    {
        try
        {
            string? agentPath = FindAgent(gameDirectory);
            if (agentPath is null)
            {
                const string reason = "no KotorCoop.Agent.exe beside the game; start it yourself";
                StartupFailure = reason;
                PluginHost.Log($"agent: {reason}");
                return;
            }

            uint pid = NativeMethods.GetCurrentProcessId();
            string name = ReadSetting(gameDirectory, "name") ?? string.Empty;
            string nameArgument = name.Length > 0 ? $" --name \"{name}\"" : string.Empty;

            // --menu: attach and wait at the main menu, feeding the server browser, rather
            // than connecting immediately. The player chooses in-game.
            StringBuilder commandLine = new($"\"{agentPath}\" run --pid {pid} --kotor --menu{nameArgument}");

            // The agent's dashboard writes to stdout; send it to a log beside the game. With
            // stdout redirected the dashboard takes its "redirected" path, so it neither
            // queries the console width nor polls for keys — both of which would throw
            // without a console.
            string logPath = Path.Combine(gameDirectory, "kotorcoop-agent.log");
            RotateAgentLogIfNeeded(gameDirectory, logPath);
            commandLine.Append($" --console-log \"{logPath}\"")
                .Append(" --console-log-mb ").Append(ReadLogCapMb(gameDirectory))
                .Append(" --console-log-keep ").Append(ReadLogKeep(gameDirectory));

            SecurityAttributes inheritable = new()
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = 0,
                InheritHandle = 1,
            };

            nint log = CreateFileW(
                logPath, GenericWrite, FileShareReadWriteDelete, ref inheritable,
                CreateAlways, FileAttributeNormal, 0);

            StartupInfo startup = default;
            startup.Cb = sizeof(StartupInfo);

            // kotorcoop-agent.log is capped mid-session by the AGENT, not by us: this handle is a
            // raw inherited OS handle that follows the file wherever it is renamed, so only the
            // writing process can close it, shift the generations and reopen the path. We hand
            // it --console-log/-mb/-keep below; the agent then routes Console.Out/Error through
            // RotatingLogWriter (same cap/generations as the plugin log). This handle stays as
            // the fallback for the agent's first moments and for an agent that cannot open the
            // file; it is opened with FILE_SHARE_DELETE so the agent's rename is never blocked.
            // RotateLogFilesOverCap still skips this file by name (the agent owns it).
            bool haveLog = log != InvalidHandle && log != 0;
            if (haveLog)
            {
                startup.Flags = UseStdHandles;
                startup.StdInput = 0;
                startup.StdOutput = log;
                startup.StdError = log;
            }

            bool started = CreateProcessW(
                null,
                commandLine,
                0,
                0,
                haveLog,           // inherit handles only when there is one to inherit
                CreateNoWindow | CreateSuspended,
                0,
                gameDirectory,
                ref startup,
                out ProcessInformation info);

            if (haveLog)
            {
                // The child holds its own duplicate; this one has done its job.
                CloseHandle(log);
            }

            if (!started)
            {
                string reason = $"could not start the co-op agent (error {Marshal.GetLastWin32Error()})";
                StartupFailure = reason;
                PluginHost.Log($"agent: {reason}: {agentPath}");
                return;
            }

            // The agent attaches by pid and exits on its own when the game does; the job is
            // the backstop that makes that true even when it cannot (see AgentJob). Assign
            // while still suspended, then let it run. Nothing else here owns it: both
            // handles are closed so the plugin holds no reference to the process.
            bool joined = AgentJob.TryAssign(info.Process);
            ResumeThread(info.Thread);
            CloseHandle(info.Thread);
            CloseHandle(info.Process);
            _startedGameDirectory = gameDirectory;
            StartupFailure = null;
            PluginHost.Log(
                $"agent: started {Path.GetFileName(agentPath)} as pid {info.ProcessId}"
                + (joined ? " (tied to the game's lifetime)" : string.Empty)
                + $"; log -> {Path.GetFileName(logPath)}");
        }
        catch (Exception ex)
        {
            StartupFailure = $"auto-start failed: {ex.Message}";
            PluginHost.Log($"agent: {StartupFailure}");
        }
    }

    /// <summary>
    /// Rotates the agent's log the same way PluginHost rotates its own (see
    /// PluginHost.RotateLogsIfNeeded and LogRotation): if the previous session's file is over
    /// coop.ini's <c>log_max_mb</c>, its generations shift up (see
    /// <see cref="LogRotation.ShiftPlan"/>) through coop.ini's <c>log_keep</c>, so that many
    /// prior sessions' tails survive instead of being silently discarded down to one.
    ///
    /// CreateFileW below always opens with CREATE_ALWAYS, so without this a launch that
    /// follows a very long agent session (the dashboard runs for hours, logging the whole
    /// time) would simply overwrite it and lose it; this is the same cap applied before that
    /// overwrite happens rather than after. A single session that grows past the cap while
    /// running is capped by the agent itself (RotatingLogWriter, started via --console-log).
    /// </summary>
    private static void RotateAgentLogIfNeeded(string gameDirectory, string logPath)
    {
        try
        {
            if (!File.Exists(logPath))
            {
                return;
            }

            int capMb = ReadLogCapMb(gameDirectory);
            int keepGenerations = ReadLogKeep(gameDirectory);

            FileInfo info = new(logPath);
            if (LogRotation.ShouldRotate(info.Length, LogRotation.CapBytes(capMb)))
            {
                LogRotation.ApplyShiftPlan(
                    LogRotation.ShiftPlan(logPath, LogRotation.ClampKeepGenerations(keepGenerations)));
            }
        }
        catch
        {
            // Best-effort: a failed rotation here must not stop the agent from launching.
        }
    }

    private static string? FindAgent(string gameDirectory)
    {
        foreach (string subdirectory in CandidateSubdirectories)
        {
            foreach (string name in CandidateNames)
            {
                string path = subdirectory.Length == 0
                    ? Path.Combine(gameDirectory, name)
                    : Path.Combine(gameDirectory, subdirectory, name);

                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>Default ON: the point of the feature is that installing the plugin is
    /// enough. Only an explicit falsy value in coop.ini turns it off.</summary>
    private static bool WantsAutoStart(string gameDirectory)
    {
        string? value = ReadSetting(gameDirectory, "auto_start_agent");
        return value is null
            || !(value == "0"
                || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                || value.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads one key from coop.ini. A deliberately tiny parser rather than a shared one: the
    /// plugin is NativeAOT x86 and cannot reference the agent's assemblies, and only two
    /// keys are needed here. Same shape the agent's own reader accepts.
    /// </summary>
    private static int ReadLogCapMb(string gameDirectory) =>
        int.TryParse(ReadSetting(gameDirectory, "log_max_mb"),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : LogRotation.DefaultCapMb;

    private static int ReadLogKeep(string gameDirectory) =>
        int.TryParse(ReadSetting(gameDirectory, "log_keep"),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : LogRotation.DefaultKeepGenerations;

    private static string? ReadSetting(string gameDirectory, string key)
    {
        string path = Path.Combine(gameDirectory, "coop.ini");
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';' or '[')
            {
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0 || !line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = line[(equals + 1)..].Trim();
            int comment = value.IndexOfAny([';', '#']);
            return comment >= 0 ? value[..comment].Trim() : value;
        }

        return null;
    }
}
