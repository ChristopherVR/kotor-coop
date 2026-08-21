using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// What the agent loop needs from a session, whether it is hosting or joining.
/// Deliberately poll-based: a game loop wants to ask "where is everyone right now",
/// not to be called back on a socket thread.
/// </summary>
public interface ICoopSession : IDisposable
{
    uint LocalPeerId { get; }

    bool IsConnected { get; }

    string LocalName { get; }

    EventFlagStore Flags { get; }

    SessionStats GetStats();

    /// <summary>Publishes the local avatar. Sent on the next tick.</summary>
    void PublishLocalState(in PlayerState state);

    /// <summary>Every remote peer, interpolated to the current render time.</summary>
    IReadOnlyList<RemotePeer> GetRemotePeers();

    /// <summary>Requests a world flag change. Delivered reliably.</summary>
    void SetFlag(uint flagId, sbyte value);

    /// <summary>Items currently lying on the ground, shared across the session.</summary>
    GroundItemStore Items { get; }

    /// <summary>
    /// Announces an item dropped by the local player. Returns the minted item id, or 0
    /// when the drop was refused (not connected yet, or the ground is full).
    ///
    /// <paramref name="identity"/> is the per-instance state read off the live item — stack
    /// size, charges, upgrades, identified — so the far side re-creates THIS item rather
    /// than a pristine one off the blueprint. Defaulting it to
    /// <see cref="ItemIdentity.Unknown"/> is deliberate: a caller that only knows a
    /// blueprint name must be able to say so, because inventing a stock identity would look
    /// exactly like a genuine reading of a stock item.
    /// </summary>
    uint DropItem(
        string resref, string module, Vector3f position, ItemIdentity identity = default);

    /// <summary>Announces that a ground item was picked up (or otherwise vanished) here.</summary>
    void RemoveItem(uint itemId);

    /// <summary>
    /// True when this peer owns enemy state. Exactly one peer does — the host — and it is
    /// the only one whose fights actually kill anything; everyone else mirrors it.
    /// </summary>
    bool IsEnemyAuthority { get; }

    /// <summary>Host only: publish the authoritative enemy list, with the module its object
    /// ids were read in. Ignored on a client.</summary>
    void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies);

    /// <summary>
    /// Client only: the host's latest enemy list, but only if the host read it in
    /// <paramref name="module"/> — object ids are per module load, so a snapshot taken
    /// somewhere else names different creatures here. Empty on the host, and empty
    /// whenever the two are apart.
    /// </summary>
    IReadOnlyList<EnemyState> GetEnemies(string module);

    /// <summary>
    /// Client only: report how much damage this instance has DEALT to each enemy, as a
    /// running cumulative total. Without this a client's fighting is cosmetic — its copies
    /// are pinned at 1 hp and only the owner's engine can actually hurt anything.
    ///
    /// This replaced reporting HEALTH, and the difference matters: health is a level that
    /// has to be sampled, and the owner's mirror wrote its own value back over the client's
    /// copy between samples, so most of a lone player's fight never crossed the wire.
    /// <paramref name="generation"/> changes whenever the sender's totals restart, which is
    /// how the owner tells a reset from a stale datagram.
    /// </summary>
    void ReportEnemyDamage(string module, uint generation, IReadOnlyList<EnemyDamageEntry> damage)
    {
    }

    /// <summary>
    /// Host only: every client's latest damage report for the given module, fresh ones
    /// only, one row per (peer, enemy). The host turns each increase into real damage on
    /// its own copy of that creature.
    /// </summary>
    IReadOnlyList<PeerEnemyDamage> GetEnemyDamage(string module) => [];

    /// <summary>
    /// Tells the peer that owns <paramref name="targetPeerId"/> how much damage their
    /// phantom has taken here in total, so their real character can take it too. Sent every
    /// tick as a running total rather than per hit: the receiver applies the difference, so
    /// a lost datagram costs a tick of staleness and a duplicate costs nothing.
    /// </summary>
    void PublishPhantomDamage(uint targetPeerId, uint cumulativeDamage, uint attackerId)
    {
    }

    /// <summary>
    /// The running total of damage this player owes for hits taken on their phantom in
    /// other instances, and who last landed one. (0, 0) when nobody has hit them.
    /// </summary>
    (uint CumulativeDamage, uint AttackerId) GetIncomingPhantomDamage() => (0, 0);

    /// <summary>
    /// Tells the peer that owns <paramref name="targetPeerId"/> that a non-damage effect
    /// (stun, paralyze, knockdown, slow, frightened, sleep, choke, Force push/whirlwind)
    /// landed on their phantom here, so it can be replayed on their real character. The
    /// non-damage sibling of <see cref="PublishPhantomDamage"/> — see
    /// docs/research/effect-sync.md.
    ///
    /// Unlike phantom damage this is a discrete EVENT, not a running total: queued for the
    /// next tick's send and gone once sent, exactly like <see cref="PublishBark"/>.
    /// </summary>
    void PublishPeerEffect(uint targetPeerId, PeerEffectKind kind, uint durationMs, uint magnitude) { }

    /// <summary>Effects a peer's hook captured landing on THIS player's phantom, since the
    /// last call, oldest first. Draining, so each is handed out exactly once — the same
    /// discipline <see cref="DrainBarks"/> uses, and for the same reason: these are discrete
    /// events, not a level to be sampled.</summary>
    IReadOnlyList<PeerEffectPacket> DrainPeerEffects() => [];

    /// <summary>
    /// Client only: "my player is standing inside these encounters." A client's own
    /// encounters are disarmed (they would drift the object-id counter), so this is how a
    /// client walking into an ambush gets one — the host runs the engine's eligibility test
    /// against that client's phantom and lets its engine spawn. Sent every tick as the
    /// current SET, not as an enter event. A host is a no-op: its engine fires its own.
    /// </summary>
    void PublishEncounterTriggers(string module, IReadOnlyList<uint> encounterIds) { }

    /// <summary>
    /// Client only: "my player is standing inside these GENERIC triggers" — a
    /// <c>CSWSTrigger</c> whose OnEnter starts a cutscene/dialogue/scripted event, never a
    /// trap or an area transition (the plugin's own watch already excludes those). Unlike
    /// <see cref="PublishEncounterTriggers"/>'s triggers, the client's OWN engine already
    /// fires these correctly for the client's own real character — this is purely so the
    /// HOST'S engine can run the same OnEnter script against that peer's phantom, keeping the
    /// host's object-id counter in lockstep with whatever the script created or destroyed.
    /// Sent every tick as the current SET. A host is a no-op: it fires its own, and does not
    /// need to be told what its own real player is standing in. See
    /// docs/research/trigger-relay.md.
    /// </summary>
    void PublishTriggerEntries(string module, IReadOnlyList<uint> triggerIds) { }

    /// <summary>
    /// Host only: the generic triggers clients are asking for right now, in
    /// <paramref name="module"/>, deduplicated and with anything already fired in that module
    /// removed. Empty on a client. See <see cref="TriggerRelayStore.Collect"/>.
    /// </summary>
    IReadOnlyList<TriggerFireRequest> GetTriggerFireRequests(string module) => [];

    /// <summary>
    /// The trigger ids known fired in <paramref name="module"/>: on the HOST, its own
    /// authoritative fired-set (<see cref="TriggerRelayStore.GetFiredIds"/>); on a CLIENT,
    /// the most recent such set the host has broadcast, so the client's watch can stop
    /// re-reporting ones already handled. Empty until the channel has said anything.
    /// </summary>
    IReadOnlyList<uint> GetFiredTriggerIds(string module) => [];

    /// <summary>
    /// Announces one NPC bark — the line an NPC throws into a bubble without ever opening a
    /// conversation, which the whole dialogue channel is blind to.
    ///
    /// <paramref name="text"/> and <paramref name="soundResRef"/> are the RESOLVED CONTENTS
    /// of the engine's arguments, not the arguments: <c>BarkString</c> takes a
    /// <c>CExoString*</c> and a <c>CResRef*</c>, and forwarding those pointers verbatim is
    /// what used to kill the receiving process (docs/research/bark-replay.md).
    /// </summary>
    void PublishBark(
        string module, uint speaker, byte[] text, byte[] soundResRef, uint flags, uint serial) { }

    /// <summary>Barks other peers have produced in <paramref name="module"/> since the last
    /// call, oldest first. Draining, so each is handed out exactly once.</summary>
    IReadOnlyList<BarkPacket> DrainBarks(string module) => [];

    /// <summary>
    /// Announces the movies this instance's imminent module transition is about to play, so
    /// whoever follows can queue the same ones and see the same cinematic.
    ///
    /// Sent BEFORE the transition, because there is no after: the queue is drained by the
    /// load and a playing movie parks the announcer's main thread for seconds. Repeat calls
    /// with the same <paramref name="serial"/> are the sender's own redundancy against a
    /// dropped datagram and are de-duplicated by the receiver.
    /// </summary>
    void PublishTransitionMovies(string module, uint serial, string[] movies) { }

    /// <summary>Transition-movie announcements from other peers since the last call, oldest
    /// first. NOT module-filtered, unlike <see cref="DrainBarks"/>: these name no object ids,
    /// and the announcer is by definition about to leave the module it announced from.</summary>
    IReadOnlyList<TransitionMoviePacket> DrainTransitionMovies() => [];

    /// <summary>
    /// Host only: the encounters clients are asking for right now, in
    /// <paramref name="module"/>, deduplicated. Empty on a client.
    /// </summary>
    IReadOnlyList<EncounterFireRequest> GetEncounterFireRequests(string module) => [];

    /// <summary>
    /// Announces this instance's in-world content fingerprint (globals catalogue, save
    /// identity, current module + its GIT digest; each field 0/empty until resolved). Sent
    /// to the peer so a mismatch only knowable in-world refines the negotiated
    /// capabilities — and, because the module fields track wherever the player is standing,
    /// re-judged on every module change. Cheap to call every pump; the session only puts
    /// it on the wire when it changes or periodically.
    /// </summary>
    void PublishWorldDigest(in WorldDigest digest);

    /// <summary>Host only: publish the authoritative party companions. Ignored on a client.</summary>
    void PublishParty(IReadOnlyList<PartyMemberState> members);

    /// <summary>Client only: the host's latest party companions. Empty on the host.</summary>
    IReadOnlyList<PartyMemberState> GetPartyMembers();

    /// <summary>
    /// Host only: publish the authoritative companion ROSTER — which of the nine NPC slots are
    /// recruited and which are in the active party. Ignored on a client.
    ///
    /// Separate from <see cref="PublishParty"/>, which carries where the companions ARE. This
    /// carries who they are, which is the thing that channel keys on: a companion recruited or
    /// dismissed inside a conversation changes only this, and until it crossed the wire the
    /// other instance kept resolving slot N to whatever it happened to have there.
    /// </summary>
    void PublishPartyRoster(PartyRoster roster) { }

    /// <summary>
    /// Client only: the host's latest roster. False until the first level has arrived — a
    /// client must never apply an all-zero roster it has merely not been told yet, which would
    /// dismiss its whole party.
    /// </summary>
    bool TryGetPartyRoster(out PartyRoster roster)
    {
        roster = PartyRoster.Empty;
        return false;
    }

    /// <summary>
    /// Announces this instance's local conversation status. <paramref name="active"/> true
    /// means the local player has entered a conversation; the session runs it through the
    /// host's arbiter. Speaker/line are the spectate payload (empty until the plugin can read
    /// the live conversation). Cheap to call every tick.
    /// </summary>
    void PublishDialogue(
        bool active, string speakerName, string line,
        string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
        int nodeIndex = DialogueState.NoNode, uint nodeSerial = 0);

    /// <summary>The authoritative conversation token — who, if anyone, is mid-dialogue.</summary>
    DialogueState GetDialogue();

    /// <summary>
    /// Claims (or stops claiming) a single world object — "hold this one still, I am using
    /// it". An empty <paramref name="tag"/> claims nothing and only keeps the session's idea
    /// of the current module fresh, so this is safe and expected to call every tick.
    ///
    /// A LEASE: the claim is re-sent while it is held and expires on its own if the claimant
    /// goes away. There is no release message to lose, which is the point — see
    /// <c>InteractionLockStore</c>.
    /// </summary>
    void PublishInteractionClaim(string module, string tag, InteractionKind kind, bool active) { }

    /// <summary>Every object currently held, by anyone, in the local module. Each peer applies
    /// this to the objects IT owns.</summary>
    IReadOnlyList<InteractionLock> GetInteractionLocks() => [];

    /// <summary>Host only: publish the party's gold and the PC's experience. Ignored on a client.</summary>
    void PublishProgression(uint gold, uint experience);

    /// <summary>
    /// Client only: this peer's contribution to the shared purse and its own experience total.
    ///
    /// Gold is a cumulative earned/spent LEDGER, never a balance — a balance cannot be merged
    /// (two instances holding 400 and 1200 have no arithmetic that says which is right) while
    /// "this peer has taken 250 out of the purse since it joined" can be applied exactly once
    /// by the host, in any order, however many times it arrives.
    ///
    /// Experience is ABSOLUTE and merges upward by max. It cannot be a delta: both engines
    /// already grant kill XP independently (a mirrored enemy dies through the real damage
    /// pipeline, so <c>ApplyDeathExperience</c> runs on the mirror too), so forwarding a delta
    /// would count every kill twice. What diverges is script-granted XP, and a max carries
    /// that without disturbing the rest.
    /// </summary>
    void ReportProgression(uint generation, uint goldEarned, uint goldSpent, uint experience) { }

    /// <summary>Host only: every client's latest progression contribution, fresh ones only.
    /// Empty on a client.</summary>
    IReadOnlyList<PeerProgression> GetProgressionReports() => [];

    /// <summary>
    /// Client only: the host's latest gold and experience. Returns false until a snapshot has
    /// arrived, so a client never mirrors zero over its own values before the first one.
    /// </summary>
    bool TryGetProgression(out uint gold, out uint experience);

    /// <summary>Sends a short text message to the other players. Empty/whitespace is ignored.</summary>
    void SendChat(string text);

    /// <summary>
    /// Returns and clears the chat messages received since the last call, oldest first. The
    /// sender's own lines are not returned here — the caller echoes them on send.
    /// </summary>
    IReadOnlyList<ChatMessage> DrainChat();

    /// <summary>
    /// Announces that THIS peer looted <paramref name="itemId"/> out of container
    /// <paramref name="containerId"/>, delivered reliably to every OTHER peer so the item
    /// vanishes from their copy of the container too. Symmetric: a client calls this exactly
    /// like the host does. <c>CoopHost</c> broadcasts directly; <c>CoopClient</c> sends to the
    /// host, which relays to every other client (including back down to the reporting client
    /// itself, harmlessly — see <see cref="ContainerLootRemovals"/>).
    /// </summary>
    void PublishContainerLoot(uint containerId, uint itemId) { }

    /// <summary>
    /// The (container, item) removals known here — this peer's own plus every other peer's,
    /// merged — which the agent re-declares to the game each tick so every side's copy of every
    /// shared container converges. Symmetric: non-empty on both host and client. A removal
    /// already applied locally is never re-reported by <see cref="PublishContainerLoot"/>
    /// (the underlying store recognises it as already known and swallows the echo), which is
    /// what keeps a peer's own removal from bouncing back to it as a "new" one.
    /// </summary>
    IReadOnlyList<ContainerLootRemoval> ContainerLootRemovals => [];

    /// <summary>
    /// Host only: announces that <paramref name="ownerEnemyId"/> — one of the host's own
    /// enemies — just died holding <paramref name="items"/>, delivered reliably to every client
    /// so a mirroring peer's materialised stand-in (if it has one for this id — see
    /// docs/research/enemy-materialisation.md §6) can receive the same real loot. A client is a
    /// no-op: only the host's engine ever holds the real corpse to read this from.
    /// </summary>
    void PublishMaterialisedLoot(uint ownerEnemyId, IReadOnlyList<MaterialisedLootItem> items) { }

    /// <summary>
    /// Client only: every materialised-enemy death-loot packet received since the last drain,
    /// oldest first, for the agent to apply and clear. Empty on the host, which never receives
    /// this (it is the only sender). Matches <see cref="DrainTransitionMovies"/>'s shape.
    /// </summary>
    IReadOnlyList<MaterialisedLootPacket> DrainMaterialisedLoot() => [];

    /// <summary>
    /// Client only: forgets every owner-enemy id already deduped by
    /// <see cref="DrainMaterialisedLoot"/>'s receive path so far. Owner enemy ids are
    /// reassigned per module load; without this, an id reused in the next module would be
    /// silently treated as "already seen" and its real loot would never reach the queue. The
    /// agent calls this exactly once, right after it notices the local module has changed (see
    /// docs/research/enemy-materialisation.md §6). A no-op on the host, which is never the
    /// receiving side of this channel.
    /// </summary>
    void ResetMaterialisedLootDedup() { }

    /// <summary>
    /// Announces that THIS peer's own conversation just placed <paramref name="items"/> into
    /// the local speaker's inventory (docs/research/dialogue-consequences.md §3), delivered
    /// reliably to every OTHER peer so their instance can give the same items. Symmetric like
    /// <see cref="PublishContainerLoot"/>: <c>CoopHost</c> broadcasts directly; <c>CoopClient</c>
    /// sends to the host, which relays to every other client under this peer's TRUE (origin,
    /// sequence) identity rather than re-attributing it, so a receiver can tell "a peer granted
    /// this" from "I am seeing my own report echo back" — see <see cref="DrainDialogueGrants"/>.
    /// A no-op when <paramref name="items"/> is empty.
    /// </summary>
    void PublishDialogueGrant(string partnerTag, IReadOnlyList<MaterialisedLootItem> items) { }

    /// <summary>
    /// Every dialogue-grant packet received from ANOTHER peer since the last drain, oldest
    /// first, for the agent to apply through the same give-loot cheat channel the materialised-
    /// loot path already uses. A grant whose origin is THIS peer's own id — the harmless echo
    /// <see cref="PublishDialogueGrant"/>'s own doc names — is filtered out here rather than
    /// left for the caller to notice, because applying it a second time is not idempotent the
    /// way a container-loot echo is (giving an item mints a fresh one every call).
    /// </summary>
    IReadOnlyList<DialogueGrantPacket> DrainDialogueGrants() => [];

    /// <summary>The shared quest journal, merged upward across every player.</summary>
    JournalStore Journal { get; }

    /// <summary>Announces a quest journal advance. Delivered reliably.</summary>
    void SetJournalEntry(string questTag, int state);

    /// <summary>
    /// The replication channels this session is currently allowed to use, after content
    /// compatibility with the peer(s) has been judged. Starts at <see
    /// cref="SessionCapabilities.All"/> and narrows if a mod mismatch is detected. The
    /// agent's pump consults this before each channel so a differently-modded pair still
    /// plays, minus whatever their difference makes unsafe.
    /// </summary>
    SessionCapabilities Capabilities { get; }

    /// <summary>The current compatibility verdict, for surfacing on the dashboard. Null
    /// until a peer has been evaluated.</summary>
    CompatibilityVerdict? Compatibility { get; }

    /// <summary>
    /// This session's transport fault injector, or null for a session with no real socket
    /// (the test stubs). Off by default. Driving it is how a drop is reproduced on the live
    /// rig without touching a cable — see <c>coop netsim</c> in <see cref="ConsoleCommand"/>.
    /// </summary>
    NetworkSim? Network => null;

    Task RunAsync(CancellationToken cancellationToken);
}

public sealed record SessionSettings
{
    public string PlayerName { get; init; } = "player";

    /// <summary>
    /// What this host calls itself in other players' server browsers (coop.ini
    /// <c>server_name</c>). Empty falls back to <see cref="PlayerName"/> — the browser must
    /// always have something to show, and the character's name is what it showed before this
    /// existed.
    /// </summary>
    public string ServerName { get; init; } = string.Empty;

    /// <summary>The name this host advertises: its server name when set, else the player's.</summary>
    public string AdvertisedName =>
        ServerName.Length > 0 ? ServerName : PlayerName;

    public int Port { get; init; } = ProtocolConstants.DefaultPort;

    public int TickRate { get; init; } = ProtocolConstants.DefaultTickRate;

    public int InterpolationDelayMs { get; init; } = ProtocolConstants.DefaultInterpolationDelayMs;

    public int PeerTimeoutMs { get; init; } = ProtocolConstants.PeerTimeoutMs;

    /// <summary>
    /// This install's content fingerprint, announced to the peer so a mod mismatch that
    /// would corrupt shared state is caught at join time. Defaults to
    /// <see cref="ContentDigest.Unknown"/> (agents without game-file access, and the mock
    /// harness), which compares as "unchecked" and never blocks a session.
    /// </summary>
    public ContentDigest Digest { get; init; } = ContentDigest.Unknown;

    /// <summary>
    /// Whether a peer on a different save is refused outright rather than admitted with the
    /// object-id-keyed channels switched off. Off by default: world adoption makes the saves
    /// match by construction, so a mismatch is a failed adoption to be reported and worked
    /// around, not a session to end. Mirrors
    /// <see cref="ContentCompatibility.RequireIdenticalSave"/>.
    /// </summary>
    public bool RequireIdenticalSave { get; init; }

    /// <summary>
    /// Session password. Empty means an open session. A host with a password admits only
    /// clients that present the same one; a client sends it in its join request. Reduced
    /// to a hash on the wire — a stranger-gate, not a security boundary.
    /// </summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// This player's stable identity, independent of its network address — the value a client
    /// puts in its Hello nonce and the host keys its peer slot on. 0 (the default) means "mint
    /// a fresh one for this session", which is the behaviour every caller had before this
    /// existed; a caller with somewhere to persist one uses <see cref="PeerIdentity"/>.
    ///
    /// Ignored by a host, which is peer 1 by definition and never dials anyone.
    /// </summary>
    public uint PeerIdentity { get; init; }

    /// <summary>
    /// How long a CLIENT'S connect attempt may sit silent before <see cref="SessionManager"/>
    /// gives up and reports "no reply from &lt;endpoint&gt;". Never applies to a host — a host
    /// has nobody to be admitted by, so there is nothing to time out waiting for.
    /// </summary>
    public int ConnectTimeoutMs { get; init; } = ProtocolConstants.DefaultConnectTimeoutMs;
}
