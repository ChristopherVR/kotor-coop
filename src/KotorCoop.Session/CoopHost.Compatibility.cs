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
    private bool _hasPublishedLocalWorldDigest;
    private bool _hasObservedHostLoad;

    /// <summary>The behaviour-flag comparison against the most recently seen Hello — see
    /// <see cref="ICapabilitySession.BehaviourWarnings"/>. A host never adopts anything
    /// (<see cref="Overrides"/> always <see cref="SessionOverrides.None"/>): this is purely
    /// informational, the same "warn, never act" role <see cref="Compatibility"/> plays for a
    /// content mismatch this host chooses not to refuse.</summary>
    private BehaviourComparisonResult? _behaviourComparison;

    public BehaviourComparisonResult? BehaviourWarnings => _behaviourComparison;

    public void PublishWorldDigest(in WorldDigest digest)
    {
        bool loadEdge = false;
        lock (_gate)
        {
            if (_hasPublishedLocalWorldDigest && _localWorld.LoadSerial != digest.LoadSerial)
            {
                loadEdge = true;
                BeginHostWorldLoadEdgeLocked();
            }

            _localWorld = digest;
            _hasPublishedLocalWorldDigest = true;
        }

        if (loadEdge)
        {
            // A host's published repository belongs to the old world until the bridge has
            // scanned the loaded one. The pump also withdraws its internal pending applies;
            // this immediate withdrawal closes the receive-loop window before that tick.
            WithdrawPartyPoolSnapshotV2();
            DiscardQueuedPartyPoolIntentsAfterWorldLoad();
        }
    }

    public void NotifyWorldLoadEdge(uint loadSerial)
    {
        bool loadEdge = false;
        lock (_gate)
        {
            if (_hasPublishedLocalWorldDigest && _localWorld.LoadSerial != loadSerial)
            {
                // Keep admission/compatibility comparisons from treating the old ready row
                // as the host's post-load world while the new world is still resolving.
                _localWorld = _localWorld with
                {
                    SaveIdentity = 0,
                    Module = string.Empty,
                    ModuleGit = 0,
                    LoadSerial = loadSerial,
                };
                BeginHostWorldLoadEdgeLocked();
                loadEdge = true;
            }
        }

        if (loadEdge)
        {
            WithdrawPartyPoolSnapshotV2();
            DiscardQueuedPartyPoolIntentsAfterWorldLoad();
        }
    }

    private void BeginHostWorldLoadEdgeLocked()
    {
        _hasObservedHostLoad = true;
        foreach (PeerConnection peer in _peers.Values)
        {
            // Existing connections cannot prove which world an already-sent UDP intent
            // belongs to. Keep them quarantined until disconnect and a fresh admission.
            peer.PartyPoolLoadQuarantined = true;
            peer.PartyPoolReleaseWhenReady = false;
        }
    }

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
        mismatch = peer.SaveLineage.Apply(mismatch, _localWorld, peer.World);
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
                if (peer.PartyPoolLoadQuarantined && peer.PartyPoolReleaseWhenReady
                    && PartyPoolLoadGateRule.PeerMatchesReadyWorld(_localWorld, peer.World))
                {
                    peer.PartyPoolLoadQuarantined = false;
                    peer.PartyPoolReleaseWhenReady = false;
                }

                _compatibility = ContentCompatibility.Combine(_peers.Values.Select(EffectiveVerdict));
            }
        }
    }
}
