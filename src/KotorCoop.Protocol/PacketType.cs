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
    // Retired for EnemyDamage (32). Health could not say WHO dealt what, and
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
    // reserves it on paper for `PrimaryPlayer` — a number concurrent agents on this tree have
    // collided on before. Taking the next free value costs nothing; PacketTypeTests fails the
    // build on any duplicate.

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

    /// <summary>
    /// host -&gt; clients: every creature the host's engine currently knows about in the loaded
    /// area — not just the 32-slot combat census <see cref="EnemySnapshot"/> carries — so a
    /// mirroring peer can notice a body left alive on one side and dead or destroyed on the
    /// other. See docs/research/creature-liveness.md.
    ///
    /// A LEVEL, published periodically (coop.ini <c>liveness_sync</c>, default off until
    /// live-verified) and possibly split across several datagrams when the area holds more
    /// than one packet's worth — each page carries the SAME digest over the whole cycle's
    /// entries, so a receiver that has gathered every page can tell its accumulated set is
    /// complete. No ack and no retransmit-until-acked sender: a lost page is simply resent
    /// whole on the next publish cycle, the same "a lost packet costs one tick of staleness"
    /// tolerance <see cref="EnemySnapshot"/> already accepts.
    /// </summary>
    CreatureLiveness = 47,
    // Numbering note, same discipline as the 39 skip above: the next type below takes 48
    // rather than the immediately-following value, to avoid colliding with concurrent work on
    // this tree. PacketTypeTests fails the build on any duplicate regardless.

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
    // 47 (CreatureLiveness above) and 48 (this type) were both picked for the same reason the
    // 39 skip was: to avoid colliding with concurrent work on this tree. See
    // TransitionMovieTests.NoTwoPacketTypesShareANumber, which fails the build on any
    // collision regardless.

    /// <summary>
    /// any -&gt; all, relayed by the host: "the module transition I am about to make goes to
    /// THIS module, entering at THIS waypoint" — sent the instant this instance's own engine
    /// commits to a transition, before the load starts.
    ///
    /// WHY IT EXISTS, and how it differs from <see cref="TransitionMovies"/>. That channel
    /// carries the film; this one carries the DESTINATION, early enough that a follower can
    /// start its OWN warp immediately instead of waiting for the mover's published
    /// <c>PlayerState.Module</c> to actually change (which, for a movie-carrying transition,
    /// can lag by the whole length of the film — the mover's main thread is blocked inside
    /// <c>WaitForMovie</c> and publishes nothing) plus the follow logic's own settle debounce.
    /// See docs/research/transition-smoothness.md.
    ///
    /// The serial is the ack-free redundancy: unreliable, like <see cref="TransitionMovies"/>,
    /// because a lost announcement costs a smoother transition, never consistency — the
    /// existing host-module-based follow (<c>CoopAgent.PumpTravel</c>'s settle-debounced path)
    /// remains the fallback whenever no announcement arrives, or arrives too late to matter.
    /// </summary>
    TransitionAnnounce = 49,

    /// <summary>
    /// any -&gt; all, relayed by the host: "my engine just played this mid-module movie" — a
    /// script's own <c>PlayMovie</c> call with NO transition involved (the Leviathan capture,
    /// the Star Forge arrival, the Taris bombardment, the Dantooine vision), as opposed to
    /// <see cref="TransitionMovies"/>, which carries a module transition's queue.
    ///
    /// WHY A SEPARATE PACKET TYPE rather than reusing <see cref="TransitionMovies"/>'s shape:
    /// that record carries <c>Module</c> as "what the announcer is LEAVING" and a LIST of up
    /// to six movies (the engine's own <c>StartNewModule</c> ceiling), because a transition's
    /// queue is drained wholesale by the next module load. A mid-module movie is a single,
    /// independent event with no transition to ride — the receiving peer has to decide,
    /// per-movie, whether to interrupt what it is doing right now
    /// (<c>KotorCoop.Plugin.MovieRelayQueue.Decide</c>) or defer it, which
    /// <see cref="TransitionMovies"/>'s "queue everything for the next load" semantics cannot
    /// express. Carrying a flag on the existing record would still leave the receiver unable
    /// to tell "defer THIS one movie" from "these movies ride the NEXT transition wholesale".
    ///
    /// <paramref name="Module"/> here is the module the ANNOUNCER is standing in when the
    /// movie played — unlike <see cref="TransitionMovies"/>'s "leaving" module, there is no
    /// transition in progress, so this is simply "where", and it is how a receiver decides
    /// <c>sameModule</c> before ever asking the plugin to act (see
    /// <c>CoopAgent.PumpMidModuleMovies</c>).
    ///
    /// UNRELIABLE, like <see cref="TransitionMovies"/>, for the identical reason: a lost
    /// announcement costs one player a cinematic, never a desync — nothing about this channel
    /// is state that has to converge. See docs/research/trigger-relay.md §6 and
    /// docs/research/cinematics.md.
    /// </summary>
    MidModuleMovie = 50,

    /// <summary>
    /// host -&gt; clients: absolute per-companion experience, keyed by NPC roster slot (bit N of
    /// the present mask says slot N's entry is meaningful) — the companion sibling of
    /// <see cref="ProgressionSnapshot"/>, on its own channel so it can be toggled independently
    /// (coop.ini <c>companion_xp_sync</c>, OFF by default, NEEDS-LIVE).
    ///
    /// Absolute, merges upward by max, for the identical reason
    /// <see cref="ProgressionSnapshot"/>'s own experience half is: both engines already grant
    /// kill XP to every ACTIVE party member independently
    /// (<c>CSWPartyTable::DistributeExperience</c> hands the FULL amount to each member in the
    /// active-slot array, not just the PC), so a delta would double-count every kill. What
    /// genuinely diverges is script-granted XP aimed at a specific companion. See
    /// docs/research/companion-state-sync.md.
    /// </summary>
    CompanionXpSnapshot = 51,

    /// <summary>
    /// client -&gt; host: a peer's own reading of each active companion's experience, same
    /// packing as <see cref="CompanionXpSnapshot"/>. The host merges every report upward by
    /// max, exactly like <see cref="ProgressionReport"/>'s experience half.
    /// </summary>
    CompanionXpReport = 52,

    /// any -&gt; all, relayed by the host: a content-keyed change to the shared party item pool
    /// (<c>CSWPartyTable+0x118</c>'s <c>CItemRepository</c>) — some number of units of a
    /// resref+identity stack were added to, or removed from, the stash. Reliable via
    /// retransmit-until-ack, the same shape as <see cref="ContainerLoot"/>, but keyed by CONTENT
    /// rather than by object id: a party-pool item is created independently on each instance and
    /// never shares an engine id across peers the way a GIT-placed door or container does. See
    /// docs/research/party-pool-sync.md. Off by default (<c>party_pool_sync</c>), NEEDS-LIVE.
    /// </summary>
    PartyPoolOp = 53,

    /// <summary>A peer's acknowledgement of a <see cref="PartyPoolOp"/>. See
    /// <see cref="ContainerLootAck"/> for the identical shape and reasoning.</summary>
    PartyPoolAck = 54,

    /// <summary>client -&gt; host: my party-pool version/digest disagrees, resync — the same
    /// role <see cref="LootSyncRequest"/> plays for container loot.</summary>
    PartyPoolSyncRequest = 55,

    /// <summary>
    /// host -&gt; the requesting peer: the answer to an explicit
    /// <see cref="InteractionClaimPacket.RequestSerial"/> arbitration request — closes the
    /// container-loot race (docs/roadmap-seamless.md Tier 1 item 4): two near-simultaneous
    /// clicks on the same container no longer both proceed locally before either removal
    /// lands, because the plugin's <c>ContainerUseHook</c>-side deferral
    /// (<c>container_lock_arbitration</c>, docs/research/container-sync.md) now waits for THIS
    /// packet (or a timeout) before letting the engine's own open through.
    ///
    /// Deliberately NOT a new claim/level packet — <see cref="InteractionClaimPacket"/> already
    /// carries everything the REQUEST needs (this just adds <c>RequestSerial</c> to it), so this
    /// type only carries the one-shot synchronous REPLY half, unicast to the single peer that
    /// asked. Unrelated to <see cref="InteractionLock"/>'s own per-tick broadcast level, which
    /// keeps working exactly as before and is what actually holds the lease once granted.
    ///
    /// Both are strictly additive (a new field appended at the end of an existing packet, one
    /// new packet type never touching an existing peer that doesn't ask for arbitration), so an
    /// old build simply never sees a nonzero <c>RequestSerial</c> and never receives this type.
    /// See <see cref="ProtocolConstants.Version"/> 44, which covers this and
    /// <see cref="InteractionClaimPacket.RequestSerial"/>.
    /// </summary>
    LockResponse = 56,

    /// any -&gt; all, relayed by the host: a change to a merchant store's stock
    /// (<c>CSWSStore+0x230</c>'s <c>CItemRepository</c>) — some number of units of a
    /// resref+identity stack were added to (a sell), or removed from (a buy), the named store.
    /// Reliable via retransmit-until-ack, the same shape as <see cref="PartyPoolOp"/>, keyed by
    /// STORE OBJECT ID plus content: unlike a party-pool item, a store is a real GIT-placed
    /// <c>CGameObject</c> both instances share an id for. See docs/research/store-sync.md. Off
    /// by default (<c>store_sync</c>), NEEDS-LIVE.
    /// </summary>
    StoreOp = 57,

    /// <summary>A peer's acknowledgement of a <see cref="StoreOp"/>. See
    /// <see cref="PartyPoolAck"/> for the identical shape and reasoning.</summary>
    StoreAck = 58,

    /// <summary>client -&gt; host: my store version/digest disagrees, resync — the same role
    /// <see cref="PartyPoolSyncRequest"/> plays for the party pool.</summary>
    StoreSyncRequest = 59,

    // Protocol version 46 covers the six V2 types below (60-65).
    // additive wire shapes (docs/research/shared-inventory-v2.md): the host-authoritative
    // "absolute snapshot + client intents" redesign of the party pool and store channels,
    // shipped behind party_pool_sync=2/store_sync=2 — an old build simply never sends or
    // requests mode 2 and never sees these types.

    /// <summary>
    /// host -&gt; clients: the party pool's ABSOLUTE per-key stock, as the host's own engine
    /// currently holds it — the host-authoritative replacement for the <see cref="PartyPoolOp"/>
    /// op-log (docs/research/shared-inventory-v2.md), gated behind <c>party_pool_sync=2</c>. A
    /// LEVEL, paged like <see cref="CreatureLiveness"/>, resent whole every publish cycle
    /// (unreliable — the next cycle is always a full resend, so a lost datagram costs one cycle
    /// of staleness, never a permanently missed fact). Never relayed — the host is the only
    /// publisher, and there is no separate resync packet: every cycle already IS the full resync
    /// (docs/research/shared-inventory-v2.md §2.7).
    /// </summary>
    PartyPoolSnapshotV2 = 60,

    /// <summary>
    /// client -&gt; host ONLY: "I deposited/withdrew this many units of this stack", carrying a
    /// client-minted serial. NOT a truth-claim about the pool's current total (unlike
    /// <see cref="PartyPoolOp"/>'s old <c>Add</c>/<c>Remove</c>/<c>Set</c>) — a bounded, one-shot
    /// REQUEST the host applies to its OWN engine through the identical staging-cheat primitive
    /// <see cref="PartyPoolOp"/> already used, then reflects back to every peer (including the
    /// sender) through the next <see cref="PartyPoolSnapshotV2"/> cycle. Never relayed onward —
    /// the host is the only party that ever asserts a total. See
    /// docs/research/shared-inventory-v2.md §2.2 for why this structurally cannot amplify the way
    /// the old op-log could.
    /// </summary>
    PartyPoolIntentV2 = 61,

    /// <summary>host -&gt; the sending client: acks (or refuses) one
    /// <see cref="PartyPoolIntentV2"/> serial — the client drops it from its own "still
    /// outstanding" reconcile-target adjustment once acked either way (see
    /// docs/research/shared-inventory-v2.md §2.5's <c>target[K]</c> formula).</summary>
    PartyPoolIntentAckV2 = 62,

    /// <summary>
    /// host -&gt; clients: a merchant store's ABSOLUTE per-(store, key) stock, as the host's own
    /// engine currently holds it — the <see cref="StoreOp"/> op-log's host-authoritative
    /// replacement, gated behind <c>store_sync=2</c>. Same paged-LEVEL shape as
    /// <see cref="PartyPoolSnapshotV2"/>; a store's <c>StoreObjectId</c> lives inside each entry's
    /// own key (unchanged from <see cref="StoreOp"/>), so one cycle can name several stores at
    /// once.
    /// </summary>
    StoreSnapshotV2 = 63,

    /// <summary>client -&gt; host ONLY: "I bought/sold this many units of this stack in this
    /// store" — the store's mirror of <see cref="PartyPoolIntentV2"/>, identical
    /// reasoning.</summary>
    StoreIntentV2 = 64,

    /// <summary>host -&gt; the sending client: acks (or refuses) one <see cref="StoreIntentV2"/>
    /// serial — the store's mirror of <see cref="PartyPoolIntentAckV2"/>.</summary>
    StoreIntentAckV2 = 65,

    // 66-69 reserved for concurrent work on this tree (the same numbering discipline as the
    // 39/47/48 skips above). 70-72 are this stream's own reservation (docs/research/HOSTLOSS
    // wave notes) — 70 and 71 are used (HostBye, CompanionDamage), 72 is still open.

    /// <summary>
    /// host -&gt; every connected client: "I am shutting down cleanly" — sent once, best-effort,
    /// the instant the host's own tick loop stops (a player-initiated disconnect or a graceful
    /// process exit), before the socket closes. NOT sent on a crash — there is nothing left to
    /// send it — so the ordinary <see cref="ProtocolConstants.PeerTimeoutMs"/> silence-based
    /// detection (<c>CoopClient.Tick</c>) remains the fallback for that case and is what a
    /// crash still relies on entirely.
    ///
    /// WHY IT EXISTS: without it, a clean host shutdown was indistinguishable on the client
    /// from a network blip until <c>PeerTimeoutMs</c> (5 s) of silence had actually elapsed —
    /// five seconds during which the client's control lock, dialogue spectate and every other
    /// plugin-side lock stayed engaged for no reason, since the host was never coming back this
    /// session. This lets a client that hears it declare "host lost" (see
    /// <c>CoopClient.HostLost</c>) immediately and start releasing those locks and re-Helloing
    /// right away, rather than waiting out the full timeout. See
    /// docs/research/disconnect-reconnect.md's "host loss" section.
    ///
    /// UNRELIABLE, like <see cref="Bark"/>: a lost datagram costs nothing but the difference
    /// between "instant" and "up to <see cref="ProtocolConstants.PeerTimeoutMs"/> later" — the
    /// silence-based fallback already covers that gap completely, so no ack and no retransmit.
    /// </summary>
    HostBye = 70,

    /// <summary>
    /// client -&gt; host: cumulative damage taken by each of this peer's companions that
    /// <c>companion_local_combat</c> has released to fight through its own engine AI — the
    /// companion sibling of <see cref="EnemyDamage"/>, mirrored for the opposite direction.
    ///
    /// THE GAP THIS CLOSES: a released companion takes real hits locally, but the host's
    /// authoritative body never saw any of it (there was no channel at all), so the host's
    /// higher figure overwrote the client's own damage on the very next mirror pass — a
    /// companion fighting on a client was effectively invulnerable, and a companion downed on
    /// the client popped back up. Keyed by NPC ROSTER SLOT rather than object id: unlike an
    /// enemy, a companion's identity is stable across a module transition, so there is no
    /// module label to carry. A running CUMULATIVE total, never an event — the host banks each
    /// peer's RISE and applies the difference through its own engine's real damage pipeline,
    /// the same "level, not event" rule <see cref="EnemyDamage"/> uses and for the identical
    /// reason. See docs/research/companion-state-sync.md §13.
    /// </summary>
    CompanionDamage = 71,

    /// <summary>
    /// any -&gt; all (relayed by the host): "my player just confirmed an interplanetary
    /// destination on the galaxy map" — <see cref="GalaxyDeparturePacket"/>. A galaxy-map
    /// accept runs the whole leg (confirm script, take-off movie, <c>StartNewModule</c> into
    /// the launch cutscene, a fresh Ebon Hawk) inside a frozen main thread, so neither
    /// <see cref="TransitionAnnounce"/> nor the module-change fallback ever fires for it; the
    /// follower was left silently in a stale Ebon Hawk (live 2026-09-03). The plugin publishes
    /// the click itself, and the receiver REPLAYS the same scripted leg natively rather than
    /// warping after the leader. See docs/research/galaxy-map-and-party-select.md's 2026-09-03
    /// relay addendum.
    /// </summary>
    GalaxyDeparture = 72,

    /// <summary>
    /// client -&gt; host: begin a UDP-only world-transfer (roadmap item 24's internet-play gap,
    /// found live 2026-09-10: a UDP-only tunnel/NAT path forwards the session's UDP traffic
    /// fine but silently drops <c>KotorCoop.Session.WorldTransfer</c>'s separate TCP
    /// listener, so <c>auto_adopt_world</c> could never complete over such a path). Carries a
    /// client-minted <c>RequestId</c> (so a resent request is distinguishable from a stale one)
    /// and the session password hash, exactly like <c>WorldTransfer</c>'s TCP request
    /// header. Multiplexed onto the SAME UDP socket the ordinary session already owns — unlike
    /// the TCP channel, a second UDP socket cannot share its port — so this rides the host's
    /// existing <c>CoopHost</c> receive loop rather than a separate listener. See
    /// docs/research/network-topology.md's UDP world-transfer section.
    /// </summary>
    WorldTransferRequest = 73,

    /// <summary>
    /// host -&gt; client: answers a <see cref="WorldTransferRequest"/> — status (ok / no world /
    /// bad password), the host's save-identity digest, and the flat payload's total size and
    /// chunk count. Retransmitted until the client's first <see cref="WorldTransferChunkStatus"/>
    /// arrives (proof it was received), the same "resend until acked" shape every reliable
    /// channel here uses.
    /// </summary>
    WorldTransferHeader = 74,

    /// <summary>
    /// host -&gt; client: one <see cref="ProtocolConstants.WorldTransferChunkPayloadSize"/>
    /// slice of the flat save payload (the same byte layout <c>WorldTransfer</c>'s TCP
    /// stream already sends — file count, then per-file name+size+bytes — just cut into
    /// datagram-sized pieces instead of one ordered stream). The last chunk is whatever is left
    /// over, its length implied by the datagram's own size rather than an extra field. Sent
    /// once for every chunk on the first pass, then only for whatever a
    /// <see cref="WorldTransferChunkStatus"/> still reports missing.
    /// </summary>
    WorldTransferChunk = 75,

    /// <summary>
    /// client -&gt; host: which chunks (out of the count <see cref="WorldTransferHeader"/>
    /// announced) are still missing, as a bitmap — one bit per chunk index, sent periodically
    /// while a transfer is in progress. A LEVEL, not an ack-per-chunk: re-sending the whole
    /// missing set every cycle costs nothing (a save-sized transfer's bitmap is at most
    /// <see cref="ProtocolConstants.MaxWorldTransferChunks"/>/8 bytes) and needs no per-chunk
    /// bookkeeping on either side — the host simply resends whatever bit is still set.
    /// </summary>
    WorldTransferChunkStatus = 76,

    /// <summary>
    /// client -&gt; host: every chunk arrived and the reassembled payload passed the same
    /// bounds/parsing checks <c>WorldTransfer</c>'s TCP path already applies. Lets the
    /// host free its per-client transfer buffer instead of holding it until the transfer's own
    /// timeout expires.
    /// </summary>
    WorldTransferComplete = 77,

    // 73-77 are this stream's own reservation (the same numbering discipline as the 39/47/48
    // skips above) — see PacketTypeTests, which fails the build on any collision regardless.
}
