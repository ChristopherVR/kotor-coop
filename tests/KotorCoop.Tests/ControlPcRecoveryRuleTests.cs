using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ControlPcRecoveryRuleTests
{
    [Fact]
    public void ClientIdIsDerivedFromTheObservedServerPcId()
    {
        Assert.Equal(0xFFFFFFFEu, ControlPcRecoveryRule.ToClientId(0x7FFFFFFEu));
        Assert.Equal(0xFFFFFFFFu, ControlPcRecoveryRule.ToClientId(0x7FFFFFFFu));
    }

    [Fact]
    public void InvalidOrUnverifiedServerIdentityCannotProduceAWrite()
    {
        ControlPcRecoveryRule.Slot[] slots =
        [
            new(0x80000134u, true),
            new(0x80000119u, false),
            new(0x80000109u, false),
        ];

        Assert.False(ControlPcRecoveryRule.Plan(slots, 1, 2, 0x7F000000u, true).CanWrite);
        Assert.False(ControlPcRecoveryRule.Plan(slots, 1, 2, 0x7FFFFFFEu, false).CanWrite);
    }

    [Fact]
    public void RecoveryUsesTheFirstStaleSlotWithinPcPlusTablePartyCapacity()
    {
        ControlPcRecoveryRule.Slot[] slots =
        [
            new(0x80000134u, true),
            new(0x80000119u, false),
            new(0x80000109u, false),
        ];

        var plan = ControlPcRecoveryRule.Plan(slots, 1, 2, 0x7FFFFFFEu, true);

        Assert.Equal(-1, plan.ExistingIndex);
        Assert.Equal(1, plan.SlotToWrite);
        Assert.Equal(2, plan.NewCount);
        Assert.Equal(0xFFFFFFFEu, plan.ClientId);
    }

    [Fact]
    public void ActiveCreatureIsNeverOverwrittenAndThirdStaleSlotIsOutOfBounds()
    {
        ControlPcRecoveryRule.Slot[] slots =
        [
            new(0x80000134u, true),
            new(0x80000119u, true),
            new(0x80000109u, false),
        ];

        var plan = ControlPcRecoveryRule.Plan(slots, 1, 2, 0x7FFFFFFEu, true);

        Assert.False(plan.CanWrite);
    }

    [Fact]
    public void DuplicateLiveIdBeyondCountCanBeReclaimed()
    {
        ControlPcRecoveryRule.Slot[] slots =
        [
            new(0x80000165u, true),
            new(0x80000165u, true),
            new(0x8000015Fu, false),
        ];

        var plan = ControlPcRecoveryRule.Plan(slots, 1, 2, 0x7FFFFFFEu, true);

        Assert.Equal(1, plan.SlotToWrite);
        Assert.Equal(2, plan.NewCount);
    }

    [Fact]
    public void DistinctLiveIdBeyondCountIsStillProtected()
    {
        ControlPcRecoveryRule.Slot[] slots =
        [
            new(0x80000165u, true),
            new(0x80000134u, true),
            new(0x8000015Fu, false),
        ];

        var plan = ControlPcRecoveryRule.Plan(slots, 1, 2, 0x7FFFFFFEu, true);

        Assert.False(plan.CanWrite);
    }

    [Fact]
    public void ExistingDerivedPcIdIsReportedWithoutASecondWrite()
    {
        ControlPcRecoveryRule.Slot[] slots =
        [
            new(0x80000134u, true),
            new(0xFFFFFFFEu, false),
            new(0x80000109u, false),
        ];

        var plan = ControlPcRecoveryRule.Plan(slots, 1, 2, 0x7FFFFFFEu, true);

        Assert.Equal(1, plan.ExistingIndex);
        Assert.False(plan.CanWrite);
    }

    [Fact]
    public void InvalidCountsFailClosed()
    {
        ControlPcRecoveryRule.Slot[] slots =
        [
            new(0x80000134u, true),
            new(0x80000119u, false),
            new(0x80000109u, false),
        ];

        Assert.False(ControlPcRecoveryRule.Plan(slots, -1, 2, 0x7FFFFFFEu, true).CanWrite);
        Assert.False(ControlPcRecoveryRule.Plan(slots, 1, 0, 0x7FFFFFFEu, true).CanWrite);
        Assert.False(ControlPcRecoveryRule.Plan(slots, 4, 3, 0x7FFFFFFEu, true).CanWrite);
    }

    [Fact]
    public void RotationChecksEveryEntryThatWillRemainUnderTheRaisedCount()
    {
        ControlPcRecoveryRule.Slot[] slots =
        [
            new(0x80000134u, true),
            new(0x80000119u, false),
            new(0xFFFFFFFEu, false),
        ];

        // Moving the not-yet-written PC at index 2 to the front exposes only
        // [PC, member 0] at count 2.  The stale member 1 is shifted out and is
        // therefore safe; omitting the replacement must fail closed.
        Assert.True(ControlPcRecoveryRule.CanExposeAfterFrontRotation(
            slots, targetIndex: 2, exposedCount: 2, replacementClientId: 0xFFFFFFFEu));
        Assert.False(ControlPcRecoveryRule.CanExposeAfterFrontRotation(
            slots, targetIndex: 2, exposedCount: 2));

        // A stale entry retained by the resulting count is never accepted merely
        // because the selected target itself resolves.
        Assert.False(ControlPcRecoveryRule.CanExposeAfterFrontRotation(
            slots, targetIndex: 2, exposedCount: 3, replacementClientId: 0xFFFFFFFEu));
    }
}
