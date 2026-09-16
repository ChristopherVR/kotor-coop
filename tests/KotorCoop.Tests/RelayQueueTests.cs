using KotorCoop.Session.Channels;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="RelayQueue{T}"/> replaces the hand-rolled inbox/outbox <see cref="Queue{T}"/> pair
/// CoopHost repeated for barks, transition movies/announces and mid-module movies. These tests
/// pin its enqueue/drain/clear semantics: FIFO order, an empty drain returns an empty list
/// without disturbing anything else queued, and inbox/outbox are independent of each other.
/// </summary>
public class RelayQueueTests
{
    [Fact]
    public void DrainInboxReturnsItemsInFifoOrderAndEmptiesTheQueue()
    {
        RelayQueue<string> queue = new();
        queue.EnqueueInbox("first");
        queue.EnqueueInbox("second");
        queue.EnqueueInbox("third");

        IReadOnlyList<string> drained = queue.DrainInbox();

        Assert.Equal(["first", "second", "third"], drained);
        Assert.Empty(queue.DrainInbox());
    }

    [Fact]
    public void DrainOutboxReturnsPacketAndExceptPeerPairsInFifoOrderAndClears()
    {
        RelayQueue<string> queue = new();
        queue.EnqueueOutbox("a", exceptPeerId: 1);
        queue.EnqueueOutbox("b", exceptPeerId: 2);

        List<(string Packet, uint ExceptPeerId)> drained = queue.DrainOutbox();

        Assert.Equal([("a", 1u), ("b", 2u)], drained);
        Assert.Empty(queue.DrainOutbox());
    }

    [Fact]
    public void DrainingOneQueueDoesNotAffectTheOther()
    {
        RelayQueue<int> queue = new();
        queue.EnqueueInbox(1);
        queue.EnqueueOutbox(2, exceptPeerId: 9);

        Assert.Equal([1], queue.DrainInbox());
        List<(int Packet, uint ExceptPeerId)> outbox = queue.DrainOutbox();
        Assert.Equal([(2, 9u)], outbox);
    }

    [Fact]
    public void EmptyDrainsReturnEmptyCollectionsNotNull()
    {
        RelayQueue<int> queue = new();

        Assert.Empty(queue.DrainInbox());
        Assert.Empty(queue.DrainOutbox());
    }

    // ---- The bound ---------------------------------------------------------------------------
    //
    // Nothing guarantees either queue is drained: the inbox waits on the agent's pump and the
    // outbox on the session's tick loop, and a client stalled behind a load screen (or a
    // session running with no game attached at all) runs neither while the far end keeps
    // talking. See RelayQueue.MaxQueued.

    [Fact]
    public void AnUndrainedInboxStopsGrowingAtItsBound()
    {
        RelayQueue<int> queue = new();

        for (int i = 0; i < RelayQueue<int>.MaxQueued * 3; i++)
        {
            queue.EnqueueInbox(i);
        }

        IReadOnlyList<int> drained = queue.DrainInbox();

        Assert.Equal(RelayQueue<int>.MaxQueued, drained.Count);
        Assert.Equal(RelayQueue<int>.MaxQueued * 2, queue.Discarded);
    }

    [Fact]
    public void TheBoundKeepsTheNEWESTPacketsOnAnUnreliableChannel()
    {
        RelayQueue<int> queue = new();

        for (int i = 0; i < RelayQueue<int>.MaxQueued + 5; i++)
        {
            queue.EnqueueInbox(i);
        }

        IReadOnlyList<int> drained = queue.DrainInbox();

        // A bark, a movie cue or a transition announcement nobody has collected in a while is
        // worthless; the newest one is the one still worth playing. This is deliberately the
        // OPPOSITE of the reliable senders, whose pending table is world truth and refuses the
        // new op instead — see RetransmitBackoff's remarks.
        Assert.Equal(5, drained[0]);
        Assert.Equal(RelayQueue<int>.MaxQueued + 4, drained[^1]);
    }

    [Fact]
    public void AnUndrainedOutboxStopsGrowingAtItsBound()
    {
        RelayQueue<int> queue = new();

        for (int i = 0; i < RelayQueue<int>.MaxQueued + 10; i++)
        {
            queue.EnqueueOutbox(i, exceptPeerId: 3);
        }

        Assert.Equal(RelayQueue<int>.MaxQueued, queue.DrainOutbox().Count);
        Assert.Equal(10, queue.Discarded);
    }

    [Fact]
    public void AHealthySessionDiscardsNothing()
    {
        RelayQueue<int> queue = new();

        for (int round = 0; round < 50; round++)
        {
            queue.EnqueueInbox(round);
            queue.EnqueueOutbox(round, exceptPeerId: 1);
            queue.DrainInbox();
            queue.DrainOutbox();
        }

        Assert.Equal(0, queue.Discarded);
    }
}
