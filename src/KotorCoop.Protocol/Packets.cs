namespace KotorCoop.Protocol;

public sealed record HelloPacket(
    string PlayerName, string Module, uint ClientNonce, ContentDigest Digest, uint PasswordHash);

public sealed record WelcomePacket(
    uint AssignedPeerId, uint SessionId, byte TickRate, byte MaxPeers, ContentDigest Digest);

public sealed record RejectPacket(string Reason);

public sealed record StateUpdatePacket(uint PeerId, uint Sequence, long ClientTimeMs, PlayerState State);

/// <summary>One peer's entry inside a <see cref="WorldSnapshotPacket"/>.</summary>
public sealed record SnapshotEntry(uint PeerId, string Name, PlayerState State);

/// <summary>
/// The host's authoritative view, broadcast every tick. <paramref name="FlagVersion"/> and
/// <paramref name="ItemVersion"/> are the cheap tripwires that tell a client its world
/// flags or ground items are stale, so full state only crosses the wire when it has
/// actually diverged.
/// </summary>
public sealed record WorldSnapshotPacket(
    long ServerTimeMs,
    uint FlagVersion,
    uint ItemVersion,
    uint JournalVersion,
    IReadOnlyList<SnapshotEntry> Entries);

/// <summary>
/// A world progression change (door opened, boss dead, quest advanced). This is the
/// channel that separates "two players see each other" from actual co-op, and in a real
/// seamless mod it is where nearly all the difficulty lives: deciding which of the game's
/// thousands of booleans are global truth and which must stay per-player.
/// </summary>
public sealed record FlagOpPacket(uint OriginPeerId, uint OpSequence, uint FlagId, sbyte Value);

public sealed record FlagAckPacket(uint OpSequence);

public sealed record FlagSyncRequestPacket(uint PeerId, uint KnownVersion);

/// <summary>
/// Values are <c>sbyte</c>, not <c>bool</c>: KOTOR's world state is a mix of booleans and
/// signed-byte numbers (quest stages, counters), and splitting a number across derived
/// boolean ids would break the merge-upwards rule that keeps the two worlds convergent.
/// </summary>
public sealed record FlagSnapshotPacket(uint Version, IReadOnlyList<KeyValuePair<uint, sbyte>> Flags);

public sealed record PingPacket(uint PeerId, long SentTimeMs);

public sealed record PongPacket(long OriginalSentTimeMs, long ServerTimeMs);

public sealed record ByePacket(uint PeerId);

/// <summary>
/// An item lying on the ground, shared by every player in the module. The id is minted by
/// the dropping peer as (peerId &lt;&lt; 16 | counter) so ids never collide across peers.
/// </summary>
public sealed record GroundItem(uint ItemId, string Resref, string Module, Vector3f Position);

public enum ItemOpKind : byte
{
    Drop = 1,
    Remove = 2,
}

/// <summary>
/// A ground-item change: someone dropped an item, or someone picked one up. Reliable via
/// retransmit-until-ack, exactly like <see cref="FlagOpPacket"/>. For a Remove, only
/// <paramref name="ItemId"/> matters and the item fields are empty.
/// </summary>
public sealed record ItemOpPacket(
    uint OriginPeerId,
    uint OpSequence,
    ItemOpKind Kind,
    GroundItem Item);

public sealed record ItemAckPacket(uint OpSequence);

/// <summary>
/// One container-loot removal: item <paramref name="ItemId"/> is no longer inside container
/// <paramref name="ContainerId"/>. Both are engine object ids, usable as cross-instance keys
/// because both games load the same save and the engine assigns ids deterministically in GIT
/// order — the same property doors and enemies rely on.
/// </summary>
public readonly record struct ContainerLootRemoval(uint ContainerId, uint ItemId);

/// <summary>
/// A container-loot removal op: the host took <paramref name="ItemId"/> out of container
/// <paramref name="ContainerId"/>, so it must vanish from that container on the client too (or
/// it could be picked up twice). Reliable via retransmit-until-ack, exactly like
/// <see cref="ItemOpPacket"/>, but broadcast host -&gt; clients rather than client -&gt; host.
/// </summary>
public sealed record ContainerLootPacket(
    uint OriginPeerId, uint OpSequence, uint ContainerId, uint ItemId);

/// <summary>
/// A client's acknowledgement of a <see cref="ContainerLootPacket"/>. Carries the acking
/// peer id so the host, which broadcasts to several clients, can tell whose ack this is.
/// </summary>
public sealed record ContainerLootAckPacket(uint PeerId, uint OpSequence);

public sealed record ItemSyncRequestPacket(uint PeerId, uint KnownVersion);

public sealed record ItemSnapshotPacket(uint Version, IReadOnlyList<GroundItem> Items);

/// <summary>
/// One enemy's authoritative state. <paramref name="ObjectId"/> is the engine's own object
/// id, which is usable as a cross-instance key because both games load the same save and
/// the engine assigns ids deterministically in GIT order — verified live, the same Sith
/// soldier is 0x14 in both instances.
///
/// <paramref name="Position"/>, <paramref name="Facing"/> and <paramref name="Animation"/>
/// are the owner's presence — where its engine has the body and what it is playing — so a
/// mirroring instance can drive its copy to match instead of letting two AIs fight two
/// private versions of the same battle. <paramref name="OneShotSerial"/> advances each
/// time the enemy plays a fire-and-forget (a swing, a parry, a weapon draw), with the id
/// in <paramref name="OneShotAnimation"/>, so the mirrored fight shows the strikes and
/// not just the positions. All presence rides only the snapshot (owner → everyone); a
/// client's <see cref="EnemyReportPacket"/> carries health alone and leaves them default.
/// </summary>
/// <remarks>
/// <paramref name="Plot"/> marks a creature the module declares un-killable — usually a
/// cutscene actor. Its PRESENCE replicates like any other creature's, so scripted scenes
/// play out identically on both screens, but the combat half of the channel must leave it
/// alone: no min-1-hp pin, no health mirror, no death, and a client's damage report for it
/// is ignored. Its health is the module's business, not the session's.
/// </remarks>
public sealed record EnemyState(
    uint ObjectId, ushort CurrentHealth, ushort MaxHealth, bool Dead,
    Vector3f Position = default, float Facing = 0f, uint Animation = 0,
    uint OneShotSerial = 0, ushort OneShotAnimation = 0, bool Plot = false);

/// <summary>
/// The host's view of every enemy. Absolute health, never damage deltas: the engine
/// applies difficulty multipliers, temporary hit points and damage resistance per
/// instance, so replaying "took 7 damage" on the far side does not produce 7.
/// </summary>
public sealed record EnemySnapshotPacket(long ServerTimeMs, IReadOnlyList<EnemyState> Enemies);

/// <summary>
/// One party companion as the host sees it. Keyed by <paramref name="SlotIndex"/> (the
/// NPC roster slot, 0..8) rather than an object id, because the slot is identical across
/// two instances of the same save while the runtime id is not. Absolute position, so a
/// lost packet is superseded next tick. <paramref name="OneShotSerial"/> and
/// <paramref name="OneShotAnimation"/> carry the companion's latest fire-and-forget
/// (a swing, a parry), same scheme as the enemy channel.
/// </summary>
public readonly record struct PartyMemberState(
    byte SlotIndex, Vector3f Position, float Facing, uint Animation, int Health,
    uint OneShotSerial = 0, ushort OneShotAnimation = 0);

/// <summary>
/// The host's authoritative party. In co-op the host owns the companions (its own engine
/// AI drives them); every client freezes its own copies (solo mode) and mirrors these, so
/// Trask and Carth do not drift into two uncoordinated versions.
/// </summary>
public sealed record PartySnapshotPacket(long ServerTimeMs, IReadOnlyList<PartyMemberState> Members);

/// <summary>
/// A client's local view of enemy health, sent each tick, unreliable. The module rides
/// along because enemy object ids reset across transitions — the host must never apply
/// a report from a client standing in a different (or stale) module.
/// </summary>
public sealed record EnemyReportPacket(uint PeerId, string Module, IReadOnlyList<EnemyState> Enemies);

/// <summary>host -> all: the authoritative conversation token (see <see cref="DialogueState"/>).</summary>
public sealed record DialogueSnapshotPacket(long ServerTimeMs, DialogueState State);

/// <summary>
/// The host's absolute shared progression, broadcast each tick. Party <paramref name="Gold"/>
/// and the PC's <paramref name="Experience"/> are whole values, not deltas — each instance
/// grants XP/gold locally on every kill, so a client mirrors the host's totals rather than
/// adding its own (which would double-count).
/// </summary>
public sealed record ProgressionSnapshotPacket(long ServerTimeMs, uint Gold, uint Experience);

/// <summary>One received chat line, for a small on-screen/console log.</summary>
public sealed record ChatMessage(uint PeerId, string Name, string Text);

/// <summary>
/// A text message from one player. Sent to the host, which relays it to every other peer.
/// The sender does not receive its own back — it echoes locally on send.
/// </summary>
public sealed record ChatPacket(uint FromPeerId, string Name, string Text);

/// <summary>
/// peer -> host: this peer's local conversation status. <paramref name="Active"/> true =
/// "I have entered a conversation" (a claim), false = "I have left it" (a release). The
/// host's arbiter decides what actually happens to the shared token.
/// </summary>
public sealed record DialogueRequestPacket(
    uint PeerId,
    bool Active,
    string SpeakerName,
    string Line,
    string PartnerTag = "",
    uint ReplySerial = 0,
    int ReplyIndex = 0,
    byte[]? PickLog = null);

/// <summary>
/// A peer's in-world content fingerprint, sent both ways once a save is loaded and
/// repeated periodically (and re-evaluated on every module change, since
/// <see cref="WorldDigest.Module"/>/<see cref="WorldDigest.ModuleGit"/> describe wherever
/// the peer is standing now). Each mismatch degrades exactly the channels it invalidates —
/// see <see cref="WorldDigest"/> for the per-field policy.
/// </summary>
public sealed record WorldDigestPacket(uint PeerId, WorldDigest Digest);

/// <summary>
/// A quest journal advance: entry <paramref name="QuestTag"/> reached
/// <paramref name="State"/>. States only rise (the engine itself refuses to lower one),
/// so the merge is a max and replays are harmless.
/// </summary>
public sealed record JournalOpPacket(uint OriginPeerId, uint OpSequence, string QuestTag, int State);

public sealed record JournalAckPacket(uint OpSequence);

public sealed record JournalSyncRequestPacket(uint PeerId, uint KnownVersion);

/// <summary>
/// The authoritative journal. A journal longer than one datagram is sent as several of
/// these in one burst, each carrying the same version.
/// </summary>
public sealed record JournalSnapshotPacket(uint Version, IReadOnlyList<KeyValuePair<string, int>> Entries);
