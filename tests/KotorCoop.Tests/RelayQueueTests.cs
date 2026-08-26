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
}
