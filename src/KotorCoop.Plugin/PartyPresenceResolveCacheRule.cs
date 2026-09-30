namespace KotorCoop.Plugin;

/// <summary>
/// The pure "may I trust the cached creature pointer" half of
/// <see cref="PartyPresenceController.Resolve"/>, pulled out (round 103,
/// docs/research/party-reconciliation.md) so the RE-ENTRY EDGE it exists to protect is directly,
/// regression-testable without a live game process — the same split
/// <see cref="PartyCreatureIdentity"/> and <see cref="PartyFreezeRule"/> already use for their own
/// engine-adjacent decisions.
///
/// The question round 103 asked: does <see cref="PartyPresenceController"/> need a "session
/// started" latch or any other special-cased re-entry state to start mirroring again after a
/// disconnect/reconnect? Reading <see cref="PartyPresenceController.Forget"/> and
/// <see cref="PartyPresenceController.Invalidate"/> (the latter called from
/// <see cref="PartyPresenceController.ReleaseSession"/> via <see cref="PartyPresenceReleaseOrderRule"/>)
/// found the answer is NO — every per-slot cache field <see cref="PartyPresenceController.Resolve"/>
/// reads is already zeroed on release, and <c>OnFrame</c> has no other gate that would need
/// resetting. This rule is what makes that a proven invariant rather than an inference: a tracked
/// id of 0 (the exact sentinel <see cref="PartyPresenceController.Forget"/> leaves behind) can
/// NEVER be trusted as a cache hit, no matter what the cached pointer or vtable/id read looks
/// like — so the moment the block's roster valid bit (<c>PartyPresenceCount</c>) rises above 0
/// again with a fresh object id, <see cref="PartyPresenceController.Resolve"/> is structurally
/// forced through the full re-resolve path below, every time, with no session-boundary tracking
/// required at all.
/// </summary>
internal static class PartyPresenceResolveCacheRule
{
    /// <summary>
    /// Whether a cached creature pointer may be trusted without asking the engine to re-resolve
    /// it this pass.
    /// </summary>
    /// <param name="cachedCreature">The last resolved pointer for this slot, or 0 if none
    /// (construction, or the last <see cref="PartyPresenceController.Forget"/>).</param>
    /// <param name="trackedId">The object id the cached pointer was last resolved against — 0 is
    /// the sentinel <see cref="PartyPresenceController.Forget"/> leaves behind, and is therefore
    /// NEVER treated as a match even if <paramref name="objectId"/> also somehow reads 0 (a
    /// candidate of 0 is already refused earlier in <see cref="PartyPresenceController.DriveOne"/>,
    /// so this rule does not need to special-case it, but it never trusts a zero pairing either
    /// way).</param>
    /// <param name="objectId">This pass's candidate object id, from the block/roster.</param>
    /// <param name="vtableAndIdStillMatch">The caller's own live read of the cached pointer's
    /// vtable and id — <see cref="PartyCreatureIdentity.IsLiveMatch"/>'s result, kept as a
    /// parameter here (not re-derived) since only the caller can do that unsafe read.</param>
    public static bool CanTrustCachedCreature(
        nint cachedCreature, uint trackedId, uint objectId, bool vtableAndIdStillMatch) =>
        cachedCreature != 0
        && trackedId != 0
        && trackedId == objectId
        && vtableAndIdStillMatch;
    public static bool CanTrustCurrentCreature(
        nint cachedCreature, nint currentCreature, uint trackedId, uint objectId, bool vtableAndIdStillMatch) =>
        cachedCreature == currentCreature
        && CanTrustCachedCreature(cachedCreature, trackedId, objectId, vtableAndIdStillMatch);
}
