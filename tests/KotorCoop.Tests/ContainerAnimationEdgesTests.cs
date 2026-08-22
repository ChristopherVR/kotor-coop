using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure diff <c>InteractionLockController.ReconcileContainerAnimations</c> (plugin) now
/// defers to for "which containers need their open/close animation played this pass" — the
/// property under test is "exactly once per edge," docs/research/container-sync.md's own build
/// requirement.
/// </summary>
public class ContainerAnimationEdgesTests
{
    private readonly List<uint> _toOpen = [];
    private readonly List<uint> _toClose = [];

    [Fact]
    public void ANewlyHeldContainerNeedsItsOpenAnimation()
    {
        ContainerAnimationEdges.Diff(
            currentlyHeld: [0x10],
            previouslyMirrored: [],
            _toOpen, _toClose);

        Assert.Equal([0x10u], _toOpen);
        Assert.Empty(_toClose);
    }

    [Fact]
    public void AReleasedContainerNeedsItsCloseAnimation()
    {
        ContainerAnimationEdges.Diff(
            currentlyHeld: [],
            previouslyMirrored: [0x10],
            _toOpen, _toClose);

        Assert.Empty(_toOpen);
        Assert.Equal([0x10u], _toClose);
    }

    [Fact]
    public void AStillHeldContainerNeedsNeitherAnimationAgain()
    {
        // The property that matters: an id held BOTH passes is not re-opened every ten frames.
        ContainerAnimationEdges.Diff(
            currentlyHeld: [0x10],
            previouslyMirrored: [0x10],
            _toOpen, _toClose);

        Assert.Empty(_toOpen);
        Assert.Empty(_toClose);
    }

    [Fact]
    public void OneContainerCanOpenWhileAnotherClosesInTheSamePass()
    {
        // A peer released 0x10 and picked up 0x20 between two of this instance's own passes.
        ContainerAnimationEdges.Diff(
            currentlyHeld: [0x20],
            previouslyMirrored: [0x10],
            _toOpen, _toClose);

        Assert.Equal([0x20u], _toOpen);
        Assert.Equal([0x10u], _toClose);
    }

    [Fact]
    public void ADuplicateIdInTheBroadcastStillOpensOnlyOnce()
    {
        // A malformed or stale broadcast repeating an id must not play the animation twice.
        ContainerAnimationEdges.Diff(
            currentlyHeld: [0x10, 0x10],
            previouslyMirrored: [],
            _toOpen, _toClose);

        Assert.Equal([0x10u], _toOpen);
    }

    [Fact]
    public void IdZeroIsNeverTreatedAsAHeldContainer()
    {
        // 0 is the "empty slot" sentinel throughout the wire/block, never a real object id.
        ContainerAnimationEdges.Diff(
            currentlyHeld: [0u],
            previouslyMirrored: [],
            _toOpen, _toClose);

        Assert.Empty(_toOpen);
    }

    [Fact]
    public void OutputListsAreClearedEachCallRatherThanAccumulated()
    {
        ContainerAnimationEdges.Diff(currentlyHeld: [0x10], previouslyMirrored: [], _toOpen, _toClose);
        Assert.Single(_toOpen);

        // A second, unrelated diff must not still carry the first call's result.
        ContainerAnimationEdges.Diff(currentlyHeld: [], previouslyMirrored: [], _toOpen, _toClose);
        Assert.Empty(_toOpen);
    }
}
