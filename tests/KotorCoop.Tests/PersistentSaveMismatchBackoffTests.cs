using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The attempt cap and exponential backoff added to <see cref="PersistentSaveMismatchRule"/>
/// on 2026-09-18 (docs/review-seamless-2026-09-18.md §2: the SaveMismatch auto-rejoin had no
/// cap, so a mismatch no fetch could heal re-ran the whole rejoin cycle every 20 s all
/// session). Pins: the delay doubles per attempt, the fifth attempt is the last for one host
/// world, healing resets the count, and a different host world starts over.
/// <see cref="PersistentSaveMismatchRuleTests"/> still covers the edge/level behaviour, which
/// is unchanged.
/// </summary>
public sealed class PersistentSaveMismatchBackoffTests
{
    private const uint WorldA = 0xA11CE;
    private const uint WorldB = 0xB0B;

    [Fact]
    public void TheRetryDelayDoublesPerAttemptFromTheBaseInterval()
    {
        long b = PersistentSaveMismatchRule.RetryIntervalMs;
        Assert.Equal(b, PersistentSaveMismatchRule.RetryDelayMs(0));
        Assert.Equal(b, PersistentSaveMismatchRule.RetryDelayMs(1));
        Assert.Equal(2 * b, PersistentSaveMismatchRule.RetryDelayMs(2));
        Assert.Equal(4 * b, PersistentSaveMismatchRule.RetryDelayMs(3));
        Assert.Equal(8 * b, PersistentSaveMismatchRule.RetryDelayMs(4));
    }

    [Fact]
    public void FiveAttemptsWithDoublingWaitsThenItGivesUpForThatWorld()
    {
        MismatchRejoinWatchState state = MismatchRejoinWatchState.Initial;
        long now = 1000;
        List<long> attemptTimes = [];

        for (int i = 0; i < 20; i++)
        {
            (bool trigger, state) = PersistentSaveMismatchRule.Observe(state, true, false, now, WorldA);
            if (trigger)
            {
                attemptTimes.Add(now);
            }

            now += PersistentSaveMismatchRule.RetryIntervalMs;
        }

        long b = PersistentSaveMismatchRule.RetryIntervalMs;
        Assert.Equal(new long[] { 1000, 1000 + b, 1000 + 3 * b, 1000 + 7 * b, 1000 + 15 * b }, attemptTimes);
        Assert.Equal(PersistentSaveMismatchRule.MaxAttemptsPerWorld, state.Attempts);
        Assert.True(state.GaveUp);

        // Far in the future, still mismatched, still the same world: no sixth attempt.
        (bool late, state) = PersistentSaveMismatchRule.Observe(state, true, false, now + 100 * b, WorldA);
        Assert.False(late);
        Assert.True(state.GaveUp);
    }

    [Fact]
    public void AWaitShorterThanTheGrownDelayDoesNotRetry()
    {
        MismatchRejoinWatchState afterTwo = new(WasMismatched: true, LastAttemptAtMs: 1000, Attempts: 2, HostWorldKey: WorldA);
        long notYet = 1000 + (2 * PersistentSaveMismatchRule.RetryIntervalMs) - 1;

        (bool trigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(afterTwo, true, false, notYet, WorldA);

        Assert.False(trigger);
        Assert.Equal(2, next.Attempts);
    }

    [Fact]
    public void HealingResetsTheAttemptCount()
    {
        MismatchRejoinWatchState capped = new(WasMismatched: true, LastAttemptAtMs: 1000, Attempts: 5, HostWorldKey: WorldA);

        (_, MismatchRejoinWatchState healed) = PersistentSaveMismatchRule.Observe(capped, false, false, 2000, WorldA);
        Assert.Equal(0, healed.Attempts);
        Assert.False(healed.GaveUp);

        (bool trigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(healed, true, false, 3000, WorldA);
        Assert.True(trigger);
        Assert.Equal(1, next.Attempts);
    }

    [Fact]
    public void ADifferentHostWorldStartsTheCountOver()
    {
        MismatchRejoinWatchState capped = new(WasMismatched: true, LastAttemptAtMs: 1000, Attempts: 5, HostWorldKey: WorldA);

        (bool trigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(capped, true, false, 1001, WorldB);

        Assert.True(trigger);
        Assert.Equal(1, next.Attempts);
        Assert.Equal(WorldB, next.HostWorldKey);
    }

    [Fact]
    public void AnInFlightRejoinDoesNotCountAsAnAttempt()
    {
        MismatchRejoinWatchState one = new(WasMismatched: true, LastAttemptAtMs: 1000, Attempts: 1, HostWorldKey: WorldA);

        (bool trigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(one, true, true, 999_999, WorldA);

        Assert.False(trigger);
        Assert.Equal(1, next.Attempts);
    }
}
