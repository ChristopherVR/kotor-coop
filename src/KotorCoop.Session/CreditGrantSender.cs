using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Retransmit-until-ack for <see cref="CreditGrantPacket"/> — modelled directly on
/// <see cref="DialogueGrantSender"/> (same reasoning: a reward's credits are world truth, not a
/// disposable sample, repeated until every current recipient has confirmed it; either peer may
/// originate one, so this class is used symmetrically by <see cref="CoopHost"/> and
/// <see cref="CoopClient"/>; pending ops are keyed by (origin, sequence) so the host's own
/// locally-numbered reports and a client's already-numbered relayed ones never collide).
/// A deliberately separate, smaller class rather than a shared generic base — see
/// docs/research/personal-inventory.md §4 for why this channel is additive rather than folded
/// into <see cref="DialogueGrantSender"/> itself.
/// </summary>
public sealed class CreditGrantSender(
    int retransmitIntervalMs = 250, int maxIntervalMs = 4000, int maxPending = CreditGrantSender.DefaultMaxPending)
{
    /// <summary>Same reasoning as <see cref="DialogueGrantSender.DefaultMaxPending"/>, smaller
    /// bound — a credits reward is far rarer over a playthrough than an item grant.</summary>
    public const int DefaultMaxPending = 256;

    private sealed class Pending
    {
        public required CreditGrantPacket Packet;
        public long LastSentMs;
        public int Attempts;
        public readonly HashSet<uint> Acked = [];
    }

    private readonly Dictionary<(uint Origin, uint Sequence), Pending> _pending = [];

    /// <summary>Every (origin, sequence) ever accepted here, kept past retirement so a
    /// straggling retransmit is never queued a second time — re-queueing it would apply the
    /// grant twice. See <see cref="DialogueGrantSender"/>'s identical field.</summary>
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
            $"[coop] credit grant refused: pending table already holds {maxPending} unacked "
            + "ops - the link has likely been down a long time.");
    }

    /// <summary>Queues a LOCALLY DETECTED grant. Refuses (returns null) for a non-positive
    /// amount or once the pending table is at its cap.</summary>
    public CreditGrantPacket? EnqueueLocal(uint originPeerId, int amount, long nowMs)
    {
        if (amount <= 0)
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
            CreditGrantPacket packet = new(originPeerId, sequence, amount);
            Remember(originPeerId, sequence);
            _pending[(originPeerId, sequence)] = new Pending { Packet = packet, LastSentMs = 0, Attempts = 0 };
            return packet;
        }
    }

    /// <summary>Relays an ALREADY-numbered grant verbatim (the host forwarding a client's own
    /// report under that client's true identity). Returns false for an already-relayed op (the
    /// double-give guard) or a non-positive amount, and once the pending table is at its cap.
    /// </summary>
    public bool EnqueueRelay(CreditGrantPacket packet, long nowMs)
    {
        if (packet.Amount <= 0)
        {
            return false;
        }

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

    /// <summary>Returns ops due for another broadcast, and marks them sent. An op every current
    /// recipient has acked is retired here. An empty recipient list is NOT "everyone has acked"
    /// — same rule as every other reliable sender in this family.</summary>
    public List<CreditGrantPacket> CollectDue(IReadOnlyCollection<uint> recipients, long nowMs)
    {
        List<CreditGrantPacket> due = [];

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

    /// <summary>Makes every pending op due again — called when a peer is (re)admitted.</summary>
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
