using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — Content-compatibility judging: the install-time digest verdict refined by
/// the in-world comparison (globals catalogue, save identity, current module's
/// GIT) as it changes, per peer, plus this host's own world-digest broadcast.
/// </summary>
public sealed partial class CoopHost
{
    private CompatibilityVerdict? _compatibility;
    private WorldDigest _localWorld = WorldDigest.Empty;

    /// <summary>The behaviour-flag comparison against the most recently seen Hello — see
    /// <see cref="ICapabilitySession.BehaviourWarnings"/>. A host never adopts anything
    /// (<see cref="Overrides"/> always <see cref="SessionOverrides.None"/>): this is purely
    /// informational, the same "warn, never act" role <see cref="Compatibility"/> plays for a
    /// content mismatch this host chooses not to refuse.</summary>
    private BehaviourComparisonResult? _behaviourComparison;

    public BehaviourComparisonResult? BehaviourWarnings => _behaviourComparison;

    public void PublishWorldDigest(in WorldDigest digest) => _localWorld = digest;

    /// <summary>
    /// This peer's effective capabilities: its install verdict, refined by the in-world
    /// comparison (globals catalogue, save identity, and the GIT of the module both are
    /// standing in). Per the Unknown-is-not-mismatch rule, a field must be resolved on
    /// BOTH sides before a difference counts — and because the module fields follow the
    /// players, walking into a module whose GITs agree restores the id-keyed channels a
    /// previous module's mismatch dropped.
    /// </summary>
    private CompatibilityVerdict EffectiveVerdict(PeerConnection peer)
    {
        WorldDigestComparison mismatch = WorldDigest.Compare(_localWorld, peer.World);
        mismatch = peer.SaveSettle.Apply(mismatch, SessionClock.NowMs);

        return ContentCompatibility.ApplyWorldMismatch(peer.InstallVerdict, mismatch);
    }

    /// <summary>
    /// The narrowest capability set across all admitted peers. One peer running a mod that
    /// drops a channel drops it for the whole session — the host cannot publish enemy
    /// state to a client that would apply it to the wrong creatures.
    /// </summary>
    public SessionCapabilities Capabilities
    {
        get
        {
            lock (_gate)
            {
                SessionCapabilities caps = SessionCapabilities.All;
                foreach (PeerConnection peer in _peers.Values)
                {
                    caps &= EffectiveVerdict(peer).Allowed;
                }

                return caps;
            }
        }
    }

    public CompatibilityVerdict? Compatibility => _compatibility;

    /// <summary>
    /// Sends the host's in-world digest so each client can judge the world-time match
    /// (globals, save, module GIT) from its side too. Cheap and mostly stable — only the
    /// module fields move, and only on a module change — so a low rate is plenty; sent
    /// only once at least one field has resolved.
    /// </summary>
    private long _lastWorldDigestMs;

    private void BroadcastWorldDigest(long now, List<PeerConnection> recipients)
    {
        WorldDigest world = _localWorld;
        if (world.IsEmpty || recipients.Count == 0 || now - _lastWorldDigestMs < 1000)
        {
            return;
        }

        _lastWorldDigestMs = now;
        int length = PacketCodec.Write(_sendBuffer, new WorldDigestPacket(HostPeerId, world));
        if (length == 0)
        {
            return;
        }

        foreach (PeerConnection peer in recipients)
        {
            _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    private void HandleWorldDigest(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadWorldDigest(span) is not { } report)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (_peers.TryGetValue(report.PeerId, out PeerConnection? peer))
            {
                peer.World = report.Digest;
                _compatibility = EffectiveVerdict(peer);
            }
        }
    }
}
