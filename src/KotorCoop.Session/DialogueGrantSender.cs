using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Retransmit-until-ack for <see cref="DialogueGrantPacket"/>, modelled directly on
/// <see cref="MaterialisedLootSender"/> — "a reward node put this item in the speaker's
/// inventory" is world truth, not a disposable sample, so the op is repeated until every
/// current recipient has confirmed it.
///
/// Unlike <see cref="MaterialisedLootSender"/> (host-only: only the host's own engine ever
/// holds a dying enemy's real corpse), a dialogue grant can legitimately originate from EITHER
/// side — whichever peer's instance owns the conversation — so this class is used symmetrically
/// by both <see cref="CoopHost"/> (broadcasting to every client, and relaying a client's own
/// report to every OTHER client) and <see cref="CoopClient"/> (reporting to the single host
/// recipient), exactly the way <see cref="ReliableLootSender"/> is shared for container loot.
///
/// Pending ops are keyed by (origin, sequence) rather than a bare sequence, because on the HOST
/// side the same instance juggles its own locally-numbered reports (<see cref="EnqueueLocal"/>)
/// alongside a client's already-numbered ones being relayed verbatim (<see cref="EnqueueRelay"/>)
/// — two peers numbering their own ops from 1 independently would otherwise collide.
/// </summary>
public sealed class DialogueGrantSender(
    int retransmitIntervalMs = 250, int maxIntervalMs = 4000, int maxPending = DialogueGrantSender.DefaultMaxPending)
{
    /// <summary>
    /// The pending-table cap, and why it is a cap rather than an unbounded queue
    /// (docs/review-seamless-2026-09-18.md §2 "long-session growth"): every pending op is walked
    /// by <see cref="CollectDue"/> on every tick, so the table has to stay small enough for
    /// that walk to be free, and a grant is only ever pending while SOME recipient has not
    /// acked it. With the link up that is milliseconds; with it down, ops accumulate one per
    /// reward node the local player plays through solo (<c>host_loss_release</c>), which is a
    /// handful per conversation. A full KOTOR 1 playthrough grants a few hundred items across
    /// the whole game, so 1024 unacked grants means an outage that has outlasted the session
    /// several times over — at which point refusing (with one logged line) loses less than
    /// walking a thousand-entry table thirty times a second forever would. Raised from 256 the
    /// same day, on the same arithmetic: the old figure was one long solo evening away.
    /// </summary>
    public const int DefaultMaxPending = 1024;

    private sealed class Pending
    {
        public required DialogueGrantPacket Packet;
        public long LastSentMs;
        public int Attempts;
        public readonly HashSet<uint> Acked = [];
    }

    private readonly Dictionary<(uint Origin, uint Sequence), Pending> _pending = [];

    /// <summary>Every (origin, sequence) ever accepted here, kept past retirement so a
    /// straggling retransmit of a report already fully relayed is not queued a second time —
    /// re-queueing it would broadcast the same grant again and double-give the item. Bounded
    /// like every other op-history set in this layer (<see cref="ContainerLootStore"/>'s
    /// <c>MaxRememberedOps</c>): a real session grants at most dozens of these, so the cap is
    /// headroom, not a real limit.</summary>
    private readonly HashSet<(uint Origin, uint Sequence)> _everQueued = [];
    private readonly Queue<(uint Origin, uint Sequence)> _everQueuedHistory = new();
    private const int MaxRememberedOps = 4096;

    private readonly Lock _gate = new();
    private uint _nextSequence = 1;

    /// <summary>Latched so a sustained overload logs once, not once per refused op — see
    /// <see cref="ReliableFlagSender"/>'s twin field. Shared by both cap checks below
    /// (<see cref="EnqueueLocal"/> and <see cref="EnqueueRelay"/>), since either can hit the
    /// same table.</summary>
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
            $"[coop] dialogue grant refused: pending table already holds {maxPending} unacked "
            + "ops — the link has likely been down a long time.");
    }

    /// <summary>
    /// Queues a LOCALLY DETECTED grant — this instance's own conversation just granted
    /// something — self-numbered under <paramref name="originPeerId"/> (the caller's own peer
    /// id: <c>CoopHost.HostPeerId</c> on the host, the session peer id on a client). Refuses
    /// (returns null) once the pending table is at its cap — see
    /// <see cref="RetransmitBackoff"/>'s remarks for the enqueue-past-cap contract every
    /// reliable sender in this file family shares. An empty item list is also refused, but
    /// that is not the cap contract and is not logged, since it is routine (a reward node with
    /// nothing to give).
    /// </summary>
    public DialogueGrantPacket? EnqueueLocal(
        uint originPeerId, string partnerTag, IReadOnlyList<MaterialisedLootItem> items, long nowMs)
    {
        if (items.Count == 0)
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
            DialogueGrantPacket packet = new(originPeerId, sequence, partnerTag, items);
            Remember(originPeerId, sequence);
            _pending[(originPeerId, sequence)] = new Pending { Packet = packet, LastSentMs = 0, Attempts = 0 };
            return packet;
        }
    }

    /// <summary>
    /// Relays an ALREADY-numbered grant verbatim — the host forwarding a client's own report to
    /// every other peer under that client's true (origin, sequence) rather than re-numbering it
    /// under the host's own identity. Returns false when this exact op has already been queued
    /// once this session (a client's retransmit arriving before its own ack does), so it is
    /// never broadcast, and therefore never given, twice — and also refuses (returns false)
    /// once the pending table is at its cap, per <see cref="RetransmitBackoff"/>'s remarks on
    /// the enqueue-past-cap contract every reliable sender in this file family shares.
    /// </summary>
    public bool EnqueueRelay(DialogueGrantPacket packet, long nowMs)
    {
        if (packet.Items.Count == 0)
        {
            return false;
        }

        lock (_gate)
        {
            (uint, uint) key = (packet.OriginPeerId, packet.OpSequence);
            if (_everQueued.Contains(key))
            {
                // Already relayed once this session — routine, not the cap contract, so not
                // logged (see the class remarks: this is what stops a client's retransmit
                // from double-giving the item).
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
    /// ⚠ AN EMPTY RECIPIENT LIST IS NOT "EVERYONE HAS ACKED" — same rule as
    /// <see cref="MaterialisedLootSender.CollectDue"/>/<see cref="ReliableLootSender.CollectDue"/>,
    /// for the same reconnect-outage reason: a peer that is merely offline right now may still
    /// need this grant when it comes back.
    /// </summary>
    public List<DialogueGrantPacket> CollectDue(IReadOnlyCollection<uint> recipients, long nowMs)
    {
        List<DialogueGrantPacket> due = [];

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
    /// rejoining player is brought up to date on the next tick rather than waiting out a
    /// backoff that grew while there was nobody to talk to.</summary>
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
