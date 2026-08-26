namespace KotorCoop.Protocol;

// Non-damage combat effects forwarded to a peer's real character. Matches
// PacketCodec.Effects.cs. (PeerEffectKind itself lives in PeerEffectKind.cs.)

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
