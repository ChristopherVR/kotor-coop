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
///   - launched exactly once per game process, detached, so nothing here can block or
///     outlive the frame loop;
///   - a failure is logged and ignored — the game must start normally with or without the
///     agent, and a player who prefers to run it by hand still can (an already-running
///     agent simply takes the session and this one exits on the duplicate attach).
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

    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint CreateAlways = 2;
    private const uint FileAttributeNormal = 0x00000080;
    private const int UseStdHandles = 0x00000100;
    private static readonly nint InvalidHandle = -1;

    private static bool _attempted;

    /// <summary>
    /// Launches the agent for this process, once. Safe to call from plugin startup on the
    /// game thread: CreateProcess returns immediately and both handles are closed.
    /// </summary>
    public static void EnsureRunning(string gameDirectory)
    {
        if (_attempted)
        {
            return;
        }

        _attempted = true;

        try
        {
            if (!WantsAutoStart(gameDirectory))
            {
                PluginHost.Log("agent: auto-start disabled in coop.ini");
                return;
            }

            string? agentPath = FindAgent(gameDirectory);
            if (agentPath is null)
            {
                PluginHost.Log("agent: no KotorCoop.Agent.exe beside the game; start it yourself");
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
            SecurityAttributes inheritable = new()
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = 0,
                InheritHandle = 1,
            };

            nint log = CreateFileW(
                logPath, GenericWrite, FileShareReadWrite, ref inheritable,
                CreateAlways, FileAttributeNormal, 0);

            StartupInfo startup = default;
            startup.Cb = sizeof(StartupInfo);

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
                CreateNoWindow,
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
                PluginHost.Log($"agent: could not start {agentPath} (error {Marshal.GetLastWin32Error()})");
                return;
            }

            // Nothing here owns the agent's lifetime; it attaches by pid and exits with the
            // game. Close both handles so the plugin holds no references to it.
            CloseHandle(info.Thread);
            CloseHandle(info.Process);
            PluginHost.Log($"agent: started {Path.GetFileName(agentPath)} as pid {info.ProcessId}; log -> {Path.GetFileName(logPath)}");
        }
        catch (Exception ex)
        {
            PluginHost.Log($"agent: auto-start failed: {ex.Message}");
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
