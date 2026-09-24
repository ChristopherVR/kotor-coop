using KotorCoop.Memory;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// What <c>KotorGameBridge.ReadEnemies</c> publishes each pump, when the live engine census is
/// bigger than the fixed census capacity — the "no cap really" design's safety net for content
/// beyond the shared-block region's own generous, content-survey-backed bound (128). Mirrors
/// <see cref="LivenessBlockPageTests"/>'s own coverage of the identical shape. See
/// docs/research/creature-liveness.md.
/// </summary>
public class EnemyCensusPageTests
{
    private static EnemyState State(uint id) => new(id, 15, 15, false);

    [Fact]
    public void EverythingFitsWhenThereIsRoom()
    {
        EnemyState[] fighting = [State(1)];
        EnemyState[] bystanders = [State(2), State(3)];

        (List<EnemyState> page, int cursor) = EnemyCensusPage.Select(fighting, bystanders, capacity: 32, cursor: 0);

        Assert.Equal(
            new HashSet<uint> { 1, 2, 3 },
            page.Select(e => e.ObjectId).ToHashSet());
        Assert.Equal(0, cursor);
    }

    [Fact]
    public void FightingCandidatesAlwaysGoFirstAndAreNeverDropped()
    {
        List<EnemyState> bystanders = [];
        for (uint i = 0; i < 10; i++)
        {
            bystanders.Add(State(i));
        }

        EnemyState[] fighting = [State(100), State(101)];

        (List<EnemyState> page, _) = EnemyCensusPage.Select(fighting, bystanders, capacity: 5, cursor: 0);

        Assert.Equal(5, page.Count);
        Assert.Contains(page, e => e.ObjectId == 100);
        Assert.Contains(page, e => e.ObjectId == 101);
    }

    [Fact]
    public void TheBystanderTailRotatesAcrossCallsInsteadOfAlwaysCoveringThePrefix()
    {
        List<EnemyState> bystanders = [];
        for (uint i = 0; i < 10; i++)
        {
            bystanders.Add(State(i));
        }

        (List<EnemyState> firstPage, int cursorAfterFirst) =
            EnemyCensusPage.Select([], bystanders, capacity: 4, cursor: 0);
        (List<EnemyState> secondPage, int cursorAfterSecond) =
            EnemyCensusPage.Select([], bystanders, capacity: 4, cursor: cursorAfterFirst);

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
    public void RotatingThroughTheWholeSetEventuallyCoversEveryBystander()
    {
        List<EnemyState> bystanders = [];
        for (uint i = 0; i < 10; i++)
        {
            bystanders.Add(State(i));
        }

        HashSet<uint> covered = [];
        int cursor = 0;
        for (int pass = 0; pass < 5; pass++)
        {
            (List<EnemyState> page, int next) = EnemyCensusPage.Select([], bystanders, capacity: 4, cursor);
            cursor = next;
            foreach (EnemyState entry in page)
            {
                covered.Add(entry.ObjectId);
            }
        }

        // Every bystander is covered well inside the 5 passes budgeted here (ceil(10/4) = 3
        // passes suffice), demonstrating the "bounded interval" refresh guarantee.
        Assert.Equal(10, covered.Count);
    }

    [Fact]
    public void FightingOverflowTruncatesRatherThanCrashing()
    {
        List<EnemyState> fighting = [];
        for (uint i = 0; i < 8; i++)
        {
            fighting.Add(State(i));
        }

        (List<EnemyState> page, int cursor) = EnemyCensusPage.Select(fighting, [], capacity: 5, cursor: 0);

        Assert.Equal(5, page.Count);
        Assert.Equal(0, cursor);
    }

    [Fact]
    public void ZeroCapacityProducesAnEmptyPage()
    {
        EnemyState[] fighting = [State(1)];

        (List<EnemyState> page, int cursor) = EnemyCensusPage.Select(fighting, [], capacity: 0, cursor: 0);

        Assert.Empty(page);
        Assert.Equal(0, cursor);
    }

    [Fact]
    public void RealSurveyScaleNeverEngagesTheRotation()
    {
        // The largest static Creature List entry count found across all 117 shipped modules
        // (manm27aa, docs/research/creature-liveness.md's cap-removal survey) is 51 - well
        // under the 128-slot capacity, so ordinary content always takes the "everything fits"
        // path and the rotation never engages in practice.
        List<EnemyState> bystanders = [];
        for (uint i = 0; i < 51; i++)
        {
            bystanders.Add(State(i));
        }

        (List<EnemyState> page, int cursor) = EnemyCensusPage.Select([], bystanders, capacity: 128, cursor: 0);

        Assert.Equal(51, page.Count);
        Assert.Equal(0, cursor);
    }
}
