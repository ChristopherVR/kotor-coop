using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The rule these enforce: a peer keeps its shared-block slot. Everything the in-process
/// plugin derives from a slot is indexed by slot alone — the body, its name, its gear, its
/// cumulative damage — so a slot that changes hands hands all of that over with it.
/// </summary>
public class PeerSlotMapTests
{
    [Fact]
    public void AssignsInOrderAndIsIdempotent()
    {
        PeerSlotMap map = new(8);
        uint[] present = [1, 2, 3];

        Assert.Equal(0, map.Assign(1, present));
        Assert.Equal(1, map.Assign(2, present));
        Assert.Equal(2, map.Assign(3, present));

        // Re-publishing the same peers must not move anybody.
        Assert.Equal(0, map.Assign(1, present));
        Assert.Equal(1, map.Assign(2, present));
        Assert.Equal(2, map.Assign(3, present));

        Assert.Equal(1u, map.Owner(0));
        Assert.Equal(3u, map.Owner(2));
        Assert.Equal(1, map.SlotOf(2));
        Assert.Equal(-1, map.SlotOf(99));
    }

    /// <summary>
    /// The bug this file exists for. Three players, the middle one leaves, and the survivor
    /// must NOT slide into the leaver's slot — the plugin would keep driving the leaver's
    /// body, still wearing the leaver's name and gear, and bill the survivor for the damage
    /// it had taken.
    /// </summary>
    [Fact]
    public void DepartureDoesNotRenumberTheSurvivors()
    {
        PeerSlotMap map = new(8);
        uint[] all = [1, 2, 3];

        map.Assign(1, all);
        map.Assign(2, all);
        map.Assign(3, all);

        uint[] afterTwoLeaves = [1, 3];
        Assert.Equal(0, map.Assign(1, afterTwoLeaves));
        Assert.Equal(2, map.Assign(3, afterTwoLeaves));

        // Peer 2's slot is left empty rather than backfilled.
        Assert.Equal(2u, map.Owner(1));
        Assert.Equal(3u, map.Owner(2));
    }

    /// <summary>
    /// A peer that walks into another module drops out of the published list for as long as
    /// it is away (the list is module-filtered before it reaches the block). Its slot must
    /// survive that, or its phantom is destroyed and respawned at every doorway.
    /// </summary>
    [Fact]
    public void SlotSurvivesAPeerBeingTemporarilyFilteredOut()
    {
        PeerSlotMap map = new(8);
        map.Assign(1, [1, 2]);
        map.Assign(2, [1, 2]);

        // Two ticks with peer 2 absent.
        map.Assign(1, [1]);
        map.Assign(1, [1]);

        Assert.Equal(1, map.Assign(2, [1, 2]));
    }

    [Fact]
    public void ReclaimsASlotFromAnAbsentHolderOnlyWhenFull()
    {
        PeerSlotMap map = new(2);
        map.Assign(10, [10, 11]);
        map.Assign(11, [10, 11]);

        // 11 is gone; 12 arrives and there is nowhere else to put it.
        Assert.Equal(1, map.Assign(12, [10, 12]));
        Assert.Equal(12u, map.Owner(1));
    }

    [Fact]
    public void RefusesWhenEverySlotHoldsAPresentPeer()
    {
        PeerSlotMap map = new(2);
        map.Assign(10, [10, 11, 12]);
        map.Assign(11, [10, 11, 12]);

        Assert.Equal(-1, map.Assign(12, [10, 11, 12]));
    }

    [Fact]
    public void ClearReleasesEverything()
    {
        PeerSlotMap map = new(4);
        map.Assign(5, [5]);
        Assert.Equal(5u, map.Owner(0));

        map.Clear();

        Assert.Equal(0u, map.Owner(0));
        Assert.Equal(-1, map.SlotOf(5));
    }

    [Fact]
    public void OutOfRangeAndZeroAreRefusedRatherThanThrowing()
    {
        PeerSlotMap map = new(4);

        Assert.Equal(-1, map.Assign(0, []));
        Assert.Equal(0u, map.Owner(-1));
        Assert.Equal(0u, map.Owner(4));
        Assert.Equal(-1, map.SlotOf(0));
    }
}
