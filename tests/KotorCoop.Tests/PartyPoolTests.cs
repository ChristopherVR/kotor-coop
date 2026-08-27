using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class PartyPoolTests
{
    private static readonly PartyPoolStackKey Blaster =
        new("g_w_blstrpstl001", 0, 0, 0, ItemIdentity.FlagKnown);

    private static readonly PartyPoolStackKey UpgradedSaber =
        new("g_w_dblsbr001", 0, 0, 0x5, (byte)(ItemIdentity.FlagKnown | ItemIdentity.FlagIdentified));

    // ---- store ------------------------------------------------------------------

    [Fact]
    public void AddAccumulatesQuantity()
    {
        PartyPoolStore store = new();

        Assert.True(store.Record(PartyPoolOpKind.Add, Blaster, 5));
        Assert.Equal(5, store.Get(Blaster));

        Assert.True(store.Record(PartyPoolOpKind.Add, Blaster, 3));
        Assert.Equal(8, store.Get(Blaster));
    }

    [Fact]
    public void RemoveFloorsAtZeroAndDropsTheKey()
    {
        PartyPoolStore store = new();
        store.Record(PartyPoolOpKind.Add, Blaster, 5);

        Assert.True(store.Record(PartyPoolOpKind.Remove, Blaster, 3));
        Assert.Equal(2, store.Get(Blaster));

        // Removing more than exists floors at zero rather than going negative.
        Assert.True(store.Record(PartyPoolOpKind.Remove, Blaster, 100));
        Assert.Equal(0, store.Get(Blaster));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void SetIsAbsoluteNotAdditive()
    {
        PartyPoolStore store = new();
        store.Record(PartyPoolOpKind.Add, Blaster, 5);

        // A resync Set to a lower value than the store already holds must REPLACE, not
        // subtract from, the existing total — the whole reason Set exists (see
        // PartyPoolOpKind.Set's own doc: a peer being resynced may already hold a different
        // nonzero total, and Add semantics would double-count it).
        Assert.True(store.Record(PartyPoolOpKind.Set, Blaster, 2));
        Assert.Equal(2, store.Get(Blaster));

        // Re-setting to the SAME value is a genuine no-op (no version bump).
        uint versionBefore = store.Version;
        Assert.False(store.Record(PartyPoolOpKind.Set, Blaster, 2));
        Assert.Equal(versionBefore, store.Version);
    }

    [Fact]
    public void DistinctStackIdentitiesOfTheSameResrefNeverMerge()
    {
        // A plain blaster pistol and one with different charges must never merge into one
        // total — that is the entire point of keying on identity, not just resref (a "stack
        // change" test: the two are the SAME resref but different stack identities).
        PartyPoolStore store = new();
        PartyPoolStackKey damaged = Blaster with { Charges = 10 };

        store.Record(PartyPoolOpKind.Add, Blaster, 5);
        store.Record(PartyPoolOpKind.Add, damaged, 2);

        Assert.Equal(5, store.Get(Blaster));
        Assert.Equal(2, store.Get(damaged));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void RetransmittedOpAppliesExactlyOnce()
    {
        PartyPoolStore store = new();
        PartyPoolOpPacket op = new(1, 5, PartyPoolOpKind.Add, Blaster, 5);

        Assert.True(store.ApplyOp(op));
        Assert.False(store.ApplyOp(op));
        Assert.Equal(5, store.Get(Blaster));
    }

    [Fact]
    public void ApplyOpIsAdditiveAcrossDistinctSequences()
    {
        // Unlike ContainerLootStore, two DIFFERENT ops for the same key are two real,
        // cumulative events — this is the property that makes an aggregate-count channel
        // different from a set-of-removed-pairs one.
        PartyPoolStore store = new();

        Assert.True(store.ApplyOp(new PartyPoolOpPacket(1, 1, PartyPoolOpKind.Add, Blaster, 5)));
        Assert.True(store.ApplyOp(new PartyPoolOpPacket(1, 2, PartyPoolOpKind.Add, Blaster, 3)));

        Assert.Equal(8, store.Get(Blaster));
    }

    [Fact]
    public void ANewKeyIsRefusedPastTheCapButAnExistingKeyStillUpdates()
    {
        PartyPoolStore store = new();

        for (int i = 0; i < ProtocolConstants.MaxPartyPoolStacks; i++)
        {
            PartyPoolStackKey key = Blaster with { Charges = (byte)i };
            Assert.True(store.Record(PartyPoolOpKind.Add, key, 1));
        }

        Assert.Equal(ProtocolConstants.MaxPartyPoolStacks, store.Count);

        // One more brand-new key is refused. Distinguished by Upgrades (not Charges, which the
        // loop above already exhausted every byte value of).
        PartyPoolStackKey overflow = Blaster with { Upgrades = 1 };
        Assert.False(store.Record(PartyPoolOpKind.Add, overflow, 1));
        Assert.Equal(ProtocolConstants.MaxPartyPoolStacks, store.Count);

        // An EXISTING key can still change quantity, including shrinking to zero and freeing
        // a slot for the next new key.
        PartyPoolStackKey existing = Blaster with { Charges = 0 };
        Assert.True(store.Record(PartyPoolOpKind.Remove, existing, 1));
        Assert.Equal(ProtocolConstants.MaxPartyPoolStacks - 1, store.Count);
        Assert.True(store.Record(PartyPoolOpKind.Add, overflow, 1));
    }

    [Fact]
    public void DigestIsInvariantToInsertionOrder()
    {
        PartyPoolStore forward = new();
        forward.Record(PartyPoolOpKind.Add, Blaster, 5);
        forward.Record(PartyPoolOpKind.Add, UpgradedSaber, 1);

        PartyPoolStore backward = new();
        backward.Record(PartyPoolOpKind.Add, UpgradedSaber, 1);
        backward.Record(PartyPoolOpKind.Add, Blaster, 5);

        Assert.Equal(forward.Version, backward.Version);
        Assert.Equal(forward.Digest, backward.Digest);
    }

    [Fact]
    public void ClearResetsEverything()
    {
        PartyPoolStore store = new();
        store.Record(PartyPoolOpKind.Add, Blaster, 5);

        store.Clear();

        Assert.Equal(0u, store.Version);
        Assert.Equal(EntryDigest.Empty, store.Digest);
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.Get(Blaster));
    }

    // ---- sender -------------------------------------------------------------------

    [Fact]
    public void EnqueueMintsAnOpAndCollectDueEmitsIt()
    {
        ReliablePartyPoolSender sender = new();
        PartyPoolOpPacket? op = sender.Enqueue(CoopHost.HostPeerId, PartyPoolOpKind.Add, Blaster, 5, 0);

        Assert.NotNull(op);
        Assert.Equal(Blaster, op.Key);
        Assert.Equal(5, op.CountDelta);

        List<PartyPoolOpPacket> due = sender.CollectDue([2u], 0);
        Assert.Single(due);
        Assert.Equal(op, due[0]);
    }

    /// <summary>
    /// ⚠ THE OPPOSITE OF ReliableLootSender's "duplicate enqueue is refused" test. Two adds of
    /// the SAME key are two DIFFERENT, cumulative facts (deposit five, then three more) — the
    /// sender must never collapse them, or one of the two quantities is silently lost.
    /// </summary>
    [Fact]
    public void TwoOpsForTheSameKeyAreBothQueuedIndependently()
    {
        ReliablePartyPoolSender sender = new();

        Assert.NotNull(sender.Enqueue(CoopHost.HostPeerId, PartyPoolOpKind.Add, Blaster, 5, 0));
        Assert.NotNull(sender.Enqueue(CoopHost.HostPeerId, PartyPoolOpKind.Add, Blaster, 3, 0));

        Assert.Equal(2, sender.PendingCount);
    }

    [Fact]
    public void OpIsRetiredOnceEveryRecipientHasAcked()
    {
        ReliablePartyPoolSender sender = new();
        PartyPoolOpPacket op = sender.Enqueue(CoopHost.HostPeerId, PartyPoolOpKind.Add, Blaster, 5, 0)!;

        uint[] recipients = [2u, 3u];
        Assert.Single(sender.CollectDue(recipients, 0));

        sender.Acknowledge(2, op.OpSequence);
        Assert.Equal(1, sender.PendingCount);

        sender.Acknowledge(3, op.OpSequence);
        Assert.Empty(sender.CollectDue(recipients, 1000));
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void AnOpWithNoRecipientsWaitsForOneInsteadOfBeingDropped()
    {
        ReliablePartyPoolSender sender = new();
        sender.Enqueue(CoopHost.HostPeerId, PartyPoolOpKind.Add, Blaster, 5, 0);

        Assert.Empty(sender.CollectDue([], 0));
        Assert.Empty(sender.CollectDue([], 10_000));
        Assert.Equal(1, sender.PendingCount);

        Assert.Single(sender.CollectDue([2u], 10_000));
    }

    [Fact]
    public void TheSenderIsBoundedRatherThanUnlimited()
    {
        ReliablePartyPoolSender sender = new(maxPending: 3);

        for (byte i = 0; i < 20; i++)
        {
            sender.Enqueue(CoopHost.HostPeerId, PartyPoolOpKind.Add, Blaster with { Charges = i }, 1, 0);
        }

        Assert.Equal(3, sender.PendingCount);
    }

    [Fact]
    public void RetransmitWaitsForTheInterval()
    {
        ReliablePartyPoolSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(CoopHost.HostPeerId, PartyPoolOpKind.Add, Blaster, 5, 0);

        uint[] recipients = [2u];
        Assert.Single(sender.CollectDue(recipients, 0));
        Assert.Empty(sender.CollectDue(recipients, 100));
        Assert.Single(sender.CollectDue(recipients, 300));
    }

    // ---- diff rule (the pure add/remove/echo decision) -----------------------------

    [Fact]
    public void NoChangeProducesNoDeltas()
    {
        Dictionary<PartyPoolStackKey, int> same = new() { [Blaster] = 5 };
        Assert.Empty(PartyPoolDiffRule.Diff(same, new Dictionary<PartyPoolStackKey, int>(same)));
    }

    [Fact]
    public void ANewKeyProducesAnAddForItsFullQuantity()
    {
        Dictionary<PartyPoolStackKey, int> expected = [];
        Dictionary<PartyPoolStackKey, int> actual = new() { [Blaster] = 5 };

        List<PartyPoolDiffRule.Delta> deltas = PartyPoolDiffRule.Diff(expected, actual);

        Assert.Equal(
            new PartyPoolDiffRule.Delta(Blaster, PartyPoolOpKind.Add, 5), Assert.Single(deltas));
    }

    [Fact]
    public void AVanishedKeyProducesARemoveForItsFullQuantity()
    {
        Dictionary<PartyPoolStackKey, int> expected = new() { [Blaster] = 5 };
        Dictionary<PartyPoolStackKey, int> actual = [];

        List<PartyPoolDiffRule.Delta> deltas = PartyPoolDiffRule.Diff(expected, actual);

        Assert.Equal(
            new PartyPoolDiffRule.Delta(Blaster, PartyPoolOpKind.Remove, 5), Assert.Single(deltas));
    }

    /// <summary>A stack whose quantity CHANGED, without disappearing outright — e.g. the same
    /// item picked up 3 more of, or 2 taken out of a partially-full stack.</summary>
    [Fact]
    public void AStackSizeIncreaseProducesAnAddForOnlyTheDelta()
    {
        Dictionary<PartyPoolStackKey, int> expected = new() { [Blaster] = 5 };
        Dictionary<PartyPoolStackKey, int> actual = new() { [Blaster] = 8 };

        List<PartyPoolDiffRule.Delta> deltas = PartyPoolDiffRule.Diff(expected, actual);

        Assert.Equal(
            new PartyPoolDiffRule.Delta(Blaster, PartyPoolOpKind.Add, 3), Assert.Single(deltas));
    }

    [Fact]
    public void AStackSizeDecreaseProducesARemoveForOnlyTheDelta()
    {
        Dictionary<PartyPoolStackKey, int> expected = new() { [Blaster] = 8 };
        Dictionary<PartyPoolStackKey, int> actual = new() { [Blaster] = 5 };

        List<PartyPoolDiffRule.Delta> deltas = PartyPoolDiffRule.Diff(expected, actual);

        Assert.Equal(
            new PartyPoolDiffRule.Delta(Blaster, PartyPoolOpKind.Remove, 3), Assert.Single(deltas));
    }

    /// <summary>
    /// The echo-suppression property this whole channel depends on: feeding the diff rule the
    /// SAME map for both sides (which is exactly what <c>PartyPoolPump</c> does for a key it
    /// has already accounted for via <c>_pendingApplyTarget</c>, or once a fresh scan catches
    /// up to the last-known baseline) must never produce a delta — otherwise the pump's own
    /// applied change would be rediscovered and re-published forever.
    /// </summary>
    [Fact]
    public void EchoSuppressionFeedingTheSameMapTwiceProducesNothing()
    {
        Dictionary<PartyPoolStackKey, int> totals = new() { [Blaster] = 5, [UpgradedSaber] = 1 };
        Assert.Empty(PartyPoolDiffRule.Diff(totals, new Dictionary<PartyPoolStackKey, int>(totals)));
    }

    [Fact]
    public void MultipleIndependentKeysEachProduceTheirOwnDelta()
    {
        Dictionary<PartyPoolStackKey, int> expected = new() { [Blaster] = 5 };
        Dictionary<PartyPoolStackKey, int> actual = new() { [Blaster] = 5, [UpgradedSaber] = 1 };

        List<PartyPoolDiffRule.Delta> deltas = PartyPoolDiffRule.Diff(expected, actual);

        Assert.Equal(
            new PartyPoolDiffRule.Delta(UpgradedSaber, PartyPoolOpKind.Add, 1), Assert.Single(deltas));
    }
}
