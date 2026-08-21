using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The decision <see cref="Odyssey.SetJournalState"/> makes after walking the live journal
/// array: add, skip, or remove-then-add. Pinned here because the live pass 2026-08-22
/// (docs/research/quest-sync.md) found the obvious approach — passing
/// <c>bOverrideHigher=1</c> straight through to <c>CSWSJournal::SetState</c> for a decrease —
/// reports success without the array's own state field actually moving. This rule is what
/// replaced it, and it is worth pinning on its own: the three-way branch (a fresh tag, an
/// advance, a decrease) is exactly the kind of thing that silently regresses to "always add"
/// if someone touches the caller without noticing the decrease case exists.
/// </summary>
public class JournalApplyRuleTests
{
    [Fact]
    public void ANonExistentTagIsAlwaysAnAddRegardlessOfTheTargetState()
    {
        Assert.Equal(
            JournalApplyAction.Add,
            JournalApplyRule.Decide(hasExisting: false, existingState: 0, targetState: 50));

        // Even a "target 0" fresh tag is still an add — there is nothing to remove.
        Assert.Equal(
            JournalApplyAction.Add,
            JournalApplyRule.Decide(hasExisting: false, existingState: 0, targetState: 0));
    }

    [Fact]
    public void AnExistingEntryAtExactlyTheTargetIsSkipped()
    {
        Assert.Equal(
            JournalApplyAction.Skip,
            JournalApplyRule.Decide(hasExisting: true, existingState: 20, targetState: 20));
    }

    [Fact]
    public void AGenuineAdvanceOnAnExistingEntryIsAPlainAdd()
    {
        // The path ordinary quest progression has always used — not in question, so it must
        // stay Add, not RemoveThenAdd (removal is unnecessary overhead and would lose the
        // engine's own entry-added date/order for no reason).
        Assert.Equal(
            JournalApplyAction.Add,
            JournalApplyRule.Decide(hasExisting: true, existingState: 10, targetState: 20));
    }

    [Fact]
    public void AGenuineDecreaseOnAnExistingEntryIsRemoveThenAdd()
    {
        // SetState's own bOverrideHigher=1 path was live-confirmed 2026-08-22 to report
        // success without moving the array's state field — remove-then-add composes two
        // primitives that ARE each live-verified instead.
        Assert.Equal(
            JournalApplyAction.RemoveThenAdd,
            JournalApplyRule.Decide(hasExisting: true, existingState: 50, targetState: 5));
    }

    [Fact]
    public void ADecreaseByExactlyOneIsStillRemoveThenAddNotAdd()
    {
        // The boundary case: the rule is a strict greater-than on existing vs target, not a
        // "meaningfully different" threshold — off-by-one here would silently drop back to
        // the disproven direct path for the smallest possible decrease.
        Assert.Equal(
            JournalApplyAction.RemoveThenAdd,
            JournalApplyRule.Decide(hasExisting: true, existingState: 21, targetState: 20));
    }

    [Fact]
    public void AnAdvanceByExactlyOneIsStillAdd()
    {
        Assert.Equal(
            JournalApplyAction.Add,
            JournalApplyRule.Decide(hasExisting: true, existingState: 19, targetState: 20));
    }
}
