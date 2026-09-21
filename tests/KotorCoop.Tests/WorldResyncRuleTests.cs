using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="WorldResyncRule"/> — when a client asks the host for a full copy of one world
/// store.
///
/// ⚠ THE CASE THAT MATTERS IS THE THIRD BLOCK. Every one of the four world channels used to
/// refuse to ask while this client still had an unacked op of its own, which sounds prudent
/// and is, for exactly one of them. An op is retired by an ACK, so on a lossy link that rule
/// makes "can I learn what the host did" wait on a round trip of an unrelated change of mine —
/// and a player who keeps changing things keeps something pending, so the wait has no end. The
/// symptom is a client that never adopts a single host-side flag, journal entry or emptied
/// container for as long as the bad link lasts, with nothing in its own state saying so.
/// </summary>
public class WorldResyncRuleTests
{
    private const int Interval = 500;

    // ---- Nothing to ask about ---------------------------------------------------------------

    [Fact]
    public void AMatchingTripwireAsksForNothing()
    {
        Assert.False(WorldResyncRule.ShouldRequest(
            forced: false, tripwireDiffers: false, localOpsPending: false,
            snapshotReplacesLocal: false, nowMs: 10_000, lastRequestMs: 0, Interval));
    }

    [Fact]
    public void ARequestJustMadeIsNotRepeatedInsideTheInterval()
    {
        Assert.False(WorldResyncRule.ShouldRequest(
            forced: false, tripwireDiffers: true, localOpsPending: false,
            snapshotReplacesLocal: false, nowMs: 10_499, lastRequestMs: 10_000, Interval));

        Assert.True(WorldResyncRule.ShouldRequest(
            forced: false, tripwireDiffers: true, localOpsPending: false,
            snapshotReplacesLocal: false, nowMs: 10_500, lastRequestMs: 10_000, Interval));
    }

    [Fact]
    public void AForcedAskNeedsNoDisagreementToJustifyIt()
    {
        // A reconnection asks for everything once: an outage is the one window where two
        // stores can diverge to the same version number, so an equal tripwire proves nothing.
        Assert.True(WorldResyncRule.ShouldRequest(
            forced: true, tripwireDiffers: false, localOpsPending: false,
            snapshotReplacesLocal: false, nowMs: 10_000, lastRequestMs: 0, Interval));
    }

    // ---- The gate, and who it is actually for ------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMergingChannelAsksEvenWithOurOwnOpsStillInFlight(bool forced)
    {
        // Flags, journal and container loot: the reply merges per entry by
        // (Serial, OriginPeerId) — or, for loot, re-offers idempotent removals — so there is
        // nothing of ours for it to undo, and waiting for an ack only loses us the host's news.
        Assert.True(WorldResyncRule.ShouldRequest(
            forced, tripwireDiffers: true, localOpsPending: true,
            snapshotReplacesLocal: false, nowMs: 10_000, lastRequestMs: 0, Interval));
    }

    [Fact]
    public void AReplacingChannelWaitsForItsOwnOpsToBeAcked()
    {
        // Ground items, and only ground items: GroundItemStore.ReplaceAll is destructive, so a
        // snapshot arriving while our own drop is in flight erases it, and the retransmit then
        // puts it back — the item flickers in and out of the world.
        Assert.False(WorldResyncRule.ShouldRequest(
            forced: false, tripwireDiffers: true, localOpsPending: true,
            snapshotReplacesLocal: true, nowMs: 10_000, lastRequestMs: 0, Interval));
    }

    [Fact]
    public void AReplacingChannelAsksTheMomentItsOpsAreAcked()
    {
        Assert.True(WorldResyncRule.ShouldRequest(
            forced: false, tripwireDiffers: true, localOpsPending: false,
            snapshotReplacesLocal: true, nowMs: 10_000, lastRequestMs: 0, Interval));
    }

    [Fact]
    public void EvenAForcedAskWaitsOnAReplacingChannel()
    {
        // The forced ask is not a licence to overwrite: it waits its turn rather than being
        // spent, which is why the caller only clears its force flag when the request goes out.
        Assert.False(WorldResyncRule.ShouldRequest(
            forced: true, tripwireDiffers: true, localOpsPending: true,
            snapshotReplacesLocal: true, nowMs: 10_000, lastRequestMs: 0, Interval));
    }
}
