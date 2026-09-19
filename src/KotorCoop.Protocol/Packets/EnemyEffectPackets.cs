namespace KotorCoop.Protocol;

/// <summary>
/// client -&gt; host (reliable): "a non-damage effect I cast just landed on enemy
/// <paramref name="EnemyId"/>" — Stasis, Stun, Fear, Whirlwind, Force Push, Slow, Paralysis,
/// Sleep or a grenade rider, captured at the engine's effect dispatcher on the mirroring
/// instance and replayed by the host on the enemy's REAL body, which is the only one whose AI
/// obeys it (docs/research/enemy-effects.md). The non-damage sibling of
/// <see cref="EnemyDamagePacket"/>.
///
/// <paramref name="EnemyId"/> is in the HOST's object-id space (the plugin translates a
/// materialised stand-in back through <c>MaterialisedEnemies.ToOwner</c> before it reaches the
/// agent), and <paramref name="Module"/> names the world those ids belong to — a retransmit that
/// outlives a module change would otherwise name a stranger. <paramref name="OriginPeerId"/> +
/// <paramref name="OpSequence"/> is the (origin, sequence) identity the reliable sender keys
/// retransmits and acks by. <paramref name="Kind"/> is a <see cref="PeerEffectKind"/> that
/// <see cref="EnemyEffectRules.IsForwardable(PeerEffectKind)"/> accepts.
/// </summary>
public sealed record EnemyEffectPacket(
    uint OriginPeerId, uint OpSequence, string Module, uint EnemyId, PeerEffectKind Kind,
    uint DurationMs, uint Magnitude);

/// <summary>host -&gt; client: acknowledges one <see cref="EnemyEffectPacket"/> op by its
/// (origin, sequence) identity, like <see cref="ClassGrantAckPacket"/>.</summary>
public sealed record EnemyEffectAckPacket(uint PeerId, uint OriginPeerId, uint OpSequence);
