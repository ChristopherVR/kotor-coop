namespace KotorCoop.Session.Channels;

/// <summary>
/// The hand-rolled "receive locally, and relay to everyone but the sender" queue pair that
/// several of the host's unreliable channels (barks, transition movies/announces, mid-module
/// movies) repeated verbatim: one inbox for this instance's own consumption, one outbox of
/// (packet, peer id to skip) pairs for the next tick's broadcast.
///
/// Not internally locked — callers hold the session's own <c>_gate</c> around every mutation,
/// exactly as they did with the two hand-rolled <see cref="Queue{T}"/> fields this replaces, so
/// this type only needs to reproduce their exact enqueue/drain/clear behaviour, not add its own
/// synchronisation.
/// </summary>
public sealed class RelayQueue<T>
{
    /// <summary>
    /// How many packets either queue will hold before the oldest is discarded.
    ///
    /// ⚠ THESE QUEUES HAVE NO OTHER BOUND, AND A DRAIN IS NOT GUARANTEED. Both are emptied by
    /// somebody else's schedule — the inbox by the agent's pump, the outbox by the session's
    /// tick loop — and neither is guaranteed to run. A client whose agent is stalled behind a
    /// load screen, a session running with no game attached at all, a host whose tick loop is
    /// starved: in every one of those the far end keeps talking and this keeps growing, for as
    /// long as it lasts. The bound is generous enough that no ordinary burst reaches it (a
    /// lobby of eight shouting at once is single digits a second) and small enough that a
    /// wedged consumer costs kilobytes rather than a session.
    ///
    /// Dropping the OLDEST rather than refusing the newest is right for these channels
    /// specifically: every one of them is unreliable and time-sensitive (a bark, a movie
    /// announcement, a transition), so the stale end of a backlog is the worthless end. That is
    /// the opposite of the reliable senders' rule, where the pending table is world truth and
    /// the cap refuses the NEW op instead — see <c>RetransmitBackoff</c>'s remarks.
    /// </summary>
    public const int MaxQueued = 256;

    private readonly Queue<T> _inbox = new();
    private readonly Queue<(T Packet, uint ExceptPeerId)> _outbox = new();

    /// <summary>How many packets have been discarded by the bound, across both queues. Zero in
    /// every healthy session; a non-zero reading means something stopped draining.</summary>
    public long Discarded { get; private set; }

    /// <summary>Queues a packet for this instance's own consumption.</summary>
    public void EnqueueInbox(T packet)
    {
        _inbox.Enqueue(packet);

        while (_inbox.Count > MaxQueued)
        {
            _inbox.Dequeue();
            Discarded++;
        }
    }

    /// <summary>Queues a packet for the next broadcast, skipping <paramref name="exceptPeerId"/>
    /// (the sender, so it never receives its own line back).</summary>
    public void EnqueueOutbox(T packet, uint exceptPeerId)
    {
        _outbox.Enqueue((packet, exceptPeerId));

        while (_outbox.Count > MaxQueued)
        {
            _outbox.Dequeue();
            Discarded++;
        }
    }

    /// <summary>Drains every queued inbound packet, oldest first.</summary>
    public IReadOnlyList<T> DrainInbox()
    {
        if (_inbox.Count == 0)
        {
            return [];
        }

        List<T> ready = [];
        while (_inbox.Count > 0)
        {
            ready.Add(_inbox.Dequeue());
        }

        return ready;
    }

    /// <summary>Drains every queued outbound (packet, except-peer) pair, oldest first, for the
    /// tick loop to broadcast.</summary>
    public List<(T Packet, uint ExceptPeerId)> DrainOutbox()
    {
        if (_outbox.Count == 0)
        {
            return [];
        }

        List<(T Packet, uint ExceptPeerId)> pending = [.. _outbox];
        _outbox.Clear();
        return pending;
    }
}
