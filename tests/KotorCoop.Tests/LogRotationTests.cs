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
}
