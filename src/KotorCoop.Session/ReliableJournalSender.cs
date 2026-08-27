using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Retransmit-until-ack for journal ops, exactly like <see cref="ReliableFlagSender"/>.
/// A quest advance is world truth: a lost position update is superseded 50 ms later, a
/// lost "the quest moved on" is a player permanently behind — which is precisely why an op
/// here is retried with a growing backoff rather than abandoned after a fixed count.
/// </summary>
public sealed class ReliableJournalSender(
    int retransmitIntervalMs = 250, int maxIntervalMs = 4000, int maxPending = 4096)
{
    private sealed class Pending
    {
        public required JournalOpPacket Packet;
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
            $"[coop] journal op refused: pending table already holds {maxPending} unacked ops — "
            + "the link has likely been down a long time.");
    }

    /// <summary>
    /// Mints and tracks a journal op, or refuses it. See <see cref="RetransmitBackoff"/>'s
    /// remarks for the enqueue-past-cap contract every reliable sender in this file family
    /// shares.
    /// </summary>
    public JournalOpPacket? Enqueue(
        uint originPeerId, string questTag, int state, long nowMs, uint serial = 0, bool removed = false)
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
            JournalOpPacket packet = new(originPeerId, sequence, questTag, state, serial, removed);
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
    public List<JournalOpPacket> CollectDue(long nowMs)
    {
        List<JournalOpPacket> due = [];

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
