using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Retransmit-until-ack for a client's <see cref="PlacedMineIntentPacket"/>s to the host — the
/// single-recipient shape of <see cref="ClassGrantSender"/>. A lost "I placed a mine" would leave
/// the placer's mine inert on its own screen (clients never fire party mines) with no host copy;
/// a lost "serial S is gone here" would leave the canonical copy armed.
/// </summary>
public sealed class PlacedMineIntentSender(
    int retransmitIntervalMs = 250, int maxIntervalMs = 4000, int maxPending = 64)
{
    private sealed class Pending
    {
        public required PlacedMineIntentPacket Packet;
        public long LastSentMs;
        public int Attempts;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<uint, Pending> _pending = [];
    private uint _nextSequence = 1;

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

    public PlacedMineIntentPacket? Enqueue(
        uint originPeerId, PlacedMineIntentKind kind, uint moduleHash, uint captureSerial, uint mineSerial, PlacedMineRecord record)
    {
        lock (_gate)
        {
            if (_pending.Count >= maxPending)
            {
                return null;
            }

            uint sequence = _nextSequence++;
            PlacedMineIntentPacket packet = new(originPeerId, sequence, kind, moduleHash, captureSerial, mineSerial, record);
            _pending[sequence] = new Pending { Packet = packet };
            return packet;
        }
    }

    public void Acknowledge(uint originPeerId, uint sequence)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(sequence, out Pending? pending) && pending.Packet.OriginPeerId == originPeerId)
            {
                _pending.Remove(sequence);
            }
        }
    }

    /// <summary>Ops due for (re)sending, oldest first; marks them sent.</summary>
    public List<PlacedMineIntentPacket> CollectDue(long nowMs)
    {
        List<PlacedMineIntentPacket> due = [];
        lock (_gate)
        {
            foreach (Pending pending in _pending.Values.OrderBy(p => p.Packet.OpSequence))
            {
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
        }

        return due;
    }

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
