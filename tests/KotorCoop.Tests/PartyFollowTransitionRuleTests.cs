using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="PartyFollowTransitionRule.ShouldSkipThisFrame"/> is the whole guard behind round
/// 87's fix for round 85's warp-transition crash (0x4AA984, inside the engine's own
/// Moving_To_Path follow continuation — see the rule's own doc comment for the full disassembly
/// finding). Deliberately a one-line pure function, mirroring WorldLoadRuleTests's own shape, so
/// the assumption is pinned here rather than buried inside PartyFollowController's unsafe
/// engine-pointer code.
/// </summary>
public sealed class PartyFollowTransitionRuleTests
{
    [Fact]
    public void AnUnreadableWorldAlwaysSkips()
    {
        // The world is not readable at all this frame (no server, or a transition mid-flight) —
        // matches Odyssey.TryGetWorldEpoch's own false case. currentEpoch is meaningless here
        // (always 0 in practice) but the rule must not depend on that.
        Assert.True(PartyFollowTransitionRule.ShouldSkipThisFrame(
            worldEpochReadable: false, currentEpoch: 0, lastSeenEpoch: 0));
        Assert.True(PartyFollowTransitionRule.ShouldSkipThisFrame(
            worldEpochReadable: false, currentEpoch: 123, lastSeenEpoch: 123));
    }

    [Fact]
    public void TheFirstFrameOfANewEpochSkips()
    {
        // Round 85's own crash frame: TryGetWorldEpoch just started reporting the NEW module's
        // epoch, but this controller has not driven anything against it yet.
        Assert.True(PartyFollowTransitionRule.ShouldSkipThisFrame(
            worldEpochReadable: true, currentEpoch: 42, lastSeenEpoch: 7));
    }

    [Fact]
    public void TheVeryFirstLiveFrameSkipsBecauseZeroIsNotAValidEpoch()
    {
        // lastSeenEpoch starts at 0 (PartyFollowController's own field default), which is
        // deliberately treated as "never seen" rather than "already resumed" — a real epoch
        // hashing to exactly 0 is not something TryGetWorldEpoch produces in practice.
        Assert.True(PartyFollowTransitionRule.ShouldSkipThisFrame(
            worldEpochReadable: true, currentEpoch: 99, lastSeenEpoch: 0));
    }

    [Fact]
    public void AnUnchangedEpochNeverSkips()
    {
        // Ordinary play, every frame after the one-frame settle above: must not regress into
        // permanently skipping (that would silently kill the diagnostics/follow-drive entirely).
        Assert.False(PartyFollowTransitionRule.ShouldSkipThisFrame(
            worldEpochReadable: true, currentEpoch: 42, lastSeenEpoch: 42));
    }
}
