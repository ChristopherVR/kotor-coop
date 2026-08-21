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
    /// client -> host: the encounters this client's own player is standing inside RIGHT NOW.
    /// The client's encounters are deliberately disarmed (an encounter spawn there would
    /// drift the object-id counter the whole enemy channel keys on), so it cannot fire one
    /// itself; the host runs the engine's own eligibility test against the client's phantom
    /// and lets its engine do the spawn.
    ///
    /// A LEVEL, not an event: the current set is re-sent every tick while the player stands
    /// in a polygon, so a lost datagram costs a tick. Repeats are free because the engine
    /// itself is idempotent here — <c>CSWSEncounter::SpawnIfAppropriate</c> early-outs on
    /// <c>started == 1</c>, which is exactly what firing sets.
    /// </summary>
    EncounterTrigger = 32,

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
    EnemyDamage = 33,

    /// <summary>
    /// A one-line NPC bark (any -> all, relayed by the host).
    ///
    /// Barks never enter the dialogue system, so no other channel carries them: one player
    /// clicked the utility droid and heard it beep while everybody else got silence.
    /// </summary>
    Bark = 34,

    /// <summary>
    /// peer -&gt; host: "I am holding this object right now — do not let anyone else interact
    /// with it, and if you own it, hold it still."
    ///
    /// A LEASE, NOT A HANDSHAKE, and that is the whole reliability design: an unheld lock
    /// strands an NPC, so there is no release message whose loss could do that. The claimant
    /// re-sends every tick and the host ages the claim out. A client that crashes
    /// mid-conversation simply stops refreshing. An explicit release makes the common case
    /// instant; it is not what makes it correct.
    /// </summary>
    InteractionClaim = 35,

    /// <summary>
    /// host -&gt; all: the authoritative set of live interaction locks, broadcast each tick.
    ///
    /// Two halves consume it. Every instance feeds the tags to the dialogue gate, so a
    /// conversation cannot be started against a claimed NPC — including by the OWNER, whose
    /// scripts and AI reach the same StartDialog choke point. The instance that OWNS the
    /// object additionally holds the body still, which is the gap
    /// docs/research/shared-state-audit.md §4 names: "the host's Trask carries on walking
    /// because the host's AI was never told otherwise".
    /// </summary>
    InteractionLock = 36,

    /// <summary>
    /// host -&gt; all: the authoritative NPC ROSTER — which of the nine companion slots are
    /// available (recruited) and which are in the active party — broadcast each tick.
    ///
    /// The gap it closes: <c>AddPartyMember</c> / <c>RemovePartyMember</c> had no channel at
    /// all. A conversation that recruits or dismisses a companion runs its scripts on ONE
    /// instance, and the roster is what the whole companion channel keys on
    /// (PartyPresenceController resolves companions by NPC slot), so the two instances ended
    /// a recruitment holding different parties and the mirror silently stopped naming the
    /// same creature.
    ///
    /// A LEVEL, not an edge, for the reason every channel here that matters is: an edge that
    /// is dropped strands the divergence forever, and a roster divergence is exactly the kind
    /// that never heals on its own. Two nine-bit masks are small enough that re-sending the
    /// complete truth every tick costs nothing.
    /// </summary>
    PartyRoster = 37,

    /// <summary>
    /// client -&gt; host: this peer's contribution to the shared purse and its own experience
    /// total.
    ///
    /// The gold half is a LEDGER, in the shape <see cref="EnemyDamage"/> proved: cumulative
    /// earned and cumulative spent, never a balance. A balance cannot be merged — two
    /// instances holding 400 and 1200 have no arithmetic that says which is right — while
    /// "this peer has taken 250 out of the purse since it joined" is a fact nothing can race,
    /// survives a lost datagram, and never double-applies a repeated one. The host spends
    /// each increase against its own party table, and its resulting balance IS the purse.
    ///
    /// The experience half is an ABSOLUTE, and merges upward by max. It cannot be a ledger,
    /// because both engines already grant kill XP independently: a client's mirrored enemy
    /// dies through the real damage pipeline (<c>KillCreature</c> → <c>OnApplyDeath</c> →
    /// <c>ApplyDeathExperience</c> 0x4FB1E0, whose only caller is 0x4E0CB7), so forwarding
    /// XP as a delta would count every kill in the game twice. What genuinely diverges is
    /// script-granted XP — <c>GivePlotXP</c> from a conversation only one instance is having,
    /// stealth XP — and a max carries that up without touching the rest.
    /// </summary>
    ProgressionReport = 38,

    // 39 is DELIBERATELY SKIPPED. docs/research/network-topology.md §"the primary player"
    // reserves it on paper for `PrimaryPlayer`, and three agents in this tree have already
    // collided on a number that only one of them could see. Taking the next value nobody has
    // written down costs nothing; PacketTypeTests now fails the build on any duplicate.

    /// <summary>
    /// any -&gt; all, relayed by the host: "the module transition I am about to make plays these
    /// movies".
    ///
    /// WHY IT EXISTS. A KOTOR movie is played by the engine from the transition queue at
    /// <c>CClientExoAppInternal+0x2D8</c>, which <c>StartNewModule</c> drains during the load.
    /// A peer that follows through the co-op travel channel has an EMPTY queue, so it loads
    /// straight in — measured 2026-08-15: the owner's main thread was gone for 7,407 ms
    /// playing <c>01c</c> while the follower logged no cinematic at all. The follower is not
    /// gated and needs no new engine call; it needs the names.
    ///
    /// A PRE-ANNOUNCEMENT, and it has to be. The queue fills on the frame the script calls
    /// <c>StartNewModule</c> and is drained by the load, and if a movie plays the announcer's
    /// main thread is inside <c>WaitForMovie</c> for seconds afterwards — so the one moment
    /// this can be sent is before the transition, not during or after it.
    ///
    /// UNRELIABLE, like <see cref="Bark"/>, and for a stronger reason than "it is cheap": a
    /// lost announcement costs a cinematic, never a desync. The rendezvous is arrival in the
    /// module, which the travel channel already guarantees on its own. The sender repeats the
    /// announcement over a few ticks so one drop does not lose it, and receivers de-duplicate
    /// on (peer, serial) — there is no ack, and adding one would put a handshake in front of
    /// something whose failure mode is cosmetic.
    /// </summary>
    TransitionMovies = 40,

    /// <summary>
    /// host -&gt; clients: an owner-authoritative enemy just died, carrying the resref + full
    /// per-instance identity of everything its real corpse was holding — see
    /// docs/research/enemy-materialisation.md §6. Enemies are always host-owned, so unlike
    /// <see cref="TransitionMovies"/> this never needs relaying, only broadcasting.
    ///
    /// Reliable via retransmit-until-ack, like <see cref="ContainerLoot"/>: a death happens
    /// once, so a dropped packet must not silently mean no loot ever arrives. A receiver with
    /// no materialised stand-in for <c>OwnerEnemyId</c> (the common case: a peer holding the
    /// real, GIT-identical creature already rolled its own equivalent loot independently) acks
    /// and discards it — see <see cref="MaterialisedLootAck"/> and
    /// <c>CheatController.GiveLoot</c>'s identical refusal rule, which the receiving side
    /// reuses rather than re-deciding.
    /// </summary>
    MaterialisedLoot = 41,

    /// <summary>A client's acknowledgement of a <see cref="MaterialisedLoot"/> packet. Carries
    /// the acking peer id so the host, which broadcasts to several clients, can tell whose ack
    /// this is — exactly like <see cref="ContainerLootAck"/>.</summary>
    MaterialisedLootAck = 42,

    /// <summary>
    /// client -&gt; host: my container-loot version or digest disagrees with the one just
    /// broadcast in <see cref="WorldSnapshot"/>, resync me.
    ///
    /// Unlike <see cref="FlagSyncRequest"/> / <see cref="ItemSyncRequest"/> /
    /// <see cref="JournalSyncRequest"/>, this has no matching "Snapshot" reply type: loot's
    /// full resync is already the reliable per-removal channel (<see cref="ContainerLoot"/>,
    /// retransmitted until every current peer acks), so the host repairs a mismatch by
    /// re-offering every removal it knows through that channel rather than by inventing a
    /// second, unreliable one-shot snapshot for the same data. See
    /// docs/research/disconnect-reconnect.md §4.1 and §4.8: before this, container loot was
    /// the one world channel with no version tripwire at all, so a straggler under sustained
    /// loss had nothing behind it but the retransmit itself.
    /// </summary>
    LootSyncRequest = 43,

    /// <summary>
    /// A non-damage effect (stun, paralyze, knockdown, slow, frightened, sleep, choke, or Force
    /// push/whirlwind) that landed on a peer's phantom, addressed to the player that phantom
    /// stands for — the non-damage sibling of <see cref="PhantomDamage"/>. See
    /// docs/research/effect-sync.md.
    ///
    /// UNRELIABLE, like <see cref="Bark"/>: this is a transient, cosmetic-adjacent event, not
    /// state that has to converge. A lost datagram costs one crowd-control tick, never a
    /// desync — the phantom's HEALTH, which is what actually matters for the fight, still
    /// crosses on <see cref="PhantomDamage"/> regardless of whether this arrives.
    /// </summary>
    PeerEffect = 44,
    /// <summary>
    /// peer -&gt; host, then host -&gt; every OTHER peer: the local conversation OWNER's reward
    /// script just put item(s) into the speaker's inventory and/or made a companion available,
    /// and the spectating side never ran <c>HandleReply</c> so its own game never saw either —
    /// docs/research/dialogue-consequences.md. Symmetric like <see cref="ContainerLoot"/>: a
    /// client sends this to the host, which relays it to every other connected peer under the
    /// SAME (origin, sequence) identity rather than re-attributing it to itself, because the
    /// receiving side must never re-apply its OWN grant when it echoes back — see
    /// <see cref="DialogueGrantAck"/> and <c>KotorCoop.Session.DialogueGrantSender</c>.
    ///
    /// Reliable via retransmit-until-ack, like <see cref="MaterialisedLoot"/>: a reward node
    /// runs once, so a dropped packet must not silently mean the item or the recruit never
    /// arrives on the other screen.
    /// </summary>
    DialogueGrant = 45,

    /// <summary>Acknowledges one <see cref="DialogueGrant"/> op, in either direction — a client
    /// confirming a report reached the host, or a host confirming a broadcast reached a client.
    /// Carries the acking peer id plus the (origin, sequence) pair being acked, unlike
    /// <see cref="ContainerLootAck"/>'s bare sequence, because <see cref="DialogueGrant"/> keeps
    /// the TRUE origin on relay and two different origins can reuse the same sequence number
    /// independently.</summary>
    DialogueGrantAck = 46,

    // 47 is DELIBERATELY SKIPPED, the same discipline PacketType 39 already established: a
    // concurrent worktree agent's block-version-45 liveness work claims the number-space
    // right after this one is likely to collide with, so this pass takes 48 instead of the
    // next free value. PacketTypeTests fails the build on any duplicate regardless.

    /// <summary>
    /// client -&gt; host: "my player is standing inside these GENERIC triggers right now"
    /// (a <c>CSWSTrigger</c> whose <c>OnEnter</c> script starts a cutscene/dialogue/scripted
    /// event — the classic "walk into the room, the scene starts" — as opposed to a trap or
    /// an area-transition trigger, which this channel never carries).
    ///
    /// host -&gt; all: the trigger ids this host has ALREADY fired in the named module, so a
    /// client's watch stops re-reporting ones the host has already handled. Distinguished
    /// from the client direction by <c>PeerId == 0</c> — no real peer ever holds that id
    /// (see <see cref="EncounterTrigger"/>'s identical zero-guard).
    ///
    /// WHY THIS EXISTS, separately from <see cref="EncounterTrigger"/>: an encounter spawn
    /// consumes object ids, so a client's encounters are disarmed outright and the host is
    /// the ONLY engine that ever fires one. A generic trigger's OnEnter script is not
    /// disarmed — the client's own engine already runs it correctly for the client's own
    /// real character. The gap is narrower: a story trigger's script commonly calls
    /// <c>GetEnteringObject() == GetFirstPC()</c> (so it fires for the client's real PC and
    /// never for the client's PHANTOM on the host's screen, which is not that instance's
    /// GetFirstPC() either way — the phantom is placed by a direct SetPosition write, so it
    /// never generates the subarea-entered event the real engine needs regardless) and
    /// commonly calls <c>CreateObject</c>/<c>DestroyObject</c> to spawn or remove a plot
    /// actor — an object-id-consuming operation the HOST's own instance must also perform, in
    /// the same order, or the two instances' id counters drift apart for the rest of the
    /// module. See docs/research/trigger-relay.md.
    ///
    /// A LEVEL in both directions, exactly like <see cref="EncounterTrigger"/>: the client
    /// resends its current set every tick and the host resends its fired set every tick, so a
    /// lost datagram costs one tick, never a permanently missed fire.
    /// </summary>
    TriggerRelay = 48,
}
