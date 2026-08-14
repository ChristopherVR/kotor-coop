using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Buffers received peer states and plays them back on a fixed delay, interpolating
/// between the two snapshots that bracket the render time.
///
/// The alternative, extrapolating from the newest snapshot, is what produces the
/// rubber-banding people complain about in Seamless Co-op: guess where the peer went,
/// be wrong, snap them back. Paying a constant 100 ms of latency instead means remote
/// players are always shown at a position they actually occupied.
/// </summary>
public sealed class SnapshotBuffer(int interpolationDelayMs = ProtocolConstants.DefaultInterpolationDelayMs)
{
    private const int MaxSamplesPerPeer = 32;

    /// <summary>
    /// A gap longer than this is a disconnection, not a dropped packet, and the two samples
    /// either side of it must not be interpolated between: the peer did not walk from where
    /// they were to where they are now, and lerping across the gap slides their avatar the
    /// whole way in one interpolation window. Comfortably longer than any burst of loss at
    /// 30 Hz, comfortably shorter than the peer timeout.
    /// </summary>
    private const int ContinuityGapMs = 1000;

    private sealed class PeerTrack
    {
        public string Name = string.Empty;
        public long LastUpdateMs;
        public readonly List<(long TimeMs, PlayerState State)> Samples = new(MaxSamplesPerPeer);
    }

    private readonly Dictionary<uint, PeerTrack> _peers = [];
    private readonly int _delayMs = interpolationDelayMs;
    private readonly Lock _gate = new();

    public int PeerCount
    {
        get
        {
            lock (_gate)
            {
                return _peers.Count;
            }
        }
    }

    public void Push(uint peerId, string name, in PlayerState state, long receivedAtMs)
    {
        if (!state.IsPlausible)
        {
            return;
        }

        lock (_gate)
        {
            if (!_peers.TryGetValue(peerId, out PeerTrack? track))
            {
                track = new PeerTrack();
                _peers[peerId] = track;
            }

            track.Name = name;
            track.LastUpdateMs = receivedAtMs;

            // Out-of-order datagrams are normal on UDP; drop anything older than the newest.
            if (track.Samples.Count > 0 && receivedAtMs < track.Samples[^1].TimeMs)
            {
                return;
            }

            // The first sample after a link came back is a fresh start, not the far end of a
            // walk: throw the pre-gap history away so the peer appears where they are instead
            // of sliding there from wherever they were standing when the link died.
            if (track.Samples.Count > 0 && receivedAtMs - track.Samples[^1].TimeMs > ContinuityGapMs)
            {
                track.Samples.Clear();
            }

            track.Samples.Add((receivedAtMs, state));

            if (track.Samples.Count > MaxSamplesPerPeer)
            {
                track.Samples.RemoveRange(0, track.Samples.Count - MaxSamplesPerPeer);
            }
        }
    }

    /// <summary>Returns every tracked peer, positioned at (now - interpolation delay).</summary>
    public IReadOnlyList<RemotePeer> Sample(long nowMs)
    {
        long renderTime = nowMs - _delayMs;
        List<RemotePeer> result = [];

        lock (_gate)
        {
            foreach ((uint peerId, PeerTrack track) in _peers)
            {
                if (track.Samples.Count == 0)
                {
                    continue;
                }

                result.Add(new RemotePeer(peerId, track.Name, Interpolate(track, renderTime)));
            }
        }

        return result;
    }

    private static PlayerState Interpolate(PeerTrack track, long renderTime)
    {
        List<(long TimeMs, PlayerState State)> samples = track.Samples;

        // Ahead of everything we have: hold the newest rather than extrapolate.
        if (renderTime >= samples[^1].TimeMs)
        {
            return samples[^1].State;
        }

        if (renderTime <= samples[0].TimeMs)
        {
            return samples[0].State;
        }

        for (int i = 0; i < samples.Count - 1; i++)
        {
            (long fromTime, PlayerState fromState) = samples[i];
            (long toTime, PlayerState toState) = samples[i + 1];

            if (renderTime < fromTime || renderTime > toTime)
            {
                continue;
            }

            // A module change is a teleport, not motion. Interpolating across it would
            // drag the avatar through the void between two areas.
            if (!string.Equals(fromState.Module, toState.Module, StringComparison.OrdinalIgnoreCase))
            {
                return toState;
            }

            long span = toTime - fromTime;
            float t = span <= 0 ? 1f : (float)(renderTime - fromTime) / span;
            t = Math.Clamp(t, 0f, 1f);

            return toState with
            {
                Position = Vector3f.Lerp(fromState.Position, toState.Position, t),
                Facing = Angles.Lerp(fromState.Facing, toState.Facing, t),
            };
        }

        return samples[^1].State;
    }

    public void Remove(uint peerId)
    {
        lock (_gate)
        {
            _peers.Remove(peerId);
        }
    }

    /// <summary>
    /// Drops every tracked peer that is not in <paramref name="present"/>.
    ///
    /// The host's world snapshot is a complete LEVEL — everyone it knows about, every tick —
    /// so a peer missing from it has gone, and there is no need to age it out. That matters on
    /// reconnection: when the host drops a peer, its own buffer is cleaned immediately, but a
    /// third player's was still being refreshed by the relay right up to the moment the host
    /// gave up, so it kept the departed avatar for another full peer timeout on top. If the
    /// player rejoined in that window there were briefly two of them on screen.
    /// </summary>
    public void RetainOnly(IReadOnlyCollection<uint> present)
    {
        lock (_gate)
        {
            List<uint>? gone = null;

            foreach (uint peerId in _peers.Keys)
            {
                if (!present.Contains(peerId))
                {
                    (gone ??= []).Add(peerId);
                }
            }

            if (gone is null)
            {
                return;
            }

            foreach (uint peerId in gone)
            {
                _peers.Remove(peerId);
            }
        }
    }

    public void PruneStale(long nowMs, long timeoutMs)
    {
        lock (_gate)
        {
            List<uint>? expired = null;

            foreach ((uint peerId, PeerTrack track) in _peers)
            {
                if (nowMs - track.LastUpdateMs > timeoutMs)
                {
                    (expired ??= []).Add(peerId);
                }
            }

            if (expired is null)
            {
                return;
            }

            foreach (uint peerId in expired)
            {
                _peers.Remove(peerId);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _peers.Clear();
        }
    }
}
