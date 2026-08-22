using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure per-object claim/release decision <c>InteractionLockStore.Claim</c> (session) now
/// defers to — see <see cref="ContainerUseRule"/>'s own remarks for why it is named for the
/// container use case even though the SAME rule also governs conversation locks.
/// </summary>
public class ContainerUseRuleTests
{
    // ---- MayClaim -----------------------------------------------------------------------

    [Fact]
    public void AFreeObjectIsClaimedByAnyone()
    {
        Assert.True(ContainerUseRule.MayClaim(currentHolderPeerId: 0, currentHolderFresh: false, requestingPeerId: 7));
    }

    [Fact]
    public void PeerZeroIsNeverGrantedAnything()
    {
        Assert.False(ContainerUseRule.MayClaim(currentHolderPeerId: 0, currentHolderFresh: false, requestingPeerId: 0));
    }

    [Fact]
    public void FirstFreshClaimWinsAgainstAContestingPeer()
    {
        Assert.False(ContainerUseRule.MayClaim(currentHolderPeerId: 1, currentHolderFresh: true, requestingPeerId: 2));
    }

    [Fact]
    public void ReEntrancyTheCurrentHolderMayReClaimTheirOwnHold()
    {
        Assert.True(ContainerUseRule.MayClaim(currentHolderPeerId: 1, currentHolderFresh: true, requestingPeerId: 1));
    }

    [Fact]
    public void AnExpiredHoldIsClaimableByAnyoneIncludingTheOldHolder()
    {
        // currentHolderFresh: false is the store's own "lease ran out" reading — the object is
        // free again, whoever asks, including the peer who used to hold it.
        Assert.True(ContainerUseRule.MayClaim(currentHolderPeerId: 1, currentHolderFresh: false, requestingPeerId: 1));
        Assert.True(ContainerUseRule.MayClaim(currentHolderPeerId: 1, currentHolderFresh: false, requestingPeerId: 2));
    }

    // ---- MayRelease ----------------------------------------------------------------------

    [Fact]
    public void OnlyTheFreshHolderMayRelease()
    {
        Assert.True(ContainerUseRule.MayRelease(currentHolderPeerId: 1, currentHolderFresh: true, requestingPeerId: 1));
    }

    [Fact]
    public void AStrayReleaseFromSomeoneElseIsANoOp()
    {
        Assert.False(ContainerUseRule.MayRelease(currentHolderPeerId: 1, currentHolderFresh: true, requestingPeerId: 2));
    }

    [Fact]
    public void ReleasingWithNoFreshHoldIsANoOp()
    {
        // The ordinary shape of "I am not interacting with anything right now," sent every
        // tick alongside a live claim — not an error, and not something that should ever
        // remove an entry that is not really there (or has already expired).
        Assert.False(ContainerUseRule.MayRelease(currentHolderPeerId: 0, currentHolderFresh: false, requestingPeerId: 1));
        Assert.False(ContainerUseRule.MayRelease(currentHolderPeerId: 1, currentHolderFresh: false, requestingPeerId: 1));
    }
}
