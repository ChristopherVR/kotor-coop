namespace KotorCoop.Protocol;

// Shared progression: the host's broadcast and a peer's report. Matches
// PacketCodec.Progression.cs.

/// <summary>
/// The host's absolute shared progression, broadcast each tick. Party <paramref name="Gold"/>
/// and the PC's <paramref name="Experience"/> are whole values, not deltas — each instance
/// grants XP/gold locally on every kill, so a client mirrors the host's totals rather than
/// adding its own (which would double-count).
/// </summary>
public sealed record ProgressionSnapshotPacket(long ServerTimeMs, uint Gold, uint Experience);

/// <summary>
/// client -> host: this peer's contribution to the shared purse, and its own experience.
///
/// <paramref name="GoldEarned"/> and <paramref name="GoldSpent"/> are running CUMULATIVE
/// totals since this peer's ledger last restarted, never a balance — the same discipline as
/// <see cref="EnemyDamagePacket"/>, and for the same reason: the host applies each increase
/// once, so the channel is order-independent, survives a lost datagram and cannot
/// double-apply a repeat. <paramref name="Generation"/> changes whenever the sender's totals
/// restart (a re-attached agent), which is how the host tells a RESET from a stale datagram.
///
/// <paramref name="Experience"/> is absolute and merges upward by max — see
/// <see cref="PacketType.ProgressionReport"/> for why XP cannot be a delta.
/// </summary>
public sealed record ProgressionReportPacket(
    uint PeerId, uint Generation, uint GoldEarned, uint GoldSpent, uint Experience);

/// <summary>One peer's progression contribution, as the host sees it after freshness
/// filtering.</summary>
public readonly record struct PeerProgression(
    uint PeerId, uint Generation, uint GoldEarned, uint GoldSpent, uint Experience);
