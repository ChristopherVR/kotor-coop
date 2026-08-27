namespace KotorCoop.Protocol;

public static class ProtocolConstants
{
    /// <summary>Two byte magic ('K','C') so stray traffic on the port is discarded cheaply.</summary>
    public const ushort Magic = 0x4B43;

    /// <summary>
    /// Bumped on any wire-format change. Mismatched peers are rejected at Hello. One line per
    /// version below — see the cited docs/research/*.md file for the full rationale behind any
    /// entry that has one; the rest are self-contained.
    ///
    /// Version 3: PlayerState flags dword, journal channel, client enemy reports.
    /// Version 4: content digest on Hello/Welcome for mod-compatibility handshaking.
    /// Version 5: session password hash on Hello; party-member snapshot channel.
    /// Version 6: dialogue ownership token (snapshot + request channels).
    /// Version 7: text chat channel.
    /// Version 8: shared progression (party gold + PC experience).
    /// Version 9: world digest carries save identity + per-module GIT digest.
    /// Version 10: dialogue token carries the partner tag and the owner's reply picks, so a
    /// spectator runs the same conversation in the game's own UI instead of reading a
    /// transcript.
    /// Version 11: dialogue token carries the owner's full pick LOG so a late joiner catches up.
    /// Version 12: enemy snapshot carries presence (position, facing, animation, one-shots) so
    /// a mirroring instance drives its copies to match the owner's fight.
    /// Version 13: enemy snapshot includes PLOT (cutscene) creatures, presence-only.
    /// Version 14: enemy snapshot carries the MODULE its ids were read in, so a peer elsewhere
    /// drops it instead of misapplying it to local creatures sharing those ids.
    /// Version 15: peer slots are STICKY per peer id, and a relayed phantom-damage report
    /// carries the true ORIGINATOR's peer id rather than the relaying host's.
    /// Version 16: EncounterTrigger — a client reports the encounters it stands inside; the
    /// host's engine (never the client's, whose own encounters stay disarmed) does the spawn.
    /// Version 17: the peer direction reports DAMAGE DEALT (EnemyDamage 33) instead of sampled
    /// health, which the owner's own mirror could overwrite before it was ever read.
    /// Version 18: EnemyState carries appearance/faction so a mirror can MATERIALISE an enemy
    /// that exists only in the owner's instance (an encounter or script spawn).
    /// Version 19: Bark (34) — one-line NPC bubbles, which never touch the dialogue system.
    /// Version 20: InteractionClaim (35) / InteractionLock (36) — a per-object LEASE the
    /// object's owner honours, so a client's conversation with a host-owned NPC stops the
    /// host's own AI from walking it away mid-scene.
    /// Version 21: DialogueState carries the owner's NODE index; a follower is driven onto that
    /// entry rather than replaying the reply SLOT, which two instances' independent scripts can
    /// resolve to different branches.
    /// Version 22: PartyRoster (37) — the host's nine companion slots as two masks, so a
    /// recruit/dismiss conversation on one instance keeps both rosters in sync.
    /// Version 23: ProgressionReport (38) — the peer's contribution: gold as a cumulative
    /// earned/spent LEDGER (a balance cannot be merged), XP as an absolute merged by max (both
    /// engines already grant kill XP independently, so a delta would double-count).
    /// Version 24: a ground item carries per-instance IDENTITY (stack/charges/upgrades/flags)
    /// instead of respawning as a pristine blueprint copy on pickup.
    /// Version 25: TransitionMovies (40) — pre-announces a transition's movie queue so a
    /// follower's own (otherwise-empty) queue plays them too.
    /// Version 26: PlayerState carries FlagInMinigame so a peer's instance parks the phantom and
    /// stops applying forwarded damage during a swoop race/turret sequence. See
    /// docs/research/minigames.md.
    /// Version 27: the world snapshot carries a content DIGEST per store (flag/item/journal/
    /// loot) beside its version counter, catching a same-count-but-different-content tie a bare
    /// counter misses. See docs/research/disconnect-reconnect.md §4.1/§4.8.
    /// Version 28: PeerEffect (44) — non-damage combat effects on a phantom, forwarded to the
    /// real character. Off by default until live-verified. See docs/research/effect-sync.md.
    /// Version 29: DialogueGrant (45) / DialogueGrantAck (46) — relays a reward node's item/
    /// companion grant to the spectating side, whose own HandleReply never ran. See
    /// docs/research/dialogue-sync.md.
    /// Version 30: CreatureLiveness (47) — every creature the host knows about, not just the
    /// combat census, so a mirror can reconcile a body left alive on one side and gone on the
    /// other. Off by default until live-verified. See docs/research/creature-liveness.md.
    /// Version 31: flag/journal ops carry a per-key Lamport Serial (plus a journal Removed bit)
    /// so a legitimate DECREASE is no longer refused forever by max-merge. See
    /// docs/research/quest-sync.md.
    /// Version 32: TriggerRelay (48) — a client reports the generic triggers it stands in; the
    /// host fires them so its own object-id counter stays in lockstep. Off by default until
    /// live-verified. See docs/research/trigger-relay.md.
    /// Version 33: TransitionAnnounce (49) — destination + waypoint announced before a
    /// transition's load starts, so a follower's own warp overlaps instead of trailing. See
    /// docs/research/transition-smoothness.md.
    /// Version 34: MidModuleMovie (50) — a script's own <c>PlayMovie</c> with no transition,
    /// relayed the same way TransitionMovies is. Off by default until live-verified. See
    /// docs/research/trigger-relay.md §6.
    /// Version 35: WorldDigestPacket gains LoadSerial, so a receiver can classify a peer's
    /// SaveIdentity change as a save or a load. Off by default until live-verified. See
    /// docs/research/session-save.md.
    /// Version 36: <see cref="TriggerRelayPacket"/> gains <c>NativeFiredIds</c>/
    /// <c>NativeFiredOriginPeerIds</c> — the reverse direction, so the HOST's own native trigger
    /// fires reach clients too, not just a client's presence reaching the host. See
    /// docs/research/trigger-relay.md.
    /// Version 37: <see cref="InteractionKind.Container"/> — a container/corpse joins the
    /// interaction lock, keyed by <c>TargetId</c> rather than <c>Tag</c> since placeables repeat
    /// tags across instances. See docs/research/container-sync.md.
    /// Version 38: <see cref="DialogueState"/> and <see cref="DialogueRequestPacket"/> gain
    /// <c>Forced</c>, so <c>dialogue_auto_join</c> can tell an engine-forced conversation (an
    /// ambush, a scripted cutscene) from an ordinary player-clicked chat. See
    /// docs/research/dialogue-sync.md's Item 1.
    /// Version 39: <see cref="PlayerState.FlagAway"/> — set on a player's last known state once
    /// <c>CoopAgent</c> has failed to read them for a sustained streak (main menu, load screen,
    /// character screen), so a peer's instance parks their phantom instead of leaving a frozen
    /// statue behind and the session can announce "away"/"back". Rides the existing
    /// <c>Flags</c> dword, so nothing in <see cref="PacketCodec"/> changes shape. See
    /// docs/research/disconnect-reconnect.md.
    ///
    /// TODO(version): bump — Version 40: PartyPoolOp (51) / PartyPoolAck (52) /
    /// PartyPoolSyncRequest (53) — the shared party item-pool channel (<c>party_pool_sync</c>,
    /// default OFF, NEEDS-LIVE). See docs/research/party-pool-sync.md.
    /// </summary>
    public const byte Version = 39;

    /// <summary>Journal quest tags are resrefs plus a little slack.</summary>
    public const int MaxJournalTagLength = 32;

    /// <summary>
    /// Journal entries per snapshot datagram: tag (up to 33 bytes) + state (4) each, so
    /// 28 stays inside <see cref="MaxPacketSize"/>. A longer journal is sent as several
    /// snapshot packets in one burst.
    /// </summary>
    public const int MaxJournalEntriesPerPacket = 28;

    /// <summary>Odyssey resource references are at most 16 characters.</summary>
    public const int MaxResrefLength = 16;

    /// <summary>
    /// Ground items a session tracks at once. 16 is the most that still fits in a single
    /// item snapshot datagram with every string at maximum length: 66 bytes of id, resref,
    /// module and position plus 9 of item identity is 75 each, so a full 16 is 1210 bytes
    /// with the header — inside <see cref="MaxPacketSize"/>, and asserted by a test rather
    /// than by this arithmetic.
    /// </summary>
    public const int MaxGroundItems = 16;

    /// <summary>
    /// Distinct party-pool stack keys a session remembers at once (bounded like
    /// <see cref="MaxGroundItems"/>, not asserted against a snapshot datagram since this
    /// channel has none — see docs/research/party-pool-sync.md). A stash rarely holds more
    /// than a handful of genuinely distinct (resref, identity) combinations; existing keys can
    /// still change quantity past the cap, only a brand-new key is refused.
    /// </summary>
    public const int MaxPartyPoolStacks = 256;

    /// <summary>
    /// Creatures the owner replicates per snapshot. 40 bytes each (id, health, death,
    /// position, facing, animation, one-shot serial + id, plot, appearance, faction), so
    /// a full 32 is 1293 bytes with the header — inside one datagram with modest slack;
    /// the fit is asserted by a test. A module can hold more creatures than this: the
    /// census takes hostiles first, then the nearest bystanders, so the cap sheds the
    /// least visible bodies rather than arbitrary ones.
    /// </summary>
    public const int MaxEnemies = 32;

    /// <summary>
    /// Enemies one peer can report damage against per datagram. 8 bytes each (object id +
    /// cumulative total), so a full 32 is 302 bytes with the header and the module string
    /// — a quarter of a datagram. It matches the plugin's own ledger capacity, which is
    /// the real limit: the ledger only holds enemies this instance has actually damaged
    /// and not yet seen die.
    /// </summary>
    public const int MaxEnemyDamageEntries = 32;

    /// <summary>
    /// Creature-liveness entries per <see cref="CreatureLivenessPacket"/> datagram.
    /// 5 bytes each (object id + a one-byte state), so a full 96 plus the header, module
    /// string and digest stays comfortably inside <see cref="MaxPacketSize"/> — asserted by
    /// a test. A module's full creature list can exceed this in one publish cycle; the extra
    /// entries ride a second (third, ...) page carrying the same digest and generation — see
    /// <see cref="CreatureLivenessPacket.PageIndex"/>.
    /// </summary>
    public const int MaxCreatureLivenessEntries = 96;

    /// <summary>Party companions the host replicates. KOTOR allows two active at once; a
    /// little headroom costs nothing.</summary>
    public const int MaxPartyMembers = 4;

    /// <summary>
    /// NPC roster slots. NINE, and this one is not a chosen cap — it is the engine's own
    /// bound, asserted in the binary rather than inferred: <c>CSWPartyTable::AddMember</c>
    /// (0x00565620) refuses <c>nNPC &lt; 0 || nNPC &gt;= 9</c> at 0x00565648, and the
    /// availability array at party table +0x30 is <c>int[9]</c>.
    ///
    /// The slot index IS the NPC identity in KOTOR (NPC_BASTILA, NPC_CARTH, ...), which is
    /// why the wire carries a bitmask and no separate id: there is nothing else to name.
    /// </summary>
    public const int PartyRosterSlots = 9;

    /// <summary>
    /// Encounters a client can report standing inside at once. Encounter activate areas are
    /// authored as disjoint polygons and the whole base game has at most 7 in a module
    /// (korr_m34aa), so overlapping four is already generous; the cap exists to bound the
    /// packet, not because a fifth is expected.
    /// </summary>
    public const int MaxEncounterTriggers = 4;

    /// <summary>
    /// Generic triggers a client can report standing inside at once, and the host's own
    /// per-module fired-set it broadcasts back. Matches <see cref="MaxEncounterTriggers"/>'s
    /// reasoning: activate polygons are authored disjoint, so overlapping a handful is
    /// already generous headroom rather than an observed need.
    /// </summary>
    public const int MaxTriggerRelayEntries = 4;

    /// <summary>
    /// Trigger ids the host reports as already-fired for one module, per
    /// <see cref="PacketType.TriggerRelay"/> broadcast. The early-game inventory in
    /// docs/research/trigger-relay.md finds at most 14 generic triggers in any one of the
    /// nine surveyed modules (end_m01aa) and the allowlist pattern narrows that further; 8 is
    /// headroom, not a measured ceiling — a module with more just has its fired-set catch up
    /// over a couple of extra ticks.
    /// </summary>
    public const int MaxTriggerFiredIds = 8;

    /// <summary>
    /// Native-fire entries (own-engine trigger fires, with origin peer) carried per
    /// <see cref="PacketType.TriggerRelay"/> packet, both directions: peer-&gt;host reports at
    /// most this many of ITS OWN recent natively-fired ids (<see cref="SharedBlock"/>'s mirror
    /// region caps at the same figure the plugin's watch region already uses); host-&gt;all
    /// rebroadcasts the merged per-module set, matched 1:1 against an origin-peer-id array of
    /// the same length. Same headroom reasoning as <see cref="MaxTriggerRelayEntries"/>.
    /// </summary>
    public const int MaxTriggerNativeFires = 4;

    /// <summary>
    /// Objects locked at once across the whole session. One conversation at a time is already
    /// the session rule, so this is headroom for the non-dialogue interactions the same
    /// primitive will carry (a container being looted, a workbench in use) rather than a
    /// number anyone is expected to reach.
    /// </summary>
    public const int MaxInteractionLocks = 4;

    /// <summary>
    /// The spectate line carried on the dialogue token.
    ///
    /// Deliberately larger than <see cref="MaxStringLength"/>: real KOTOR dialogue runs to
    /// ~200 characters and the shared 63-byte cap cut the spectator's subtitle mid-word —
    /// observed live as "Trask: That door's locked. I don't have the codes to open it, but
    /// swit". The dialogue token is a single string in a packet of its own, so the extra
    /// bytes cannot crowd out a peer snapshot. Still one length byte, so 255 is the hard
    /// ceiling.
    /// </summary>
    public const int MaxDialogueLineLength = 220;

    /// <summary>magic(2) + version(1) + type(1)</summary>
    public const int HeaderSize = 4;

    /// <summary>
    /// Comfortably inside the smallest common path MTU, so we never fragment. (Ethernet's
    /// 1500 less an IPv4 header and a UDP header leaves 1472; IPv6's guaranteed minimum is
    /// 1280 less 48 = 1232.)
    ///
    /// ⚠ Raised from 1200, which a FULL LOBBY did not fit in. Worst case per snapshot entry
    /// is 152 bytes — id, a 24-char name, and a player state whose module name and three
    /// equipment resrefs are all at their caps — so eight peers plus the header is 1241, and
    /// the whole snapshot was dropped. The guard test claimed to prove otherwise while
    /// quietly measuring a state with no equipment; see FullPeerSnapshotFitsInOneDatagram.
    /// Nothing on the wire encodes this value, so it is a buffer size and nothing more.
    /// </summary>
    public const int MaxPacketSize = 1400;

    /// <summary>Default cap for the short identity fields (names, resrefs, tags), chosen so
    /// a full snapshot with every one at maximum still fits a datagram. NOT the absolute
    /// limit — see <see cref="MaxLengthPrefixedString"/>.</summary>
    public const int MaxStringLength = 63;

    /// <summary>What a single length byte can express. The absolute upper bound on any
    /// length-prefixed string, whatever cap the field itself declares.</summary>
    public const int MaxLengthPrefixedString = 255;

    /// <summary>
    /// Per-field caps, chosen so a full <see cref="MaxPeers"/> snapshot with every string
    /// at maximum length still fits in one datagram. Without these the worst case is
    /// ~1265 bytes and every tick would be silently dropped once the session filled up.
    /// </summary>
    public const int MaxNameLength = 24;

    /// <summary>
    /// Odyssey resource references are 16 characters; 32 leaves room without bloating
    /// the snapshot.
    /// </summary>
    public const int MaxModuleLength = 32;

    /// <summary>Object tags identify the NPC a shared conversation is with. Odyssey tags are
    /// authored short; 32 matches the plugin's buffer.</summary>
    public const int MaxTagLength = 32;

    public const int DefaultPort = 27015;

    /// <summary>
    /// State broadcast rate. Raised from 20 Hz after play testing reported the peer lagging:
    /// once the phantom is position-driven rather than walked to a goal, the tick interval is
    /// visible latency rather than just goal freshness, so it is worth spending. The packets
    /// are small and the expensive world-state scanning runs on its own divided-down pass.
    /// </summary>
    public const int DefaultTickRate = 30;

    /// <summary>
    /// How far in the past remote peers are rendered. Remote motion is interpolated
    /// between two received snapshots rather than extrapolated, which trades a fixed
    /// latency for the absence of rubber-banding.
    ///
    /// This is now the dominant term in how far behind a peer looks, so it is set just above
    /// two tick intervals — enough that there is always a later snapshot to interpolate
    /// towards on a LAN, without paying for jitter that a LAN does not have.
    /// </summary>
    public const int DefaultInterpolationDelayMs = 80;

    public const int PeerTimeoutMs = 5000;

    /// <summary>
    /// How long a client waits in Connecting before giving up on a host that never
    /// answers Hello at all (as opposed to answering with a Reject, which is the
    /// wrong-password path and resolves immediately, never through this timeout). A dead
    /// host, a firewalled port or a mistyped address all look identical from here: total
    /// silence. 15 s is long enough that a genuinely slow LAN handshake still lands, short
    /// enough that a player is not left staring at "connecting..." for a full minute.
    /// </summary>
    public const int DefaultConnectTimeoutMs = 15000;

    public const int MaxPeers = 8;
}
