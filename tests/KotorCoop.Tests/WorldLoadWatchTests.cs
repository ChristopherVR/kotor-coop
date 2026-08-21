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
}
