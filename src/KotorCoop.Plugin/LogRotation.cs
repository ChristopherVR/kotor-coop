namespace KotorCoop.Plugin;

/// <summary>
/// The pure decision behind log rotation: is this file, found at startup, big enough that it
/// should be rotated to its ".1.log" sibling before the new session starts appending to it —
/// and, once rotated, how the older generations already sitting there (".1.log" up through
/// ".N.log", <c>log_keep</c>'s N) shift up to make room instead of the newest one simply
/// overwriting the only prior copy kept.
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

    /// <summary>How often PluginHost.WorkerLoop re-checks log sizes once the game is running
    /// (see PluginHost.RotateLogsIfNeededMidSession). A long-running session used to only
    /// ever rotate at the NEXT process start, so a single session that outlived the cap grew
    /// unbounded until then; this is the period that caps it without the worker's ~16ms loop
    /// stat-ing every "kotorcoop-*.log" file every tick.</summary>
    public const int PeriodicCheckIntervalMs = 30_000;

    /// <summary>True once at least <paramref name="intervalMs"/> has elapsed since the last
    /// periodic check — the timing half of the mid-session decision, kept separate from
    /// <see cref="ShouldRotate"/> (the size half) so a caller can test "is it time to even
    /// look at the files" without touching the filesystem. <paramref name="nowMs"/> and
    /// <paramref name="lastCheckMs"/> are expected to come from the same monotonic clock
    /// (<c>Environment.TickCount64</c>); a <paramref name="lastCheckMs"/> of 0 (nothing
    /// checked yet this session) is due the moment <paramref name="nowMs"/> itself reaches
    /// <paramref name="intervalMs"/> — <c>TickCount64</c> is process-uptime-scaled, not
    /// zero-based, so in practice that is already true the first time this runs.</summary>
    public static bool ShouldCheckPeriodically(long lastCheckMs, long nowMs, long intervalMs) =>
        nowMs - lastCheckMs >= intervalMs;

    /// <summary>The ".1.log" sibling for a "*.log" path, preserving everything before the
    /// final ".log" — "kotorcoop-plugin.log" becomes "kotorcoop-plugin.1.log", keeping the
    /// per-channel scheme (see PluginHost.ChannelFor). A path that does not end in ".log" is
    /// returned unchanged; callers only ever pass paths that do. Equivalent to
    /// <c>GenerationPath(logPath, 1)</c>.</summary>
    public static string RotatedPath(string logPath) => GenerationPath(logPath, 1);

    /// <summary>coop.ini's <c>log_keep</c> default: how many rotated generations (".1.log"
    /// through ".N.log") a rotation keeps before the oldest is dropped. 3 rather than the old
    /// single generation because a mid-session rotation losing the ONLY prior generation cost
    /// real evidence live on 2026-09-02 — a client's kotorcoop-plugin.log rotated at the exact
    /// second a corrupt damage effect was read, and the next rotation overwrote that evidence
    /// before anyone could look at it. Small enough that a long session's log pile stays
    /// bounded either way.</summary>
    public const int DefaultKeepGenerations = 3;

    /// <summary>Clamped the same way <see cref="CapBytes"/> clamps <c>log_max_mb</c>: a bad or
    /// missing coop.ini value can neither collapse back to zero kept generations (nonsensical —
    /// there is always at least the one ".1.log" a rotation produces) nor pile up an unbounded
    /// number of old logs from a units mistake.</summary>
    public static int ClampKeepGenerations(int keep) => Math.Clamp(keep, 1, 10);

    /// <summary>The "*.N.log" sibling for a "*.log" path — the generalisation of
    /// <see cref="RotatedPath"/> that <see cref="ShiftPlan"/> needs to name every generation.
    /// <paramref name="generation"/> 0 returns <paramref name="logPath"/> itself (the live
    /// file currently being appended to). A path that does not end in ".log" is returned
    /// unchanged, same as <see cref="RotatedPath"/>.</summary>
    public static string GenerationPath(string logPath, int generation)
    {
        if (generation <= 0 || !logPath.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            return logPath;
        }

        return string.Concat(logPath.AsSpan(0, logPath.Length - ".log".Length), $".{generation}.log");
    }

    /// <summary>
    /// The ordered renames that shift every rotated generation up by one before the live file
    /// takes generation 1: "*.log" -&gt; "*.1.log" -&gt; "*.2.log" -&gt; ... -&gt;
    /// "*.&lt;keepGenerations&gt;.log", dropping whatever previously sat in the last slot
    /// (nothing renames it further, so the next move there simply overwrites it).
    ///
    /// Ordered HIGHEST generation first — (keepGenerations-1 -&gt; keepGenerations), down to
    /// (0 -&gt; 1) — so that applying each move with <see cref="File.Move(string,string,bool)"/>
    /// (overwrite: true) in order never clobbers a file before that file has itself been moved
    /// out of the way. A "From" that does not exist (a missing intermediate generation — e.g.
    /// no ".2.log" yet because this is only the second rotation ever) is simply not there to
    /// move; callers skip it rather than treating it as a failure, so the rest of the shift
    /// still happens.
    ///
    /// keepGenerations=1 produces exactly one move — the live file straight to "*.1.log",
    /// overwriting whatever was there — which is byte-for-byte the old single-generation
    /// rotation, unchanged.
    /// </summary>
    public static IReadOnlyList<(string From, string To)> ShiftPlan(string logPath, int keepGenerations)
    {
        var plan = new List<(string From, string To)>(keepGenerations);
        for (int generation = keepGenerations; generation >= 1; generation--)
        {
            plan.Add((GenerationPath(logPath, generation - 1), GenerationPath(logPath, generation)));
        }

        return plan;
    }

    /// <summary>Applies a <see cref="ShiftPlan"/> against the real filesystem, in the plan's
    /// own order: each move whose "From" exists is executed with overwrite: true; a missing
    /// "From" is skipped rather than stopping the rest of the shift. Shared by
    /// PluginHost.RotateLogFilesOverCap and AgentLauncher.RotateAgentLogIfNeeded so both call
    /// sites shift generations identically.</summary>
    public static void ApplyShiftPlan(IReadOnlyList<(string From, string To)> plan)
    {
        foreach ((string from, string to) in plan)
        {
            if (File.Exists(from))
            {
                File.Move(from, to, overwrite: true);
            }
        }
    }
}
