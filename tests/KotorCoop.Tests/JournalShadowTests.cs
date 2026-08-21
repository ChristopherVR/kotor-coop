using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The idempotence rules that keep the journal sync from replaying a save's own entries
/// through the engine on join. The engine's SetState shows the "Journal Entry Added" box
/// even for a no-op apply, so every redundant apply the shadow lets through is a spurious
/// popup on somebody's screen.
///
/// Protocol 31 made both halves direction-neutral (see JournalShadow's remarks): a
/// legitimate decrease or an outright removal is now reported and applied just like an
/// advance, rather than being silently refused forever.
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
        Assert.False(shadow.ShouldRemove("end_attack"));
    }

    [Fact]
    public void AnIdenticalJournalJoinProducesZeroApplies()
    {
        // Both players load the same save; the store snapshot and the live journal agree.
        JournalShadow shadow = new();
        shadow.Observe([new("end_attack", 20), new("k_swg_bastila", 10)]);

        Assert.False(shadow.ShouldApply("end_attack", 20));
        Assert.False(shadow.ShouldApply("k_swg_bastila", 10));
    }

    [Fact]
    public void ADecreaseIsWorthApplying()
    {
        // The session holds a legitimate override-lower target (a peer's own engine
        // authoritatively pushed the stage back down). This used to be refused forever —
        // now any genuine difference from what the game holds is worth applying.
        JournalShadow shadow = new();
        shadow.Observe([new("end_attack", 20)]);

        Assert.True(shadow.ShouldApply("end_attack", 10));

        shadow.MarkApplied("end_attack", 10);
        Assert.False(shadow.ShouldApply("end_attack", 10));
    }

    [Fact]
    public void AnEmptyJournalStillPrimes()
    {
        // A new game's journal genuinely holds nothing; that read is a success, not a
        // failure, and syncing must start from it.
        JournalShadow shadow = new();
        List<JournalChange> advances = shadow.Observe([]);

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
    public void ObserveReturnsOnlyChanges()
    {
        JournalShadow shadow = new();

        List<JournalChange> first = shadow.Observe([new("quest_a", 10), new("quest_b", 5)]);
        Assert.Equal(2, first.Count);
        Assert.All(first, c => Assert.False(c.Removed));

        List<JournalChange> unchanged = shadow.Observe([new("quest_a", 10), new("quest_b", 5)]);
        Assert.Empty(unchanged);

        List<JournalChange> advanced = shadow.Observe([new("quest_a", 30), new("quest_b", 5)]);
        JournalChange only = Assert.Single(advanced);
        Assert.Equal("quest_a", only.QuestTag);
        Assert.Equal(30, only.State);
        Assert.False(only.Removed);
    }

    [Fact]
    public void ObserveReportsADecreaseTheSameAsAnAdvance()
    {
        // A stale read used to be indistinguishable from a genuine decrease; both compared
        // as "not higher" and were dropped. Direction no longer matters — only "did the
        // value actually change".
        JournalShadow shadow = new();
        shadow.Observe([new("quest", 30)]);

        List<JournalChange> lowered = shadow.Observe([new("quest", 10)]);
        JournalChange only = Assert.Single(lowered);
        Assert.Equal("quest", only.QuestTag);
        Assert.Equal(10, only.State);
        Assert.False(only.Removed);

        Assert.False(shadow.ShouldApply("quest", 10));
    }

    [Fact]
    public void ATagThatDisappearsFromTheLiveReadIsReportedAsRemoved()
    {
        // RemoveJournalQuestEntry takes the array entry out entirely — the next read simply
        // does not mention the tag any more. That has to surface as an explicit removal, not
        // silently vanish (the old code had no way to notice this at all).
        JournalShadow shadow = new();
        shadow.Observe([new("quest_a", 10), new("quest_b", 5)]);

        List<JournalChange> changes = shadow.Observe([new("quest_b", 5)]);
        JournalChange only = Assert.Single(changes);
        Assert.Equal("quest_a", only.QuestTag);
        Assert.True(only.Removed);

        // Reported exactly once — the next read (still absent) is not news any more.
        Assert.Empty(shadow.Observe([new("quest_b", 5)]));
    }

    [Fact]
    public void ARemovedTagCanBeReAddedLater()
    {
        JournalShadow shadow = new();
        shadow.Observe([new("quest_a", 10)]);
        shadow.Observe([]); // quest_a removed

        List<JournalChange> readded = shadow.Observe([new("quest_a", 5)]);
        JournalChange only = Assert.Single(readded);
        Assert.Equal("quest_a", only.QuestTag);
        Assert.Equal(5, only.State);
        Assert.False(only.Removed);
    }

    [Fact]
    public void ShouldRemoveAndMarkRemovedTrackTheApplySide()
    {
        JournalShadow shadow = new();
        shadow.Observe([new("end_attack", 20)]);

        // Nothing to remove for a tag the shadow never heard of.
        Assert.False(shadow.ShouldRemove("k_swg_bastila"));

        Assert.True(shadow.ShouldRemove("end_attack"));
        shadow.MarkRemoved("end_attack");

        // Idempotent: already removed, nothing left to do.
        Assert.False(shadow.ShouldRemove("end_attack"));

        // And the tag is once again a fresh apply target, exactly as if it had never
        // existed — MarkApplied after a removal must not be refused as "unchanged".
        Assert.True(shadow.ShouldApply("end_attack", 20));
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
    public void ARemovedEntryDoesNotEchoBackToTheSession()
    {
        // The removal twin of AnAppliedEntryDoesNotEchoBackToTheSession: after this
        // instance applies a peer's removal, the game's journal genuinely no longer has the
        // tag — and the next read (which never mentions it, same as before the tag ever
        // existed) must not announce it as a fresh removal a second time.
        JournalShadow shadow = new();
        shadow.Observe([new("end_attack", 20)]);
        shadow.MarkRemoved("end_attack");

        Assert.Empty(shadow.Observe([]));
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
}
