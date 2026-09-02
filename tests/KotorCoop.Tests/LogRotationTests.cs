using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The rotation decision behind PluginHost.RotateLogsIfNeeded, AgentLauncher's
/// RotateAgentLogIfNeeded, and AgentDashboard's --log rotation: whether a file found at
/// startup is big enough to move aside before the new session starts writing, and what its
/// rotated name is. A live test rig's combined plugin log reached 170 MB across 158
/// concatenated sessions before this existed — this is the arithmetic that stops that.
/// </summary>
public class LogRotationTests
{
    [Fact]
    public void FileAtOrUnderTheCapIsNotRotated()
    {
        long cap = LogRotation.CapBytes(8);

        Assert.False(LogRotation.ShouldRotate(0, cap));
        Assert.False(LogRotation.ShouldRotate(cap, cap));
    }

    [Fact]
    public void FileOverTheCapIsRotated()
    {
        long cap = LogRotation.CapBytes(8);

        Assert.True(LogRotation.ShouldRotate(cap + 1, cap));
    }

    [Fact]
    public void CapBytesConvertsMegabytesToBytes()
    {
        Assert.Equal(8L * 1024 * 1024, LogRotation.CapBytes(8));
        Assert.Equal(1L * 1024 * 1024, LogRotation.CapBytes(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void CapBytesClampsNonPositiveValuesInsteadOfDisablingRotation(int badMb)
    {
        // A coop.ini typo (log_max_mb = 0, or a negative number) must not turn rotation off
        // by making every file "under the cap" forever.
        long cap = LogRotation.CapBytes(badMb);

        Assert.True(cap > 0);
        Assert.True(LogRotation.ShouldRotate(cap + 1, cap));
    }

    [Fact]
    public void CapBytesClampsAbsurdlyLargeValues()
    {
        // Bounded so a bad value (e.g. a units mistake) cannot hold every log open until the
        // disk fills.
        long cap = LogRotation.CapBytes(int.MaxValue);

        Assert.Equal(1024L * 1024 * 1024, cap);
    }

    [Theory]
    [InlineData("kotorcoop-plugin.log", "kotorcoop-plugin.1.log")]
    [InlineData("kotorcoop-combat.log", "kotorcoop-combat.1.log")]
    [InlineData(@"C:\game\kotorcoop-agent.log", @"C:\game\kotorcoop-agent.1.log")]
    public void RotatedPathKeepsThePerChannelScheme(string logPath, string expected)
    {
        Assert.Equal(expected, LogRotation.RotatedPath(logPath));
    }

    [Fact]
    public void RotatedPathLeavesANonLogPathUnchanged()
    {
        Assert.Equal("kotorcoop-plugin.txt", LogRotation.RotatedPath("kotorcoop-plugin.txt"));
    }

    [Fact]
    public void PeriodicCheckIsNotDueBeforeTheIntervalElapses()
    {
        Assert.False(LogRotation.ShouldCheckPeriodically(lastCheckMs: 1000, nowMs: 1000 + LogRotation.PeriodicCheckIntervalMs - 1, LogRotation.PeriodicCheckIntervalMs));
    }

    [Fact]
    public void PeriodicCheckIsDueExactlyAtTheInterval()
    {
        Assert.True(LogRotation.ShouldCheckPeriodically(lastCheckMs: 1000, nowMs: 1000 + LogRotation.PeriodicCheckIntervalMs, LogRotation.PeriodicCheckIntervalMs));
    }

    [Fact]
    public void PeriodicCheckIsDueWellPastTheInterval()
    {
        Assert.True(LogRotation.ShouldCheckPeriodically(lastCheckMs: 1000, nowMs: 1000 + LogRotation.PeriodicCheckIntervalMs * 10, LogRotation.PeriodicCheckIntervalMs));
    }

    [Fact]
    public void PeriodicCheckIsDueOnceElapsedFromALastCheckMsOfZero()
    {
        // _lastPeriodicLogCheckMs starts at its default (0) the first time WorkerLoop runs;
        // Environment.TickCount64 (the real nowMs) is system-uptime-scaled, not zero-based,
        // so in practice this is already true the first time WorkerLoop calls it.
        Assert.True(LogRotation.ShouldCheckPeriodically(lastCheckMs: 0, nowMs: LogRotation.PeriodicCheckIntervalMs, LogRotation.PeriodicCheckIntervalMs));
        Assert.False(LogRotation.ShouldCheckPeriodically(lastCheckMs: 0, nowMs: LogRotation.PeriodicCheckIntervalMs - 1, LogRotation.PeriodicCheckIntervalMs));
    }

    // --- log_keep: multi-generation shift (2026-09-02) --------------------------------------
    //
    // A mid-session rotation used to overwrite the ONLY prior generation ("*.1.log"). Live on
    // 2026-09-02 that cost the evidence for a real bug: a client's kotorcoop-plugin.log
    // rotated at the exact second a corrupt damage effect was read, and the next rotation
    // overwrote that evidence before anyone could look at it. log_keep (default 3) keeps that
    // many generations instead of one.

    [Theory]
    [InlineData(0, "kotorcoop-plugin.log")]
    [InlineData(1, "kotorcoop-plugin.1.log")]
    [InlineData(3, "kotorcoop-plugin.3.log")]
    public void GenerationPathNamesEachGeneration(int generation, string expected)
    {
        Assert.Equal(expected, LogRotation.GenerationPath("kotorcoop-plugin.log", generation));
    }

    [Fact]
    public void GenerationPathLeavesANonLogPathUnchanged()
    {
        Assert.Equal("kotorcoop-plugin.txt", LogRotation.GenerationPath("kotorcoop-plugin.txt", 1));
    }

    [Fact]
    public void RotatedPathAgreesWithGenerationPathOne()
    {
        Assert.Equal(LogRotation.GenerationPath("kotorcoop-plugin.log", 1), LogRotation.RotatedPath("kotorcoop-plugin.log"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ClampKeepGenerationsClampsNonPositiveValuesToOne(int badKeep)
    {
        Assert.Equal(1, LogRotation.ClampKeepGenerations(badKeep));
    }

    [Fact]
    public void ClampKeepGenerationsClampsAbsurdlyLargeValues()
    {
        Assert.Equal(10, LogRotation.ClampKeepGenerations(int.MaxValue));
    }

    [Fact]
    public void ClampKeepGenerationsLeavesAnInRangeValueAlone()
    {
        Assert.Equal(3, LogRotation.ClampKeepGenerations(3));
    }

    [Fact]
    public void ShiftPlanWithKeepOneBehavesExactlyLikeTheOldSingleGenerationRotation()
    {
        var plan = LogRotation.ShiftPlan("kotorcoop-plugin.log", keepGenerations: 1);

        Assert.Single(plan);
        Assert.Equal(("kotorcoop-plugin.log", "kotorcoop-plugin.1.log"), plan[0]);
    }

    [Fact]
    public void ShiftPlanOrdersHighestGenerationFirstSoNoMoveClobbersAFileStillToBeMoved()
    {
        var plan = LogRotation.ShiftPlan("kotorcoop-plugin.log", keepGenerations: 3);

        Assert.Equal(
            [
                ("kotorcoop-plugin.2.log", "kotorcoop-plugin.3.log"),
                ("kotorcoop-plugin.1.log", "kotorcoop-plugin.2.log"),
                ("kotorcoop-plugin.log", "kotorcoop-plugin.1.log"),
            ],
            plan);
    }

    [Fact]
    public void ApplyShiftPlanSkipsAMissingIntermediateGenerationInsteadOfStoppingTheShift()
    {
        // .2.log is deliberately absent (e.g. only two rotations have ever happened) while
        // .log and .1.log both exist. The shift for .1.log -> .2.log has nothing to move, but
        // the surrounding moves (.log -> .1.log) must still happen.
        string directory = Directory.CreateTempSubdirectory("kotorcoop-logrotation-").FullName;
        try
        {
            string logPath = Path.Combine(directory, "kotorcoop-plugin.log");
            string gen1 = Path.Combine(directory, "kotorcoop-plugin.1.log");
            File.WriteAllText(logPath, "live");
            File.WriteAllText(gen1, "gen1");

            LogRotation.ApplyShiftPlan(LogRotation.ShiftPlan(logPath, keepGenerations: 3));

            Assert.False(File.Exists(logPath));
            Assert.Equal("live", File.ReadAllText(Path.Combine(directory, "kotorcoop-plugin.1.log")));
            Assert.Equal("gen1", File.ReadAllText(Path.Combine(directory, "kotorcoop-plugin.2.log")));
            Assert.False(File.Exists(Path.Combine(directory, "kotorcoop-plugin.3.log")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ApplyShiftPlanKeepsExactlyKeepGenerationsDroppingTheOldest()
    {
        string directory = Directory.CreateTempSubdirectory("kotorcoop-logrotation-").FullName;
        try
        {
            string logPath = Path.Combine(directory, "kotorcoop-plugin.log");
            File.WriteAllText(logPath, "live");
            File.WriteAllText(Path.Combine(directory, "kotorcoop-plugin.1.log"), "gen1");
            File.WriteAllText(Path.Combine(directory, "kotorcoop-plugin.2.log"), "gen2");
            File.WriteAllText(Path.Combine(directory, "kotorcoop-plugin.3.log"), "gen3-oldest");

            LogRotation.ApplyShiftPlan(LogRotation.ShiftPlan(logPath, keepGenerations: 3));

            Assert.Equal("live", File.ReadAllText(Path.Combine(directory, "kotorcoop-plugin.1.log")));
            Assert.Equal("gen1", File.ReadAllText(Path.Combine(directory, "kotorcoop-plugin.2.log")));
            Assert.Equal("gen2", File.ReadAllText(Path.Combine(directory, "kotorcoop-plugin.3.log")));
            // gen3-oldest is gone - it was in the last kept slot and nothing shifts it further.
            Assert.False(File.Exists(Path.Combine(directory, "kotorcoop-plugin.4.log")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
