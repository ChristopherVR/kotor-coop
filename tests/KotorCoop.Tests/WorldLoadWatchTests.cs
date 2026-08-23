using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="SerialEdgeWatch"/> — the same edge-detector shape <see cref="SaveIdentityWatch"/>
/// already has, minus that type's "0 = unresolved" special case: a world-load serial legitimately
/// starts at 0 (no load observed yet this process).
/// </summary>
public sealed class SerialEdgeWatchTests
{
    [Fact]
    public void TheFirstReadingBecomesTheBaselineNotAChangeEvenWhenItIsZero()
    {
        SerialEdgeWatchState state = SerialEdgeWatchState.Initial;

        (state, bool changed) = SerialEdgeWatch.Observe(state, 0);

        Assert.True(state.Established);
        Assert.Equal(0u, state.Baseline);
        Assert.False(changed);
    }

    [Fact]
    public void TheSameSerialRepeatedIsNeverAChange()
    {
        SerialEdgeWatchState state = SerialEdgeWatchState.Initial;
        (state, _) = SerialEdgeWatch.Observe(state, 3);

        for (int i = 0; i < 4; i++)
        {
            (state, bool changed) = SerialEdgeWatch.Observe(state, 3);
            Assert.False(changed);
        }
    }

    [Fact]
    public void AnAdvancingSerialIsTheEdgeAndReBaselines()
    {
        SerialEdgeWatchState state = SerialEdgeWatchState.Initial;
        (state, _) = SerialEdgeWatch.Observe(state, 5);

        (state, bool changed) = SerialEdgeWatch.Observe(state, 6);
        Assert.True(changed);
        Assert.Equal(6u, state.Baseline);

        (state, bool repeat) = SerialEdgeWatch.Observe(state, 6);
        Assert.False(repeat);
    }

    [Fact]
    public void ASkippedRangeIsStillJustOneEdge()
    {
        // The plugin may bump the serial several times between two agent polls; the exact
        // count carries no meaning, only "at least one load happened since last observed".
        SerialEdgeWatchState state = SerialEdgeWatchState.Initial;
        (state, _) = SerialEdgeWatch.Observe(state, 1);

        (state, bool changed) = SerialEdgeWatch.Observe(state, 9);
        Assert.True(changed);
        Assert.Equal(9u, state.Baseline);
    }
}

/// <summary>
/// <see cref="SaveOrLoadClassifier"/> — the save-vs-load correlation roadmap item 14 phase 2
/// needed: a digest change alone cannot tell a save from a load (KOTOR autosaves through
/// ordinary transitions too), so a digest change is classified against whether a world-load
/// edge (<see cref="WorldLoadWatchTests"/>'s counterpart) was seen recently.
/// </summary>
public sealed class SaveOrLoadClassifierTests
{
    [Fact]
    public void ADigestChangeWithNoLoadEdgeIsASave()
    {
        SaveOrLoadClassifier classifier = new();

        SaveOrLoadVerdict verdict = classifier.Observe(nowMs: 1000, loadEdge: false, digestChanged: true);

        Assert.Equal(SaveOrLoadVerdict.Save, verdict);
    }

    [Fact]
    public void ADigestChangeCoincidingWithALoadEdgeIsALoad()
    {
        SaveOrLoadClassifier classifier = new();

        SaveOrLoadVerdict verdict = classifier.Observe(nowMs: 1000, loadEdge: true, digestChanged: true);

        Assert.Equal(SaveOrLoadVerdict.Load, verdict);
    }

    [Fact]
    public void ALoadEdgeThatArrivesBeforeTheDigestCatchesUpIsStillALoad()
    {
        // The digest is a re-stat of a file on disk with its own refresh cadence; the load
        // edge is written the instant the plugin notices it. The two do not update in
        // lockstep, so a load edge a couple of ticks before the digest change must still
        // correlate.
        SaveOrLoadClassifier classifier = new();

        Assert.Equal(SaveOrLoadVerdict.None, classifier.Observe(nowMs: 1000, loadEdge: true, digestChanged: false));
        Assert.Equal(SaveOrLoadVerdict.None, classifier.Observe(nowMs: 1200, loadEdge: false, digestChanged: false));

        SaveOrLoadVerdict verdict = classifier.Observe(nowMs: 1900, loadEdge: false, digestChanged: true);

        Assert.Equal(SaveOrLoadVerdict.Load, verdict);
    }

    [Fact]
    public void ALoadEdgeOutsideTheCorrelationWindowDoesNotMatchALaterDigestChange()
    {
        SaveOrLoadClassifier classifier = new(correlationWindowMs: 1000);

        Assert.Equal(SaveOrLoadVerdict.None, classifier.Observe(nowMs: 0, loadEdge: true, digestChanged: false));

        // Well past the window: this is an unrelated, later save.
        SaveOrLoadVerdict verdict = classifier.Observe(nowMs: 5000, loadEdge: false, digestChanged: true);

        Assert.Equal(SaveOrLoadVerdict.Save, verdict);
    }

    [Fact]
    public void ALoadEdgeIsConsumedByTheFirstDigestChangeAndCannotMatchASecondOne()
    {
        SaveOrLoadClassifier classifier = new();

        Assert.Equal(SaveOrLoadVerdict.Load, classifier.Observe(nowMs: 0, loadEdge: true, digestChanged: true));

        // A second, unrelated digest change later must not reuse the already-spent load edge.
        SaveOrLoadVerdict second = classifier.Observe(nowMs: 500, loadEdge: false, digestChanged: true);

        Assert.Equal(SaveOrLoadVerdict.Save, second);
    }

    [Fact]
    public void NoDigestChangeIsNeverAVerdictEvenWithALoadEdge()
    {
        SaveOrLoadClassifier classifier = new();

        Assert.Equal(SaveOrLoadVerdict.None, classifier.Observe(nowMs: 0, loadEdge: false, digestChanged: false));
        Assert.Equal(SaveOrLoadVerdict.None, classifier.Observe(nowMs: 100, loadEdge: true, digestChanged: false));
    }

    [Fact]
    public void ResetForgetsAPendingLoadEdge()
    {
        SaveOrLoadClassifier classifier = new();
        classifier.Observe(nowMs: 0, loadEdge: true, digestChanged: false);

        classifier.Reset();

        SaveOrLoadVerdict verdict = classifier.Observe(nowMs: 100, loadEdge: false, digestChanged: true);

        Assert.Equal(SaveOrLoadVerdict.Save, verdict);
    }

    // ---- round 86: the `loadgame` verb's REVERSED ordering (digest edge before load edge) --

    [Fact]
    public void ADigestChangeFollowedByALoadEdgeWithinTheWindowIsUpgradedToALoad()
    {
        // The exact shape round 86 found live: `TryStageSaveAsAutosave` rewrites the save
        // file on the agent's own thread before the engine load is even requested, so the
        // digest edge routinely arrives BEFORE the load-serial edge for `loadgame`. The first
        // tick can only honestly report Save (nothing has correlated yet) — but a load edge
        // arriving later, still inside the window, must retroactively surface as the Load the
        // caller needs to trigger a rejoin.
        SaveOrLoadClassifier classifier = new();

        SaveOrLoadVerdict first = classifier.Observe(nowMs: 0, loadEdge: false, digestChanged: true);
        Assert.Equal(SaveOrLoadVerdict.Save, first);

        SaveOrLoadVerdict second = classifier.Observe(nowMs: 3000, loadEdge: true, digestChanged: false);
        Assert.Equal(SaveOrLoadVerdict.Load, second);
    }

    [Fact]
    public void ADigestChangeFollowedByALoadEdgeOutsideTheWindowIsNotRetroactivelyUpgraded()
    {
        SaveOrLoadClassifier classifier = new(correlationWindowMs: 1000);

        SaveOrLoadVerdict first = classifier.Observe(nowMs: 0, loadEdge: false, digestChanged: true);
        Assert.Equal(SaveOrLoadVerdict.Save, first);

        // Well past the window: an unrelated, later load (or the false-positive an ordinary
        // peer-follow warp's own load-serial bump could produce) must not retroactively
        // reclassify a save that has nothing to do with it.
        SaveOrLoadVerdict second = classifier.Observe(nowMs: 5000, loadEdge: true, digestChanged: false);
        Assert.Equal(SaveOrLoadVerdict.None, second);
    }

    [Fact]
    public void ADigestChangeIsConsumedByTheFirstLoadEdgeAndCannotMatchASecondOne()
    {
        SaveOrLoadClassifier classifier = new();

        Assert.Equal(SaveOrLoadVerdict.Save, classifier.Observe(nowMs: 0, loadEdge: false, digestChanged: true));
        Assert.Equal(SaveOrLoadVerdict.Load, classifier.Observe(nowMs: 1000, loadEdge: true, digestChanged: false));

        // A FRESH digest change afterward, with no load edge pending, is judged independently
        // — proving the earlier upgrade left no stale "pending digest" state behind that a
        // later, unrelated digest change could wrongly reuse.
        SaveOrLoadVerdict third = classifier.Observe(nowMs: 1100, loadEdge: false, digestChanged: true);
        Assert.Equal(SaveOrLoadVerdict.Save, third);
    }

    [Fact]
    public void TheOriginalLoadThenDigestOrderingStillWorksUnchanged()
    {
        // Regression guard: the fix must not disturb the direction this class already got
        // right (a real pause-menu Load reading an EXISTING save, no staging rewrite, is
        // still expected to announce the load-serial edge before the digest catches up).
        SaveOrLoadClassifier classifier = new();

        Assert.Equal(SaveOrLoadVerdict.None, classifier.Observe(nowMs: 0, loadEdge: true, digestChanged: false));
        SaveOrLoadVerdict verdict = classifier.Observe(nowMs: 1500, loadEdge: false, digestChanged: true);

        Assert.Equal(SaveOrLoadVerdict.Load, verdict);
    }

    [Fact]
    public void ResetForgetsAPendingDigestEdgeToo()
    {
        SaveOrLoadClassifier classifier = new();
        classifier.Observe(nowMs: 0, loadEdge: false, digestChanged: true);

        classifier.Reset();

        // Without the reset this load edge would have upgraded the forgotten digest change;
        // with no digest change on this tick it correctly produces nothing instead.
        SaveOrLoadVerdict verdict = classifier.Observe(nowMs: 100, loadEdge: true, digestChanged: false);

        Assert.Equal(SaveOrLoadVerdict.None, verdict);
    }
}
