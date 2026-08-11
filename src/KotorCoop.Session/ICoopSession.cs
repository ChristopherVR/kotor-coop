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
    /// </summary>
    uint DropItem(string resref, string module, Vector3f position);

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
    /// Client only: "my player is standing inside these encounters." A client's own
    /// encounters are disarmed (they would drift the object-id counter), so this is how a
    /// client walking into an ambush gets one — the host runs the engine's eligibility test
    /// against that client's phantom and lets its engine spawn. Sent every tick as the
    /// current SET, not as an enter event. A host is a no-op: its engine fires its own.
    /// </summary>
    void PublishEncounterTriggers(string module, IReadOnlyList<uint> encounterIds) { }

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
    /// Host only: announces that the host looted <paramref name="itemId"/> out of container
    /// <paramref name="containerId"/>, delivered reliably to every client so the item vanishes
    /// from their copy of the container too. A client is a no-op (loot is host-authoritative).
    /// </summary>
    void PublishContainerLoot(uint containerId, uint itemId) { }

    /// <summary>
    /// Client only: the (container, item) removals the host has announced, which the agent
    /// re-declares to the game each tick. Empty on the host, which never mirrors loot.
    /// </summary>
    IReadOnlyList<ContainerLootRemoval> ContainerLootRemovals => [];

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

    Task RunAsync(CancellationToken cancellationToken);
}

public sealed record SessionSettings
{
    public string PlayerName { get; init; } = "player";

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

    /// <summary>Whether to admit a peer whose save differs, degrading to id-independent
    /// channels instead of refusing. Mirrors <see cref="ContentCompatibility.AllowDifferentSave"/>.</summary>
    public bool AllowDifferentSave { get; init; }

    /// <summary>
    /// Session password. Empty means an open session. A host with a password admits only
    /// clients that present the same one; a client sends it in its join request. Reduced
    /// to a hash on the wire — a stranger-gate, not a security boundary.
    /// </summary>
    public string Password { get; init; } = string.Empty;
}
