namespace KotorCoop.Protocol;

// Connection lifecycle and handshake packets: Hello/Welcome/Reject, Ping/Pong/Bye, and
// the per-peer WorldDigest. Matches PacketCodec.Session.cs.

// Behaviour default: BehaviourDigest.Default is a static property, not a compile-time
// constant, so a positional record's optional parameter (needed so every existing
// `new HelloPacket(...)`/`new WelcomePacket(...)` call site keeps compiling without naming the
// new field) cannot use it directly as a default-value expression — `default` is the only
// constant-expression option, and it happens to equal BehaviourDigest.Default's own "all
// flags off, interp 0, no follow-lead label" shape, which is fine: nothing treats a
// zero-valued digest as meaningful on its own, the same way ContentDigest.Unknown (also
// `default`) already works.
public sealed record HelloPacket(
    string PlayerName, string Module, uint ClientNonce, ContentDigest Digest, uint PasswordHash,
    BehaviourDigest Behaviour = default);

public sealed record WelcomePacket(
    uint AssignedPeerId, uint SessionId, byte TickRate, byte MaxPeers, ContentDigest Digest,
    BehaviourDigest Behaviour = default);

public sealed record RejectPacket(string Reason);

public sealed record PingPacket(uint PeerId, long SentTimeMs);

public sealed record PongPacket(long OriginalSentTimeMs, long ServerTimeMs);

public sealed record ByePacket(uint PeerId);

/// <summary>host -&gt; every client: a clean shutdown announcement — see
/// <see cref="PacketType.HostBye"/> for why this exists and why it is unreliable.
/// <paramref name="SessionId"/> is carried purely as a sanity value for a future receiver that
/// might want to confirm it against the one it saw in its own <see cref="WelcomePacket"/>;
/// today's <see cref="KotorCoop.Session.CoopClient"/> does not need it because <c>Handle</c>
/// already filters every inbound datagram to the single host endpoint this client joined.</summary>
public sealed record HostByePacket(uint SessionId);

/// <summary>
/// A peer's in-world content fingerprint, sent both ways once a save is loaded and
/// repeated periodically (and re-evaluated on every module change, since
/// <see cref="WorldDigest.Module"/>/<see cref="WorldDigest.ModuleGit"/> describe wherever
/// the peer is standing now). Each mismatch degrades exactly the channels it invalidates —
/// see <see cref="WorldDigest"/> for the per-field policy.
/// </summary>
public sealed record WorldDigestPacket(uint PeerId, WorldDigest Digest);
