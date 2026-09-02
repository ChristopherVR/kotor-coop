using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The agent-side exclusion filter for local_combat (<see cref="LocalCombatExclusionRule"/>):
/// an excluded id is dropped from the mirror's feed while the owner lists it alive, passed
/// through the moment the owner lists it dead, and republished as dead once the owner has
/// stopped listing it for the grace period — the immortal-Sith hole from the 2026-09-02
/// A-slots pass (docs/live-verification.md).
/// </summary>
public class LocalCombatExclusionRuleTests
{
    private const int Cap = LocalCombatExclusionRule.FeedCapacity;
    private const long Grace = LocalCombatExclusionRule.AbsenceGraceMs;

    private static EnemyState Alive(uint id) => new(id, 12, 12, Dead: false);
    private static EnemyState Corpse(uint id) => new(id, 0, 12, Dead: true);

    [Fact]
    public void NothingExcluded_FeedPassesUntouched()
    {
        LocalCombatExclusionRule rule = new();
        List<EnemyState> feed = [Alive(0x38), Alive(0x3B)];

        List<EnemyState> filtered = rule.Filter(feed, [], Cap, 1000);

        Assert.Equal(feed, filtered);
        Assert.Empty(rule.Verdicts);
    }

    [Fact]
    public void ExcludedAndAlive_IsDropped()
    {
        LocalCombatExclusionRule rule = new();

        List<EnemyState> filtered = rule.Filter([Alive(0x38), Alive(0x3B)], [0x3B], Cap, 1000);

        Assert.Single(filtered);
        Assert.Equal(0x38u, filtered[0].ObjectId);
        Assert.Contains((0x3Bu, LocalCombatExclusionRule.Verdict.Excluded), rule.Verdicts);
    }

    [Fact]
    public void ExcludedAndOwnerReportsDead_PassesThrough()
    {
        LocalCombatExclusionRule rule = new();
        rule.Filter([Alive(0x3B)], [0x3B], Cap, 1000);

        List<EnemyState> filtered = rule.Filter([Corpse(0x3B)], [0x3B], Cap, 1200);

        Assert.Single(filtered);
        Assert.True(filtered[0].Dead);
        Assert.Contains((0x3Bu, LocalCombatExclusionRule.Verdict.OwnerReportsDead), rule.Verdicts);
    }

    [Fact]
    public void ExcludedAndOwnerStopsListing_RepublishedDeadAfterGrace()
    {
        LocalCombatExclusionRule rule = new();
        rule.Filter([Alive(0x38), Alive(0x3B)], [0x3B], Cap, 1000);

        // Gone from the feed, still inside the grace: still just excluded.
        List<EnemyState> early = rule.Filter([Alive(0x38)], [0x3B], Cap, 1000 + Grace - 1);
        Assert.Single(early);
        Assert.Contains((0x3Bu, LocalCombatExclusionRule.Verdict.Excluded), rule.Verdicts);

        // Grace elapsed: the last seen entry comes back as a corpse.
        List<EnemyState> late = rule.Filter([Alive(0x38)], [0x3B], Cap, 1000 + Grace);
        Assert.Equal(2, late.Count);
        EnemyState corpse = late[1];
        Assert.Equal(0x3Bu, corpse.ObjectId);
        Assert.True(corpse.Dead);
        Assert.Equal(0, corpse.CurrentHealth);
        Assert.Contains((0x3Bu, LocalCombatExclusionRule.Verdict.OwnerNoLongerLists), rule.Verdicts);
    }

    [Fact]
    public void AbsenceClockRestarts_WhenTheOwnerListsItAgain()
    {
        LocalCombatExclusionRule rule = new();
        rule.Filter([Alive(0x3B)], [0x3B], Cap, 1000);
        rule.Filter([], [0x3B], Cap, 1000 + Grace - 500);
        rule.Filter([Alive(0x3B)], [0x3B], Cap, 1000 + Grace - 100);

        List<EnemyState> filtered = rule.Filter([], [0x3B], Cap, 1000 + Grace + 100);

        Assert.Empty(filtered);
    }

    [Fact]
    public void NeverSeenWhileExcluded_AbsenceProvesNothing()
    {
        LocalCombatExclusionRule rule = new();

        rule.Filter([], [0x3B], Cap, 1000);
        List<EnemyState> filtered = rule.Filter([], [0x3B], Cap, 1000 + (Grace * 2));

        Assert.Empty(filtered);
    }

    [Fact]
    public void FeedAtCapacity_AbsenceDoesNotStartTheClock()
    {
        LocalCombatExclusionRule rule = new();
        rule.Filter([Alive(0x3B)], [0x3B], Cap, 1000);

        List<EnemyState> full = [];
        for (uint id = 0x100; id < 0x100 + Cap; id++)
        {
            full.Add(Alive(id));
        }

        rule.Filter(full, [0x3B], Cap, 1000 + (Grace * 2));
        Assert.Contains((0x3Bu, LocalCombatExclusionRule.Verdict.Excluded), rule.Verdicts);

        // The capped pass restarted the clock: a full grace has to elapse from IT.
        List<EnemyState> soon = rule.Filter([Alive(0x38)], [0x3B], Cap, 1000 + (Grace * 2) + 100);
        Assert.Single(soon);
        List<EnemyState> later = rule.Filter([Alive(0x38)], [0x3B], Cap, 1000 + (Grace * 3));
        Assert.Equal(2, later.Count);
        Assert.True(later[1].Dead);
    }

    [Fact]
    public void SlotReleased_ForgetsTheId()
    {
        LocalCombatExclusionRule rule = new();
        rule.Filter([Alive(0x3B)], [0x3B], Cap, 1000);
        rule.Filter([], [], Cap, 1100);

        // Re-excluded later with no sighting since: back to "never seen".
        List<EnemyState> filtered = rule.Filter([], [0x3B], Cap, 1100 + (Grace * 2));

        Assert.Empty(filtered);
    }

    [Fact]
    public void Reset_ForgetsEverything()
    {
        LocalCombatExclusionRule rule = new();
        rule.Filter([Alive(0x3B)], [0x3B], Cap, 1000);
        rule.Reset();

        List<EnemyState> filtered = rule.Filter([], [0x3B], Cap, 1000 + (Grace * 2));

        Assert.Empty(filtered);
        Assert.Empty(rule.Verdicts);
    }
}
