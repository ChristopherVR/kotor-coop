using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The idempotence rules that keep the journal sync from replaying a save's own entries
/// through the engine on join. The engine's SetState shows the "Journal Entry Added" box
/// even for a no-op apply, so every redundant apply the shadow lets through is a spurious
/// popup on somebody's screen.
/// </summary>
public class JournalShadowTests
{
    [Fact]
    public void NothingAppliesBeforeTheJournalHasBeenReadOnce()
    {
        // The join race: the session store fills from the host's snapshot while this game
        // is still on the loading screen. Until the live journal has actually been read,
        // "the game doesn't have this entry" is not a fact anyone knows.
        JournalShadow shadow = new();

        Assert.False(shadow.IsPrimed);
        Assert.False(shadow.ShouldApply("end_attack", 20));
    }

    [Fact]
    public void AnIdenticalJournalJoinProducesZeroApplies()
    {
        // Both players load the same save; the store snapshot and the live journal agree.
        JournalShadow shadow = new();
        shadow.Observe([new("end_attack", 20), new("k_swg_bastila", 10)]);

        Assert.False(shadow.ShouldApply("end_attack", 20));
        Assert.False(shadow.ShouldApply("k_swg_bastila", 10));

        // At OR past: a store lagging the local journal is also nothing to apply.
        Assert.False(shadow.ShouldApply("end_attack", 10));
    }

    [Fact]
    public void AnEmptyJournalStillPrimes()
    {
        // A new game's journal genuinely holds nothing; that read is a success, not a
        // failure, and syncing must start from it.
        JournalShadow shadow = new();
        List<KeyValuePair<string, int>> advances = shadow.Observe([]);

        Assert.True(shadow.IsPrimed);
        Assert.Empty(advances);
        Assert.True(shadow.ShouldApply("end_attack", 20));
    }

    [Fact]
    public void AGenuinePeerAdvanceAppliesExactlyOnce()
    {
        JournalShadow shadow = new();
        shadow.Observe([new("end_attack", 10)]);

        // The peer is ahead: this must go through (and the popup it causes is correct
        // feedback — the quest really moved).
        Assert.True(shadow.ShouldApply("end_attack", 20));

        shadow.MarkApplied("end_attack", 20);

        // The pump offers the whole store every tick; only the first offer may pass.
        Assert.False(shadow.ShouldApply("end_attack", 20));
    }

    [Fact]
    public void ObserveReturnsOnlyAdvances()
    {
        JournalShadow shadow = new();

        List<KeyValuePair<string, int>> first = shadow.Observe([new("quest_a", 10), new("quest_b", 5)]);
        Assert.Equal(2, first.Count);

        List<KeyValuePair<string, int>> unchanged = shadow.Observe([new("quest_a", 10), new("quest_b", 5)]);
        Assert.Empty(unchanged);

        List<KeyValuePair<string, int>> advanced = shadow.Observe([new("quest_a", 30), new("quest_b", 5)]);
        KeyValuePair<string, int> only = Assert.Single(advanced);
        Assert.Equal("quest_a", only.Key);
        Assert.Equal(30, only.Value);
    }

    [Fact]
    public void AnAppliedEntryDoesNotEchoBackToTheSession()
    {
        // After this instance applies a peer's advance, the game's journal now contains
        // it — and the next read must not announce it as a fresh local advance, or the
        // entry ping-pongs between peers forever.
        JournalShadow shadow = new();
        shadow.Observe([]);
        shadow.MarkApplied("end_attack", 20);

        Assert.Empty(shadow.Observe([new("end_attack", 20)]));
    }

    [Fact]
    public void TagsAreCaseInsensitive()
    {
        // Odyssey resrefs compare case-insensitively; two casings must be one quest.
        JournalShadow shadow = new();
        shadow.Observe([new("End_Attack", 20)]);

        Assert.False(shadow.ShouldApply("end_attack", 20));
        Assert.True(shadow.ShouldApply("END_ATTACK", 30));
    }

    [Fact]
    public void ObserveNeverWalksAnEntryBackwards()
    {
        // A stale read (or a lower state arriving out of order) must not regress the
        // shadow — the store's own merge is a max for the same reason.
        JournalShadow shadow = new();
        shadow.Observe([new("quest", 30)]);

        Assert.Empty(shadow.Observe([new("quest", 10)]));
        Assert.False(shadow.ShouldApply("quest", 30));
    }
}
