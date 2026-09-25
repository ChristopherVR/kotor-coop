using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>The reliable send/relay/dedup half of docs/research/personal-inventory.md §4's
/// credits-reward channel — "never double-pay on resync" lives here and in
/// <see cref="CoopClient.CreditGrant.cs"/>/<see cref="CoopHost.CreditGrant.cs"/>'s own inbox
/// dedup sets.</summary>
public class CreditGrantSenderTests
{
    [Fact]
    public void EnqueueLocalRefusesANonPositiveAmount()
    {
        CreditGrantSender sender = new();
        Assert.Null(sender.EnqueueLocal(1, 0, 0));
        Assert.Null(sender.EnqueueLocal(1, -5, 0));
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void EnqueueLocalQueuesAPositiveAmountWithAnIncrementingSequence()
    {
        CreditGrantSender sender = new();
        CreditGrantPacket? first = sender.EnqueueLocal(1, 50, 0);
        CreditGrantPacket? second = sender.EnqueueLocal(1, 25, 0);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1u, first!.OpSequence);
        Assert.Equal(2u, second!.OpSequence);
        Assert.Equal(2, sender.PendingCount);
    }

    [Fact]
    public void EnqueueRelayRefusesTheSameOpTwice()
    {
        CreditGrantSender sender = new();
        CreditGrantPacket packet = new(OriginPeerId: 3, OpSequence: 7, Amount: 100);

        Assert.True(sender.EnqueueRelay(packet, 0));

        // The exact "never double-pay on resync" guard: a client's retransmit of the same op,
        // arriving at the host before its ack does, must never be queued (and therefore
        // broadcast, and therefore applied on every receiver's own purse) a second time.
        Assert.False(sender.EnqueueRelay(packet, 100));
        Assert.Equal(1, sender.PendingCount);
    }

    [Fact]
    public void EnqueueRelayRefusesANonPositiveAmount()
    {
        CreditGrantSender sender = new();
        Assert.False(sender.EnqueueRelay(new CreditGrantPacket(1, 1, 0), 0));
        Assert.False(sender.EnqueueRelay(new CreditGrantPacket(1, 1, -10), 0));
    }

    [Fact]
    public void CollectDueRetransmitsUntilEveryRecipientHasAcked()
    {
        CreditGrantSender sender = new(retransmitIntervalMs: 10, maxIntervalMs: 100);
        sender.EnqueueLocal(1, 50, 0);

        List<CreditGrantPacket> firstPass = sender.CollectDue([2, 3], 0);
        Assert.Single(firstPass);

        // Recipient 3 acks; 2 has not - still due.
        sender.Acknowledge(1, 1, 3);
        List<CreditGrantPacket> stillPendingBeforeBackoff = sender.CollectDue([2, 3], 5);
        Assert.Empty(stillPendingBeforeBackoff); // inside the retransmit backoff window

        List<CreditGrantPacket> retransmit = sender.CollectDue([2, 3], 50);
        Assert.Single(retransmit);

        sender.Acknowledge(1, 1, 2);
        List<CreditGrantPacket> afterEveryoneAcked = sender.CollectDue([2, 3], 100);
        Assert.Empty(afterEveryoneAcked);
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void AnEmptyRecipientListIsNeverTreatedAsEveryoneHavingAcked()
    {
        CreditGrantSender sender = new();
        sender.EnqueueLocal(1, 50, 0);

        // Nobody connected right now - the op must survive, not be silently retired.
        sender.CollectDue([], 0);
        Assert.Equal(1, sender.PendingCount);
    }

    [Fact]
    public void EnqueueLocalRefusesPastTheCap()
    {
        CreditGrantSender sender = new(maxPending: 2);
        Assert.NotNull(sender.EnqueueLocal(1, 10, 0));
        Assert.NotNull(sender.EnqueueLocal(1, 10, 0));
        Assert.Null(sender.EnqueueLocal(1, 10, 0));
        Assert.Equal(2, sender.PendingCount);
    }
}
