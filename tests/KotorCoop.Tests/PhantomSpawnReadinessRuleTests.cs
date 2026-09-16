using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// When a phantom may be spawned, and when a body spawned too early must be replaced.
///
/// THE REGRESSION THIS GUARDS: a phantom created in the window between the SERVER world
/// coming up and the CLIENT world being built is never drawn — the joined player simply
/// cannot see the host — while every field a probe would check (appearance row, model
/// resref, Gob pointer, live position) reads back correct. Live-measured 2026-09-16/17
/// on the world-adoption reload. See PhantomSpawnReadinessRule's own doc comment.
/// </summary>
public class PhantomSpawnReadinessRuleTests
{
    private const int Budget = PhantomSpawnReadinessRule.ClientWorldWaitMs;

    [Fact]
    public void TheOrdinaryCaseSpawnsImmediately()
    {
        Assert.Equal(
            PhantomSpawnReadiness.Spawn,
            PhantomSpawnReadinessRule.Decide(
                localBodyRendering: true, nowMs: 5_000, waitingSinceMs: 0, budgetMs: Budget));
    }

    [Fact]
    public void AWorldThatComesUpWhileWaitingSpawnsEvenThoughTheClockIsRunning()
    {
        // The budget must never pre-empt the good case: the moment the local player has a
        // body, the gate is open regardless of how long the wait had been going.
        Assert.Equal(
            PhantomSpawnReadiness.Spawn,
            PhantomSpawnReadinessRule.Decide(
                localBodyRendering: true, nowMs: 999_999, waitingSinceMs: 1, budgetMs: Budget));
    }

    [Fact]
    public void TheFirstRefusalDefersAndCannotBeOutOfPatienceYet()
    {
        // waitingSinceMs == 0 means the caller has not started its clock, so a large `nowMs`
        // must not be mistaken for a long wait — the bug this guards would have spawned an
        // invisible phantom on the very first refusal of any session more than 10 s old.
        Assert.Equal(
            PhantomSpawnReadiness.Defer,
            PhantomSpawnReadinessRule.Decide(
                localBodyRendering: false, nowMs: 3_600_000, waitingSinceMs: 0, budgetMs: Budget));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Budget - 1)]
    public void WaitingInsideTheBudgetKeepsDeferring(int elapsedMs)
    {
        Assert.Equal(
            PhantomSpawnReadiness.Defer,
            PhantomSpawnReadinessRule.Decide(
                localBodyRendering: false,
                nowMs: 100_000 + elapsedMs,
                waitingSinceMs: 100_000,
                budgetMs: Budget));
    }

    [Theory]
    [InlineData(Budget)]
    [InlineData(Budget + 1)]
    [InlineData(Budget * 10)]
    public void OnceTheBudgetIsSpentTheBodyIsSpawnedProvisionally(int elapsedMs)
    {
        // A peer who might not render still beats no peer at all — but the body is flagged,
        // not silently accepted.
        Assert.Equal(
            PhantomSpawnReadiness.SpawnProvisionally,
            PhantomSpawnReadinessRule.Decide(
                localBodyRendering: false,
                nowMs: 100_000 + elapsedMs,
                waitingSinceMs: 100_000,
                budgetMs: Budget));
    }

    [Fact]
    public void AProvisionalBodyIsReplacedOnceTheClientWorldArrives()
    {
        Assert.True(PhantomSpawnReadinessRule.ShouldReplaceProvisional(
            provisional: true, localBodyRendering: true, replacementsDone: 0,
            maxReplacements: PhantomSpawnReadinessRule.MaxProvisionalReplacements));
    }

    [Fact]
    public void AnOrdinaryBodyIsNeverReplaced()
    {
        Assert.False(PhantomSpawnReadinessRule.ShouldReplaceProvisional(
            provisional: false, localBodyRendering: true, replacementsDone: 0,
            maxReplacements: PhantomSpawnReadinessRule.MaxProvisionalReplacements));
    }

    [Fact]
    public void AProvisionalBodyIsNotReplacedWhileTheWorldIsStillNotUp()
    {
        // Replacing it now would only produce another provisional body — a destroy/spawn loop.
        Assert.False(PhantomSpawnReadinessRule.ShouldReplaceProvisional(
            provisional: true, localBodyRendering: false, replacementsDone: 0,
            maxReplacements: PhantomSpawnReadinessRule.MaxProvisionalReplacements));
    }

    [Fact]
    public void TheReplacementBudgetIsSpentAfterOneGo()
    {
        Assert.False(PhantomSpawnReadinessRule.ShouldReplaceProvisional(
            provisional: true, localBodyRendering: true,
            replacementsDone: PhantomSpawnReadinessRule.MaxProvisionalReplacements,
            maxReplacements: PhantomSpawnReadinessRule.MaxProvisionalReplacements));
    }
}
