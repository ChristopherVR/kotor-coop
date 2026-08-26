namespace KotorCoop.Protocol;

// The world-flag channel. Matches PacketCodec.Flags.cs.

/// <summary>
/// A world progression change (door opened, boss dead, quest advanced, or — just as
/// legitimately — a counter decremented or a boolean cleared back to false). This is the
/// channel that separates "two players see each other" from actual co-op, and in a real
/// seamless mod it is where nearly all the difficulty lives: deciding which of the game's
/// thousands of booleans are global truth and which must stay per-player.
///
/// <paramref name="Serial"/> is this ORIGIN's own Lamport clock reading at the moment the
/// change was made — see <c>EventFlagStore</c>'s remarks for why a per-key value comparison
/// alone cannot tell a real advance from a stale replay once decreases are legal. Carried
/// alongside <paramref name="OriginPeerId"/> (already on the wire) so the receiver's
/// (Serial, OriginPeerId) tie-break is deterministic without a second round trip.
/// </summary>
public sealed record FlagOpPacket(
    uint OriginPeerId, uint OpSequence, uint FlagId, sbyte Value, uint Serial);

public sealed record FlagAckPacket(uint OpSequence);

public sealed record FlagSyncRequestPacket(uint PeerId, uint KnownVersion);

/// <summary>One entry inside a <see cref="FlagSnapshotPacket"/>: a flag's current value plus
/// the (Serial, OriginPeerId) pair that won it, so a resyncing peer can apply the SAME
/// last-writer-wins comparison a live op would have used rather than blindly overwriting.</summary>
public readonly record struct FlagSnapshotEntry(uint FlagId, sbyte Value, uint Serial, uint OriginPeerId);

/// <summary>
/// Values are <c>sbyte</c>, not <c>bool</c>: KOTOR's world state is a mix of booleans and
/// signed-byte numbers (quest stages, counters), and splitting a number across derived
/// boolean ids would break the last-writer-wins rule that keeps the two worlds convergent.
/// </summary>
public sealed record FlagSnapshotPacket(uint Version, IReadOnlyList<FlagSnapshotEntry> Flags);
