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
public sealed class SnapshotBuffer(
    int interpolationDelayMs = ProtocolConstants.DefaultInterpolationDelayMs,
    int sendPeriodMs = 1000 / ProtocolConstants.DefaultTickRate,
    bool dejitter = true)
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

        /// <summary>What <see cref="SnapshotTimingRule"/> remembers for this peer — the arrival
        /// and playback timestamps of the last sample actually laid down. Deliberately NOT
        /// advanced by a suppressed duplicate: the whole point is that the next real sample's
        /// gap is measured from the last real one, so the two periods a duplicate spanned are
        /// counted as two.</summary>
        public SnapshotTimingState Timing = SnapshotTimingState.Fresh;

        public readonly List<(long TimeMs, PlayerState State)> Samples = new(MaxSamplesPerPeer);
    }

    private readonly Dictionary<uint, PeerTrack> _peers = [];
    private readonly int _delayMs = interpolationDelayMs;
    private readonly bool _dejitter = dejitter;
    private readonly Lock _gate = new();

    /// <summary>
    /// The nominal interval between a peer's sends, in milliseconds — what
    /// <see cref="SnapshotTimingRule.Assign"/> quantises inter-arrival gaps to.
    ///
    /// Settable rather than fixed at construction because a CLIENT does not know the rate it
    /// will actually receive at until the host's <c>Welcome</c> announces one, and the buffer
    /// is built before the handshake (see <c>CoopClient</c>'s constructor and
    /// <c>HandleWelcome</c>'s tick-rate adoption). Written from the receive thread and read
    /// under <see cref="_gate"/> on the same thread as every other buffer mutation, so a plain
    /// property is enough — a stale read costs one sample filed against the old period.
    /// </summary>
    public int SendPeriodMs { get; set; } = sendPeriodMs;

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

            // ⚠ ALWAYS, and before any suppression below. This is the liveness stamp
            // PruneStale judges a peer against; a player standing perfectly still publishes
            // byte-identical states that ShouldAccept correctly declines to sample, and
            // letting that path skip this would time them out of the session for holding
            // still. See SnapshotTimingRule.ShouldAccept's own warning.
            track.LastUpdateMs = receivedAtMs;

            // Out-of-order datagrams are normal on UDP; drop anything older than the newest.
            if (track.Samples.Count > 0 && receivedAtMs < track.Samples[^1].TimeMs)
            {
                return;
            }

            // A state that repeats the newest one exactly is not an observation, it is the
            // sender's send timer having fired twice between two of its pump ticks (or a peer
            // standing still). Filing it would assert "demonstrably motionless for a period",
            // which is what turns the FOLLOWING sample into a double-speed dash. Suppressed
            // here rather than in Interpolate because the damage is done at record time — and
            // track.Timing is deliberately left alone so the next real sample's gap spans both
            // periods. See SnapshotTimingRule.
            if (_dejitter && track.Samples.Count > 0
                && !SnapshotTimingRule.ShouldAccept(track.Samples[^1].State, state))
            {
                return;
            }

            // The first sample after a link came back is a fresh start, not the far end of a
            // walk: throw the pre-gap history away so the peer appears where they are instead
            // of sliding there from wherever they were standing when the link died.
            //
            // ⚠ Judged on ARRIVAL times, not the assigned playback times below: the question is
            // "how long has this peer been silent", which is a wall-clock fact about the link,
            // not something the regularised timeline gets a say in.
            if (track.Samples.Count > 0 && track.Timing.HasPrevious
                && receivedAtMs - track.Timing.LastArrivalMs > ContinuityGapMs)
            {
                track.Samples.Clear();
                track.Timing = SnapshotTimingState.Fresh;
            }

            long sampleTimeMs = _dejitter
                ? SnapshotTimingRule.Assign(track.Timing, receivedAtMs, SendPeriodMs)
                : receivedAtMs;

            // Monotonicity is the buffer's own invariant (Interpolate walks the list assuming
            // ascending times), and the rule's resync branches hand back a raw arrival that
            // could in principle land on or before the last assigned stamp.
            if (track.Samples.Count > 0 && sampleTimeMs <= track.Samples[^1].TimeMs)
            {
                sampleTimeMs = track.Samples[^1].TimeMs + 1;
            }

            track.Timing = track.Timing.Accept(receivedAtMs, sampleTimeMs);
            track.Samples.Add((sampleTimeMs, state));

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
