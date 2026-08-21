using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The debounce that stops an ordinary module transition from reading as a save-identity
/// mismatch (round 54, 2026-08-21 — see docs/research/shared-state-audit.md §2 round 53's
/// live <c>[W_SAVE]</c> flash on <c>tar_m10aa</c>, and <see cref="SaveIdentitySettle"/>'s own
/// doc comment for the full mechanism).
/// </summary>
public class SaveIdentitySettleTests
{
    private static WorldDigestComparison Mismatch(bool save) =>
        new(GlobalsCatalogueMismatch: false, SaveIdentityMismatch: save, ModuleGitMismatch: false);

    [Fact]
    public void ANonMismatchPassesThroughUntouched()
    {
        SaveIdentitySettle settle = new();
        WorldDigestComparison clean = Mismatch(save: false);

        Assert.Equal(clean, settle.Apply(clean, nowMs: 0));
        Assert.Equal(clean, settle.Apply(clean, nowMs: 100_000));
    }

    [Fact]
    public void ASingleTickMismatchIsSuppressed()
    {
        // Exactly round 53's crossing: the digest disagrees for one observation, right at the
        // transition, and never again.
        SaveIdentitySettle settle = new(settleMs: 5000);

        WorldDigestComparison result = settle.Apply(Mismatch(save: true), nowMs: 0);

        Assert.False(result.SaveIdentityMismatch);
    }

    [Fact]
    public void AMismatchThatPersistsPastTheSettleWindowIsHonored()
    {
        SaveIdentitySettle settle = new(settleMs: 5000);

        Assert.False(settle.Apply(Mismatch(save: true), nowMs: 0).SaveIdentityMismatch);
        Assert.False(settle.Apply(Mismatch(save: true), nowMs: 2000).SaveIdentityMismatch);
        Assert.False(settle.Apply(Mismatch(save: true), nowMs: 4999).SaveIdentityMismatch);

        // A genuinely different save never agrees again — once the window has fully elapsed
        // since the FIRST observation, it must read true so real content drift is still caught.
        Assert.True(settle.Apply(Mismatch(save: true), nowMs: 5000).SaveIdentityMismatch);
        Assert.True(settle.Apply(Mismatch(save: true), nowMs: 9000).SaveIdentityMismatch);
    }

    [Fact]
    public void TheTransientThenAgreeSequenceNeverReadsAsAMismatch()
    {
        // The exact shape round 53 hit live: the crossing side's digest updates first (one or
        // two disagreeing observations), then the slower peer catches up and the two agree —
        // all inside the settle window, well before it would have been honored.
        SaveIdentitySettle settle = new(settleMs: 5000);

        Assert.False(settle.Apply(Mismatch(save: true), nowMs: 0).SaveIdentityMismatch);
        Assert.False(settle.Apply(Mismatch(save: true), nowMs: 1000).SaveIdentityMismatch);
        Assert.False(settle.Apply(Mismatch(save: false), nowMs: 2000).SaveIdentityMismatch);

        // And agreement is not merely masked — it is forgotten. A LATER, independent mismatch
        // (a real content difference discovered after the crossing settled) starts its own
        // fresh window rather than inheriting time already spent on the first one.
        Assert.False(settle.Apply(Mismatch(save: true), nowMs: 2100).SaveIdentityMismatch);
        Assert.False(settle.Apply(Mismatch(save: true), nowMs: 6000).SaveIdentityMismatch);
        Assert.True(settle.Apply(Mismatch(save: true), nowMs: 7101).SaveIdentityMismatch);
    }

    [Fact]
    public void HonoredMismatchClearsOnTheNextCleanTick()
    {
        // Restoring a channel is never delayed — only taking one away is. Once a real
        // mismatch has been honored, the two peers agreeing again (walking into a module both
        // installs match on, or a rejoin adopting the host's world) must clear it on the very
        // next clean observation.
        SaveIdentitySettle settle = new(settleMs: 5000);

        Assert.False(settle.Apply(Mismatch(save: true), nowMs: 0).SaveIdentityMismatch);
        Assert.True(settle.Apply(Mismatch(save: true), nowMs: 5000).SaveIdentityMismatch);

        Assert.False(settle.Apply(Mismatch(save: false), nowMs: 5100).SaveIdentityMismatch);

        // And a fresh mismatch right after must serve its own full window again, not resume
        // wherever the cleared one left off.
        Assert.False(settle.Apply(Mismatch(save: true), nowMs: 5200).SaveIdentityMismatch);
        Assert.False(settle.Apply(Mismatch(save: true), nowMs: 9000).SaveIdentityMismatch);
        Assert.True(settle.Apply(Mismatch(save: true), nowMs: 10200).SaveIdentityMismatch);
    }

    [Fact]
    public void ResetForgetsAnInFlightTimer()
    {
        SaveIdentitySettle settle = new(settleMs: 5000);

        Assert.False(settle.Apply(Mismatch(save: true), nowMs: 0).SaveIdentityMismatch);
        settle.Reset();

        // Without the reset this would have honored at nowMs=5000 (5000ms after the first
        // observation at nowMs=0). A reconnect/new-peer must not inherit that clock.
        Assert.False(settle.Apply(Mismatch(save: true), nowMs: 5000).SaveIdentityMismatch);
    }

    [Fact]
    public void OtherFieldsPassThroughUnaffected()
    {
        SaveIdentitySettle settle = new(settleMs: 5000);
        WorldDigestComparison mismatch = new(
            GlobalsCatalogueMismatch: true, SaveIdentityMismatch: true, ModuleGitMismatch: true,
            Module: "tar_m10aa");

        WorldDigestComparison result = settle.Apply(mismatch, nowMs: 0);

        Assert.True(result.GlobalsCatalogueMismatch);
        Assert.True(result.ModuleGitMismatch);
        Assert.Equal("tar_m10aa", result.Module);
        Assert.False(result.SaveIdentityMismatch); // the only field this class ever touches
    }
}
