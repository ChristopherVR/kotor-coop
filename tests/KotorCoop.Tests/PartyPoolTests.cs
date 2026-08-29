using KotorCoop.Plugin;
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

    // ---- stack matcher (RemovePartyPoolStack's resref-vs-Tag precedence, docs/research/
    // party-pool-sync.md §13/§14) -----------------------------------------------------------

    /// <summary>The ring remembers this item's own spawn and confirms it — the ordinary case for
    /// a stack removed moments after it was deposited.</summary>
    [Fact]
    public void SpawnRingMatchIsTrusted()
    {
        Assert.True(PartyPoolStackMatcher.Matches("g_i_progspike01", "g_i_progspike01", tagMatches: false));
    }

    /// <summary>
    /// The exact live symptom (docs/research/party-pool-sync.md §13.2): the ring says this item's
    /// resref does NOT match, even though the item's live Tag happens to. The ring's answer wins —
    /// it is the authoritative signal once it has one, precisely because a just-inserted item's
    /// Tag is the field under suspicion, not to be trusted as a tie-breaker against it.
    /// </summary>
    [Fact]
    public void SpawnRingDisagreementOverridesATagMatch()
    {
        Assert.False(PartyPoolStackMatcher.Matches("g_w_blstrpstl001", "g_i_progspike01", tagMatches: true));
    }

    /// <summary>Case-insensitive, matching this codebase's own convention for every other resref
    /// comparison (<c>TagEqualsIgnoreCase</c>).</summary>
    [Fact]
    public void SpawnRingComparisonIsCaseInsensitive()
    {
        Assert.True(PartyPoolStackMatcher.Matches("G_I_PROGSPIKE01", "g_i_progspike01", tagMatches: false));
    }

    /// <summary>No ring entry for this item's id at all — evicted, a module change cleared it, or
    /// it predates this plugin session. Tag is the only signal left, exactly RemovePartyPoolStack's
    /// pre-fix behaviour for every candidate.</summary>
    [Fact]
    public void NoRingEntryFallsBackToTag()
    {
        Assert.True(PartyPoolStackMatcher.Matches(null, "g_i_progspike01", tagMatches: true));
        Assert.False(PartyPoolStackMatcher.Matches(null, "g_i_progspike01", tagMatches: false));
    }

    // ---- identity matcher (RemovePartyPoolStack's charge clamp + wildcard rule, docs/research/
    // party-pool-sync.md §20) -----------------------------------------------------------------

    /// <summary>The ordinary case: every field named, every field must match exactly.</summary>
    [Fact]
    public void AnExactIdentityMatchSucceeds()
    {
        Assert.True(PartyPoolIdentityMatcher.Matches(
            itemCharges: 50, itemMaxCharges: 50, itemUpgrades: 0, itemWireFlags: 1,
            requestedCharges: 50, requestedMaxCharges: 50, requestedUpgrades: 0, requestedWireFlags: 1));
    }

    /// <summary>
    /// ⚠ The flags row demands STOLEN (bit 2, wire) against an item that is not stolen, rather
    /// than the bare "1 vs 0" it used to. Since docs/research/shared-inventory-v2.md §14 the
    /// matcher narrows both sides to <see cref="PartyPoolIdentityMatcher.StackIdentityFlags"/> —
    /// Stolen alone, the ONE bit the engine's own merge predicate at 0x00553CF0 compares — so a
    /// difference in any OTHER flag bit is deliberately no longer a refusal, and a row asserting
    /// that it is would be asserting the defect §13.2 recorded. Stolen still refuses, which is
    /// what this row now pins.
    /// </summary>
    [Theory]
    [InlineData(49u, 50u, 0u, 1u)]
    [InlineData(50u, 49u, 0u, 1u)]
    [InlineData(50u, 50u, 1u, 1u)]
    [InlineData(50u, 50u, 0u, 4u)]
    public void ASingleMismatchedFieldRefusesTheWholeMatch(
        uint requestedCharges, uint requestedMaxCharges, uint requestedUpgrades, uint requestedWireFlags)
    {
        Assert.False(PartyPoolIdentityMatcher.Matches(
            itemCharges: 50, itemMaxCharges: 50, itemUpgrades: 0, itemWireFlags: 1,
            requestedCharges, requestedMaxCharges, requestedUpgrades, requestedWireFlags));
    }

    /// <summary>
    /// The exact fix for docs/research/party-pool-sync.md §19.2's own live "identity-key
    /// mismatch": a bare <c>poolremove &lt;resref&gt;</c> has no charges/upgrades/flags to give,
    /// so every one of those fields arrives as the wildcard — and must match ANY live value,
    /// including a nonzero one an ambient script's own deposit actually carries.
    /// </summary>
    [Fact]
    public void EveryFieldWildcardedMatchesAnyLiveValue()
    {
        Assert.True(PartyPoolIdentityMatcher.Matches(
            itemCharges: 50, itemMaxCharges: 50, itemUpgrades: 7, itemWireFlags: 1,
            requestedCharges: PartyPoolIdentityMatcher.Wildcard,
            requestedMaxCharges: PartyPoolIdentityMatcher.Wildcard,
            requestedUpgrades: PartyPoolIdentityMatcher.Wildcard,
            requestedWireFlags: PartyPoolIdentityMatcher.Wildcard));
    }

    /// <summary>A wildcard on SOME fields still enforces the ones actually named — partial
    /// specificity is not "match anything."</summary>
    [Fact]
    public void AMixOfWildcardAndConcreteFieldsOnlyEnforcesTheConcreteOnes()
    {
        Assert.True(PartyPoolIdentityMatcher.Matches(
            itemCharges: 50, itemMaxCharges: 50, itemUpgrades: 7, itemWireFlags: 1,
            requestedCharges: 50, requestedMaxCharges: PartyPoolIdentityMatcher.Wildcard,
            requestedUpgrades: PartyPoolIdentityMatcher.Wildcard, requestedWireFlags: 1));

        Assert.False(PartyPoolIdentityMatcher.Matches(
            itemCharges: 50, itemMaxCharges: 50, itemUpgrades: 7, itemWireFlags: 1,
            requestedCharges: 49, requestedMaxCharges: PartyPoolIdentityMatcher.Wildcard,
            requestedUpgrades: PartyPoolIdentityMatcher.Wildcard, requestedWireFlags: 1));
    }

    /// <summary>
    /// docs/research/item-identity.md §1.1's own measured example: an item whose template never
    /// fully loaded reads "charges 50/1768092" — MaxCharges above the 0..255 GFF-byte range is
    /// uninitialised memory, never a real value, and falls back to Charges (the engine's own
    /// documented default when a .uti declares no MaxCharges field at all).
    /// </summary>
    [Fact]
    public void SafeChargeFieldsClampsAnOutOfRangeMaxChargesDownToCharges()
    {
        (uint charges, uint maxCharges) = PartyPoolIdentityMatcher.SafeChargeFields(50, 1768092);

        Assert.Equal(50u, charges);
        Assert.Equal(50u, maxCharges);
    }

    /// <summary>An out-of-range CHARGES value (not just MaxCharges) falls back to 0 — there is
    /// no other field left to default to.</summary>
    [Fact]
    public void SafeChargeFieldsClampsAnOutOfRangeChargesToZero()
    {
        (uint charges, uint maxCharges) = PartyPoolIdentityMatcher.SafeChargeFields(999999, 40);

        Assert.Equal(0u, charges);
        Assert.Equal(40u, maxCharges);
    }

    /// <summary>Both fields already in range pass through unchanged — the clamp never touches a
    /// genuinely valid pair.</summary>
    [Fact]
    public void SafeChargeFieldsLeavesInRangeValuesAlone()
    {
        (uint charges, uint maxCharges) = PartyPoolIdentityMatcher.SafeChargeFields(10, 50);

        Assert.Equal(10u, charges);
        Assert.Equal(50u, maxCharges);
    }

    /// <summary>
    /// The whole point of clamping the SAME way on both ends: a remove request built from a
    /// scan's own (already-clamped) key must reproduce a match against the SAME live item, even
    /// though a bare re-read of that item's raw MaxCharges would still show the garbage value —
    /// the asymmetry docs/research/party-pool-sync.md §20 identifies as the root cause of
    /// §19.2's own "matcher can't reproduce on remove" gap.
    /// </summary>
    [Fact]
    public void AKeyDerivedFromAClampedScanMatchesTheSameLiveItemsRawGarbageMaxCharges()
    {
        // What a scan would have reported for this item (KotorCoop.Memory.ItemIdentityLayout.
        // Decode's own clamp, mirrored here by SafeChargeFields).
        (uint scannedCharges, uint scannedMaxCharges) = PartyPoolIdentityMatcher.SafeChargeFields(50, 1768092);

        // The SAME live item, re-read later for the remove — MaxCharges still reads raw garbage.
        (uint itemCharges, uint itemMaxCharges) = PartyPoolIdentityMatcher.SafeChargeFields(50, 1768092);

        Assert.True(PartyPoolIdentityMatcher.Matches(
            itemCharges, itemMaxCharges, itemUpgrades: 0, itemWireFlags: 1,
            scannedCharges, scannedMaxCharges, requestedUpgrades: 0, requestedWireFlags: 1));
    }
}
