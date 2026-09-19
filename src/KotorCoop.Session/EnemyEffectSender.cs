using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Retransmit-until-ack for <see cref="EnemyEffectPacket"/>, the client's report to the host
/// (docs/research/enemy-effects.md). Modelled on <see cref="ClassGrantSender"/> but one-way, and
/// deliberately SHORT-LIVED: an effect is only worth replaying while it is still running, so an
/// op older than <see cref="EnemyEffectRules.MaxDurationMs"/>-scale patience is dropped instead of
/// retransmitted for ever, and the pending table is small — a spam of casts costs the caster
/// reports, never host work (the host inbox and the plugin's per-enemy cooldown cap the rest).
/// </summary>
public sealed class EnemyEffectSender(
    int retransmitIntervalMs = 250, int maxIntervalMs = 2000, int maxPending = 32, long maxAgeMs = 15_000)
{
    private sealed class Pending
    {
        public required EnemyEffectPacket Packet;
        public long EnqueuedMs;
        public long LastSentMs;
        public int Attempts;
        public bool Acked;
    }

    private readonly Dictionary<(uint Origin, uint Sequence), Pending> _pending = [];
    private readonly Lock _gate = new();
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

    /// <summary>Queues one captured effect under <paramref name="originPeerId"/>. Null when the
    /// kind is not forwardable, the enemy id is empty, or the table is full.</summary>
    public EnemyEffectPacket? EnqueueLocal(
        uint originPeerId, string module, uint enemyId, PeerEffectKind kind,
        uint durationMs, uint magnitude, long nowMs)
    {
        if (enemyId == 0 || !EnemyEffectRules.IsForwardable(kind))
        {
            return null;
        }

        lock (_gate)
        {
            if (_pending.Count >= maxPending)
            {
                return null;
            }

            uint sequence = _nextSequence++;
            EnemyEffectPacket packet = new(
                originPeerId, sequence, module, enemyId, kind,
                EnemyEffectRules.ClampDurationMs(durationMs), EnemyEffectRules.ClampMagnitude(magnitude));
            _pending[(originPeerId, sequence)] = new Pending { Packet = packet, EnqueuedMs = nowMs };
            return packet;
        }
    }

    public void Acknowledge(uint origin, uint sequence)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue((origin, sequence), out Pending? pending))
            {
                pending.Acked = true;
            }
        }
    }

    /// <summary>Ops due for a send or retransmit, marked sent. Acked and expired ops are retired.
    /// An op the host has not acked (the link is down) is held only until it goes stale.</summary>
    public List<EnemyEffectPacket> CollectDue(long nowMs)
    {
        List<EnemyEffectPacket> due = [];

        lock (_gate)
        {
            List<(uint, uint)>? finished = null;

            foreach (((uint origin, uint sequence), Pending pending) in _pending)
            {
                if (pending.Acked || nowMs - pending.EnqueuedMs > maxAgeMs)
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

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }
}
