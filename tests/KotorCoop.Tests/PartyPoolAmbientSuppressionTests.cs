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
        _bridge.RememberItemCreation(0x403, isCheat: false, nowMs: 10_000);

        Assert.False(_bridge.IsAmbientPartyPoolNoise(0x403, nowMs: 12_000));
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
        _bridge.RememberItemCreation(0x408, isCheat: false, nowMs: 10_000);

        Assert.False(_bridge.IsAmbientPartyPoolNoise(0x408, nowMs: 11_000));
    }
}
