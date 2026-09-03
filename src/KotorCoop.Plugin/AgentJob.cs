using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Ties the auto-started agent's lifetime to the game's with a Windows job object, so the
/// agent can never outlive the game — not on a clean quit, not on a crash, not when the
/// player kills swkotor.exe from Task Manager.
///
/// The agent already exits by itself when it sees its game go (RunCommand's WaitForExitAsync
/// watch), and that remains the normal path: it sends the Bye, the host announces the leave.
/// This is the backstop for the cases that path cannot cover — the agent wedged in teardown,
/// a UDP socket blocked on a dead route, a debugger attached — where a hidden, windowless
/// process would otherwise sit in Task Manager for ever with nothing on screen to say so.
///
/// Mechanism: the job carries JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE and THIS process holds the
/// only handle to it, never closed. When the game process ends for any reason the kernel
/// closes that handle, the job closes, and every process in it is terminated. The agent is
/// created suspended, assigned, then resumed, so there is no window in which it runs outside
/// the job. JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK keeps the job to the agent itself: anything
/// the agent launches later (a second game instance from the rig, say) is not pulled in.
///
/// Job objects nest on every supported Windows, so a game already inside a job (a store
/// launcher's, a test rig's) is fine. Any failure here is logged and the agent is simply
/// resumed unjoined — an unjoined agent is the pre-existing behaviour, not a regression.
/// </summary>
internal static unsafe class AgentJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint LimitKillOnJobClose = 0x2000;
    private const uint LimitSilentBreakawayOk = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        nint job, int informationClass, void* information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    /// <summary>
    /// The one handle. Deliberately never closed: closing it IS the kill signal, and the
    /// process exiting is the only time that should happen.
    /// </summary>
    private static nint _job;

    private static bool _creationFailed;

    /// <summary>
    /// Puts a freshly created (still suspended) agent process into the game-lifetime job.
    /// Returns false, having logged why, when the agent will run unjoined instead.
    /// </summary>
    public static bool TryAssign(nint process)
    {
        nint job = EnsureJob();
        if (job == 0)
        {
            return false;
        }

        if (!AssignProcessToJobObject(job, process))
        {
            PluginHost.Log(
                $"agent: could not tie the agent to the game's lifetime (AssignProcessToJobObject error {Marshal.GetLastWin32Error()}); "
                + "it will still exit on its own when the game does");
            return false;
        }

        return true;
    }

    private static nint EnsureJob()
    {
        if (_job != 0)
        {
            return _job;
        }

        if (_creationFailed)
        {
            return 0;
        }

        nint job = CreateJobObjectW(0, null);
        if (job == 0)
        {
            _creationFailed = true;
            PluginHost.Log($"agent: CreateJobObject failed (error {Marshal.GetLastWin32Error()}); the agent runs unjoined");
            return 0;
        }

        ExtendedLimitInformation limits = default;
        limits.BasicLimitInformation.LimitFlags = LimitKillOnJobClose | LimitSilentBreakawayOk;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, (uint)sizeof(ExtendedLimitInformation)))
        {
            _creationFailed = true;
            PluginHost.Log($"agent: SetInformationJobObject failed (error {Marshal.GetLastWin32Error()}); the agent runs unjoined");
            CloseHandle(job);
            return 0;
        }

        _job = job;
        return job;
    }
}
