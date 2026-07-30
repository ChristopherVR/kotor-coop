namespace KotorCoop.Protocol;

public enum PacketType : byte
{
    None = 0,

    /// <summary>client -> host: request to join.</summary>
    Hello = 1,

    /// <summary>host -> client: accepted, here is your peer id and session parameters.</summary>
    Welcome = 2,

    /// <summary>host -> client: rejected, with a reason.</summary>
    Reject = 3,

    /// <summary>client -> host: my current avatar state, sent every tick.</summary>
    StateUpdate = 4,

    /// <summary>host -> all: everyone's avatar state plus the current world flag version.</summary>
    WorldSnapshot = 5,

    /// <summary>peer -> host: set a world flag. Retransmitted until acked.</summary>
    FlagOp = 6,

    /// <summary>host -> peer: flag op received, stop retransmitting.</summary>
    FlagAck = 7,

    /// <summary>client -> host: my flag version is stale, send me the full set.</summary>
    FlagSyncRequest = 8,

    /// <summary>host -> client: the authoritative flag set.</summary>
    FlagSnapshot = 9,

    Ping = 10,
    Pong = 11,

    /// <summary>peer -> host: leaving cleanly.</summary>
    Bye = 12,

    /// <summary>peer -> host: drop or remove a ground item. Retransmitted until acked.</summary>
    ItemOp = 13,

    /// <summary>host -> peer: item op received, stop retransmitting.</summary>
    ItemAck = 14,

    /// <summary>client -> host: my ground-item version is stale, send me the full set.</summary>
    ItemSyncRequest = 15,

    /// <summary>host -> client: the authoritative set of items currently on the ground.</summary>
    ItemSnapshot = 16,

    /// <summary>
    /// host -> clients: authoritative health of every enemy in the module, broadcast each
    /// tick. The host owns enemy state; clients mirror it and cannot kill anything locally.
    /// </summary>
    EnemySnapshot = 17,

    /// <summary>peer -> host: advance a quest journal entry. Retransmitted until acked.</summary>
    JournalOp = 18,

    /// <summary>host -> peer: journal op received, stop retransmitting.</summary>
    JournalAck = 19,

    /// <summary>client -> host: my journal version is stale, send me the full set.</summary>
    JournalSyncRequest = 20,

    /// <summary>host -> client: the authoritative journal (possibly split over packets).</summary>
    JournalSnapshot = 21,

    /// <summary>
    /// client -> host: my local view of enemy health, sent each tick. The host merges
    /// downward (an enemy is as hurt as ANY instance says it is), which is what makes a
    /// client's damage real — client copies are pinned at 1 hp, so without this a
    /// client's whole fight would be cosmetic.
    /// </summary>
    EnemyReport = 22,

    /// <summary>
    /// both ways: a fingerprint of the in-world state that only becomes knowable once a
    /// save is loaded (the global-variable catalogue). Exchanged so a mismatch the
    /// install digest could not see — the catalogue is read from game memory, not files —
    /// refines the negotiated capabilities.
    /// </summary>
    WorldDigest = 23,

    /// <summary>
    /// host -> clients: the authoritative party companions, each keyed by NPC slot,
    /// broadcast each tick. The host owns them; clients freeze their own copies and mirror
    /// these so companions do not drift into two versions.
    /// </summary>
    PartySnapshot = 24,
}
