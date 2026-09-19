using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Retransmit-until-ack for <see cref="ClassGrantPacket"/>, modelled directly on
/// <see cref="DialogueGrantSender"/> — "my character just became a Jedi" is world truth, not
/// a disposable sample, and a dropped packet must not silently mean the other player never
/// gets the class (docs/research/class-and-power-grants.md). Used symmetrically by
/// <see cref="CoopHost"/> (broadcasting its own grants, and relaying a client's under the
/// client's TRUE (origin, sequence) identity) and <see cref="CoopClient"/> (reporting to the
/// single host recipient).
///
/// Pending ops are keyed by (origin, sequence) rather than a bare sequence for the same
/// reason as the dialogue-grant sender: the host juggles its own locally-numbered reports
/// alongside a client's already-numbered ones being relayed verbatim.
/// </summary>
public sealed class ClassGrantSender(
    int retransmitIntervalMs = 250, int maxIntervalMs = 4000, int maxPending = 64)
{
    private sealed class Pending
    {
        public required ClassGrantPacket Packet;
        public long LastSentMs;
        public int Attempts;
        public readonly HashSet<uint> Acked = [];
    }

    private readonly Dictionary<(uint Origin, uint Sequence), Pending> _pending = [];

    /// <summary>Every (origin, sequence) ever accepted here, kept past retirement so a
    /// straggling retransmit of a report already fully relayed is not queued a second time.
    /// A playthrough grants a class once per character, so the cap is headroom.</summary>
    private readonly HashSet<(uint Origin, uint Sequence)> _everQueued = [];
    private readonly Queue<(uint Origin, uint Sequence)> _everQueuedHistory = new();
    private const int MaxRememberedOps = 1024;

    private readonly Lock _gate = new();
    private uint _nextSequence = 1;

    private bool _capRefusalLogged;

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    private void LogCapRefusal()
    {
        if (_capRefusalLogged)
        {
            return;
        }

        _capRefusalLogged = true;
        Console.Error.WriteLine(
            $"[coop] class grant refused: pending table already holds {maxPending} unacked "
            + "ops — the link has likely been down a long time.");
    }

    /// <summary>
    /// Queues a LOCALLY CAPTURED grant — a script on this instance just added
    /// <paramref name="classId"/> to this player's character — self-numbered under
    /// <paramref name="originPeerId"/>. Refuses (null) once the pending table is at its cap
    /// or the class is not a <c>classes.2da</c> row.
    /// </summary>
    public ClassGrantPacket? EnqueueLocal(uint originPeerId, int classId, long nowMs)
    {
        if (classId is < 0 or > ClassGrantPacket.MaxClassId)
        {
            return null;
        }

        lock (_gate)
        {
            if (_pending.Count >= maxPending)
            {
                LogCapRefusal();
                return null;
            }

            _capRefusalLogged = false;

            uint sequence = _nextSequence++;
            ClassGrantPacket packet = new(originPeerId, sequence, classId);
            Remember(originPeerId, sequence);
            _pending[(originPeerId, sequence)] = new Pending { Packet = packet, LastSentMs = 0, Attempts = 0 };
            return packet;
        }
    }

    /// <summary>
    /// Relays an ALREADY-numbered grant verbatim — the host forwarding a client's report to
    /// every other peer under the client's true identity. Returns false when this exact op has
    /// already been queued once this session (a client's retransmit arriving before its own
    /// ack does), so it is never relayed, and therefore never applied, twice — and also refuses
    /// once the pending table is at its cap.
    /// </summary>
    public bool EnqueueRelay(ClassGrantPacket packet, long nowMs)
    {
        lock (_gate)
        {
            (uint, uint) key = (packet.OriginPeerId, packet.OpSequence);
            if (_everQueued.Contains(key))
            {
                return false;
            }

            if (_pending.Count >= maxPending)
            {
                LogCapRefusal();
                return false;
            }

            _capRefusalLogged = false;

            Remember(packet.OriginPeerId, packet.OpSequence);
            _pending[key] = new Pending { Packet = packet, LastSentMs = 0, Attempts = 0 };
            return true;
        }
    }

    private void Remember(uint origin, uint sequence)
    {
        (uint, uint) key = (origin, sequence);
        if (!_everQueued.Add(key))
        {
            return;
        }

        _everQueuedHistory.Enqueue(key);
        while (_everQueuedHistory.Count > MaxRememberedOps)
        {
            _everQueued.Remove(_everQueuedHistory.Dequeue());
        }
    }

    public void Acknowledge(uint origin, uint sequence, uint ackingPeerId)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue((origin, sequence), out Pending? pending))
            {
                pending.Acked.Add(ackingPeerId);
            }
        }
    }

    /// <summary>
    /// Returns ops due for another broadcast, and marks them sent. An op every current
    /// recipient has acked is retired here.
    ///
    /// ⚠ AN EMPTY RECIPIENT LIST IS NOT "EVERYONE HAS ACKED" — same rule as every other
    /// reliable sender in this layer: a peer that is merely offline right now may still need
    /// this grant when it comes back.
    /// </summary>
    public List<ClassGrantPacket> CollectDue(IReadOnlyCollection<uint> recipients, long nowMs)
    {
        List<ClassGrantPacket> due = [];

        lock (_gate)
        {
            List<(uint, uint)>? finished = null;

            foreach (((uint origin, uint sequence), Pending pending) in _pending)
            {
                if (recipients.Count == 0)
                {
                    continue;
                }

                bool allAcked = true;
                foreach (uint recipient in recipients)
                {
                    if (!pending.Acked.Contains(recipient))
                    {
                        allAcked = false;
                        break;
                    }
                }

                if (allAcked)
                {
                    (finished ??= []).Add((origin, sequence));
                    continue;
                }

                if (pending.Attempts > 0
                    && nowMs - pending.LastSentMs < RetransmitBackoff.IntervalMs(
                        pending.Attempts, retransmitIntervalMs, maxIntervalMs))
                {
                    continue;
                }

                pending.Attempts++;
                pending.LastSentMs = nowMs;
                due.Add(pending.Packet);
            }

            if (finished is not null)
            {
                foreach ((uint, uint) key in finished)
                {
                    _pending.Remove(key);
                }
            }
        }

        return due;
    }

    /// <summary>Makes every pending op due again — called when a peer is (re)admitted, so a
    /// rejoining player is brought up to date on the next tick.</summary>
    public void ResetBackoff()
    {
        lock (_gate)
        {
            foreach (Pending pending in _pending.Values)
            {
                pending.Attempts = 0;
                pending.LastSentMs = 0;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }
}
