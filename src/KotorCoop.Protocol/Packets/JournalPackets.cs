namespace KotorCoop.Protocol;

// The quest-journal channel. Matches PacketCodec.Journal.cs.

/// <summary>
/// A quest journal change: entry <paramref name="QuestTag"/> reached <paramref name="State"/>
/// — or, when <paramref name="Removed"/> is set, the tag was taken out of the journal
/// entirely (KOTOR's own <c>RemoveJournalQuestEntry</c>; <paramref name="State"/> is unused
/// and sent as 0). Real quest content does both: a stage number can be pushed back down by a
/// script that re-offers an earlier step, and a handful of scripts (12 found by the module
/// scan behind docs/research/quest-sync.md's enumeration — <c>danm13/k_pdan_trig1303</c>,
/// <c>ebo_m41aa/k_pebn_remove</c> among them) call <c>RemoveJournalQuestEntry</c> outright.
/// <paramref name="Serial"/> is the origin's own Lamport clock reading, exactly like
/// <see cref="FlagOpPacket.Serial"/> — see <c>JournalStore</c>'s remarks for the comparison
/// this makes possible once the merge is no longer a bare max.
/// </summary>
public sealed record JournalOpPacket(
    uint OriginPeerId, uint OpSequence, string QuestTag, int State, uint Serial, bool Removed = false);

public sealed record JournalAckPacket(uint OpSequence);

public sealed record JournalSyncRequestPacket(uint PeerId, uint KnownVersion);

/// <summary>One entry inside a <see cref="JournalSnapshotPacket"/> — see
/// <see cref="FlagSnapshotEntry"/> for why Serial/OriginPeerId ride along, and
/// <see cref="JournalOpPacket.Removed"/> for what a tombstone means here: a resyncing peer
/// must be TOLD a tag was removed, not merely find it absent, or its own stale copy (from an
/// earlier op or an already-applied engine write) never learns to retract it.</summary>
public readonly record struct JournalSnapshotEntry(
    string QuestTag, int State, uint Serial, uint OriginPeerId, bool Removed);

/// <summary>
/// The authoritative journal, tombstones included. A journal longer than one datagram is sent
/// as several of these in one burst, each carrying the same version.
/// </summary>
public sealed record JournalSnapshotPacket(uint Version, IReadOnlyList<JournalSnapshotEntry> Entries);
