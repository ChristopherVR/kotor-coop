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
/// The host's authoritative view, broadcast every tick. <paramref name="FlagVersion"/>,
/// <paramref name="ItemVersion"/>, <paramref name="JournalVersion"/> and
/// <paramref name="LootVersion"/> are the cheap tripwires that tell a client one of its world
/// stores is stale, so full state only crosses the wire when it has actually diverged.
///
/// Each version rides beside its store's <c>Digest</c> — <paramref name="FlagDigest"/>,
/// <paramref name="ItemDigest"/>, <paramref name="JournalDigest"/>,
/// <paramref name="LootDigest"/> — an order-independent content fingerprint (see
/// <c>KotorCoop.Session.EntryDigest</c>). The version alone has a blind spot: it counts local
/// CHANGES, not content, so two stores that diverged by the same count during an outage can
/// tie and never ask for a resync (docs/research/disconnect-reconnect.md §4.1). The digest
/// depends on what is actually held, so a mismatch there is caught even when the counters
/// agree. Container loot did not carry a version at all before this — see §4.8 — so
/// <paramref name="LootVersion"/> and <paramref name="LootDigest"/> are both new.
/// </summary>
public sealed record WorldSnapshotPacket(
    long ServerTimeMs,
    uint FlagVersion,
    uint ItemVersion,
    uint JournalVersion,
    uint LootVersion,
    ulong FlagDigest,
    ulong ItemDigest,
    ulong JournalDigest,
    ulong LootDigest,
    IReadOnlyList<SnapshotEntry> Entries);

/// <summary>
/// A world progression change (door opened, boss dead, quest advanced, or — just as
/// legitimately — a counter decremented or a boolean cleared back to false). This is the
/// channel that separates "two players see each other" from actual co-op, and in a real
/// seamless mod it is where nearly all the difficulty lives: deciding which of the game's
/// thousands of booleans are global truth and which must stay per-player.
///
/// <paramref name="Serial"/> is this ORIGIN's own Lamport clock reading at the moment the
/// change was made — see <c>EventFlagStore</c>'s remarks for why a per-key value comparison
/// alone cannot tell a real advance from a stale replay once decreases are legal. Carried
/// alongside <paramref name="OriginPeerId"/> (already on the wire) so the receiver's
/// (Serial, OriginPeerId) tie-break is deterministic without a second round trip.
/// </summary>
public sealed record FlagOpPacket(
    uint OriginPeerId, uint OpSequence, uint FlagId, sbyte Value, uint Serial);

public sealed record FlagAckPacket(uint OpSequence);

public sealed record FlagSyncRequestPacket(uint PeerId, uint KnownVersion);

/// <summary>One entry inside a <see cref="FlagSnapshotPacket"/>: a flag's current value plus
/// the (Serial, OriginPeerId) pair that won it, so a resyncing peer can apply the SAME
/// last-writer-wins comparison a live op would have used rather than blindly overwriting.</summary>
public readonly record struct FlagSnapshotEntry(uint FlagId, sbyte Value, uint Serial, uint OriginPeerId);

/// <summary>
/// Values are <c>sbyte</c>, not <c>bool</c>: KOTOR's world state is a mix of booleans and
/// signed-byte numbers (quest stages, counters), and splitting a number across derived
/// boolean ids would break the last-writer-wins rule that keeps the two worlds convergent.
/// </summary>
public sealed record FlagSnapshotPacket(uint Version, IReadOnlyList<FlagSnapshotEntry> Flags);

public sealed record PingPacket(uint PeerId, long SentTimeMs);

public sealed record PongPacket(long OriginalSentTimeMs, long ServerTimeMs);

public sealed record ByePacket(uint PeerId);

/// <summary>
/// What is true of ONE item instance rather than of its blueprint.
///
/// A resref names a stock item. Everything a player has done to their copy of it — spending
/// its charges, stacking twenty of them, having it identified, bolting three upgrades into a
/// lightsaber — lives on the live <c>CSWSItem</c> and used to be dropped on the floor of the
/// sending instance and never picked up again. The receiving side re-created the blueprint
/// and handed the other player a pristine one.
///
/// The four fields are the four the engine itself persists per instance and re-applies when
/// it rebuilds an item from a template: <c>SaveItem</c> (0x0055CCD0) writes exactly
/// StackSize / Charges / MaxCharges / Upgrades / the Identified bit alongside the blueprint
/// name, and the upgrade workbench (0x006C5620) rebuilds an upgraded weapon by calling
/// <c>LoadFromTemplate</c> and then copying the Upgrades dword and the Stolen bit onto the
/// new body. Reproducing that sequence is therefore the engine's own idiom, not an
/// invention. See docs/research/item-identity.md.
///
/// ⚠ <see cref="Flags"/> bit 0 is <see cref="FlagKnown"/> and it is load-bearing: an
/// identity nobody managed to read must be distinguishable from a genuinely stock item, or
/// "I could not read it" would silently mean "stack of zero, no charges, unidentified". A
/// zeroed identity therefore means UNKNOWN and the receiver leaves the fresh item exactly as
/// the engine made it.
/// </summary>
public readonly record struct ItemIdentity(
    ushort StackSize, byte Charges, byte MaxCharges, uint Upgrades, byte Flags)
{
    /// <summary>This identity was actually read off a live item. Without it every other
    /// field means nothing — see the type remarks.</summary>
    public const byte FlagKnown = 1 << 0;

    /// <summary><c>CSWSItem+0x288</c> bit 0, the bit <c>SetIdentified</c> (0x00554170) toggles.</summary>
    public const byte FlagIdentified = 1 << 1;

    /// <summary><c>CSWSItem+0x288</c> bit 5, saved as the GFF <c>Stolen</c> field.</summary>
    public const byte FlagStolen = 1 << 2;

    /// <summary><c>CSWSItem+0x288</c> bit 6, saved as the GFF <c>NonEquippable</c> field.</summary>
    public const byte FlagNonEquippable = 1 << 3;

    /// <summary>Nothing was read. The receiver must not apply any of it.</summary>
    public static ItemIdentity Unknown => default;

    public bool IsKnown => (Flags & FlagKnown) != 0;

    public bool Identified => (Flags & FlagIdentified) != 0;

    public bool Stolen => (Flags & FlagStolen) != 0;

    public bool NonEquippable => (Flags & FlagNonEquippable) != 0;
}

/// <summary>
/// An item lying on the ground, shared by every player in the module. The id is minted by
/// the dropping peer as (peerId &lt;&lt; 16 | counter) so ids never collide across peers.
///
/// <paramref name="Identity"/> is what makes the item THIS item rather than a fresh one off
/// the blueprint. It defaults to <see cref="ItemIdentity.Unknown"/> because a drop can be
/// announced by something that never held the live object (the test harness names a
/// blueprint), and "unknown" has to stay a first-class answer.
/// </summary>
public sealed record GroundItem(
    uint ItemId, string Resref, string Module, Vector3f Position,
    ItemIdentity Identity = default);

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

/// <summary>
/// client -&gt; host: my container-loot version or digest does not match the host's latest
/// <see cref="WorldSnapshotPacket"/>, please resync. <paramref name="KnownVersion"/> and
/// <paramref name="KnownDigest"/> are carried for the same reason the other three sync-request
/// packets carry a known version — diagnostic, not consulted by the handler, which always
/// re-offers everything it knows rather than trying to compute a partial diff.
/// </summary>
public sealed record LootSyncRequestPacket(uint PeerId, uint KnownVersion, ulong KnownDigest);

/// <summary>One item off a materialised enemy's real corpse, by resref plus the same identity
/// fields <see cref="GroundItem"/> carries. No object id: the receiving side spawns a fresh
/// local item, it does not need to name the sender's.</summary>
public readonly record struct MaterialisedLootItem(string Resref, ItemIdentity Identity);

/// <summary>
/// The full real loot of one owner-authoritative enemy that just died, for a mirroring peer's
/// materialised stand-in to receive. See <see cref="PacketType.MaterialisedLoot"/> for why this
/// is host -&gt; clients only and reliable. <paramref name="OwnerEnemyId"/> is the id in the
/// HOST's own id space — the same id <c>CheatController.GiveLoot</c> already expects and
/// translates via <c>MaterialisedEnemies.ToLocal</c>.
/// </summary>
public sealed record MaterialisedLootPacket(
    uint OriginPeerId, uint OpSequence, uint OwnerEnemyId,
    IReadOnlyList<MaterialisedLootItem> Items)
{
    /// <summary>A corpse's worth of loot fits comfortably inside one datagram at this cap —
    /// asserted by <c>PacketCodecTests</c>. A count past it is truncated at capture time, not
    /// silently dropped whole.</summary>
    public const int MaxItems = 8;
}

/// <summary>A client's acknowledgement of a <see cref="MaterialisedLootPacket"/>. Carries the
/// acking peer id so the host, which broadcasts to several clients, can tell whose ack this
/// is — exactly like <see cref="ContainerLootAckPacket"/>.</summary>
public sealed record MaterialisedLootAckPacket(uint PeerId, uint OpSequence);

/// <summary>
/// One conversation's worth of net-new items placed directly into the OWNER's speaker
/// inventory, and/or a companion the owner's party table just made available — see
/// <see cref="PacketType.DialogueGrant"/> for the full rationale. <paramref name="PartnerTag"/>
/// is the NPC tag the conversation was with, carried for logging/diagnostics only — dedup is
/// entirely on (<paramref name="OriginPeerId"/>, <paramref name="OpSequence"/>), never on this.
/// </summary>
public sealed record DialogueGrantPacket(
    uint OriginPeerId, uint OpSequence, string PartnerTag,
    IReadOnlyList<MaterialisedLootItem> Items)
{
    /// <summary>A single reward node granting more than one item at once is rare (§5 item 3 of
    /// dialogue-consequences.md flags it as untested, not impossible); this cap matches
    /// <see cref="MaterialisedLootPacket.MaxItems"/> for the same "fits one datagram
    /// comfortably" reasoning — asserted by <c>PacketCodecTests</c>.</summary>
    public const int MaxItems = 8;
}

/// <summary>
/// Acknowledges one <see cref="DialogueGrantPacket"/> op, in either direction. Carries the
/// acking peer's own id plus the (origin, sequence) pair being acked — unlike
/// <see cref="ContainerLootAckPacket"/>'s bare sequence, <see cref="DialogueGrantPacket"/> keeps
/// the TRUE reporting peer's origin on relay rather than re-attributing it to the host, so two
/// different origins can legitimately reuse the same sequence number and the ack has to say
/// which one it means.
/// </summary>
public sealed record DialogueGrantAckPacket(uint PeerId, uint OriginPeerId, uint OpSequence);

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
/// A non-damage effect that landed on a peer's phantom in the sender's game, addressed to the
/// player that phantom stands for — the non-damage sibling of <see cref="PhantomDamagePacket"/>.
/// See docs/research/effect-sync.md.
///
/// Unlike phantom damage, this is a discrete EVENT rather than a running total: two Stuns two
/// seconds apart are two things that happened, not one bigger stun, so <see cref="Sequence"/>
/// is what lets the receiver tell a repeat from something new — the same role a bark's serial
/// plays, not the "difference since last time" role <c>CumulativeDamage</c> plays.
/// </summary>
public sealed record PeerEffectPacket(
    uint FromPeerId, uint TargetPeerId, PeerEffectKind Kind, uint DurationMs, uint Magnitude, uint Sequence);

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
/// Two directions sharing one shape, distinguished by <paramref name="PeerId"/> exactly like
/// <see cref="WorldDigest"/>'s "both ways" packets:
///
/// - client -&gt; host (<paramref name="PeerId"/> is the sender's real, non-zero peer id):
///   the GENERIC triggers (a <c>CSWSTrigger</c> whose OnEnter starts a cutscene, dialogue or
///   scripted event — never a trap, never an area transition) this peer's player is standing
///   inside RIGHT NOW, in <paramref name="TriggerIds"/>. <paramref name="FiredIds"/> is empty.
///
/// - host -&gt; all (<paramref name="PeerId"/> == 0, which no real peer ever holds): the
///   trigger ids this host has already fired in <paramref name="Module"/>, in
///   <paramref name="FiredIds"/>, so a client's watch stops re-reporting ones the host has
///   already handled. <paramref name="TriggerIds"/> is empty.
///
/// <paramref name="Module"/> rides along for the same reason it does on every module-scoped
/// channel here: trigger object ids reset on a transition, so a report or a fired-set from a
/// different module names nothing the receiver should act on.
///
/// A LEVEL in both directions, like <see cref="EncounterTriggerPacket"/>: both sides resend
/// their current truth every tick rather than an edge, so one lost datagram costs a tick, not
/// a permanently missed relay. See docs/research/trigger-relay.md.
/// </summary>
public sealed record TriggerRelayPacket(
    uint PeerId, string Module, IReadOnlyList<uint> TriggerIds, IReadOnlyList<uint> FiredIds);

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

/// <summary>
/// any -&gt; all: "the transition I am about to make plays these movies".
///
/// <paramref name="Module"/> is the module the announcer is LEAVING, not the destination —
/// at the moment the queue fills, the destination has been written to the engine's
/// move-to-module fields but the announcer has not moved, and the follower does not need it
/// (it decides where to go from the owner's published module, as it always has). It rides
/// along for the log and for telling two announcements apart.
///
/// <paramref name="Movies"/> are RESREFS, stripped of the path and extension the engine
/// stores. That is a correctness requirement, not tidiness: <c>AddMovieToExoArrayList</c>
/// resolves a name to <c>.\movies\01c.bik</c> and stores THAT, and feeding the stored form
/// back in makes the engine build <c>MOVIES:.\movies\01c.bik</c>, fail to open it and refuse
/// the movie — silently, on every transition. See <c>MovieStaging.TryStripToResRef</c>.
/// </summary>
public sealed record TransitionMoviePacket(uint PeerId, string Module, uint Serial, string[] Movies)
{
    /// <summary>The engine's own ceiling: NWScript <c>StartNewModule</c> takes exactly six
    /// movie arguments, so six is the longest queue the game can build.</summary>
    public const int MaxMovies = 6;

    /// <summary>An Odyssey resref is 16 characters.</summary>
    public const int MaxMovieLength = 16;
}

/// <summary>
/// any -&gt; all: "the transition I am about to make goes to THIS module, entering at THIS
/// waypoint" — sent the instant this instance's own engine has committed to the destination,
/// which is BEFORE the load starts.
///
/// Unlike <see cref="TransitionMoviePacket"/>, whose <c>Module</c> is what the announcer is
/// LEAVING, <paramref name="Module"/> here is the DESTINATION — the whole point of this
/// channel is to let a follower start its own warp toward the right place immediately,
/// instead of waiting for the mover's published <see cref="PlayerState.Module"/> to catch up
/// (which can lag by the length of a movie: the mover's main thread is inside
/// <c>WaitForMovie</c> and publishes nothing for the duration) plus the follow logic's own
/// settle debounce.
///
/// <paramref name="Waypoint"/> is the entry point the engine's own move-to-module fields
/// carry (<c>door-&gt;linked_to</c> for a real door transition), so the follower's warp lands
/// where the mover is actually headed rather than the destination module's default entry.
/// Empty when the source could not resolve one (a plugin-initiated warp with no waypoint) —
/// the engine's own move-to-module path already treats an empty waypoint as "use the
/// default", so this degrades exactly like an ordinary warp.
/// </summary>
public sealed record TransitionAnnouncePacket(uint PeerId, string Module, string Waypoint, uint Serial);

/// <summary>
/// any -&gt; all: "my engine just played this mid-module movie" — a script's own
/// <c>PlayMovie</c> call with no transition involved. See <see cref="PacketType.MidModuleMovie"/>
/// for why this is a separate shape from <see cref="TransitionMoviePacket"/>.
///
/// <paramref name="Module"/> is where the announcer is standing (there is no transition in
/// progress to make it ambiguous, unlike <see cref="TransitionMoviePacket.Module"/>'s "leaving"
/// sense) — a receiver compares it against its own current module before ever asking the
/// plugin to act. <paramref name="Movie"/> is a single resref, stripped of any path/extension
/// exactly like <see cref="TransitionMoviePacket.Movies"/>'s entries.
/// </summary>
public sealed record MidModuleMoviePacket(uint PeerId, string Module, uint Serial, string Movie)
{
    /// <summary>An Odyssey resref is 16 characters.</summary>
    public const int MaxMovieLength = 16;
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

/// <summary>
/// client -> host: this peer's contribution to the shared purse, and its own experience.
///
/// <paramref name="GoldEarned"/> and <paramref name="GoldSpent"/> are running CUMULATIVE
/// totals since this peer's ledger last restarted, never a balance — the same discipline as
/// <see cref="EnemyDamagePacket"/>, and for the same reason: the host applies each increase
/// once, so the channel is order-independent, survives a lost datagram and cannot
/// double-apply a repeat. <paramref name="Generation"/> changes whenever the sender's totals
/// restart (a re-attached agent), which is how the host tells a RESET from a stale datagram.
///
/// <paramref name="Experience"/> is absolute and merges upward by max — see
/// <see cref="PacketType.ProgressionReport"/> for why XP cannot be a delta.
/// </summary>
public sealed record ProgressionReportPacket(
    uint PeerId, uint Generation, uint GoldEarned, uint GoldSpent, uint Experience);

/// <summary>One peer's progression contribution, as the host sees it after freshness
/// filtering.</summary>
public readonly record struct PeerProgression(
    uint PeerId, uint Generation, uint GoldEarned, uint GoldSpent, uint Experience);

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
    byte[]? PickLog = null,
    int NodeIndex = DialogueState.NoNode,
    uint NodeSerial = 0);

/// <summary>
/// A peer's in-world content fingerprint, sent both ways once a save is loaded and
/// repeated periodically (and re-evaluated on every module change, since
/// <see cref="WorldDigest.Module"/>/<see cref="WorldDigest.ModuleGit"/> describe wherever
/// the peer is standing now). Each mismatch degrades exactly the channels it invalidates —
/// see <see cref="WorldDigest"/> for the per-field policy.
/// </summary>
public sealed record WorldDigestPacket(uint PeerId, WorldDigest Digest);

/// <summary>
/// A quest journal change: entry <paramref name="QuestTag"/> reached <paramref name="State"/>
/// — or, when <paramref name="Removed"/> is set, the tag was taken out of the journal
/// entirely (KOTOR's own <c>RemoveJournalQuestEntry</c>; <paramref name="State"/> is unused
/// and sent as 0). Real quest content does both: a stage number can be pushed back down by a
/// script that re-offers an earlier step, and a handful of scripts (12 found by the module
/// scan behind docs/research/quest-sync.md's enumeration — <c>danm13/k_pdan_trig1303</c>,
/// <c>ebo_m41aa/k_pebn_remove</c> among them) call <c>RemoveJournalQuestEntry</c> outright.
/// <paramref name="Serial"/> is the origin's own Lamport clock reading, exactly like
/// <see cref="FlagOpPacket.Serial"/> — see <c>JournalStore</c>'s remarks for the comparison
/// this makes possible once the merge is no longer a bare max.
/// </summary>
public sealed record JournalOpPacket(
    uint OriginPeerId, uint OpSequence, string QuestTag, int State, uint Serial, bool Removed = false);

public sealed record JournalAckPacket(uint OpSequence);

public sealed record JournalSyncRequestPacket(uint PeerId, uint KnownVersion);

/// <summary>One entry inside a <see cref="JournalSnapshotPacket"/> — see
/// <see cref="FlagSnapshotEntry"/> for why Serial/OriginPeerId ride along, and
/// <see cref="JournalOpPacket.Removed"/> for what a tombstone means here: a resyncing peer
/// must be TOLD a tag was removed, not merely find it absent, or its own stale copy (from an
/// earlier op or an already-applied engine write) never learns to retract it.</summary>
public readonly record struct JournalSnapshotEntry(
    string QuestTag, int State, uint Serial, uint OriginPeerId, bool Removed);

/// <summary>
/// The authoritative journal, tombstones included. A journal longer than one datagram is sent
/// as several of these in one burst, each carrying the same version.
/// </summary>
public sealed record JournalSnapshotPacket(uint Version, IReadOnlyList<JournalSnapshotEntry> Entries);

/// <summary>What a lock is for. Only conversations exist today; the field is on the wire
/// because the release rules differ per kind, and adding one later must not renumber.</summary>
public enum InteractionKind : byte
{
    None = 0,
    Conversation = 1,
}

/// <summary>
/// peer -&gt; host: "I am interacting with <paramref name="Tag"/> right now."
///
/// A TAG, not an object id, for the reason the dialogue channel already uses one: the tag is
/// what the dialogue gate compares and what <c>CreatureFinder.FindByTag</c> resolves, and it
/// survives being read on an instance that never loaded the same body.
///
/// <paramref name="Module"/> rides along for the usual reason — tags and ids are only
/// meaningful where they were read, so the owner must never act on a claim from a peer
/// standing somewhere else.
///
/// Sent every tick while the claim is live. <paramref name="Active"/> false is a release; it
/// is an optimisation, not the safety net (see <see cref="PacketType.InteractionClaim"/>).
/// </summary>
public sealed record InteractionClaimPacket(
    uint PeerId, string Module, string Tag, InteractionKind Kind, bool Active);

/// <summary>One granted lock, as the host's arbiter sees it.</summary>
public readonly record struct InteractionLock(uint PeerId, string Tag, InteractionKind Kind);

/// <summary>
/// host -&gt; all: every lock currently granted, broadcast each tick.
///
/// The SET is the message. A lock that stops being broadcast is released — there is no
/// "released" entry to lose, which is what makes a client crashing mid-conversation cost a
/// lease rather than an NPC.
/// </summary>
public sealed record InteractionLockPacket(
    long ServerTimeMs, string Module, IReadOnlyList<InteractionLock> Locks);

/// <summary>
/// One instance's whole companion roster, as two nine-bit masks over the engine's NPC slots.
///
/// ⚠ THE SLOT INDEX IS THE COMPANION'S IDENTITY, and nothing else on the wire names one.
/// KOTOR's roster is a fixed nine-entry table (NPC_BASTILA .. NPC_ZAALBAR) and both instances
/// loaded the same save, so slot N means the same character on both. That is also the key
/// PartyPresenceController already resolves companions by — see its "companions are keyed by
/// NPC roster slot (identical across two instances of the same save)" — so this channel and
/// the companion mirror agree on identity by construction rather than by convention.
///
/// <paramref name="AvailableMask"/> = recruited: the story has handed this companion over,
/// whether or not they are in the active party right now. Read from party table +0x30
/// (<c>int[9]</c>, nonzero = available; <c>CSWPartyTable::AddNPC</c> at 0x00564350 writes the
/// literal 1). Every roster operation in the engine refuses a slot that is not available.
///
/// <paramref name="PartyMask"/> = in the ACTIVE party: the at-most-two companions walking
/// behind the player. Read from party table +0x00 (count, max 2) and +0x04
/// (<c>int[2]</c> of NPC slot indices — slot indices, NOT object ids).
/// </summary>
public readonly record struct PartyRoster(ushort AvailableMask, ushort PartyMask)
{
    /// <summary>Nothing recruited, nobody in the party. What a peer's roster reads as before
    /// the first level has arrived, and what must never be applied as if it were real.</summary>
    public static readonly PartyRoster Empty = new(0, 0);

    /// <summary>Only the nine slots the engine has (<see cref="ProtocolConstants.PartyRosterSlots"/>).
    /// Anything above them is a malformed sender, and is dropped rather than acted on.</summary>
    public const ushort SlotMask = (1 << ProtocolConstants.PartyRosterSlots) - 1;

    public bool IsAvailable(int slot) => (AvailableMask & (1 << slot)) != 0;

    public bool IsInParty(int slot) => (PartyMask & (1 << slot)) != 0;

    /// <summary>
    /// Slots this roster has in the party that <paramref name="local"/> does not — the adds a
    /// peer owes. Availability is deliberately NOT considered here: whether the local engine
    /// can honour an add is the local engine's business, and it refuses on its own.
    /// </summary>
    public ushort AddsAgainst(PartyRoster local) =>
        (ushort)(PartyMask & ~local.PartyMask & SlotMask);

    /// <summary>Slots <paramref name="local"/> has in the party that this roster does not —
    /// the removes a peer owes. This is the direction that fixes a dismissal, and the one that
    /// must run FIRST: the party holds at most two companions, so an add can be refused purely
    /// because a stale member is still occupying the seat.</summary>
    public ushort RemovesAgainst(PartyRoster local) =>
        (ushort)(local.PartyMask & ~PartyMask & SlotMask);
}

/// <summary>
/// host -&gt; all: the authoritative companion roster, broadcast each tick.
///
/// No module rides along, and that is deliberate rather than an omission: the party table is
/// SAVE state, not module state (it lives in PARTYTABLE.res and survives every transition), so
/// unlike an object id or a tag it means the same thing to a peer standing anywhere. The
/// enemy and lock channels carry a module because ids and tags do not.
/// </summary>
public sealed record PartyRosterPacket(long ServerTimeMs, PartyRoster Roster);

/// <summary>
/// One creature's liveness as the host's engine currently sees it — not health, just whether
/// a body exists to be seen. The reconciliation this exists for
/// (docs/research/creature-liveness.md) only ever cares about three things: is there a live
/// body, a corpse, or nothing at all.
/// </summary>
public enum CreatureLivenessState : byte
{
    /// <summary>The engine still owns a live, non-corpse body under this id.</summary>
    Alive = 0,

    /// <summary>The engine owns the body but its hit points read at or below zero — a
    /// corpse, still resolvable, exactly what <see cref="EnemyState.Dead"/> already means for
    /// the 32-slot combat census.</summary>
    Dead = 1,

    /// <summary>Nothing answers to this id here any more (<c>DestroyObject</c>, a cutscene
    /// that removed the actor, or a script kill that also destroyed the body) — no corpse to
    /// mirror, unlike <see cref="Dead"/>.</summary>
    Gone = 2,
}

/// <summary>
/// One entry of a <see cref="CreatureLivenessPacket"/>: an id in the HOST's own id space.
/// Always GIT-identical — a receiving peer translates a materialised stand-in's LOCAL id via
/// its own <c>MaterialisedEnemies.ToLocal</c> before comparing against this, never the other
/// way around (the host never sees a peer's stand-in ids at all).
/// </summary>
public readonly record struct CreatureLivenessEntry(uint ObjectId, CreatureLivenessState State);

/// <summary>
/// host -&gt; clients: every creature the host's engine currently knows about in
/// <paramref name="Module"/> — not just the 32-slot combat census <see cref="EnemySnapshotPacket"/>
/// carries — so a mirroring peer can notice a body left alive on one side and dead or
/// destroyed on the other. See docs/research/creature-liveness.md.
///
/// A module's full creature list can exceed one datagram, so a publish cycle may be split
/// across several packets sharing the same <paramref name="Generation"/>:
/// <paramref name="PageIndex"/>/<paramref name="PageCount"/> say where this packet sits
/// (0-based index, total count), and <paramref name="Digest"/> is the SAME
/// order-independent fingerprint over every entry in the WHOLE cycle (computed the way
/// <c>KotorCoop.Session.EntryDigest</c> computes every other store's — Protocol itself takes
/// no dependency on Session, so this type only ever carries the already-computed value) on
/// every page, so a receiver that has gathered every page can confirm its accumulated set is
/// complete rather than trusting the last packet blindly.
/// </summary>
public sealed record CreatureLivenessPacket(
    long ServerTimeMs, string Module, ulong Digest, uint Generation,
    byte PageIndex, byte PageCount, IReadOnlyList<CreatureLivenessEntry> Entries)
{
    /// <summary>See <see cref="ProtocolConstants.MaxCreatureLivenessEntries"/>.</summary>
    public const int MaxEntriesPerPacket = ProtocolConstants.MaxCreatureLivenessEntries;
}
