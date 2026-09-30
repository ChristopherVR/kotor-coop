namespace KotorCoop.Protocol;

// A client's damage TAKEN by a companion `companion_local_combat` has released to fight
// locally, forwarded to the host so the companion's real, authoritative body actually takes
// it. The companion sibling of EnemiesPackets.cs's EnemyDamage half — same "cumulative total,
// never health" shape, same reason (a level is racy against the owner's own mirror write; a
// monotonic sum is never read back out of the game, so nothing can erase it). Matches
// PacketCodec.CompanionDamage.cs. See docs/research/companion-state-sync.md §13 and
// docs/research/peer-damage-ledger.md.

/// <summary>
/// One companion's cumulative damage taken, keyed by NPC ROSTER SLOT rather than object id.
///
/// Unlike an enemy (<see cref="EnemyDamageEntry"/>), a companion's identity does not need a
/// module label: <paramref name="RosterSlotPlusOne"/> (1..9, 0 unused — the same "plus one"
/// convention <c>PartyPresenceView.RosterSlotPlusOne</c> already uses on the plugin side) names
/// the same companion for the whole session, transition or not, so there is nothing to
/// invalidate when the world changes.
/// </summary>
public readonly record struct CompanionDamageEntry(uint RosterSlotPlusOne, uint CumulativeDamage, uint OccupantGeneration = 0);

/// <summary>
/// A client's report of what its own engine's damage pipeline has actually done to each
/// companion <c>companion_local_combat</c> released to it, since <paramref name="Generation"/>
/// last changed.
///
/// Sent every tick, unreliable — a lost datagram costs one tick of staleness, never a missed
/// hit, because the total is cumulative and the host applies the DIFFERENCE against what it
/// has already spent. <paramref name="Generation"/> is the sender's own reset marker (the
/// plugin's watch restarting: a module change, a re-attach, a fresh release after a reclaim),
/// exactly like <see cref="EnemyDamagePacket.Generation"/> — it is how the host tells a
/// legitimate restart from a stale, reordered datagram.
/// </summary>
public sealed record CompanionDamagePacket(
    uint PeerId, uint Generation, IReadOnlyList<CompanionDamageEntry> Damage, uint Incarnation = 0, uint HostSessionId = 0,
    uint HostLoadSerial = 0, uint HostNativeEpoch = 0);

/// <summary>One peer's cumulative damage against one of ITS OWN companions, as the host sees
/// it after freshness filtering.</summary>
public readonly record struct PeerCompanionDamage(
    uint PeerId, uint Generation, uint RosterSlotPlusOne, uint CumulativeDamage, uint OccupantGeneration = 0, uint Incarnation = 0, uint HostLoadSerial = 0, uint HostNativeEpoch = 0);
