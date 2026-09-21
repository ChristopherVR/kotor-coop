using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Covers the bounded availability half of roster convergence. The native call is intentionally
/// excluded from the test host; these cases prove which slot may be handed to it and that mask
/// garbage or an already-converged roster cannot cause a removal.
/// </summary>
public sealed class PartyRosterAvailabilityRuleTests
{
    [Fact]
    public void FindsClearedHostBitsOneSlotAtATime()
    {
        uint pending = PartyRosterAvailabilityRule.PendingRemovals(0b00000_0111u, 0b00000_0001u);

        Assert.Equal(0b00000_0110u, pending);
        Assert.Equal(1, PartyRosterAvailabilityRule.LowestPendingSlot(pending));
    }

    [Fact]
    public void ConvergedAvailabilityHasNoRemoval()
    {
        Assert.Equal(0u, PartyRosterAvailabilityRule.PendingRemovals(0x1FFu, 0x1FFu));
        Assert.Equal(-1, PartyRosterAvailabilityRule.LowestPendingSlot(0));
    }

    [Fact]
    public void IgnoresBitsOutsideTheNineEngineSlots()
    {
        uint pending = PartyRosterAvailabilityRule.PendingRemovals(uint.MaxValue, 0);

        Assert.Equal(0x1FFu, pending);
        Assert.Equal(0, PartyRosterAvailabilityRule.LowestPendingSlot(pending));
    }

    [Fact]
    public void HostRetainingAHighSlotDoesNotRemoveIt()
    {
        uint pending = PartyRosterAvailabilityRule.PendingRemovals(1u << 8, 1u << 8);

        Assert.Equal(0u, pending);
        Assert.Equal(-1, PartyRosterAvailabilityRule.LowestPendingSlot(pending));
    }

    [Fact]
    public void RetainedHostPartySeatDefersAvailabilityRemoval()
    {
        uint pending = PartyRosterAvailabilityRule.PendingRemovals(
            localAvailable: 1u << 3,
            hostAvailable: 0,
            hostParty: 1u << 3);

        Assert.Equal(0u, pending);
    }

    [Fact]
    public void NewlyLocalRecruitmentDefersAgainstAnOldHostSnapshot()
    {
        Assert.Equal(1u << 4, PartyRosterAvailabilityRule.NewlyAvailable(0, 1u << 4));
        Assert.Equal(0u, PartyRosterAvailabilityRule.ConfirmedHostRemovals(
            PartyRosterAvailabilityRule.UnknownMask, 0));
    }

    [Fact]
    public void StableLocalAvailabilityStillHonoursHostRemovalImmediately()
    {
        Assert.Equal(1u << 4, PartyRosterAvailabilityRule.ConfirmedHostRemovals(
            1u << 4, 0));
    }

    [Fact]
    public void HostReaddCancelsAPendingRemovalEdge()
    {
        uint edge = PartyRosterAvailabilityRule.UpdateHostRemovalEdges(
            1u << 4, 1u << 4, 0);
        Assert.Equal(1u << 4, edge);
        Assert.Equal(0u, PartyRosterAvailabilityRule.UpdateHostRemovalEdges(edge, 0, 1u << 4));
    }

    [Fact]
    public void InitialHostZeroDoesNotBecomeARemovalEdge()
    {
        Assert.Equal(0u, PartyRosterAvailabilityRule.ConfirmedHostRemovals(
            PartyRosterAvailabilityRule.UnknownMask, 0));
    }

    [Fact]
    public void LostInitialHostReadCannotInventARemoval()
    {
        uint edge = PartyRosterAvailabilityRule.UpdateHostRemovalEdges(
            0, PartyRosterAvailabilityRule.UnknownMask, 0);
        Assert.Equal(0u, edge);
        Assert.Equal(0u, PartyRosterAvailabilityRule.UpdateHostRemovalEdges(edge, 0, 0));
    }

    [Fact]
    public void ANewLocalRecruitmentCancelsAnOlderPendingHostRemoval()
    {
        uint hostRemoval = PartyRosterAvailabilityRule.ConfirmedHostRemovals(1u << 4, 0);
        uint localRecruitment = PartyRosterAvailabilityRule.NewlyAvailable(0, 1u << 4);

        Assert.Equal(0u, hostRemoval & ~localRecruitment);
    }
}
