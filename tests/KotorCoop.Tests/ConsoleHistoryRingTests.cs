using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public class ConsoleHistoryRingTests
{
    [Fact]
    public void EmptyHistoryNavigatesNowhere()
    {
        ConsoleHistoryRing ring = new();
        Assert.Null(ring.Older());
        Assert.Null(ring.Newer());
    }

    [Fact]
    public void UpRecallsNewestFirstThenOlder()
    {
        ConsoleHistoryRing ring = new();
        ring.Record("host");
        ring.Record("status");
        ring.Record("say hello");

        Assert.Equal("say hello", ring.Older());
        Assert.Equal("status", ring.Older());
        Assert.Equal("host", ring.Older());
    }

    [Fact]
    public void UpPastTheOldestStaysAtTheOldest()
    {
        ConsoleHistoryRing ring = new();
        ring.Record("host");
        ring.Record("status");

        Assert.Equal("status", ring.Older());
        Assert.Equal("host", ring.Older());
        Assert.Equal("host", ring.Older());
        Assert.Equal("host", ring.Older());
    }

    [Fact]
    public void DownWalksBackAndClearsPastTheNewest()
    {
        ConsoleHistoryRing ring = new();
        ring.Record("host");
        ring.Record("status");

        ring.Older(); // status
        ring.Older(); // host
        Assert.Equal("status", ring.Newer());

        // Past the newest: the line clears back to live...
        Assert.Equal(string.Empty, ring.Newer());

        // ...and once live, Down is a no-op (null = leave the buffer alone).
        Assert.Null(ring.Newer());
    }

    [Fact]
    public void EmptyAndWhitespaceLinesAreNotRecorded()
    {
        ConsoleHistoryRing ring = new();
        ring.Record("");
        ring.Record("   ");
        ring.Record(null);
        Assert.Equal(0, ring.Count);
        Assert.Null(ring.Older());
    }

    [Fact]
    public void ConsecutiveDuplicatesCollapse()
    {
        ConsoleHistoryRing ring = new();
        ring.Record("status");
        ring.Record("status");
        ring.Record("status");
        Assert.Equal(1, ring.Count);

        // Non-consecutive repeats are kept: they are distinct history moments.
        ring.Record("clear");
        ring.Record("status");
        Assert.Equal(3, ring.Count);
    }

    [Theory]
    [InlineData("password hunter2")]
    [InlineData("pw hunter2")]
    [InlineData("PASSWORD hunter2")]
    [InlineData("/password hunter2")]
    [InlineData("  password   hunter2")]
    [InlineData("coop password hunter2")]
    [InlineData("/coop pw hunter2")]
    public void SecretsAreNeverRecorded(string line)
    {
        ConsoleHistoryRing ring = new();
        ring.Record(line);
        Assert.Equal(0, ring.Count);
        Assert.Null(ring.Older());
    }

    [Fact]
    public void PasswordAsAnArgumentIsStillRecallable()
    {
        // Only the password VERB is a secret; `connect host secret` is the line whose
        // recall is the entire point of history.
        ConsoleHistoryRing ring = new();
        ring.Record("connect 10.0.0.5 hunter2");
        Assert.Equal("connect 10.0.0.5 hunter2", ring.Older());
    }

    [Fact]
    public void RecordingResetsTheNavigationCursor()
    {
        ConsoleHistoryRing ring = new();
        ring.Record("host");
        ring.Record("status");

        Assert.Equal("status", ring.Older());
        Assert.Equal("host", ring.Older());

        ring.Record("clear");

        // Up after a submit starts from the new newest again, not where the cursor was.
        Assert.Equal("clear", ring.Older());
    }

    [Fact]
    public void ResetCursorReturnsToLive()
    {
        ConsoleHistoryRing ring = new();
        ring.Record("host");
        ring.Older();
        ring.ResetCursor();
        Assert.Null(ring.Newer());
        Assert.Equal("host", ring.Older());
    }

    [Fact]
    public void FullRingDropsTheOldestLines()
    {
        ConsoleHistoryRing ring = new(capacity: 4);
        for (int i = 1; i <= 6; i++)
        {
            ring.Record($"line {i}");
        }

        Assert.Equal(4, ring.Count);

        Assert.Equal("line 6", ring.Older());
        Assert.Equal("line 5", ring.Older());
        Assert.Equal("line 4", ring.Older());
        Assert.Equal("line 3", ring.Older());

        // lines 1 and 2 are gone; Up stays parked at the oldest survivor.
        Assert.Equal("line 3", ring.Older());
    }

    [Fact]
    public void RecordedLinesAreTrimmed()
    {
        ConsoleHistoryRing ring = new();
        ring.Record("  status  ");
        Assert.Equal("status", ring.Older());
    }
}
