using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Retry/ack coverage for the four retransmit-until-ack senders
/// (<see cref="ReliableFlagSender"/>, <see cref="ReliableItemSender"/>,
/// <see cref="ReliableJournalSender"/>, <see cref="ReliableLootSender"/>). All four take an
/// explicit <c>nowMs</c> on every call rather than reading a clock themselves, so the clock is
/// already injectable — these tests just drive it directly.
///
/// The three client-&gt;host senders (Flag/Item/Journal) share one shape: <c>Enqueue</c> marks
/// the op sent immediately (Attempts = 1, LastSentMs = nowMs), and <c>CollectDue</c> looks only
/// at elapsed time, not a peer set. <see cref="ReliableLootSender"/> is the host-&gt;clients
/// broadcast twin: it tracks per-peer acks and its <c>CollectDue</c> takes the current recipient
/// set on every call, which is also how it learns a peer left (no separate Remove/Forget API —
/// the caller simply stops naming that peer as a recipient).
/// </summary>
public class ReliableSenderTests
{
    // ---- ReliableFlagSender --------------------------------------------------------------

    [Fact]
    public void Flag_FirstSendGoesOutImmediately()
    {
        ReliableFlagSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, flagId: 10, value: 1, nowMs: 1_000);

        // Enqueue itself is the first transmission (the caller sends the returned packet);
        // CollectDue at the same instant must not also emit it as a retransmit yet.
        Assert.Empty(sender.CollectDue(nowMs: 1_000));
        Assert.Equal(1, sender.PendingCount);
    }

    [Fact]
    public void Flag_RetransmitsAfterIntervalNotBefore()
    {
        ReliableFlagSender sender = new(retransmitIntervalMs: 250);
        FlagOpPacket op = sender.Enqueue(originPeerId: 1, flagId: 10, value: 1, nowMs: 1_000);

        Assert.Empty(sender.CollectDue(nowMs: 1_249));
        List<FlagOpPacket> due = sender.CollectDue(nowMs: 1_250);

        Assert.Single(due);
        Assert.Equal(op, due[0]);
    }

    [Fact]
    public void Flag_AckStopsRetransmission()
    {
        ReliableFlagSender sender = new(retransmitIntervalMs: 250);
        FlagOpPacket op = sender.Enqueue(originPeerId: 1, flagId: 10, value: 1, nowMs: 1_000);

        sender.Acknowledge(op.OpSequence);

        Assert.Equal(0, sender.PendingCount);
        Assert.Empty(sender.CollectDue(nowMs: 10_000));
    }

    [Fact]
    public void Flag_AckForUnknownOpIsIgnored()
    {
        ReliableFlagSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, flagId: 10, value: 1, nowMs: 1_000);

        sender.Acknowledge(sequence: 999);

        // The real (unrelated) op is unaffected and still retransmits on schedule.
        Assert.Equal(1, sender.PendingCount);
        Assert.Single(sender.CollectDue(nowMs: 1_250));
    }

    [Fact]
    public void Flag_GivesUpAfterMaxAttempts()
    {
        ReliableFlagSender sender = new(retransmitIntervalMs: 100, maxAttempts: 3);
        sender.Enqueue(originPeerId: 1, flagId: 10, value: 1, nowMs: 0);

        // Enqueue = attempt 1. Two more CollectDue calls at the interval reach attempt 3
        // (the cap); a further call must not retransmit a fourth time and must drop it.
        Assert.Single(sender.CollectDue(nowMs: 100));  // attempt 2
        Assert.Single(sender.CollectDue(nowMs: 200));  // attempt 3 (== maxAttempts)
        Assert.Empty(sender.CollectDue(nowMs: 300));
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void Flag_LargeBurstIsDrainedAcrossTicksWithoutLoss()
    {
        ReliableFlagSender sender = new(retransmitIntervalMs: 250);
        const int count = 500;

        HashSet<uint> enqueued = [];
        for (uint i = 0; i < count; i++)
        {
            FlagOpPacket op = sender.Enqueue(originPeerId: 1, flagId: i, value: 1, nowMs: 1_000);
            enqueued.Add(op.OpSequence);
        }

        Assert.Equal(count, sender.PendingCount);

        // No sender in this codebase caps how many ops CollectDue returns in one call, so a
        // burst this size still all comes back the moment it is due, and it comes back again
        // on every following retransmit tick — the property under test is that nothing is
        // silently dropped, not that it is spread out.
        List<FlagOpPacket> firstRetransmit = sender.CollectDue(nowMs: 1_250);
        Assert.Equal(count, firstRetransmit.Count);
        Assert.Equal(enqueued, firstRetransmit.Select(op => op.OpSequence).ToHashSet());

        List<FlagOpPacket> secondRetransmit = sender.CollectDue(nowMs: 1_500);
        Assert.Equal(count, secondRetransmit.Count);
        Assert.Equal(enqueued, secondRetransmit.Select(op => op.OpSequence).ToHashSet());

        // Acking half mid-stream removes exactly that half from the next tick, proving the
        // burst is tracked per-op rather than as an opaque batch.
        foreach (uint sequence in enqueued.Where(s => s % 2 == 0))
        {
            sender.Acknowledge(sequence);
        }

        Assert.Equal(count / 2, sender.PendingCount);
        List<FlagOpPacket> thirdRetransmit = sender.CollectDue(nowMs: 1_750);
        Assert.Equal(count / 2, thirdRetransmit.Count);
        Assert.All(thirdRetransmit, op => Assert.True(op.OpSequence % 2 != 0));
    }

    [Fact]
    public void Flag_ClearDropsAllPending()
    {
        ReliableFlagSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, flagId: 10, value: 1, nowMs: 1_000);
        sender.Enqueue(originPeerId: 1, flagId: 11, value: 0, nowMs: 1_000);

        sender.Clear();

        Assert.Equal(0, sender.PendingCount);
        Assert.Empty(sender.CollectDue(nowMs: 10_000));
    }

    // ---- ReliableItemSender ----------------------------------------------------------------

    private static GroundItem Item(uint id) => new(id, "g_w_lghtsbr01", "end_m01aa", new Vector3f(1, 2, 3));

    [Fact]
    public void Item_FirstSendGoesOutImmediately()
    {
        ReliableItemSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, ItemOpKind.Drop, Item(1), nowMs: 1_000);

        Assert.Empty(sender.CollectDue(nowMs: 1_000));
        Assert.Equal(1, sender.PendingCount);
    }

    [Fact]
    public void Item_RetransmitsAfterIntervalNotBefore()
    {
        ReliableItemSender sender = new(retransmitIntervalMs: 250);
        ItemOpPacket op = sender.Enqueue(originPeerId: 1, ItemOpKind.Drop, Item(1), nowMs: 1_000);

        Assert.Empty(sender.CollectDue(nowMs: 1_249));
        List<ItemOpPacket> due = sender.CollectDue(nowMs: 1_250);

        Assert.Single(due);
        Assert.Equal(op, due[0]);
    }

    [Fact]
    public void Item_AckStopsRetransmission()
    {
        ReliableItemSender sender = new(retransmitIntervalMs: 250);
        ItemOpPacket op = sender.Enqueue(originPeerId: 1, ItemOpKind.Drop, Item(1), nowMs: 1_000);

        sender.Acknowledge(op.OpSequence);

        Assert.Equal(0, sender.PendingCount);
        Assert.Empty(sender.CollectDue(nowMs: 10_000));
    }

    [Fact]
    public void Item_AckForUnknownOpIsIgnored()
    {
        ReliableItemSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, ItemOpKind.Drop, Item(1), nowMs: 1_000);

        sender.Acknowledge(sequence: 999);

        Assert.Equal(1, sender.PendingCount);
        Assert.Single(sender.CollectDue(nowMs: 1_250));
    }

    [Fact]
    public void Item_LargeBurstIsDrainedAcrossTicksWithoutLoss()
    {
        ReliableItemSender sender = new(retransmitIntervalMs: 250);
        const int count = 500;

        HashSet<uint> enqueued = [];
        for (uint i = 0; i < count; i++)
        {
            ItemOpPacket op = sender.Enqueue(originPeerId: 1, ItemOpKind.Drop, Item(i), nowMs: 1_000);
            enqueued.Add(op.OpSequence);
        }

        List<ItemOpPacket> due = sender.CollectDue(nowMs: 1_250);
        Assert.Equal(count, due.Count);
        Assert.Equal(enqueued, due.Select(op => op.OpSequence).ToHashSet());
        Assert.Equal(count, sender.PendingCount);
    }

    [Fact]
    public void Item_ClearDropsAllPending()
    {
        ReliableItemSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, ItemOpKind.Drop, Item(1), nowMs: 1_000);

        sender.Clear();

        Assert.Equal(0, sender.PendingCount);
    }

    // ---- ReliableJournalSender -------------------------------------------------------------

    [Fact]
    public void Journal_FirstSendGoesOutImmediately()
    {
        ReliableJournalSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, questTag: "end_m01aa", state: 10, nowMs: 1_000);

        Assert.Empty(sender.CollectDue(nowMs: 1_000));
        Assert.Equal(1, sender.PendingCount);
    }

    [Fact]
    public void Journal_RetransmitsAfterIntervalNotBefore()
    {
        ReliableJournalSender sender = new(retransmitIntervalMs: 250);
        JournalOpPacket op = sender.Enqueue(originPeerId: 1, questTag: "end_m01aa", state: 10, nowMs: 1_000);

        Assert.Empty(sender.CollectDue(nowMs: 1_249));
        List<JournalOpPacket> due = sender.CollectDue(nowMs: 1_250);

        Assert.Single(due);
        Assert.Equal(op, due[0]);
    }

    [Fact]
    public void Journal_AckStopsRetransmission()
    {
        ReliableJournalSender sender = new(retransmitIntervalMs: 250);
        JournalOpPacket op = sender.Enqueue(originPeerId: 1, questTag: "end_m01aa", state: 10, nowMs: 1_000);

        sender.Acknowledge(op.OpSequence);

        Assert.Equal(0, sender.PendingCount);
        Assert.Empty(sender.CollectDue(nowMs: 10_000));
    }

    [Fact]
    public void Journal_AckForUnknownOpIsIgnored()
    {
        ReliableJournalSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, questTag: "end_m01aa", state: 10, nowMs: 1_000);

        sender.Acknowledge(sequence: 999);

        Assert.Equal(1, sender.PendingCount);
        Assert.Single(sender.CollectDue(nowMs: 1_250));
    }

    [Fact]
    public void Journal_LargeBurstIsDrainedAcrossTicksWithoutLoss()
    {
        ReliableJournalSender sender = new(retransmitIntervalMs: 250);
        const int count = 500;

        HashSet<uint> enqueued = [];
        for (int i = 0; i < count; i++)
        {
            JournalOpPacket op = sender.Enqueue(originPeerId: 1, questTag: $"quest_{i}", state: i, nowMs: 1_000);
            enqueued.Add(op.OpSequence);
        }

        List<JournalOpPacket> due = sender.CollectDue(nowMs: 1_250);
        Assert.Equal(count, due.Count);
        Assert.Equal(enqueued, due.Select(op => op.OpSequence).ToHashSet());
    }

    [Fact]
    public void Journal_ClearDropsAllPending()
    {
        ReliableJournalSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, questTag: "end_m01aa", state: 10, nowMs: 1_000);

        sender.Clear();

        Assert.Equal(0, sender.PendingCount);
    }

    // ---- ReliableLootSender (host -> clients broadcast, per-peer acks) --------------------

    [Fact]
    public void Loot_FirstSendGoesOutOnTheVeryNextTickNotOnEnqueue()
    {
        // Unlike the client->host senders, LootSender does not treat Enqueue itself as the
        // first transmission (LastSentMs = 0, Attempts = 0) - it relies on CollectDue to emit
        // it, which happens immediately because Attempts == 0 skips the interval wait.
        ReliableLootSender sender = new(retransmitIntervalMs: 250);
        ContainerLootPacket? op = sender.Enqueue(originPeerId: 1, containerId: 100, itemId: 200, nowMs: 1_000);

        Assert.NotNull(op);
        List<ContainerLootPacket> due = sender.CollectDue(recipients: [2, 3], nowMs: 1_000);

        Assert.Single(due);
        Assert.Equal(op, due[0]);
    }

    [Fact]
    public void Loot_RetransmitsAfterIntervalNotBefore()
    {
        ReliableLootSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, containerId: 100, itemId: 200, nowMs: 1_000);
        sender.CollectDue(recipients: [2, 3], nowMs: 1_000); // first send, Attempts -> 1

        Assert.Empty(sender.CollectDue(recipients: [2, 3], nowMs: 1_249));
        Assert.Single(sender.CollectDue(recipients: [2, 3], nowMs: 1_250));
    }

    [Fact]
    public void Loot_RetiresOnlyOnceEveryRecipientHasAcked()
    {
        // Retirement itself happens inside CollectDue (that is where the "finished" set is
        // computed and removed), so PendingCount only reflects an ack once CollectDue has run
        // since that ack - Acknowledge alone just records who confirmed.
        ReliableLootSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, containerId: 100, itemId: 200, nowMs: 1_000);
        sender.CollectDue(recipients: [2, 3], nowMs: 1_000);

        sender.Acknowledge(peerId: 2, sequence: 1);
        Assert.Single(sender.CollectDue(recipients: [2, 3], nowMs: 1_250)); // 3 still hasn't acked
        Assert.Equal(1, sender.PendingCount);

        sender.Acknowledge(peerId: 3, sequence: 1);
        Assert.Empty(sender.CollectDue(recipients: [2, 3], nowMs: 1_500));
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void Loot_AckForUnknownOpIsIgnored()
    {
        ReliableLootSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, containerId: 100, itemId: 200, nowMs: 1_000);
        sender.CollectDue(recipients: [2], nowMs: 1_000);

        sender.Acknowledge(peerId: 2, sequence: 999);

        Assert.Equal(1, sender.PendingCount);
        Assert.Single(sender.CollectDue(recipients: [2], nowMs: 1_250));
    }

    [Fact]
    public void Loot_DuplicateEnqueueForSameContainerItemIsSuppressed()
    {
        ReliableLootSender sender = new(retransmitIntervalMs: 250);
        ContainerLootPacket? first = sender.Enqueue(originPeerId: 1, containerId: 100, itemId: 200, nowMs: 1_000);
        ContainerLootPacket? second = sender.Enqueue(originPeerId: 1, containerId: 100, itemId: 200, nowMs: 1_100);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1, sender.PendingCount);
    }

    [Fact]
    public void Loot_PeerLeavingDropsItFromRequiredAcksAndCanRetireTheOp()
    {
        // No Remove/Forget API on the sender itself: CollectDue takes the CURRENT recipient
        // set every call, so a peer that disconnects simply stops being named - the host's
        // caller is what tracks membership (CoopHost recomputes `recipients` from its live
        // PeerConnection list every tick).
        ReliableLootSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, containerId: 100, itemId: 200, nowMs: 1_000);
        sender.CollectDue(recipients: [2, 3], nowMs: 1_000);

        sender.Acknowledge(peerId: 2, sequence: 1); // peer 3 never acks before leaving
        Assert.Equal(1, sender.PendingCount);

        // Peer 3 left; the caller now presents only peer 2 as a recipient.
        List<ContainerLootPacket> due = sender.CollectDue(recipients: [2], nowMs: 1_250);

        Assert.Empty(due); // already-acked-by-2, and 2 is now the entire recipient set
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void Loot_EmptyRecipientSetRetiresPendingOpsImmediately()
    {
        // Every peer left (or none ever joined): nothing to ack, so the op is retired rather
        // than retransmitted forever into a void.
        ReliableLootSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, containerId: 100, itemId: 200, nowMs: 1_000);
        sender.CollectDue(recipients: [2], nowMs: 1_000);

        List<ContainerLootPacket> due = sender.CollectDue(recipients: [], nowMs: 1_250);

        Assert.Empty(due);
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void Loot_LargeBurstIsDrainedAcrossTicksWithoutLoss()
    {
        ReliableLootSender sender = new(retransmitIntervalMs: 250);
        const int count = 500;
        uint[] recipients = [2, 3];

        HashSet<uint> enqueued = [];
        for (uint i = 0; i < count; i++)
        {
            ContainerLootPacket? op = sender.Enqueue(originPeerId: 1, containerId: i, itemId: i, nowMs: 1_000);
            Assert.NotNull(op);
            enqueued.Add(op!.OpSequence);
        }

        // Every op has Attempts == 0, so all of them go out on the very first CollectDue
        // regardless of interval.
        List<ContainerLootPacket> firstSend = sender.CollectDue(recipients, nowMs: 1_000);
        Assert.Equal(count, firstSend.Count);
        Assert.Equal(enqueued, firstSend.Select(op => op.OpSequence).ToHashSet());

        // Half the recipients ack fully, half only partially - both groups still retransmit
        // until every recipient in the set has confirmed.
        foreach (uint sequence in enqueued.Where(s => s % 2 == 0))
        {
            sender.Acknowledge(2, sequence);
            sender.Acknowledge(3, sequence);
        }

        foreach (uint sequence in enqueued.Where(s => s % 2 != 0))
        {
            sender.Acknowledge(2, sequence); // only one of two recipients
        }

        // Retirement of the fully-acked half happens inside this CollectDue call.
        List<ContainerLootPacket> secondSend = sender.CollectDue(recipients, nowMs: 1_250);
        Assert.Equal(count / 2, secondSend.Count);
        Assert.All(secondSend, op => Assert.True(op.OpSequence % 2 != 0));
        Assert.Equal(count / 2, sender.PendingCount);
    }

    [Fact]
    public void Loot_ClearDropsAllPending()
    {
        ReliableLootSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, containerId: 100, itemId: 200, nowMs: 1_000);

        sender.Clear();

        Assert.Equal(0, sender.PendingCount);
        Assert.Empty(sender.CollectDue(recipients: [2], nowMs: 10_000));
    }
}
