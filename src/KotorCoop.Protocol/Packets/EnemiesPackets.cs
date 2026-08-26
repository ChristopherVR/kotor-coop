namespace KotorCoop.Protocol;

// The host's authoritative combat census. Matches PacketCodec.Enemies.cs.

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
