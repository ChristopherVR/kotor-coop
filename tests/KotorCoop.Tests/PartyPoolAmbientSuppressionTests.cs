using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins <see cref="KotorGameBridge.IsAmbientPartyPoolNoise"/>'s decision rule — the ambient-
/// script-noise suppression docs/research/party-pool-sync.md §20 added, so a vanilla k_*
/// heartbeat/plot script's own <c>CreateItemOnObject(..., GetFirstPC())</c> deposit into the
/// party pool is not counted as sync-relevant content (and therefore never republished as a
/// player action, and never double-counted once the peer's own independently-created copy is
/// merged in).
///
/// Seeds <see cref="KotorGameBridge.RememberItemCreation"/> directly (internal, granted to this
/// assembly via <c>InternalsVisibleTo</c> — the identical seam
/// <c>ItemTemplateSpawnCachePrecedenceTests</c> already uses for the sibling resref cache) rather
/// than through a real plugin block: nothing here needs a game process, and the decision rule is
/// pure once the creation fact is known. <c>nowMs</c> is threaded through
/// <see cref="KotorGameBridge.IsAmbientPartyPoolNoise"/> explicitly for exactly this reason — a
/// deterministic clock, never a real timer.
/// </summary>
public class PartyPoolAmbientSuppressionTests : IDisposable
{
    private readonly ProcessMemory _memory = ProcessMemory.Attach(Environment.ProcessId);
    private readonly KotorGameBridge _bridge;

    public PartyPoolAmbientSuppressionTests()
    {
        _bridge = new KotorGameBridge(_memory, ownsMemory: false);
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _memory.Dispose();
    }

    /// <summary>No creation fact at all — an item this session never watched get created (it
    /// predates this plugin session, or the plugin is not attached). Never suppressed: the
    /// pre-§20 behaviour for everything the ring cannot speak to.</summary>
    [Fact]
    public void AnItemWithNoCreationFactIsNeverSuppressed()
    {
        Assert.False(_bridge.IsAmbientPartyPoolNoise(0x400, nowMs: 10_000));
    }

    /// <summary>The exact pattern this section exists for: created moments ago, NOT via our own
    /// code — a vanilla script's own heartbeat/plot deposit.</summary>
    [Fact]
    public void AFreshNonCheatCreationIsSuppressed()
    {
        _bridge.RememberItemCreation(0x401, isCheat: false, nowMs: 10_000);

        Assert.True(_bridge.IsAmbientPartyPoolNoise(0x401, nowMs: 10_300));
    }

    /// <summary>The exact case this section must NOT break: our own <c>pooladd</c>/wire-apply
    /// deposit, created just as recently as an ambient script's would be — the ONLY signal that
    /// tells the two apart is the cheat flag, since both routes funnel through the identical VM
    /// command (ItemTemplateSpawnHook.NextSpawnIsCheat's own doc comment).</summary>
    [Fact]
    public void AFreshCheatCreationIsNeverSuppressed()
    {
        _bridge.RememberItemCreation(0x402, isCheat: true, nowMs: 10_000);

        Assert.False(_bridge.IsAmbientPartyPoolNoise(0x402, nowMs: 10_300));
    }

    /// <summary>An item that existed well before this scan first noticed it — created, then
    /// manually dragged into the pool via the GUI much later. Never a script's own immediate
    /// deposit pattern, so never suppressed.</summary>
    [Fact]
    public void ANonCheatCreationOutsideTheWindowIsNotSuppressed()
    {
        _bridge.NotePartyPoolWorldSwept();
        _bridge.RememberItemCreation(0x403, isCheat: false, nowMs: 10_000);

        Assert.False(_bridge.IsAmbientPartyPoolNoise(0x403, nowMs: 12_000));
    }

    /// <summary>
    /// The 2026-09-02 load-time case (docs/research/shared-inventory-v2.md §17): END_M01AA's
    /// own ambient scripting pooled a random consumable at world-up, the ring recorded it as
    /// non-cheat, and the world's FIRST sweep — held back by the load ack, the player resolve
    /// and the settle interval — met the id many seconds later, outside the window. Nothing but
    /// a script can create and pool an item before a world's first sweep, so while the world is
    /// opening the creation fact alone is the answer.
    /// </summary>
    [Fact]
    public void WhileTheWorldIsOpeningANonCheatCreationIsSuppressedHoweverOldItIs()
    {
        Assert.True(_bridge.PartyPoolWorldOpening);
        _bridge.RememberItemCreation(0x409, isCheat: false, nowMs: 10_000);

        Assert.True(_bridge.IsAmbientPartyPoolNoise(0x409, nowMs: 25_000));
    }

    /// <summary>Our own reconcile applies land during the opening too (the pump's first sweep
    /// after a rejoin) — the cheat flag still wins, opening or not.</summary>
    [Fact]
    public void WhileTheWorldIsOpeningACheatCreationIsStillCounted()
    {
        _bridge.RememberItemCreation(0x40A, isCheat: true, nowMs: 10_000);

        Assert.False(_bridge.IsAmbientPartyPoolNoise(0x40A, nowMs: 25_000));
    }

    /// <summary>Once the first full sweep has run, the ordinary window is back in force for
    /// anything that appears later — the "much older item dragged in by hand" case.</summary>
    [Fact]
    public void OnceTheWorldHasBeenSweptTheWindowAppliesAgain()
    {
        _bridge.NotePartyPoolWorldSwept();
        Assert.False(_bridge.PartyPoolWorldOpening);

        _bridge.RememberItemCreation(0x40B, isCheat: false, nowMs: 10_000);
        Assert.False(_bridge.IsAmbientPartyPoolNoise(0x40B, nowMs: 25_000));

        _bridge.RememberItemCreation(0x40C, isCheat: false, nowMs: 30_000);
        Assert.True(_bridge.IsAmbientPartyPoolNoise(0x40C, nowMs: 30_500));
    }

    /// <summary>
    /// The other half of the same live round: the world reset that runs when the NEW world's
    /// player resolves used to clear every creation fact — including the ones the drain had
    /// already recorded for the new world's own ambient deposits. Facts tagged with the world
    /// being entered survive; the old world's are dropped (its ids are reused).
    /// </summary>
    [Fact]
    public void AWorldResetKeepsTheCreationFactsOfTheWorldBeingEntered()
    {
        _bridge.RememberItemCreation(0x410, isCheat: false, nowMs: 10_000, worldSerial: 5);
        _bridge.RememberItemCreation(0x411, isCheat: false, nowMs: 20_000, worldSerial: 6);
        _bridge.RememberItemCreation(0x412, isCheat: true, nowMs: 20_000, worldSerial: 6);

        _bridge.ForgetItemCreationsFromOtherWorlds(keepWorldSerial: 6);

        Assert.False(_bridge.TryGetItemCreationObservation(0x410, out _, out _));
        Assert.True(_bridge.TryGetItemCreationObservation(0x411, out bool cheat411, out long at411));
        Assert.False(cheat411);
        Assert.Equal(20_000, at411);
        Assert.True(_bridge.TryGetItemCreationObservation(0x412, out bool cheat412, out _));
        Assert.True(cheat412);
    }

    /// <summary>A kept fact is still a first-class entry afterwards: a repeat report overwrites
    /// it and it still ages out of the FIFO normally (no orphaned order entry, no ghost).</summary>
    [Fact]
    public void AKeptCreationFactStillOverwritesAndForgetsNormally()
    {
        _bridge.RememberItemCreation(0x420, isCheat: false, nowMs: 20_000, worldSerial: 6);
        _bridge.ForgetItemCreationsFromOtherWorlds(keepWorldSerial: 6);

        _bridge.RememberItemCreation(0x420, isCheat: true, nowMs: 21_000, worldSerial: 6);
        Assert.True(_bridge.TryGetItemCreationObservation(0x420, out bool cheat, out long at));
        Assert.True(cheat);
        Assert.Equal(21_000, at);

        _bridge.ForgetItemCreationsFromOtherWorlds(keepWorldSerial: 7);
        Assert.False(_bridge.TryGetItemCreationObservation(0x420, out _, out _));
    }

    /// <summary>
    /// THE LOAD-BEARING CASE: once a decision is made, it never flips, even once the window
    /// that produced it has long since elapsed. A live re-check on every scan would make a
    /// suppressed item suddenly start counting once its window passed — reproducing, on a
    /// delay, the exact double-count bug this section exists to prevent (see
    /// <c>KotorGameBridge.PartyPool.cs</c>'s own remarks on <c>_partyPoolSuppressionDecided</c>).
    /// </summary>
    [Fact]
    public void ASuppressionDecisionIsPermanentEvenAfterTheWindowWouldHaveExpired()
    {
        _bridge.RememberItemCreation(0x404, isCheat: false, nowMs: 10_000);

        // First look, inside the window: suppressed, and the decision is locked in.
        Assert.True(_bridge.IsAmbientPartyPoolNoise(0x404, nowMs: 10_500));

        // Second look, long past the window: STILL suppressed — the decision was never
        // re-evaluated, because IsAmbientPartyPoolNoise checks the decided-set FIRST.
        Assert.True(_bridge.IsAmbientPartyPoolNoise(0x404, nowMs: 500_000));
    }

    /// <summary>The same permanence, the other direction: an item decided NOT suppressed stays
    /// that way even if a later (impossible in practice, but never zero-cost to assume away)
    /// creation fact for the same id would have said otherwise.</summary>
    [Fact]
    public void ANotSuppressedDecisionIsAlsoPermanent()
    {
        _bridge.RememberItemCreation(0x405, isCheat: true, nowMs: 10_000);
        Assert.False(_bridge.IsAmbientPartyPoolNoise(0x405, nowMs: 10_100));

        // A later (later-writer-wins, per RememberItemCreation's own remarks) fact says this id
        // was actually ambient — but the SCAN's decision was already locked in on first sight.
        _bridge.RememberItemCreation(0x405, isCheat: false, nowMs: 10_050);
        Assert.False(_bridge.IsAmbientPartyPoolNoise(0x405, nowMs: 10_100));
    }

    /// <summary>Different ids are decided independently — suppressing one must never suppress
    /// (or un-suppress) an unrelated id.</summary>
    [Fact]
    public void DifferentItemIdsAreDecidedIndependently()
    {
        _bridge.RememberItemCreation(0x406, isCheat: false, nowMs: 10_000);
        _bridge.RememberItemCreation(0x407, isCheat: true, nowMs: 10_000);

        Assert.True(_bridge.IsAmbientPartyPoolNoise(0x406, nowMs: 10_100));
        Assert.False(_bridge.IsAmbientPartyPoolNoise(0x407, nowMs: 10_100));
    }

    /// <summary>Exactly at the window boundary is NOT suppressed — the check is a strict
    /// less-than, matching every other "within N ms" window in this codebase
    /// (<c>RecentlySyncedItems</c>'s own <c>ExpiresAtMs</c> comparison).</summary>
    [Fact]
    public void ExactlyAtTheWindowBoundaryIsNotSuppressed()
    {
        _bridge.NotePartyPoolWorldSwept();
        _bridge.RememberItemCreation(0x408, isCheat: false, nowMs: 10_000);

        Assert.False(_bridge.IsAmbientPartyPoolNoise(0x408, nowMs: 11_000));
    }

    // ---- How MUCH of a suppressed stack is ambient (version 64, shared-inventory-v2.md §17) ----
    //
    // Live 2026-09-02: the id the ring reports is the SURVIVING stack the ambient item was
    // merged into (the engine merges the loaded stack into the new item, frees the loaded one,
    // and the new item takes its id). A save-loaded medpac stack of 2 that END_M01AA's world-up
    // script topped up by 1 therefore shows as "0x12B created, count 1" with a stack of 3.
    // Baselining all 3 (version 63) hid the loaded 2 as well, the key vanished from the host's
    // snapshot, and the client re-deposited its own copy — physical duplication the sync could
    // not see. The baseline may hide exactly what the creation call contributed, no more.

    /// <summary>The case that duplicated live: loaded 2 + ambient 1 → only 1 is ambient.</summary>
    [Fact]
    public void AMergedAmbientStackIsBaselinedByTheCreatedCountNotTheStackSize()
    {
        _bridge.RememberItemCreation(0x12B, isCheat: false, nowMs: 10_000, worldSerial: 6, stackCount: 1);

        Assert.Equal(1, _bridge.AmbientUnitsOf(0x12B, stackSize: 3));
    }

    /// <summary>A stack that is entirely ambient (nothing loaded to merge into) still comes off
    /// in full — the created count IS the stack.</summary>
    [Fact]
    public void AWhollyAmbientStackIsStillBaselinedInFull()
    {
        _bridge.RememberItemCreation(0x12C, isCheat: false, nowMs: 10_000, worldSerial: 6, stackCount: 6);

        Assert.Equal(6, _bridge.AmbientUnitsOf(0x12C, stackSize: 6));
    }

    /// <summary>A merge can never leave fewer units than the call created, so a created count
    /// above the observed stack (part of it consumed between creation and this scan) is bounded
    /// by what is actually there — the same "offset never exceeds raw" rule
    /// <see cref="PartyPoolAmbientBaseline"/>'s Net applies per key.</summary>
    [Fact]
    public void TheCreatedCountIsBoundedByTheStackActuallyThere()
    {
        _bridge.RememberItemCreation(0x12D, isCheat: false, nowMs: 10_000, worldSerial: 6, stackCount: 6);

        Assert.Equal(4, _bridge.AmbientUnitsOf(0x12D, stackSize: 4));
    }

    /// <summary>The pre-64 rule survives only for a fact with no count at all (0 = the plugin
    /// could not read it): the whole stack, as before.</summary>
    [Fact]
    public void AnUnknownCreatedCountFallsBackToTheWholeStack()
    {
        _bridge.RememberItemCreation(0x12E, isCheat: false, nowMs: 10_000, worldSerial: 6);

        Assert.Equal(3, _bridge.AmbientUnitsOf(0x12E, stackSize: 3));
    }

    /// <summary>The seam a caller without any creation fact would hit (it never does — only a
    /// suppressed id is asked — but the answer is the safe one regardless).</summary>
    [Fact]
    public void NoCreationFactMeansTheWholeStack()
    {
        Assert.Equal(2, _bridge.AmbientUnitsOf(0x12F, stackSize: 2));
    }

    /// <summary>The count travels with the fact through a world reset the same way the cheat
    /// flag and the serial do.</summary>
    [Fact]
    public void TheCreatedCountSurvivesAWorldResetThatKeepsTheFact()
    {
        _bridge.RememberItemCreation(0x130, isCheat: false, nowMs: 10_000, worldSerial: 6, stackCount: 1);
        _bridge.ForgetItemCreationsFromOtherWorlds(keepWorldSerial: 6);

        Assert.True(_bridge.TryGetItemCreationObservation(0x130, out _, out _, out int stackCount));
        Assert.Equal(1, stackCount);
        Assert.Equal(1, _bridge.AmbientUnitsOf(0x130, stackSize: 3));
    }
}
