using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="SnapshotBuffer"/> with the shipping default (<c>dejitter = 1</c>): the end-to-end
/// behaviour <see cref="SnapshotTimingRuleTests"/> pins the arithmetic of. The tests are written
/// as the symptom players reported — "the other player catches up in bursts" — expressed as a
/// property of the interpolated output, so a regression reads as the bug returning rather than
/// as an arithmetic detail changing.
/// </summary>
public class SnapshotDejitterTests
{
    private const int Period = 33;
    private const int Delay = 80;

    private static PlayerState At(float x) => new(new Vector3f(x, 0f, 0f), 0f, "end_m01aa", 100, 0);

    private static SnapshotBuffer NewBuffer(bool dejitter = true) => new(Delay, Period, dejitter);

    [Fact]
    public void ADuplicateStateDoesNotBecomeASample()
    {
        SnapshotBuffer buffer = NewBuffer();

        // Three arrivals, one observation repeated: the sender's send timer fired twice
        // between two of its pump ticks. Filing the repeats would assert the peer stood still.
        buffer.Push(2, "Malak", At(0f), 1000);
        buffer.Push(2, "Malak", At(0f), 1033);
        buffer.Push(2, "Malak", At(4f), 1066);

        // Render exactly one period after the first sample. If the duplicate had been filed,
        // the 4 units would have to be covered in the single period from 1033 to 1066 and this
        // would read 4; on an honest timeline it is two periods of travel, so half of it.
        IReadOnlyList<RemotePeer> peers = buffer.Sample(1033 + Delay);

        Assert.Equal(2f, peers[0].State.Position.X, 2);
    }

    [Fact]
    public void ADuplicateStillCountsAsLiveness()
    {
        SnapshotBuffer buffer = NewBuffer();

        buffer.Push(2, "Malak", At(0f), 1000);

        // A player standing perfectly still publishes nothing but duplicates. Timing them out
        // for holding still would be a far worse bug than the one dejitter fixes.
        for (long t = 1033; t <= 4000; t += Period)
        {
            buffer.Push(2, "Malak", At(0f), t);
        }

        buffer.PruneStale(4000, 1000);

        Assert.Equal(1, buffer.PeerCount);
    }

    [Fact]
    public void AJitteredArrivalStreamPlaysBackAtAConstantSpeed()
    {
        // THE regression test for the reported symptom. The peer walks at a dead-constant
        // rate; the arrivals are mangled the way a real localhost link mangles them (the
        // 17/49 ms beat, plus a coalesced pair landing in the same millisecond). Sampling the
        // playback at a steady cadence must show a steady speed — no stalls, no dashes.
        SnapshotBuffer buffer = NewBuffer();

        const float StepUnits = 0.13f;
        long[] arrivalGaps = [17, 49, 33, 0, 66, 17, 49, 33, 20, 46, 33, 33, 17, 49, 33, 33];

        long arrival = 1000;
        float x = 0f;
        buffer.Push(2, "Malak", At(x), arrival);

        foreach (long gap in arrivalGaps)
        {
            arrival += gap;
            x += StepUnits;
            buffer.Push(2, "Malak", At(x), arrival);
        }

        List<float> speeds = [];
        float previous = buffer.Sample(1000 + Delay + Period)[0].State.Position.X;

        for (long t = 1000 + Delay + (2 * Period); t <= arrival; t += Period)
        {
            float now = buffer.Sample(t)[0].State.Position.X;
            speeds.Add(now - previous);
            previous = now;
        }

        Assert.NotEmpty(speeds);

        // A burst is a sample-to-sample speed far above the average. On the arrival-stamped
        // path this stream produces exactly that; regularised, every step is within a small
        // fraction of the nominal one-period displacement.
        foreach (float speed in speeds)
        {
            Assert.InRange(speed, StepUnits * 0.5f, StepUnits * 1.5f);
        }
    }

    [Fact]
    public void TheArrivalStampedPathStillBurstsAndIsWhatDejitterFixes()
    {
        // The control for the test above, and the reason this is a real fix rather than a
        // tuning preference: the SAME stream through the old behaviour does produce a step far
        // outside the plausible band. If this ever stops being true, the test above has stopped
        // measuring anything.
        SnapshotBuffer buffer = NewBuffer(dejitter: false);

        const float StepUnits = 0.13f;
        long arrival = 1000;
        float x = 0f;
        buffer.Push(2, "Malak", At(x), arrival);

        // A duplicate (the send timer beating against the pump timer), then two periods of
        // real travel arriving as one sample.
        buffer.Push(2, "Malak", At(x), arrival + Period);
        x += 2 * StepUnits;
        buffer.Push(2, "Malak", At(x), arrival + (2 * Period));

        float a = buffer.Sample(arrival + Delay + Period)[0].State.Position.X;
        float b = buffer.Sample(arrival + Delay + (2 * Period))[0].State.Position.X;

        Assert.True(b - a > StepUnits * 1.5f, $"expected a burst, saw {b - a}");
    }

    [Fact]
    public void ASilenceLongerThanTheResyncThresholdIsBelievedRatherThanFabricated()
    {
        SnapshotBuffer buffer = NewBuffer();

        buffer.Push(2, "Malak", At(0f), 1000);
        long returned = 1000 + SnapshotTimingRule.ResyncGapMs + 200;
        buffer.Push(2, "Malak", At(9f), returned);

        // Sampling well past the return must show the peer where they actually are, not
        // partway along a fabricated walk from where they stood before the gap.
        Assert.Equal(9f, buffer.Sample(returned + Delay + 500)[0].State.Position.X, 2);
    }

    [Fact]
    public void SamplesStayStrictlyOrderedAcrossAResync()
    {
        // Interpolate walks the sample list assuming ascending times; the rule's resync
        // branches hand back a raw arrival, which could otherwise land on or before the last
        // assigned stamp after the timeline had run ahead.
        SnapshotBuffer buffer = NewBuffer();

        buffer.Push(2, "Malak", At(0f), 1000);
        for (int i = 1; i <= 20; i++)
        {
            buffer.Push(2, "Malak", At(i * 0.1f), 1000 + i);
        }

        // Nothing threw, the peer is still tracked, and playback lands inside the real range.
        float x = buffer.Sample(3000)[0].State.Position.X;
        Assert.InRange(x, 0f, 2f);
    }
}
