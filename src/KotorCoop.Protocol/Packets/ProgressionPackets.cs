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

// ---- companion experience (2026-08-27, docs/research/companion-state-sync.md) -------------
//
// The companion sibling of the PC's own experience above, keyed by NPC roster slot (0..8,
// ProtocolConstants.PartyRosterSlots) rather than carried on the single scalar the PC uses.
// Same shape and the same reason: both engines already grant kill XP to every ACTIVE party
// member independently (CSWPartyTable::DistributeExperience hands the FULL amount to each
// member in the active-slot array, not just the PC — see docs/research/progression-sync.md
// §2), so this is absolute and merges upward by max, never a delta. A bit in the present
// mask means that slot's entry in the array is meaningful; a companion with no live body
// (benched, not yet recruited on this instance) has no bit and its array entry is 0.

/// <summary>
/// host -&gt; clients: absolute experience for every currently-active companion, keyed by NPC
/// roster slot. <paramref name="Xp"/> is fixed-length
/// (<see cref="ProtocolConstants.PartyRosterSlots"/>); only the slots set in
/// <paramref name="PresentMask"/> are meaningful.
/// </summary>
public sealed record CompanionXpSnapshotPacket(long ServerTimeMs, ushort PresentMask, uint[] Xp,
    uint HostSessionId = 0, uint HostLoadSerial = 0, uint HostNativeEpoch = 0, uint[]? OccupantGenerations = null);

/// <summary>
/// client -&gt; host: this peer's own reading of each active companion's experience, same
/// packing as <see cref="CompanionXpSnapshotPacket"/>.
/// </summary>
public sealed record CompanionXpReportPacket(uint PeerId, ushort PresentMask, uint[] Xp,
    uint HostSessionId = 0, uint HostLoadSerial = 0, uint HostNativeEpoch = 0, uint[]? OccupantGenerations = null);

/// <summary>One peer's companion-experience report, as the host sees it after freshness
/// filtering.</summary>
public readonly record struct PeerCompanionXp(uint PeerId, ushort PresentMask, uint[] Xp);
