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
}
