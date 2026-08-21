using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// What a mirroring peer's agent hands the plugin's reconcile controller each pump, when the
/// accumulated set is bigger than the shared block's fixed capacity. See
/// docs/research/creature-liveness.md.
/// </summary>
public class LivenessBlockPageTests
{
    [Fact]
    public void EverythingFitsWhenThereIsRoom()
    {
        CreatureLivenessEntry[] entries =
        [
            new CreatureLivenessEntry(1, CreatureLivenessState.Alive),
            new CreatureLivenessEntry(2, CreatureLivenessState.Dead),
            new CreatureLivenessEntry(3, CreatureLivenessState.Gone),
        ];

        (List<CreatureLivenessEntry> page, int cursor) = LivenessBlockPage.Select(entries, capacity: 96, cursor: 0);

        // Not an ORDER guarantee — actionable entries always sort first by design, even when
        // there is room for everything (see the next test) — only that nothing is dropped.
        Assert.Equal(entries.ToHashSet(), page.ToHashSet());
        Assert.Equal(0, cursor);
    }

    [Fact]
    public void DeadAndGoneAlwaysGoFirstAndAreNeverDropped()
    {
        List<CreatureLivenessEntry> entries = [];
        for (int i = 0; i < 10; i++)
        {
            entries.Add(new CreatureLivenessEntry((uint)i, CreatureLivenessState.Alive));
        }

        entries.Add(new CreatureLivenessEntry(100, CreatureLivenessState.Dead));
        entries.Add(new CreatureLivenessEntry(101, CreatureLivenessState.Gone));

        (List<CreatureLivenessEntry> page, _) = LivenessBlockPage.Select(entries, capacity: 5, cursor: 0);

        Assert.Equal(5, page.Count);
        Assert.Contains(page, e => e.ObjectId == 100 && e.State == CreatureLivenessState.Dead);
        Assert.Contains(page, e => e.ObjectId == 101 && e.State == CreatureLivenessState.Gone);
    }

    [Fact]
    public void TheAliveTailRotatesAcrossCallsInsteadOfAlwaysCoveringThePrefix()
    {
        List<CreatureLivenessEntry> entries = [];
        for (int i = 0; i < 10; i++)
        {
            entries.Add(new CreatureLivenessEntry((uint)i, CreatureLivenessState.Alive));
        }

        (List<CreatureLivenessEntry> firstPage, int cursorAfterFirst) =
            LivenessBlockPage.Select(entries, capacity: 4, cursor: 0);
        (List<CreatureLivenessEntry> secondPage, int cursorAfterSecond) =
            LivenessBlockPage.Select(entries, capacity: 4, cursor: cursorAfterFirst);

        Assert.Equal(4, firstPage.Count);
        Assert.Equal(4, secondPage.Count);

        // Different windows, not the same four ids every time.
        Assert.NotEqual(
            firstPage.Select(e => e.ObjectId).ToHashSet(),
            secondPage.Select(e => e.ObjectId).ToHashSet());

        Assert.NotEqual(0, cursorAfterFirst);
        Assert.NotEqual(cursorAfterFirst, cursorAfterSecond);
    }

    [Fact]
    public void RotatingThroughTheWholeSetEventuallyCoversEveryEntry()
    {
        List<CreatureLivenessEntry> entries = [];
        for (int i = 0; i < 10; i++)
        {
            entries.Add(new CreatureLivenessEntry((uint)i, CreatureLivenessState.Alive));
        }

        HashSet<uint> covered = [];
        int cursor = 0;
        for (int pass = 0; pass < 5; pass++)
        {
            (List<CreatureLivenessEntry> page, int next) = LivenessBlockPage.Select(entries, capacity: 4, cursor);
            cursor = next;
            foreach (CreatureLivenessEntry entry in page)
            {
                covered.Add(entry.ObjectId);
            }
        }

        Assert.Equal(10, covered.Count);
    }

    [Fact]
    public void ActionableOverflowTruncatesRatherThanCrashing()
    {
        List<CreatureLivenessEntry> entries = [];
        for (int i = 0; i < 8; i++)
        {
            entries.Add(new CreatureLivenessEntry((uint)i, CreatureLivenessState.Dead));
        }

        (List<CreatureLivenessEntry> page, int cursor) = LivenessBlockPage.Select(entries, capacity: 5, cursor: 0);

        Assert.Equal(5, page.Count);
        Assert.All(page, e => Assert.Equal(CreatureLivenessState.Dead, e.State));
        Assert.Equal(0, cursor);
    }

    [Fact]
    public void ZeroCapacityProducesAnEmptyPage()
    {
        CreatureLivenessEntry[] entries = [new CreatureLivenessEntry(1, CreatureLivenessState.Alive)];

        (List<CreatureLivenessEntry> page, int cursor) = LivenessBlockPage.Select(entries, capacity: 0, cursor: 0);

        Assert.Empty(page);
        Assert.Equal(0, cursor);
    }
}
