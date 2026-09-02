using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins <see cref="ItemSweepProvenanceRule"/>: an item sweep taken between lives is served but
/// not trusted, and the first lookup in the settled world rebuilds before answering. The live
/// round is in the rule's own doc comment (death-handling.md §6.7 — the client's own short
/// sword, missed by the load-time sweep and published as a script grant fifteen seconds later).
/// </summary>
public class ItemSweepProvenanceRuleTests
{
    [Fact]
    public void NothingHeldMeansNothingToRebuild()
    {
        ItemSweepProvenanceRule rule = new();

        Assert.False(rule.Provisional);
        Assert.False(rule.MustRebuildNow(betweenLives: false));
        Assert.False(rule.MustRebuildNow(betweenLives: true));
    }

    [Fact]
    public void ASweepInASettledWorldIsTrusted()
    {
        ItemSweepProvenanceRule rule = new();

        rule.Swept(betweenLives: false);

        Assert.False(rule.Provisional);
        Assert.False(rule.MustRebuildNow(betweenLives: false));
    }

    /// <summary>The live shape: the inline sweep ran 3.5 s before the plugin acked the world;
    /// every lookup until the ack keeps serving it, the first one after rebuilds.</summary>
    [Fact]
    public void ASweepBetweenLivesIsProvisionalUntilTheWorldSettles()
    {
        ItemSweepProvenanceRule rule = new();

        rule.Swept(betweenLives: true);

        Assert.True(rule.Provisional);
        Assert.False(rule.MustRebuildNow(betweenLives: true), "still loading: another sweep now would be just as provisional");
        Assert.True(rule.MustRebuildNow(betweenLives: false), "the world settled: rebuild before trusting the load-time view");
    }

    [Fact]
    public void TheSettledRebuildClearsTheProvisionalMark()
    {
        ItemSweepProvenanceRule rule = new();

        rule.Swept(betweenLives: true);
        Assert.True(rule.MustRebuildNow(betweenLives: false));

        rule.Swept(betweenLives: false);

        Assert.False(rule.Provisional);
        Assert.False(rule.MustRebuildNow(betweenLives: false));
    }

    /// <summary>A background sweep that lands while a later load is in flight is provisional
    /// again — provenance follows the most recent sweep, not the first.</summary>
    [Fact]
    public void ALaterSweepBetweenLivesMakesTheCachesProvisionalAgain()
    {
        ItemSweepProvenanceRule rule = new();

        rule.Swept(betweenLives: false);
        rule.Swept(betweenLives: true);

        Assert.True(rule.Provisional);
        Assert.True(rule.MustRebuildNow(betweenLives: false));
    }

    [Fact]
    public void ResetForgetsTheHeldSweep()
    {
        ItemSweepProvenanceRule rule = new();

        rule.Swept(betweenLives: true);
        rule.Reset();

        Assert.False(rule.Provisional);
        Assert.False(rule.MustRebuildNow(betweenLives: false));
    }

    // ---- Settling: the 2026-09-02 23:31 round -----------------------------------------
    //
    // A game-over reload of END_M01AA settled (IsWorldBetweenLives cleared) while the save's
    // equipped clothes and short sword were still being created by the engine. The "settled"
    // sweep that built the script-grant baseline missed them just as a between-lives sweep
    // would have, and nothing looked again for the ordinary 15 s rebuild interval — by which
    // point the two items diffed in as a fresh script grant. Settling covers exactly that
    // window: it opens at every world edge and does not close until two sweeps, spaced apart,
    // agree on the same set of item ids.

    [Fact]
    public void ANewRuleIsNotSettling()
    {
        ItemSweepProvenanceRule rule = new();

        Assert.False(rule.Settling, "a bridge that never saw a world edge has nothing to settle");
    }

    [Fact]
    public void ResetOpensTheSettlingWindow()
    {
        ItemSweepProvenanceRule rule = new();

        rule.Reset(nowMs: 1_000);

        Assert.True(rule.Settling);
    }

    [Fact]
    public void TwoIdenticalSweepsASecondAndAHalfApartCloseTheWindow()
    {
        ItemSweepProvenanceRule rule = new();
        rule.Reset(nowMs: 0);

        uint[] ids = [0x127, 0x128];

        rule.Swept(betweenLives: false, ids, nowMs: 0);
        Assert.True(rule.Settling, "one sweep is not enough to trust");

        rule.Swept(betweenLives: false, ids, nowMs: 1_500);

        Assert.False(rule.Settling);
    }

    [Fact]
    public void TwoIdenticalSweepsTooCloseTogetherDoNotCloseTheWindow()
    {
        ItemSweepProvenanceRule rule = new();
        rule.Reset(nowMs: 0);

        uint[] ids = [0x127, 0x128];

        rule.Swept(betweenLives: false, ids, nowMs: 0);
        rule.Swept(betweenLives: false, ids, nowMs: 200);

        Assert.True(rule.Settling, "two reads a moment apart agree on the same still-forming heap");
    }

    /// <summary>The live shape exactly: the first settled sweep has the clothes but not the
    /// sword (0x128 missing), the next has both — a differing sweep restarts the wait rather
    /// than closing it, and only the FOLLOWING agreeing pair closes it.</summary>
    [Fact]
    public void ADifferingSweepRestartsTheWaitInsteadOfClosingIt()
    {
        ItemSweepProvenanceRule rule = new();
        rule.Reset(nowMs: 0);

        rule.Swept(betweenLives: false, new uint[] { 0x127 }, nowMs: 0);
        Assert.True(rule.Settling);

        // The sword shows up: this sweep disagrees with the held candidate, so it becomes the
        // new candidate rather than confirming the old one.
        rule.Swept(betweenLives: false, new uint[] { 0x127, 0x128 }, nowMs: 1_500);
        Assert.True(rule.Settling, "a differing sweep must not be read as agreement");

        // A short-spaced repeat of the same set does not count either.
        rule.Swept(betweenLives: false, new uint[] { 0x127, 0x128 }, nowMs: 1_600);
        Assert.True(rule.Settling);

        // Properly spaced from the SECOND sweep (the new candidate), the set finally agrees.
        rule.Swept(betweenLives: false, new uint[] { 0x127, 0x128 }, nowMs: 3_000);
        Assert.False(rule.Settling);
    }

    [Fact]
    public void ASweepStillBetweenLivesNeverCountsTowardsSettling()
    {
        ItemSweepProvenanceRule rule = new();
        rule.Reset(nowMs: 0);

        uint[] ids = [0x127];

        // Between-lives sweeps are expected to keep changing; they must never become (or
        // confirm) a settle candidate no matter how many of them agree.
        rule.Swept(betweenLives: true, ids, nowMs: 0);
        rule.Swept(betweenLives: true, ids, nowMs: 5_000);

        Assert.True(rule.Settling);

        // The first sweep once the world genuinely settles starts the real comparison fresh.
        rule.Swept(betweenLives: false, ids, nowMs: 6_000);
        Assert.True(rule.Settling);

        rule.Swept(betweenLives: false, ids, nowMs: 7_600);
        Assert.False(rule.Settling);
    }

    [Fact]
    public void TheSettlingCapGivesUpOnAGenuinelyChurningInventory()
    {
        ItemSweepProvenanceRule rule = new();
        rule.Reset(nowMs: 0);

        // Every sweep disagrees with the last — an inventory that never stops changing.
        rule.Swept(betweenLives: false, new uint[] { 1 }, nowMs: 0);
        rule.Swept(betweenLives: false, new uint[] { 1, 2 }, nowMs: 2_000);
        rule.Swept(betweenLives: false, new uint[] { 1, 2, 3 }, nowMs: 4_000);
        rule.Swept(betweenLives: false, new uint[] { 1, 2, 3, 4 }, nowMs: 19_000);
        Assert.True(rule.Settling, "still under the cap");

        rule.Swept(betweenLives: false, new uint[] { 1, 2, 3, 4, 5 }, nowMs: 20_500);

        Assert.False(rule.Settling, "the cap must not pin every caller to rebaseline-only forever");
    }

    [Fact]
    public void ProvisionalStillForcesTheInlineRebuildWhileSettling()
    {
        ItemSweepProvenanceRule rule = new();
        rule.Reset(nowMs: 0);

        rule.Swept(betweenLives: true, [], nowMs: 0);

        Assert.True(rule.Provisional);
        Assert.True(rule.Settling);
        Assert.False(rule.MustRebuildNow(betweenLives: true), "still loading: another sweep now would be just as provisional");
        Assert.True(rule.MustRebuildNow(betweenLives: false), "the world settled: rebuild before trusting the load-time view");
    }
}
