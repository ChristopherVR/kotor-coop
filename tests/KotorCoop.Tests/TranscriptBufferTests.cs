using KotorCoop.Agent;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The dashboard's transcript store: the bounded raw-line ring and the follow/"N new" state
/// the UI glue drives from Terminal.Gui's own TextView scroll events. No console or
/// Terminal.Gui dependency — wrapping and actual scrolling are the TextView's job now.
/// </summary>
public class TranscriptBufferTests
{
    [Fact]
    public void LinesAreHeldOldestFirst()
    {
        TranscriptBuffer buffer = new();
        buffer.Append("one");
        buffer.Append("two");
        buffer.Append("three");

        Assert.Equal(["one", "two", "three"], buffer.Lines);
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void CapacityEvictsTheOldestLines()
    {
        TranscriptBuffer buffer = new(capacity: 5);
        for (int i = 0; i < 8; i++)
        {
            buffer.Append($"{i}");
        }

        Assert.Equal(5, buffer.Count);
        Assert.Equal(["3", "4", "5", "6", "7"], buffer.Lines);
    }

    [Fact]
    public void ClearWipesEverythingAndResumesFollowing()
    {
        TranscriptBuffer buffer = new();
        buffer.Append("one");
        buffer.Append("two");
        buffer.SetFollowing(false);
        buffer.Append("three"); // counts toward NewSinceScrolled

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.Lines);
        Assert.True(buffer.IsFollowing);
        Assert.Equal(0, buffer.NewSinceScrolled);
    }

    // ---- follow / new-count state --------------------------------------------------

    [Fact]
    public void StartsFollowingTheBottom()
    {
        TranscriptBuffer buffer = new();
        Assert.True(buffer.IsFollowing);
        Assert.Equal(0, buffer.NewSinceScrolled);
    }

    [Fact]
    public void AppendingWhileFollowingNeverCountsAsNew()
    {
        TranscriptBuffer buffer = new();
        buffer.Append("a");
        buffer.Append("b");

        Assert.Equal(0, buffer.NewSinceScrolled);
    }

    [Fact]
    public void SetFollowingFalseThenAppendingCountsNewLines()
    {
        TranscriptBuffer buffer = new();
        buffer.SetFollowing(false);

        buffer.Append("new 1");
        buffer.Append("new 2");
        buffer.Append("new 3");

        Assert.False(buffer.IsFollowing);
        Assert.Equal(3, buffer.NewSinceScrolled);
    }

    [Fact]
    public void SetFollowingTrueResetsTheNewCounter()
    {
        TranscriptBuffer buffer = new();
        buffer.SetFollowing(false);
        buffer.Append("new");
        Assert.Equal(1, buffer.NewSinceScrolled);

        buffer.SetFollowing(true);

        Assert.True(buffer.IsFollowing);
        Assert.Equal(0, buffer.NewSinceScrolled);
    }

    [Fact]
    public void SetFollowingFalseRepeatedlyDoesNotResetTheCounter()
    {
        TranscriptBuffer buffer = new();
        buffer.SetFollowing(false);
        buffer.Append("new 1");
        buffer.SetFollowing(false); // e.g. the viewport moved but is still not at the bottom
        buffer.Append("new 2");

        Assert.Equal(2, buffer.NewSinceScrolled);
    }
}
