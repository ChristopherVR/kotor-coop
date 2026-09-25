using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure decision behind telling a host-opened party-select screen's confirm from its
/// cancel, once it closes: does the party-table roster differ from the snapshot taken at open?
/// See <see cref="PartySelectOutcomeRule"/>'s own doc comment for the known limitation
/// (re-confirming the IDENTICAL party reads as a cancel).
/// </summary>
public class PartySelectOutcomeRuleTests
{
    [Fact]
    public void StillOpenIsAlwaysPendingRegardlessOfTheRoster()
    {
        Assert.Equal(
            PartySelectOutcomeCode.Pending,
            PartySelectOutcomeRule.Decide(panelStillOpen: true, rosterMaskAtOpen: 0x11, rosterMaskNow: 0x22));
    }

    [Fact]
    public void StillOpenWithAnUnchangedRosterIsAlsoPending()
    {
        Assert.Equal(
            PartySelectOutcomeCode.Pending,
            PartySelectOutcomeRule.Decide(panelStillOpen: true, rosterMaskAtOpen: 0x11, rosterMaskNow: 0x11));
    }

    [Fact]
    public void ClosedWithAChangedRosterIsConfirmed()
    {
        Assert.Equal(
            PartySelectOutcomeCode.Confirmed,
            PartySelectOutcomeRule.Decide(panelStillOpen: false, rosterMaskAtOpen: 0x11, rosterMaskNow: 0x22));
    }

    [Fact]
    public void ClosedWithAnUnchangedRosterIsCancelled()
    {
        Assert.Equal(
            PartySelectOutcomeCode.Cancelled,
            PartySelectOutcomeRule.Decide(panelStillOpen: false, rosterMaskAtOpen: 0x11, rosterMaskNow: 0x11));
    }

    [Fact]
    public void ClosedWithBothRostersZeroIsCancelled()
    {
        // The "unreadable roster" fallback (the controller passes the baseline back when the
        // second read fails) must not be misread as a confirm.
        Assert.Equal(
            PartySelectOutcomeCode.Cancelled,
            PartySelectOutcomeRule.Decide(panelStillOpen: false, rosterMaskAtOpen: 0, rosterMaskNow: 0));
    }
}
