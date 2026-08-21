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
    public void Flag_NeverGivesUpOnAnUnackedOp()
    {
        // The sender's own header: an unacked op is never abandoned (a link drop long enough
        // to exhaust a fixed attempt cap used to lose the flag for good). Retries back off
        // but never stop — only an ack retires the op.
        ReliableFlagSender sender = new(retransmitIntervalMs: 100);
        sender.Enqueue(originPeerId: 1, flagId: 10, value: 1, nowMs: 0);

        int sent = 0;
        for (long now = 100; now <= 600_000; now += 100)
        {
            sent += sender.CollectDue(nowMs: now).Count;
        }

        Assert.True(sent >= 10, $"expected sustained retries, saw {sent}");
        Assert.Equal(1, sender.PendingCount);
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
        // on a later retransmit tick (the interval backs off after each attempt, so later
        // passes are sampled well past any backoff) — the property under test is that
        // nothing is silently dropped, not that it is spread out.
        List<FlagOpPacket> firstRetransmit = sender.CollectDue(nowMs: 1_250);
        Assert.Equal(count, firstRetransmit.Count);
        Assert.Equal(enqueued, firstRetransmit.Select(op => op.OpSequence).ToHashSet());

        List<FlagOpPacket> secondRetransmit = sender.CollectDue(nowMs: 600_000);
        Assert.Equal(count, secondRetransmit.Count);
        Assert.Equal(enqueued, secondRetransmit.Select(op => op.OpSequence).ToHashSet());

        // Acking half mid-stream removes exactly that half from the next tick, proving the
        // burst is tracked per-op rather than as an opaque batch.
        foreach (uint sequence in enqueued.Where(s => s % 2 == 0))
        {
            sender.Acknowledge(sequence);
        }

        Assert.Equal(count / 2, sender.PendingCount);
        List<FlagOpPacket> thirdRetransmit = sender.CollectDue(nowMs: 1_200_000);
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
    public void Loot_EmptyRecipientSetHoldsPendingOpsForWhoeverComesBack()
    {
        // The sender's own header: an empty recipient list is NOT "everyone has acked". A
        // client whose link dropped leaves the peer table after a few seconds; retiring the
        // unacked removals at that moment let the returning client loot the same chest twice.
        // The op must be held, unsent, and go out the moment somebody is there to receive it.
        ReliableLootSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(originPeerId: 1, containerId: 100, itemId: 200, nowMs: 1_000);
        sender.CollectDue(recipients: [2], nowMs: 1_000);

        List<ContainerLootPacket> whileAlone = sender.CollectDue(recipients: [], nowMs: 1_250);
        Assert.Empty(whileAlone);
        Assert.Equal(1, sender.PendingCount);

        List<ContainerLootPacket> onReturn = sender.CollectDue(recipients: [2], nowMs: 60_000);
        Assert.Single(onReturn);
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

    // ---- DialogueGrantSender (used both host->clients AND client->host, tuple-keyed) ------

    private static MaterialisedLootItem[] OneItem(string resref = "g_w_blstrpstl001") =>
        [new MaterialisedLootItem(resref, new ItemIdentity(1, 0, 0, 0, 0))];

    [Fact]
    public void Grant_FirstSendGoesOutOnTheVeryNextTickNotOnEnqueue()
    {
        DialogueGrantSender sender = new(retransmitIntervalMs: 250);
        DialogueGrantPacket? op = sender.EnqueueLocal(originPeerId: 1, "carth", OneItem(), nowMs: 1_000);

        Assert.NotNull(op);
        Assert.Equal(1u, op!.OriginPeerId);
        Assert.Equal(1u, op.OpSequence);

        List<DialogueGrantPacket> due = sender.CollectDue(recipients: [2, 3], nowMs: 1_000);
        Assert.Single(due);
        Assert.Equal(op, due[0]);
    }

    [Fact]
    public void Grant_EnqueueLocalWithNoItemsIsRefused()
    {
        DialogueGrantSender sender = new(retransmitIntervalMs: 250);
        Assert.Null(sender.EnqueueLocal(originPeerId: 1, "carth", [], nowMs: 1_000));
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void Grant_RetransmitsAfterIntervalNotBefore()
    {
        DialogueGrantSender sender = new(retransmitIntervalMs: 250);
        sender.EnqueueLocal(originPeerId: 1, "carth", OneItem(), nowMs: 1_000);
        sender.CollectDue(recipients: [2, 3], nowMs: 1_000);

        Assert.Empty(sender.CollectDue(recipients: [2, 3], nowMs: 1_249));
        Assert.Single(sender.CollectDue(recipients: [2, 3], nowMs: 1_250));
    }

    [Fact]
    public void Grant_RetiresOnlyOnceEveryRecipientHasAcked()
    {
        DialogueGrantSender sender = new(retransmitIntervalMs: 250);
        sender.EnqueueLocal(originPeerId: 1, "carth", OneItem(), nowMs: 1_000);
        sender.CollectDue(recipients: [2, 3], nowMs: 1_000);

        sender.Acknowledge(origin: 1, sequence: 1, ackingPeerId: 2);
        Assert.Single(sender.CollectDue(recipients: [2, 3], nowMs: 1_250)); // 3 still hasn't acked
        Assert.Equal(1, sender.PendingCount);

        sender.Acknowledge(origin: 1, sequence: 1, ackingPeerId: 3);
        Assert.Empty(sender.CollectDue(recipients: [2, 3], nowMs: 1_500));
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void Grant_AckForUnknownOriginOrSequenceIsIgnored()
    {
        DialogueGrantSender sender = new(retransmitIntervalMs: 250);
        sender.EnqueueLocal(originPeerId: 1, "carth", OneItem(), nowMs: 1_000);
        sender.CollectDue(recipients: [2], nowMs: 1_000);

        sender.Acknowledge(origin: 1, sequence: 999, ackingPeerId: 2); // wrong sequence
        sender.Acknowledge(origin: 99, sequence: 1, ackingPeerId: 2); // wrong origin

        Assert.Equal(1, sender.PendingCount);
        Assert.Single(sender.CollectDue(recipients: [2], nowMs: 1_250));
    }

    /// <summary>Two different origins independently numbering their own ops from 1 must not
    /// collide — the whole reason the pending dict is keyed on (origin, sequence) rather than
    /// a bare sequence, unlike the single-origin <see cref="MaterialisedLootSender"/>.</summary>
    [Fact]
    public void Grant_TwoOriginsReusingTheSameSequenceNumberDoNotCollide()
    {
        DialogueGrantSender sender = new(retransmitIntervalMs: 250);
        sender.EnqueueLocal(originPeerId: 1, "carth", OneItem("a"), nowMs: 1_000); // host, sequence 1
        DialogueGrantPacket relayed = new(2, 1, "bastila", OneItem("b")); // client 2, ALSO sequence 1
        sender.EnqueueRelay(relayed, nowMs: 1_000);

        Assert.Equal(2, sender.PendingCount);

        sender.Acknowledge(origin: 1, sequence: 1, ackingPeerId: 5);
        List<DialogueGrantPacket> due = sender.CollectDue(recipients: [5], nowMs: 1_250);

        // Only the client's op (origin 2) is still owed a resend; the host's own (origin 1) is
        // fully acked and retired.
        Assert.Single(due);
        Assert.Equal(2u, due[0].OriginPeerId);
        Assert.Equal(1, sender.PendingCount);
    }

    [Fact]
    public void Grant_RelayPreservesTheReportingPeersTrueOriginAndSequence()
    {
        DialogueGrantSender sender = new(retransmitIntervalMs: 250);
        DialogueGrantPacket fromClient = new(OriginPeerId: 7, OpSequence: 3, "carth", OneItem());

        Assert.True(sender.EnqueueRelay(fromClient, nowMs: 1_000));

        List<DialogueGrantPacket> due = sender.CollectDue(recipients: [1, 2], nowMs: 1_000);
        Assert.Single(due);
        Assert.Equal(7u, due[0].OriginPeerId);
        Assert.Equal(3u, due[0].OpSequence);
    }

    /// <summary>A client's own retransmit of a report it already sent must not be relayed (and
    /// therefore given) twice, even after the FIRST relay has been fully acked and retired —
    /// unlike container loot, giving an item is not idempotent.</summary>
    [Fact]
    public void Grant_RelayOfAnAlreadyQueuedOriginSequenceIsRefused()
    {
        DialogueGrantSender sender = new(retransmitIntervalMs: 250);
        DialogueGrantPacket fromClient = new(OriginPeerId: 7, OpSequence: 3, "carth", OneItem());

        Assert.True(sender.EnqueueRelay(fromClient, nowMs: 1_000));
        sender.CollectDue(recipients: [1], nowMs: 1_000);
        sender.Acknowledge(origin: 7, sequence: 3, ackingPeerId: 1);
        sender.CollectDue(recipients: [1], nowMs: 1_000); // retires it, PendingCount back to 0

        Assert.Equal(0, sender.PendingCount);
        Assert.False(sender.EnqueueRelay(fromClient, nowMs: 2_000)); // the client's retransmit
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void Grant_EmptyRecipientSetHoldsPendingOpsForWhoeverComesBack()
    {
        DialogueGrantSender sender = new(retransmitIntervalMs: 250);
        sender.EnqueueLocal(originPeerId: 1, "carth", OneItem(), nowMs: 1_000);
        sender.CollectDue(recipients: [2], nowMs: 1_000);

        Assert.Empty(sender.CollectDue(recipients: [], nowMs: 1_250));
        Assert.Equal(1, sender.PendingCount);

        Assert.Single(sender.CollectDue(recipients: [2], nowMs: 60_000));
    }

    [Fact]
    public void Grant_ClearDropsAllPending()
    {
        DialogueGrantSender sender = new(retransmitIntervalMs: 250);
        sender.EnqueueLocal(originPeerId: 1, "carth", OneItem(), nowMs: 1_000);

        sender.Clear();

        Assert.Equal(0, sender.PendingCount);
        Assert.Empty(sender.CollectDue(recipients: [2], nowMs: 10_000));
    }
}
