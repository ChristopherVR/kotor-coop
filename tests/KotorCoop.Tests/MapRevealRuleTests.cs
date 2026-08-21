using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The throttle behind fog-of-war sync (roadmap item 19): <c>MapRevealController</c> calls
/// the engine's <c>CSWSAreaMap::SetWorldPointExplored</c> at each remote peer's position, and
/// this is the pure "is it worth another call yet" decision, checked independently of the
/// engine call itself (docs/research/map-reveal.md). Two gates, both required — time AND
/// distance — so a peer standing still (talking, looting, waiting at a door) does not re-fire
/// the call every throttle interval forever, and a peer sprinting does not get a reveal call
/// faster than the cadence allows.
/// </summary>
public class MapRevealRuleTests
{
    private static Vec3 At(float x, float y, float z) => new() { X = x, Y = y, Z = z };

    [Fact]
    public void ANeverRevealedSlotAlwaysRevealsImmediately()
    {
        // No prior comparison point exists, and a peer who just appeared in this module
        // should show up on the map without waiting for a 2 m walk first.
        Assert.True(MapRevealRule.ShouldReveal(
            hasRevealedBefore: false,
            lastRevealedAtMs: 0,
            nowMs: 0,
            lastRevealedPosition: default,
            currentPosition: At(100f, 100f, 0f)));
    }

    [Fact]
    public void StaysSilentBeforeTheThrottleIntervalElapsesEvenWithMovement()
    {
        Vec3 last = At(0f, 0f, 0f);
        Vec3 farAway = At(50f, 50f, 0f); // well past the move threshold

        Assert.False(MapRevealRule.ShouldReveal(
            hasRevealedBefore: true,
            lastRevealedAtMs: 1000,
            nowMs: 1000 + MapRevealRule.MinIntervalMs - 1,
            lastRevealedPosition: last,
            currentPosition: farAway));
    }

    [Fact]
    public void RevealsOnceTheThrottleIntervalHasElapsedAndTheMoveThresholdIsCleared()
    {
        Vec3 last = At(0f, 0f, 0f);
        Vec3 moved = At(0f, MapRevealRule.MinMoveDistance + 0.1f, 0f);

        Assert.True(MapRevealRule.ShouldReveal(
            hasRevealedBefore: true,
            lastRevealedAtMs: 1000,
            nowMs: 1000 + MapRevealRule.MinIntervalMs,
            lastRevealedPosition: last,
            currentPosition: moved));
    }

    [Fact]
    public void DoesNotRevealAgainWhenTheIntervalPassedButThePeerBarelyMoved()
    {
        // A player standing at a shop counter or a door for ten seconds must not re-fire
        // the engine call forty times in that span.
        Vec3 last = At(10f, 10f, 0f);
        Vec3 barelyMoved = At(10.2f, 10f, 0f); // well under the 2 m threshold

        Assert.False(MapRevealRule.ShouldReveal(
            hasRevealedBefore: true,
            lastRevealedAtMs: 1000,
            nowMs: 1000 + (MapRevealRule.MinIntervalMs * 10),
            lastRevealedPosition: last,
            currentPosition: barelyMoved));
    }

    [Fact]
    public void ExactlyAtTheMoveThresholdCounts()
    {
        Vec3 last = At(0f, 0f, 0f);
        Vec3 exact = At(MapRevealRule.MinMoveDistance, 0f, 0f);

        Assert.True(MapRevealRule.ShouldReveal(
            hasRevealedBefore: true,
            lastRevealedAtMs: 0,
            nowMs: MapRevealRule.MinIntervalMs,
            lastRevealedPosition: last,
            currentPosition: exact));
    }

    [Fact]
    public void DistanceIsMeasuredInAllThreeAxes()
    {
        // A peer who only changed elevation (a ladder, a lift, a set of stairs) still moved
        // through the world and should still be worth a reveal.
        Vec3 last = At(0f, 0f, 0f);
        Vec3 movedUp = At(0f, 0f, MapRevealRule.MinMoveDistance + 0.1f);

        Assert.True(MapRevealRule.ShouldReveal(
            hasRevealedBefore: true,
            lastRevealedAtMs: 0,
            nowMs: MapRevealRule.MinIntervalMs,
            lastRevealedPosition: last,
            currentPosition: movedUp));
    }

    [Fact]
    public void ARealisticWalkProducesRevealsSpacedByBothTheIntervalAndTheDistance()
    {
        // A peer walking steadily at ~4 m/s, sampled every 100 ms (~0.4 m/sample) for four
        // seconds: the interval floor (250 ms) and the distance floor (2 m) together mean a
        // reveal only every ~500 ms once both gates are satisfied together, not every sample.
        bool hasRevealed = false;
        long lastAt = 0;
        Vec3 lastPos = default;
        int reveals = 0;

        for (int sample = 0; sample < 40; sample++)
        {
            long now = sample * 100L;
            Vec3 current = At(sample * 0.4f, 0f, 0f);

            if (MapRevealRule.ShouldReveal(hasRevealed, lastAt, now, lastPos, current))
            {
                reveals++;
                hasRevealed = true;
                lastAt = now;
                lastPos = current;
            }
        }

        // The first sample always reveals (nothing to compare against yet); after that,
        // covering 2 m at 0.4 m/sample takes 5 samples (500 ms), which also clears the
        // 250 ms interval floor — so a reveal roughly every 5 samples, not all 40.
        Assert.True(reveals is > 1 and < 15, $"expected a handful of throttled reveals, got {reveals}");
    }
}
