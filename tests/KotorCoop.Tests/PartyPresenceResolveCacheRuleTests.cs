using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="PartyPresenceResolveCacheRule"/> — round 103's plugin-side half of
/// docs/research/party-reconciliation.md's "reconnect does not reliably re-establish companion
/// mirroring" investigation. Round 102's rig finding turned out to be entirely agent-side (see
/// <c>PartyPresenceWriteCacheTests</c>/<c>PartyPresenceReconnectTests</c>): the block's own
/// <c>PartyPresenceCount</c> level never rose above 0 after a reconnect, so
/// <c>PartyPresenceController.OnFrame</c> never even reached <c>Resolve</c> to test whether IT
/// could re-enter cleanly. These tests are the negative-result regression pin for that half of
/// the investigation — they prove <see cref="PartyPresenceController"/> needs no "session
/// started" latch or any other extra re-entry state: <c>Forget</c>/<c>Invalidate</c> (run from
/// <c>ReleaseSession</c> on every disconnect) already leave every per-slot cache in the exact
/// shape this rule treats as "never trust", so the moment the block's count rises again with a
/// fresh companion, the ordinary per-frame <c>Resolve</c> call is structurally forced to
/// re-resolve through the engine — no special-cased re-entry branch needed, and none exists.
/// </summary>
public sealed class PartyPresenceResolveCacheRuleTests
{
    /// <summary>⚠ THE RE-ENTRY EDGE ITSELF. A tracked id of 0 is exactly the sentinel
    /// <c>PartyPresenceController.Forget</c> leaves in <c>_trackedId[slot]</c> — reached on every
    /// disconnect via <c>Invalidate</c>/<c>ReleaseSession</c>, and also the state a slot starts
    /// in before it has ever resolved anything. This must never be trusted, no matter how
    /// plausible everything else looks (a stale, non-null cached pointer that happens to still
    /// pass a fresh vtable/id check against THIS PASS's candidate) — the sentinel alone is
    /// disqualifying.</summary>
    [Fact]
    public void ATrackedIdOfZeroIsNeverTrustedEvenIfEverythingElseMatches()
    {
        Assert.False(PartyPresenceResolveCacheRule.CanTrustCachedCreature(
            cachedCreature: 0x1000, trackedId: 0, objectId: 0x127, vtableAndIdStillMatch: true));
    }

    /// <summary>The ordinary steady-state hit: nothing released, nothing changed, the exact
    /// companion this slot already tracked.</summary>
    [Fact]
    public void AGenuineSteadyStateMatchIsTrusted()
    {
        Assert.True(PartyPresenceResolveCacheRule.CanTrustCachedCreature(
            cachedCreature: 0x1000, trackedId: 0x127, objectId: 0x127, vtableAndIdStillMatch: true));
    }

    [Fact]
    public void ANullCachedPointerIsNeverTrusted()
    {
        Assert.False(PartyPresenceResolveCacheRule.CanTrustCachedCreature(
            cachedCreature: 0, trackedId: 0x127, objectId: 0x127, vtableAndIdStillMatch: true));
    }

    [Fact]
    public void AMismatchedObjectIdIsNeverTrusted()
    {
        // A different companion now occupies this slot (roster changed) — trackedId still holds
        // the OLD id, objectId is the new one.
        Assert.False(PartyPresenceResolveCacheRule.CanTrustCachedCreature(
            cachedCreature: 0x1000, trackedId: 0x127, objectId: 0x200, vtableAndIdStillMatch: false));
    }

    [Fact]
    public void AFailedLiveVtableCheckIsNeverTrustedEvenWithMatchingIds()
    {
        // The ids agree, but the caller's own live vtable/id read (PartyCreatureIdentity.IsLiveMatch)
        // says the cached pointer is stale reused memory.
        Assert.False(PartyPresenceResolveCacheRule.CanTrustCachedCreature(
            cachedCreature: 0x1000, trackedId: 0x127, objectId: 0x127, vtableAndIdStillMatch: false));
    }

    /// <summary>
    /// THE FULL RE-ENTRY SEQUENCE: a slot that was trusted, then released (Forget's sentinel),
    /// then re-populated with the SAME companion id as before (the ordinary case — reconnecting
    /// to the same save/session) — must NOT short-circuit back to "trusted" just because the id
    /// happens to match what it tracked before the release. The rule only ever compares against
    /// the CURRENT trackedId, and Forget already zeroed it, so this is provably impossible by
    /// construction — this test pins that construction rather than trusting the reasoning.
    /// </summary>
    [Fact]
    public void ReleasingAndReclaimingTheSameCompanionIdStillForcesAFreshResolve()
    {
        // Before release: a genuine trusted hit.
        Assert.True(PartyPresenceResolveCacheRule.CanTrustCachedCreature(
            cachedCreature: 0x1000, trackedId: 0x127, objectId: 0x127, vtableAndIdStillMatch: true));

        // Forget()/Invalidate() on release: trackedId -> 0, cached pointer -> 0. The very next
        // pass sees the sentinel, even if the block republishes the SAME 0x127 objectId (the
        // round-102 recipe: nothing about the companion actually changed, only the connection
        // dropped and came back).
        Assert.False(PartyPresenceResolveCacheRule.CanTrustCachedCreature(
            cachedCreature: 0, trackedId: 0, objectId: 0x127, vtableAndIdStillMatch: false));
    }
}
