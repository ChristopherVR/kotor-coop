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
    /// Version 40: <see cref="HelloPacket"/>/<see cref="WelcomePacket"/> gain a <see
    /// cref="BehaviourDigest"/> — coop.ini/CLI behaviour flags (local_combat, party_sync,
    /// follow_lead, interp_ms and 16 more) are now negotiated the way game content already
    /// is: a difference always warns (<c>W_CFG</c>); a difference on a host-authoritative
    /// channel additionally makes the client adopt the host's value at runtime. See
    /// docs/research/mod-compatibility.md's "Behaviour-flag negotiation" section and
    /// docs/roadmap-seamless.md Tier 1 item 7.
    ///
    /// Version 41: companion-state sync — <see cref="PacketType.CompanionXpSnapshot"/> (51) /
    /// <see cref="PacketType.CompanionXpReport"/> (52) are new packet types, and
    /// <see cref="PartyMemberState"/> gained three equip fields on the existing
    /// <see cref="PacketType.PartySnapshot"/> shape. See docs/research/companion-state-sync.md.
    ///
    /// Version 42: <see cref="PacketType.PartyPoolOp"/> (53) / <see cref="PacketType.PartyPoolAck"/>
    /// (54) / <see cref="PacketType.PartyPoolSyncRequest"/> (55) — the shared party item-pool
    /// channel (<c>party_pool_sync</c>, default OFF, NEEDS-LIVE). See docs/research/party-pool-sync.md.
    ///
    /// Version 43: <see cref="WorldSnapshotPacket"/> gains <c>PageIndex</c>/<c>PageCount</c> —
    /// a full lobby's aggregate entry size that would overrun <see cref="MaxPacketSize"/> now
    /// degrades to several datagrams per tick (graceful pagination, like
    /// <see cref="PacketType.CreatureLiveness"/> already does for its own wider publish)
    /// instead of being silently dropped whole; the flag/item/journal/loot version+digest
    /// tripwires ride on page 0 only, so a later page costs fewer bytes. See
    /// docs/research/lag-audit.md's 2026-08-27 entry.
    ///
    /// Version 44: container-lock explicit arbitration — <see cref="PacketType.LockResponse"/> (56)
    /// is new and <c>InteractionClaimPacket</c> gained <c>RequestSerial</c>. See
    /// docs/research/container-sync.md.
    ///
    /// Version 45: <see cref="PacketType.StoreOp"/> (57) / <see cref="PacketType.StoreAck"/> (58) /
    /// <see cref="PacketType.StoreSyncRequest"/> (59) — the merchant/store stock channel
    /// (<c>store_sync</c>, default OFF, NEEDS-LIVE). See docs/research/store-sync.md.
    ///
    /// Version 46: <see cref="PacketType.PartyPoolSnapshotV2"/>
    /// (60) / <see cref="PacketType.PartyPoolIntentV2"/> (61) / <see cref="PacketType.PartyPoolIntentAckV2"/>
    /// (62) / <see cref="PacketType.StoreSnapshotV2"/> (63) / <see cref="PacketType.StoreIntentV2"/> (64) /
    /// <see cref="PacketType.StoreIntentAckV2"/> (65) — the host-authoritative "absolute snapshot +
    /// client intents" redesign of the party pool and store channels
    /// (<c>party_pool_sync=2</c>/<c>store_sync=2</c>, default OFF, NEEDS-LIVE — mode 1, the
    /// existing op-log, is unchanged). See docs/research/shared-inventory-v2.md.
    ///
    /// Version 47: <see cref="PacketType.HostBye"/> (70) — the host's clean-shutdown broadcast
    /// that lets clients release every lock immediately instead of waiting out the silence
    /// timeout (<c>host_loss_release</c>). Also the flag snapshot became paged (same packet
    /// types, but a v46 peer would misread a paged snapshot). See
    /// docs/research/disconnect-reconnect.md §11 and world-adoption.md (2026-08-29).
    /// </summary>
    public const byte Version = 47;

    /// <summary>Journal quest tags are resrefs plus a little slack.</summary>
    public const int MaxJournalTagLength = 32;

    /// <summary>
    /// Journal entries per snapshot datagram: tag (up to 33 bytes) + state (4) each, so
    /// 28 stays inside <see cref="MaxPacketSize"/>. A longer journal is sent as several
    /// snapshot packets in one burst.
    /// </summary>
    public const int MaxJournalEntriesPerPacket = 28;

    /// <summary>
    /// Flag entries per snapshot datagram: <c>FlagId</c> 4 + <c>Value</c> 1 + <c>Serial</c> 4 +
    /// <c>OriginPeerId</c> 4 = 13 bytes each, after a 12-byte header, so 106 is what fits inside
    /// <see cref="MaxPacketSize"/>. A longer store is sent as several snapshot packets in one
    /// burst, exactly like <see cref="MaxJournalEntriesPerPacket"/>.
    ///
    /// ⚠ THIS USED TO BE A SILENT TRUNCATION, AND IT WAS THE HOST-TO-CLIENT BACKFILL PATH FOR
    /// DOORS. There are no host→client flag OPS — <c>CoopHost.SetFlag</c> applies locally and
    /// says "clients pick them up through the version tripwire" — so this one packet IS the
    /// only way a host-side door open, placeable search or global ever reaches a client that
    /// did not see it happen. Doors and placeables mint keys into the SAME
    /// <c>EventFlagStore</c> as globals and nothing is ever pruned from it, so a session
    /// reaches 106 entries in ordinary play, after which every later change was structurally
    /// undeliverable — and, because the digest is computed over the WHOLE store, the client
    /// then re-requested the same truncated head every 500 ms for the rest of the session.
    /// Live symptom: a door OPEN on the host, CLOSED on the client, forever.
    /// </summary>
    public const int MaxFlagEntriesPerPacket = 106;

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
    /// Distinct store stack keys (across every store, since <see cref="StoreStackKey"/> already
    /// carries the store's object id) a session remembers at once. Same reasoning and the same
    /// cap value as <see cref="MaxPartyPoolStacks"/> — see docs/research/store-sync.md.
    /// </summary>
    public const int MaxStoreStacks = 256;

    /// <summary>
    /// Party-pool snapshot entries per <see cref="PartyPoolSnapshotPacket"/> datagram
    /// (docs/research/shared-inventory-v2.md, <c>party_pool_sync=2</c>). ~28 bytes each
    /// (a length-prefixed <see cref="MaxResrefLength"/> resref + charges + max charges +
    /// upgrades + flags + a count), so a full 32 stays comfortably inside
    /// <see cref="MaxPacketSize"/> alongside the header/digest/generation/page overhead —
    /// asserted by a test. A pool holding more than 32 distinct stacks in one cycle (up to
    /// <see cref="MaxPartyPoolStacks"/>) rides a second (third, ...) page carrying the same
    /// digest and generation, exactly like <see cref="CreatureLivenessPacket"/>.
    /// </summary>
    public const int MaxPartyPoolSnapshotEntriesPerPacket = 32;

    /// <summary>
    /// Store snapshot entries per <see cref="StoreSnapshotPacket"/> datagram — the same
    /// reasoning as <see cref="MaxPartyPoolSnapshotEntriesPerPacket"/>, one entry wider (each
    /// entry additionally carries its own <c>StoreObjectId</c>) so the per-packet cap is
    /// slightly lower.
    /// </summary>
    public const int MaxStoreSnapshotEntriesPerPacket = 28;

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

    /// <summary>
    /// Peer entries per <see cref="WorldSnapshotPacket"/> page. Each entry is up to 152 bytes
    /// (id + a <see cref="MaxNameLength"/> name + a <c>PlayerState</c> whose module and three
    /// equipment resrefs are all at their caps), and page 0 additionally carries the
    /// flag/item/journal/loot version+digest tripwires (which ride on page 0 only — see
    /// <see cref="WorldSnapshotPacket"/>'s remarks), so 5 is sized against page 0's own
    /// tighter budget rather than a later page's roomier one — the fit is asserted by a test
    /// rather than by this arithmetic alone. A full <see cref="MaxPeers"/> lobby needs at most
    /// two pages this way, comfortably inside <see cref="WorldSnapshotPacket.MaxPages"/>. See
    /// docs/research/lag-audit.md's 2026-08-27 entry.
    /// </summary>
    public const int MaxSnapshotEntriesPerPage = 5;

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
    ///
    /// 2026-08-27: raising the cap only ever bought back one growth cycle — the next field
    /// added to <c>PlayerState</c>, <c>MaxNameLength</c>, or <see cref="MaxPeers"/> itself
    /// would have reopened the identical silent-drop failure. <see cref="WorldSnapshotPacket"/>
    /// now pages instead (<see cref="MaxSnapshotEntriesPerPage"/>,
    /// <see cref="WorldSnapshotPacket.MaxPages"/>), the same fix already shipped for
    /// <see cref="PacketType.CreatureLiveness"/>'s wider publish, so a future overrun degrades
    /// to more datagrams per tick rather than reopening this bug. See
    /// docs/research/lag-audit.md.
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
