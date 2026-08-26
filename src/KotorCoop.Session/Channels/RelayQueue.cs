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
    private readonly Queue<T> _inbox = new();
    private readonly Queue<(T Packet, uint ExceptPeerId)> _outbox = new();

    /// <summary>Queues a packet for this instance's own consumption.</summary>
    public void EnqueueInbox(T packet) => _inbox.Enqueue(packet);

    /// <summary>Queues a packet for the next broadcast, skipping <paramref name="exceptPeerId"/>
    /// (the sender, so it never receives its own line back).</summary>
    public void EnqueueOutbox(T packet, uint exceptPeerId) => _outbox.Enqueue((packet, exceptPeerId));

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
