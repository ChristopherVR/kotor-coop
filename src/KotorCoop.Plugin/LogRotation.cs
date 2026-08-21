namespace KotorCoop.Plugin;

/// <summary>
/// The pure decision behind log rotation: is this file, found at startup, big enough that it
/// should be rotated to its ".1.log" sibling before the new session starts appending to it.
///
/// Split out from the file-system side (PluginHost.RotateLogsIfNeeded,
/// AgentLauncher.RotateAgentLogIfNeeded) so the arithmetic is testable without a real
/// directory: a live test rig's kotorcoop-plugin.log reached 170 MB, 158 concatenated
/// "=== KotorCoop plugin loaded ===" banners deep, because File.AppendAllText never
/// truncates on its own. Rotation only ever runs at startup, never mid-session — a tool
/// like tools/verify.ps1 tails the live file, and swapping it out from under a tail would
/// lose the tail.
/// </summary>
internal static class LogRotation
{
    private const long BytesPerMb = 1024 * 1024;

    /// <summary>coop.ini's <c>log_max_mb</c> default: generous enough that a normal session
    /// never rotates, small enough that a runaway one (a stuck retry loop logging every
    /// frame) is capped within a couple of minutes rather than growing to hundreds of MB.</summary>
    public const int DefaultCapMb = 8;

    /// <summary>Clamped so a bad or missing coop.ini value can neither disable rotation
    /// (0 or negative) nor hold every log open until the disk fills (an absurdly large
    /// value); a value read as milliseconds instead of MB by a typo still rotates eventually
    /// instead of never.</summary>
    public static long CapBytes(int capMb) => Math.Clamp(capMb, 1, 1024) * BytesPerMb;

    /// <summary>True when a file this large should be rotated before the fresh session
    /// starts appending to it. Equal to the cap does not rotate — only strictly over,
    /// so a freshly-rotated file sitting exactly at the cap is not immediately re-rotated.</summary>
    public static bool ShouldRotate(long existingSizeBytes, long capBytes) =>
        existingSizeBytes > capBytes;

    /// <summary>The ".1.log" sibling for a "*.log" path, preserving everything before the
    /// final ".log" — "kotorcoop-plugin.log" becomes "kotorcoop-plugin.1.log", keeping the
    /// per-channel scheme (see PluginHost.ChannelFor). A path that does not end in ".log" is
    /// returned unchanged; callers only ever pass paths that do.</summary>
    public static string RotatedPath(string logPath) =>
        logPath.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            ? string.Concat(logPath.AsSpan(0, logPath.Length - ".log".Length), ".1.log")
            : logPath;
}
