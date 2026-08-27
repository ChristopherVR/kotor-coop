using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Retransmit-until-ack for ground-item ops, the item twin of
/// <see cref="ReliableFlagSender"/>. "A lightsaber is lying there" is world truth, not a
/// disposable position sample, so the op is repeated until the host confirms it — and, as
/// with flags, never abandoned. See <see cref="ReliableFlagSender"/> for why giving up on an
/// unacked op silently loses a change that nothing will ever re-announce.
/// </summary>
public sealed class ReliableItemSender(
    int retransmitIntervalMs = 250, int maxIntervalMs = 4000, int maxPending = 4096)
{
    private sealed class Pending
    {
        public required ItemOpPacket Packet;
        public long LastSentMs;
        public int Attempts;
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

    private void LogCapRefusal()
    {
        if (_capRefusalLogged)
        {
            return;
        }

        _capRefusalLogged = true;
        Console.Error.WriteLine(
            $"[coop] item op refused: pending table already holds {maxPending} unacked ops — "
            + "the link has likely been down a long time.");
    }

    /// <summary>
    /// Mints and tracks an item op, or refuses it. See <see cref="RetransmitBackoff"/>'s
    /// remarks for the enqueue-past-cap contract every reliable sender in this file family
    /// shares.
    /// </summary>
    public ItemOpPacket? Enqueue(uint originPeerId, ItemOpKind kind, GroundItem item, long nowMs)
    {
        lock (_gate)
        {
            if (_pending.Count >= maxPending)
            {
                LogCapRefusal();
                return null;
            }

            _capRefusalLogged = false;

            uint sequence = _nextSequence++;
            ItemOpPacket packet = new(originPeerId, sequence, kind, item);
            _pending[sequence] = new Pending
            {
                Packet = packet,
                LastSentMs = nowMs,
                Attempts = 1,
            };

            return packet;
        }
    }

    public void Acknowledge(uint sequence)
    {
        lock (_gate)
        {
            _pending.Remove(sequence);
        }
    }

    /// <summary>Returns ops due for another attempt, and marks them as sent.</summary>
    public List<ItemOpPacket> CollectDue(long nowMs)
    {
        List<ItemOpPacket> due = [];

        lock (_gate)
        {
            foreach (Pending pending in _pending.Values)
            {
                if (nowMs - pending.LastSentMs < RetransmitBackoff.IntervalMs(
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

    /// <summary>Makes every pending op due immediately; called when a dropped link returns.</summary>
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
