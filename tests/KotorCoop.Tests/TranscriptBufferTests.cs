using KotorCoop.Agent;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The dashboard's scrolling transcript: word-wrap, the bounded raw-line history, and the
/// follow/scroll-offset/"N new" state a PageUp/PageDown key drives. No console involved.
/// </summary>
public class TranscriptBufferTests
{
    [Fact]
    public void ShortLinesPassThroughUnwrapped()
    {
        TranscriptBuffer buffer = new();
        buffer.Append("hello");

        Assert.Equal(["hello"], buffer.VisibleRows(80, 10));
    }

    [Fact]
    public void ALongLineWrapsAtTheLastSpaceWithinWidth()
    {
        TranscriptBuffer buffer = new();
        buffer.Append("the quick brown fox jumps over the lazy dog");

        // width 10: "the quick " is 10 chars but the break must land BEFORE the trailing
        // space, i.e. at the last space at-or-before column 10.
        IReadOnlyList<string> rows = buffer.VisibleRows(10, 10);

        Assert.All(rows, row => Assert.True(row.Length <= 10, $"row too long: '{row}'"));
        Assert.Equal("the quick brown fox jumps over the lazy dog",
            string.Join(" ", rows).Replace("  ", " "));
    }

    [Fact]
    public void AWordLongerThanTheWidthHardBreaks()
    {
        TranscriptBuffer buffer = new();
        buffer.Append("supercalifragilisticexpialidocious");

        IReadOnlyList<string> rows = buffer.VisibleRows(10, 10);

        Assert.All(rows, row => Assert.True(row.Length <= 10));
        Assert.Equal("supercalifragilisticexpialidocious", string.Concat(rows));
    }

    [Fact]
    public void AnEmptyLineStaysABlankRow()
    {
        TranscriptBuffer buffer = new();
        buffer.Append("one");
        buffer.Append(string.Empty);
        buffer.Append("two");

        Assert.Equal(["one", string.Empty, "two"], buffer.VisibleRows(80, 10));
    }

    [Fact]
    public void VisibleRowsShowsAtMostTheRequestedHeightFromTheBottom()
    {
        TranscriptBuffer buffer = new();
        for (int i = 0; i < 20; i++)
        {
            buffer.Append($"line {i}");
        }

        IReadOnlyList<string> visible = buffer.VisibleRows(80, 5);

        Assert.Equal(["line 15", "line 16", "line 17", "line 18", "line 19"], visible);
    }

    [Fact]
    public void CapacityEvictsTheOldestRawLines()
    {
        TranscriptBuffer buffer = new(capacity: 5);
        for (int i = 0; i < 8; i++)
        {
            buffer.Append($"{i}");
        }

        Assert.Equal(5, buffer.Count);
        Assert.Equal(["3", "4", "5", "6", "7"], buffer.VisibleRows(80, 10));
    }

    // ---- scroll / follow state ----------------------------------------------------

    [Fact]
    public void StartsFollowingTheBottom()
    {
        TranscriptBuffer buffer = new();
        Assert.True(buffer.IsFollowing);
    }

    [Fact]
    public void ScrollingUpStopsFollowingAndShowsOlderRows()
    {
        TranscriptBuffer buffer = new();
        for (int i = 0; i < 20; i++)
        {
            buffer.Append($"line {i}");
        }

        buffer.ScrollUp(5, width: 80, height: 5);

        Assert.False(buffer.IsFollowing);
        Assert.Equal(["line 10", "line 11", "line 12", "line 13", "line 14"], buffer.VisibleRows(80, 5));
    }

    [Fact]
    public void ScrollingUpPastTheTopClampsThere()
    {
        TranscriptBuffer buffer = new();
        for (int i = 0; i < 10; i++)
        {
            buffer.Append($"{i}");
        }

        buffer.ScrollUp(1000, width: 80, height: 5);

        Assert.Equal(["0", "1", "2", "3", "4"], buffer.VisibleRows(80, 5));
    }

    [Fact]
    public void ScrollDownBackToTheBottomResumesFollowing()
    {
        TranscriptBuffer buffer = new();
        for (int i = 0; i < 20; i++)
        {
            buffer.Append($"line {i}");
        }

        buffer.ScrollUp(10, width: 80, height: 5);
        buffer.ScrollDown(1000, width: 80, height: 5);

        Assert.True(buffer.IsFollowing);
        Assert.Equal(["line 15", "line 16", "line 17", "line 18", "line 19"], buffer.VisibleRows(80, 5));
    }

    [Fact]
    public void ScrollToTopThenScrollToBottomRoundTrip()
    {
        TranscriptBuffer buffer = new();
        for (int i = 0; i < 20; i++)
        {
            buffer.Append($"{i}");
        }

        buffer.ScrollToTop(width: 80, height: 5);
        Assert.Equal(["0", "1", "2", "3", "4"], buffer.VisibleRows(80, 5));
        Assert.False(buffer.IsFollowing);

        buffer.ScrollToBottom();
        Assert.True(buffer.IsFollowing);
        Assert.Equal(["15", "16", "17", "18", "19"], buffer.VisibleRows(80, 5));
    }

    [Fact]
    public void NewLinesWhileFollowingDoNotCountAsNew()
    {
        TranscriptBuffer buffer = new();
        buffer.Append("a");
        buffer.Append("b");

        Assert.Equal(0, buffer.NewSinceScrolled);
    }

    [Fact]
    public void NewLinesWhileScrolledUpAreCounted()
    {
        TranscriptBuffer buffer = new();
        for (int i = 0; i < 20; i++)
        {
            buffer.Append($"{i}");
        }

        buffer.ScrollUp(5, width: 80, height: 5);
        buffer.Append("new 1");
        buffer.Append("new 2");

        Assert.Equal(2, buffer.NewSinceScrolled);
    }

    [Fact]
    public void ReturningToTheBottomResetsTheNewCounter()
    {
        TranscriptBuffer buffer = new();
        for (int i = 0; i < 10; i++)
        {
            buffer.Append($"{i}");
        }

        buffer.ScrollUp(3, width: 80, height: 5);
        buffer.Append("new");
        Assert.Equal(1, buffer.NewSinceScrolled);

        buffer.ScrollToBottom();
        Assert.Equal(0, buffer.NewSinceScrolled);
    }

    [Fact]
    public void WrappedCountReflectsTheGivenWidth()
    {
        TranscriptBuffer buffer = new();
        buffer.Append("0123456789"); // 10 chars

        Assert.Equal(1, buffer.WrappedCount(10));
        Assert.Equal(2, buffer.WrappedCount(5)); // "01234" + "56789"
    }
}
