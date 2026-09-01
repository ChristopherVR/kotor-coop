namespace KotorCoop.Protocol;

// Peer-dealt damage: against a host-owned enemy, and landed on a peer's phantom.
// Matches PacketCodec.Damage.cs.

/// <summary>
/// How much damage the sender has dealt to one enemy since its ids were last reassigned.
/// A running CUMULATIVE total of the RAW effect amounts, not a per-hit event and not a
/// health reading.
/// </summary>
public readonly record struct EnemyDamageEntry(uint ObjectId, uint CumulativeDamage);

/// <summary>
/// A peer's contribution to the owner's fights, sent each tick, unreliable.
///
/// This replaces the old health report, and the reason is worth keeping: health is a LEVEL
/// that must be sampled, and the owner's mirror writes its own value back over the peer's
/// copy several times a second, so a peer's hit had to win a race to be seen at all. A
/// cumulative sum of damage DEALT cannot be raced — the owner never reads it back out of
/// the game — and, unlike health, it says who dealt what.
///
/// <paramref name="Module"/> rides along because object ids are assigned per module load,
/// so the owner must not apply a total collected somewhere else. <paramref
/// name="Generation"/> is the sender's ledger epoch: it changes whenever the sender's own
/// totals restart (a module transition, a re-attached plugin), which is how the owner
/// knows a smaller number is a RESET rather than a stale datagram.
/// </summary>
public sealed record EnemyDamagePacket(
    uint PeerId, string Module, uint Generation, IReadOnlyList<EnemyDamageEntry> Damage);

/// <summary>One peer's cumulative damage against one enemy, as the owner sees it after
/// freshness and module filtering.</summary>
public readonly record struct PeerEnemyDamage(
    uint PeerId, uint Generation, uint ObjectId, uint CumulativeDamage);

/// <summary>
/// Damage that landed on a peer's phantom in the sender's game, addressed to the player
/// that phantom stands for.
///
/// <paramref name="CumulativeDamage"/> is a running total, never a per-hit amount: the
/// receiver applies the difference against what it has already taken, so a lost datagram
/// costs a tick of staleness rather than a missed hit and a duplicate applies nothing.
/// Same "level, not event" rule as enemy health. <paramref name="AttackerId"/> is the
/// engine object id of whoever LAST landed one — kept for an older reader during the one-
/// release overlap; <paramref name="Attackers"/> (protocol version 48) is the same total
/// split per attacker, up to <see cref="ProtocolConstants.MaxPhantomAttackerEntries"/>
/// entries, which is what lets the receiver bill a mixed local/mirrored window correctly
/// instead of wholesale to whichever id landed last. See
/// docs/research/peer-damage-ledger.md §7.
/// </summary>
public sealed record PhantomDamagePacket(
    uint FromPeerId, uint TargetPeerId, uint CumulativeDamage, uint AttackerId,
    IReadOnlyList<PhantomAttackerEntry> Attackers);
