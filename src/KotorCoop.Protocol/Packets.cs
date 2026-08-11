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
/// not just the positions. Presence rides only the snapshot (owner → everyone); the peer
/// direction is <see cref="EnemyDamagePacket"/>, which carries damage dealt and nothing
/// about health at all.
/// </summary>
/// <remarks>
/// <paramref name="Plot"/> marks a creature the module declares un-killable — usually a
/// cutscene actor. Its PRESENCE replicates like any other creature's, so scripted scenes
/// play out identically on both screens, but the combat half of the channel must leave it
/// alone: no min-1-hp pin, no health mirror, no death, and a client's damage report for it
/// is ignored. Its health is the module's business, not the session's.
/// </remarks>
/// <remarks>
/// <paramref name="Appearance"/> (the appearance.2da row) and <paramref name="Faction"/>
/// (the repute.2da row) exist for MATERIALISATION: an enemy the owner's engine spawned
/// (an encounter, a script) has no copy on the mirror at all — `ResolveServerObject`
/// returns nothing — and no blueprint name survives on a live creature to send. What does
/// survive is what the creature looks like and whose side it is on, which is enough for
/// the mirror to build a stand-in from the co-op blueprint the same way phantoms are
/// built, and everything else (position, health, swings, death) already rides the
/// channel. Zero appearance means "unknown — do not materialise".
/// </remarks>
public sealed record EnemyState(
    uint ObjectId, ushort CurrentHealth, ushort MaxHealth, bool Dead,
    Vector3f Position = default, float Facing = 0f, uint Animation = 0,
    uint OneShotSerial = 0, ushort OneShotAnimation = 0, bool Plot = false,
    ushort Appearance = 0, ushort Faction = 0);

/// <summary>
/// The host's view of every enemy. Absolute health, never damage deltas: the engine
/// applies difficulty multipliers, temporary hit points and damage resistance per
/// instance, so replaying "took 7 damage" on the far side does not produce 7.
///
/// <paramref name="Module"/> is the module the ids were read IN, and it is load-bearing:
/// object ids are assigned per module load, so the same id names a different creature in a
/// different module. A receiver somewhere else must drop the snapshot rather than write the
/// host's healths and positions onto whatever local bodies happen to hold those ids. The
/// report direction has always carried its module; this is the same guard the other way.
/// </summary>
public sealed record EnemySnapshotPacket(
    long ServerTimeMs, string Module, IReadOnlyList<EnemyState> Enemies);

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
/// How much damage the sender has dealt to one enemy since its ids were last reassigned.
/// A running CUMULATIVE total of the RAW effect amounts, not a per-hit event and not a
/// health reading.
/// </summary>
public readonly record struct EnemyDamageEntry(uint ObjectId, uint CumulativeDamage);

/// <summary>
/// A peer's contribution to the owner's fights, sent each tick, unreliable.
///
/// This replaces the old health report, and the reason is worth keeping: health is a LEVEL
/// that must be sampled, and the owner's mirror writes its own value back over the peer's
/// copy several times a second, so a peer's hit had to win a race to be seen at all. A
/// cumulative sum of damage DEALT cannot be raced — the owner never reads it back out of
/// the game — and, unlike health, it says who dealt what.
///
/// <paramref name="Module"/> rides along because object ids are assigned per module load,
/// so the owner must not apply a total collected somewhere else. <paramref
/// name="Generation"/> is the sender's ledger epoch: it changes whenever the sender's own
/// totals restart (a module transition, a re-attached plugin), which is how the owner
/// knows a smaller number is a RESET rather than a stale datagram.
/// </summary>
public sealed record EnemyDamagePacket(
    uint PeerId, string Module, uint Generation, IReadOnlyList<EnemyDamageEntry> Damage);

/// <summary>One peer's cumulative damage against one enemy, as the owner sees it after
/// freshness and module filtering.</summary>
public readonly record struct PeerEnemyDamage(
    uint PeerId, uint Generation, uint ObjectId, uint CumulativeDamage);

/// <summary>
/// Damage that landed on a peer's phantom in the sender's game, addressed to the player
/// that phantom stands for.
///
/// <paramref name="CumulativeDamage"/> is a running total, never a per-hit amount: the
/// receiver applies the difference against what it has already taken, so a lost datagram
/// costs a tick of staleness rather than a missed hit and a duplicate applies nothing.
/// Same "level, not event" rule as enemy health. <paramref name="AttackerId"/> is the
/// engine object id of whoever last landed one, which is the same number in both
/// instances.
/// </summary>
public sealed record PhantomDamagePacket(
    uint FromPeerId, uint TargetPeerId, uint CumulativeDamage, uint AttackerId);

/// <summary>
/// client -> host: "my player is standing inside these encounters right now."
///
/// <paramref name="EncounterIds"/> are engine object ids, which is a legitimate
/// cross-instance key here for the same reason enemy ids are: <c>CSWSArea::LoadGIT</c>
/// walks the module's GIT lists in a fixed order and hands out ids from a monotonic
/// counter, and the Encounter List is one of those lists (Handoff 4.5;
/// <c>tools/kotorfiles.py objectids</c> reproduces the assignment offline).
///
/// <paramref name="Module"/> rides along for the same reason it does on
/// <see cref="EnemyReportPacket"/>: ids reset on a module transition, so the host must
/// never act on a request from a peer standing somewhere else.
///
/// Sent every tick while the set is non-empty. The set is the message — there is no
/// "entered" edge on the wire, because an edge that is lost is lost forever while a level
/// re-converges on the next datagram.
/// </summary>
public sealed record EncounterTriggerPacket(
    uint PeerId, string Module, IReadOnlyList<uint> EncounterIds);

/// <summary>
/// One NPC bark: the line an NPC throws into a bubble without opening a conversation.
///
/// <paramref name="Speaker"/> is the engine SERVER object id, which names the same creature
/// in every instance for anything the module loaded (Handoff 4.5).
///
/// ⚠ <paramref name="Text"/> and <paramref name="SoundResRef"/> are the RESOLVED CONTENTS of
/// the engine's arguments, not the arguments. <c>BarkString</c> takes a <c>CExoString*</c>
/// and a <c>CResRef*</c>, and an earlier version of this packet carried those POINTERS
/// verbatim on the principle that a value passed through untouched needs no interpretation.
/// In the receiving process they are wild addresses, and the engine dereferences both — that
/// is what killed two instances. See docs/research/bark-replay.md.
///
/// <paramref name="Module"/> rides along for the usual reason: object ids reset on a
/// transition, so a bark must never be replayed by someone standing somewhere else.
/// <paramref name="Serial"/> makes a repeat of the same line visible as a new one.
/// </summary>
public sealed record BarkPacket(
    uint PeerId, string Module, uint Speaker, byte[] Text, byte[] SoundResRef,
    uint Flags, uint Serial)
{
    /// <summary>Bytes of bark text carried. Nothing in the binary bounds a bark's length;
    /// this is anchored to the dialogue channel's own line limit.</summary>
    public const int MaxTextBytes = 200;

    /// <summary>A <c>CResRef</c> is a fixed <c>char[16]</c> — not a NUL-terminated string,
    /// and an empty one is an ordinary case (a line with no voice-over).</summary>
    public const int ResRefBytes = 16;
}

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
