namespace KotorCoop.Protocol;

public sealed record HelloPacket(string PlayerName, string Module, uint ClientNonce, ContentDigest Digest);

public sealed record WelcomePacket(
    uint AssignedPeerId, uint SessionId, byte TickRate, byte MaxPeers, ContentDigest Digest);

public sealed record RejectPacket(string Reason);

public sealed record StateUpdatePacket(uint PeerId, uint Sequence, long ClientTimeMs, PlayerState State);

/// <summary>One peer's entry inside a <see cref="WorldSnapshotPacket"/>.</summary>
public sealed record SnapshotEntry(uint PeerId, string Name, PlayerState State);

/// <summary>
/// The host's authoritative view, broadcast every tick. <paramref name="FlagVersion"/> and
/// <paramref name="ItemVersion"/> are the cheap tripwires that tell a client its world
/// flags or ground items are stale, so full state only crosses the wire when it has
/// actually diverged.
/// </summary>
public sealed record WorldSnapshotPacket(
    long ServerTimeMs,
    uint FlagVersion,
    uint ItemVersion,
    uint JournalVersion,
    IReadOnlyList<SnapshotEntry> Entries);

/// <summary>
/// A world progression change (door opened, boss dead, quest advanced). This is the
/// channel that separates "two players see each other" from actual co-op, and in a real
/// seamless mod it is where nearly all the difficulty lives: deciding which of the game's
/// thousands of booleans are global truth and which must stay per-player.
/// </summary>
public sealed record FlagOpPacket(uint OriginPeerId, uint OpSequence, uint FlagId, sbyte Value);

public sealed record FlagAckPacket(uint OpSequence);

public sealed record FlagSyncRequestPacket(uint PeerId, uint KnownVersion);

/// <summary>
/// Values are <c>sbyte</c>, not <c>bool</c>: KOTOR's world state is a mix of booleans and
/// signed-byte numbers (quest stages, counters), and splitting a number across derived
/// boolean ids would break the merge-upwards rule that keeps the two worlds convergent.
/// </summary>
public sealed record FlagSnapshotPacket(uint Version, IReadOnlyList<KeyValuePair<uint, sbyte>> Flags);

public sealed record PingPacket(uint PeerId, long SentTimeMs);

public sealed record PongPacket(long OriginalSentTimeMs, long ServerTimeMs);

public sealed record ByePacket(uint PeerId);

/// <summary>
/// An item lying on the ground, shared by every player in the module. The id is minted by
/// the dropping peer as (peerId &lt;&lt; 16 | counter) so ids never collide across peers.
/// </summary>
public sealed record GroundItem(uint ItemId, string Resref, string Module, Vector3f Position);

public enum ItemOpKind : byte
{
    Drop = 1,
    Remove = 2,
}

/// <summary>
/// A ground-item change: someone dropped an item, or someone picked one up. Reliable via
/// retransmit-until-ack, exactly like <see cref="FlagOpPacket"/>. For a Remove, only
/// <paramref name="ItemId"/> matters and the item fields are empty.
/// </summary>
public sealed record ItemOpPacket(
    uint OriginPeerId,
    uint OpSequence,
    ItemOpKind Kind,
    GroundItem Item);

public sealed record ItemAckPacket(uint OpSequence);

public sealed record ItemSyncRequestPacket(uint PeerId, uint KnownVersion);

public sealed record ItemSnapshotPacket(uint Version, IReadOnlyList<GroundItem> Items);

/// <summary>
/// One enemy's authoritative state. <paramref name="ObjectId"/> is the engine's own object
/// id, which is usable as a cross-instance key because both games load the same save and
/// the engine assigns ids deterministically in GIT order — verified live, the same Sith
/// soldier is 0x14 in both instances.
/// </summary>
public sealed record EnemyState(uint ObjectId, ushort CurrentHealth, ushort MaxHealth, bool Dead);

/// <summary>
/// The host's view of every enemy. Absolute health, never damage deltas: the engine
/// applies difficulty multipliers, temporary hit points and damage resistance per
/// instance, so replaying "took 7 damage" on the far side does not produce 7.
/// </summary>
public sealed record EnemySnapshotPacket(long ServerTimeMs, IReadOnlyList<EnemyState> Enemies);

/// <summary>
/// A client's local view of enemy health, sent each tick, unreliable. The module rides
/// along because enemy object ids reset across transitions — the host must never apply
/// a report from a client standing in a different (or stale) module.
/// </summary>
public sealed record EnemyReportPacket(uint PeerId, string Module, IReadOnlyList<EnemyState> Enemies);

/// <summary>
/// A peer's in-world content fingerprint, sent both ways once a save is loaded and
/// repeated periodically. <paramref name="GlobalsCatalogue"/> is a digest of the
/// global-variable catalogue (0 until resolved); a mismatch means the two saves' (type,
/// index) keys name different variables, which would silently corrupt quest state, so the
/// world-state and door channels are dropped when it differs.
/// </summary>
public sealed record WorldDigestPacket(uint PeerId, uint GlobalsCatalogue);

/// <summary>
/// A quest journal advance: entry <paramref name="QuestTag"/> reached
/// <paramref name="State"/>. States only rise (the engine itself refuses to lower one),
/// so the merge is a max and replays are harmless.
/// </summary>
public sealed record JournalOpPacket(uint OriginPeerId, uint OpSequence, string QuestTag, int State);

public sealed record JournalAckPacket(uint OpSequence);

public sealed record JournalSyncRequestPacket(uint PeerId, uint KnownVersion);

/// <summary>
/// The authoritative journal. A journal longer than one datagram is sent as several of
/// these in one burst, each carrying the same version.
/// </summary>
public sealed record JournalSnapshotPacket(uint Version, IReadOnlyList<KeyValuePair<string, int>> Entries);
