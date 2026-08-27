using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Retransmit-until-ack for party-pool ops, broadcast to a SET of recipients — the same shape
/// as <see cref="ReliableLootSender"/> (used by both <c>CoopHost</c>, broadcasting to every
/// client, and <c>CoopClient</c>, whose one recipient is the host) and the same one-empty-
/// recipient-list-is-not-"everyone-acked" fix that class exists for.
///
/// The one real difference from <see cref="ReliableLootSender"/>: no in-flight dedup by
/// content. A container removal for (container, item) already in flight is a legitimate no-op
/// to skip re-enqueuing (the same fact, twice), but two party-pool ops for the same stack key
/// are NOT the same fact — "add 5" then "add 3" are both real, additive events, and collapsing
/// them would silently drop one. Every <see cref="Enqueue"/> call therefore always queues a
/// new, distinct, sequenced op; only the <see cref="RetransmitBackoff"/> contract every
/// reliable sender in this family shares — refuse past <c>maxPending</c>, log once, retry with
/// backoff, never abandon an unacked op — carries over.
/// </summary>
public sealed class ReliablePartyPoolSender(
    int retransmitIntervalMs = 250, int maxIntervalMs = 4000, int maxPending = 1024)
{
    private sealed class Pending
    {
        public required PartyPoolOpPacket Packet;
        public long LastSentMs;
        public int Attempts;
        public readonly HashSet<uint> Acked = [];
    }

    private readonly Dictionary<uint, Pending> _pending = [];
    private readonly Lock _gate = new();
    private uint _nextSequence = 1;

    /// <summary>Latched so a sustained overload logs once, not once per refused op — see
    /// <see cref="ReliableFlagSender"/>'s twin field.</summary>
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

    /// <summary>
    /// Queues an add/remove for reliable broadcast, or refuses it past the cap. See
    /// <see cref="RetransmitBackoff"/>'s remarks for the enqueue-past-cap contract every
    /// reliable sender in this file family shares.
    /// </summary>
    public PartyPoolOpPacket? Enqueue(
        uint originPeerId, PartyPoolOpKind kind, PartyPoolStackKey key, ushort countDelta, long nowMs)
    {
        lock (_gate)
        {
            if (_pending.Count >= maxPending)
            {
                if (!_capRefusalLogged)
                {
                    _capRefusalLogged = true;
                    Console.Error.WriteLine(
                        $"[coop] party-pool op refused: pending table already holds "
                        + $"{maxPending} unacked ops — the link has likely been down a long time.");
                }

                return null;
            }

            _capRefusalLogged = false;

            uint sequence = _nextSequence++;
            PartyPoolOpPacket packet = new(originPeerId, sequence, kind, key, countDelta);

            // LastSentMs = 0 so CollectDue emits it on the very next tick.
            _pending[sequence] = new Pending { Packet = packet, LastSentMs = 0, Attempts = 0 };
            return packet;
        }
    }

    public void Acknowledge(uint peerId, uint sequence)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(sequence, out Pending? pending))
            {
                pending.Acked.Add(peerId);
            }
        }
    }

    /// <summary>
    /// Returns ops due for another broadcast, and marks them sent. An op every current
    /// recipient has acked is retired here. An empty recipient list holds every unacked op
    /// rather than retiring it — see <see cref="ReliableLootSender.CollectDue"/>'s identical
    /// rule and the disconnect bug it was fixed for.
    /// </summary>
    public List<PartyPoolOpPacket> CollectDue(IReadOnlyCollection<uint> recipients, long nowMs)
    {
        List<PartyPoolOpPacket> due = [];

        lock (_gate)
        {
            List<uint>? finished = null;

            foreach ((uint sequence, Pending pending) in _pending)
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
                    (finished ??= []).Add(sequence);
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
                foreach (uint sequence in finished)
                {
                    _pending.Remove(sequence);
                }
            }
        }

        return due;
    }

    /// <summary>Makes every pending op due again — called when a peer is admitted, so a
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
