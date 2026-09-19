namespace KotorCoop.Protocol;

/// <summary>
/// any -&gt; all (relayed by the host, retransmitted until every recipient acks): "a script just
/// added <paramref name="ClassId"/> to MY player character" — Master Dorak's Jedi path choice on
/// Dantooine (<c>k_pdan_makejedi</c>'s <c>AddMultiClass</c>), captured at the VM handler on the
/// instance that owned the conversation. Every other peer applies the same class to its own
/// character through the same engine handler, so the joiner becomes a Jedi too
/// (docs/research/class-and-power-grants.md, docs/review-seamless-2026-09-18.md blocker #1).
///
/// <paramref name="OriginPeerId"/> + <paramref name="OpSequence"/> is the (origin, sequence)
/// identity <c>KotorCoop.Session.ClassGrantSender</c> keys retransmits and acks by — the same
/// shape <see cref="DialogueGrantPacket"/> uses, for the same reason: the host relays a
/// client's report under the client's TRUE origin, so a receiver can tell a peer's grant from
/// its own echo. <paramref name="ClassId"/> is the <c>classes.2da</c> row (0-8; 3 Guardian,
/// 4 Consular, 5 Sentinel are the three a vanilla script ever adds).
/// </summary>
public sealed record ClassGrantPacket(uint OriginPeerId, uint OpSequence, int ClassId)
{
    /// <summary>K1's <c>classes.2da</c> has nine rows.</summary>
    public const int MaxClassId = 8;
}

/// <summary>Acknowledges one <see cref="ClassGrantPacket"/> op, in either direction — a client
/// acking the host's broadcast, or the host acking a client's report. Carries the op's own
/// (origin, sequence) identity, like <see cref="DialogueGrantAckPacket"/>.</summary>
public sealed record ClassGrantAckPacket(uint PeerId, uint OriginPeerId, uint OpSequence);
