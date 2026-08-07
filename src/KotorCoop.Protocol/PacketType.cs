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

    // 22 was EnemyReport: a client's SAMPLED view of enemy health, min-merged by the host.
    // Retired 2026-08-07 for EnemyDamage (32). Health could not say WHO dealt what, and
    // sampling it lost most of a lone player's fight — the owner's mirror wrote its own
    // value back onto the client's copy eight times a second, so a hit had to win a race
    // against the next mirror pass to be reported at all (measured: 15 -> 11 hp and then
    // nothing, for ninety seconds). ⚠ Do not reuse the value: an old build still sends it.

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

    /// <summary>
    /// host -> all: the authoritative conversation token — who, if anyone, is mid-dialogue,
    /// with a serial and the spectate line. Broadcast each tick so late/reconnecting peers
    /// converge.
    /// </summary>
    DialogueSnapshot = 25,

    /// <summary>
    /// peer -> host: "I am (not) in a conversation, here is my current line." The host runs
    /// it through the arbiter — granting the token if free, updating it if the requester
    /// already owns it, releasing it on end, and ignoring a claim while another peer owns it.
    /// </summary>
    DialogueRequest = 26,

    /// <summary>
    /// peer -> host, then host -> everyone else: a short text message. The players cannot see
    /// each other's screens, so this is the coordination channel; the host relays in the star
    /// topology, and the sender shows its own line locally.
    /// </summary>
    Chat = 27,

    /// <summary>
    /// host -> clients: absolute shared progression (party gold and the PC's experience),
    /// broadcast each tick. Host-authoritative — the host's values are truth and clients
    /// overwrite their own, so both parties level and spend together. Absolute, never deltas:
    /// every kill runs the XP/gold grant locally on both instances, so combining would double.
    /// </summary>
    ProgressionSnapshot = 28,

    /// <summary>
    /// host -> clients: an item was looted out of a shared container (chest/corpse), keyed by
    /// (container, item) object id. Retransmitted until acked, like <see cref="ItemOp"/>, so a
    /// removal is not lost. Host-authoritative: only the host detects and publishes these; a
    /// client applies the removal to its copy of the container so the item cannot be taken twice.
    /// </summary>
    ContainerLoot = 29,

    /// <summary>client -> host: container-loot op received, stop retransmitting to me.</summary>
    ContainerLootAck = 30,

    /// <summary>
    /// peer -> host (-> the addressed peer): damage that landed on that peer's phantom in
    /// the sender's game, so it can be applied to their real character. A running
    /// CUMULATIVE total sent every tick, not an event — the receiver applies the
    /// difference, so a lost datagram costs a tick and a duplicate costs nothing. The host
    /// relays in the star topology, and applies it directly when it is the addressee.
    /// </summary>
    PhantomDamage = 31,

    /// <summary>
    /// peer -> enemy owner: how much damage this instance has DEALT to each enemy, as a
    /// running cumulative total per object id.
    ///
    /// The replacement for <c>EnemyReport</c> (22), and the difference is the whole point:
    /// health is a level that has to be sampled and can be overwritten by the owner's next
    /// mirror write, while damage dealt is a monotonic sum that nothing in the game can
    /// erase — it is never read back out of the engine. The owner keeps the last total it
    /// applied per (peer, enemy) and applies each increase as real damage to its own copy,
    /// so the channel is order-independent, survives a lost datagram, and never
    /// double-applies a repeated one.
    /// </summary>
    EnemyDamage = 32,
}
