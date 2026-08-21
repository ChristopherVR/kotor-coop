namespace KotorCoop.Protocol;

public static class ProtocolConstants
{
    /// <summary>Two byte magic ('K','C') so stray traffic on the port is discarded cheaply.</summary>
    public const ushort Magic = 0x4B43;

    /// <summary>Bumped on any wire-format change. Mismatched peers are rejected at Hello.
    /// Version 3: PlayerState flags dword, journal channel, client enemy reports.
    /// Version 4: content digest on Hello/Welcome for mod-compatibility handshaking.
    /// Version 5: session password hash on Hello; party-member snapshot channel.
    /// Version 6: dialogue ownership token (snapshot + request channels).
    /// Version 7: text chat channel.
    /// Version 8: shared progression (party gold + PC experience).
    /// Version 9: world digest carries save identity + per-module GIT digest.
    /// Version 10: dialogue token carries the partner tag and the owner's reply picks, so a
    /// spectator runs the same conversation in the game's own UI instead of reading a
    /// transcript; the spectate line grew past the shared string cap.
    /// Version 11: the dialogue token carries the owner's pick LOG (each pick stamped with
    /// the line it was made on) so a spectator replays picks on the right node and a late
    /// joiner catches up.
    /// Version 12: the enemy snapshot carries presence (position, facing, animation, and
    /// per-enemy one-shot swings) so a mirroring instance drives its copies to match the
    /// owner's fight instead of running a private one. Reports still carry health alone.
    /// Version 13: the snapshot includes PLOT creatures (cutscene actors) with a plot flag
    /// — presence-only, exempt from the combat half — so scripted scenes play out
    /// identically on both screens.
    /// Version 14: the enemy snapshot carries the MODULE its object ids were read in, so a
    /// peer somewhere else drops it instead of writing the owner's healths and positions
    /// onto whatever local creatures happen to share those ids. The report direction has
    /// always carried its module; this is the same guard the other way.
    /// Version 15: peer slots are STICKY per peer id rather than positional, and a relayed
    /// phantom-damage report carries the ORIGINATOR's peer id rather than the relaying
    /// host's — with three or more players the positional scheme billed the wrong person
    /// and a leaver's slot handed their body, gear and damage total to a survivor.
    /// Version 16: EncounterTrigger — a client reports the encounters it is standing inside
    /// and the host's engine does the spawn. A client's own encounters are disarmed (an
    /// encounter spawn there would advance that instance's object-id counter, which every
    /// other channel keys on), so before this a joiner could walk into an ambush and nothing
    /// happened until the host walked in too.
    /// Version 17: the peer direction stops reporting enemy HEALTH and reports DAMAGE DEALT
    /// instead (EnemyDamage 33 replaces EnemyReport 22). Health was a sample the owner's
    /// mirror could overwrite before it was ever read, and it could not say who dealt what;
    /// a cumulative sum of damage is monotonic, order-independent and un-eraseable.
    /// Version 18: EnemyState carries the appearance.2da row and repute.2da faction id, so
    /// a mirror can MATERIALISE an enemy that exists only in the owner's instance (an
    /// encounter or script spawn) — before this such an enemy was invisible on the mirror
    /// while still able to hurt its player through the phantom.
    /// Version 19: Bark (34) — the one-line NPC bubbles that never touch the dialogue system
    /// and so reached no other channel at all.
    /// Version 20: InteractionClaim (35) / InteractionLock (36) — a per-object lock the
    /// object's OWNER honours. Ownership was one-directional (shared-state-audit §4): a
    /// client could open a conversation with a host-owned NPC and the host's AI, never told,
    /// kept walking him around. Shaped as a LEASE so a client that dies mid-conversation
    /// cannot strand the NPC.
    /// Version 21: DialogueState carries the owner's NODE — the index of the dialogue entry
    /// on screen — and the follower is driven onto that entry instead of replaying the reply
    /// SLOT the owner pressed. Slot replay can never converge: which entry a conversation
    /// starts on, which replies are offered, and where a reply leads are all decided by
    /// scripts that run separately on each instance, so a slot does not name the same branch
    /// on two machines. The pick fields stay on the wire as a diagnostic.
    /// Version 22: PartyRoster (37) — the host's nine companion slots as two masks
    /// (available, in-party), broadcast as a LEVEL. AddPartyMember / RemovePartyMember had no
    /// channel at all, so a conversation that recruited or dismissed a companion left the two
    /// instances holding different rosters — and the roster is the key the whole companion
    /// channel resolves companions by.
    /// Version 23: ProgressionReport (38) — the peer direction of the progression channel,
    /// which had none. The host published absolute gold and XP and every client OVERWROTE its
    /// own with them, so a client's plot XP was erased within a tick and a client could not
    /// spend gold at all (the host's value restored it). Gold now travels as a cumulative
    /// earned/spent LEDGER the host spends against its own party table — the balance cannot be
    /// merged, but "this peer has taken 250 out of the purse" can — while XP travels as an
    /// absolute merged by max, because both engines already grant kill XP independently and a
    /// delta would count every kill twice.
    /// Version 24: a ground item carries its IDENTITY (stack size, charges, max charges,
    /// installed-upgrade mask, and the identified/stolen/non-equippable bits) and not just
    /// its blueprint resref. Before this the receiving instance re-created a pristine stock
    /// item, so a half-spent stack of twenty arrived as one fresh one and an upgraded
    /// lightsaber arrived with its upgrades stripped. A zeroed identity still means
    /// "unknown", which is what the harness sends and what the receiver leaves alone.
    /// Version 25: TransitionMovies (40) — the movies a transition is about to play,
    /// announced before it happens so a follower's own transition plays them too. The
    /// follower was never gated; its move-to-module queue was simply empty, so it loaded
    /// straight in while the owner's main thread was gone for 7,407 ms playing 01c. Packet
    /// type 39 is deliberately skipped: network-topology.md reserves it on paper.
    /// Version 26: PlayerState carries FlagInMinigame — set while the engine's own
    /// CSWCModule::GetMiniGame is non-null (a swoop race or a forced turret sequence). A
    /// peer's instance parks that phantom instead of mirroring it, and the minigame
    /// player's own instance stops applying forwarded damage for the duration; see
    /// docs/research/minigames.md.</summary>
    /// Version 27: the world snapshot carries a content DIGEST beside each of the flag, item
    /// and journal version counters, plus a version and digest for container loot, which had
    /// neither. A version counter alone counts local CHANGES, not content: two stores that
    /// diverged by the same number of changes during an outage tie and never ask for a
    /// resync (docs/research/disconnect-reconnect.md §4.1). The digest is an
    /// order-independent fingerprint of what is actually held (KotorCoop.Session.EntryDigest),
    /// so it catches that tie. LootSyncRequest (43) is new for the same reason §4.8 flags:
    /// container loot was retransmit-only with no tripwire of its own, so a straggler under
    /// sustained loss had nothing behind it but the retransmit.
    /// Version 28: PeerEffect (44) — non-damage combat effects (stun, paralyze, knockdown,
    /// slow, frightened, sleep, choke, Force push/whirlwind) landing on a peer's phantom,
    /// forwarded to that peer's real character. The non-damage sibling of PhantomDamage, which
    /// only ever carried plain damage; before this a Force-Stun on a peer's phantom left them
    /// free to act on their own screen. Gated OFF by default (coop.ini effect_sync) until
    /// live-verified — see docs/research/effect-sync.md.</summary>
    public const byte Version = 28;

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
