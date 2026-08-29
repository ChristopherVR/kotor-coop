using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class SnapshotBufferTests
{
    private const int Delay = 100;

    /// <summary>
    /// Every test in THIS class exercises the interpolation arithmetic against hand-made
    /// timestamps that are deliberately not a real send cadence (0 ms, 100 ms and 5 s gaps at
    /// a nominal 33 ms period), so they are built with the arrival-stamped behaviour and say
    /// exactly what they mean. The regularised timeline the shipping default uses has its own
    /// tests — <see cref="SnapshotDejitterTests"/> and <see cref="SnapshotTimingRuleTests"/> —
    /// rather than being smuggled into assertions written to pin down something else.
    /// </summary>
    private static SnapshotBuffer NewBuffer() => new(Delay, dejitter: false);

    private static PlayerState At(float x, string module = "end_m01aa", float facing = 0f) =>
        new(new Vector3f(x, 0f, 0f), facing, module, 100, 0);

    [Fact]
    public void InterpolatesBetweenBracketingSnapshots()
    {
        SnapshotBuffer buffer = NewBuffer();

        buffer.Push(2, "Malak", At(0f), 1000);
        buffer.Push(2, "Malak", At(10f), 1100);

        // Render time is now - delay, so now = 1150 renders at 1050: exactly halfway.
        IReadOnlyList<RemotePeer> peers = buffer.Sample(1150);

        Assert.Single(peers);
        Assert.Equal(5f, peers[0].State.Position.X, 3);
    }

    [Fact]
    public void HoldsNewestRatherThanExtrapolating()
    {
        SnapshotBuffer buffer = NewBuffer();

        buffer.Push(2, "Malak", At(0f), 1000);
        buffer.Push(2, "Malak", At(10f), 1100);

        // Far past the newest sample. Extrapolating would run the avatar off into space.
        IReadOnlyList<RemotePeer> peers = buffer.Sample(5000);

        Assert.Equal(10f, peers[0].State.Position.X, 3);
    }

    [Fact]
    public void ClampsToOldestWhenRenderTimePredatesBuffer()
    {
        SnapshotBuffer buffer = NewBuffer();

        buffer.Push(2, "Malak", At(4f), 1000);
        buffer.Push(2, "Malak", At(8f), 1100);

        IReadOnlyList<RemotePeer> peers = buffer.Sample(1000);

        Assert.Equal(4f, peers[0].State.Position.X, 3);
    }

    [Fact]
    public void ModuleChangeSnapsInsteadOfInterpolating()
    {
        SnapshotBuffer buffer = NewBuffer();

        buffer.Push(2, "Malak", At(0f, "end_m01aa"), 1000);
        buffer.Push(2, "Malak", At(90f, "tar_m02aa"), 1100);

        IReadOnlyList<RemotePeer> peers = buffer.Sample(1150);

        // Crossing modules is a load screen, not a walk. Blending would drag the avatar
        // across the map through geometry that does not exist.
        Assert.Equal("tar_m02aa", peers[0].State.Module);
        Assert.Equal(90f, peers[0].State.Position.X, 3);
    }

    [Fact]
    public void FacingInterpolatesTheShortWayAroundZero()
    {
        SnapshotBuffer buffer = NewBuffer();

        float justBelowFull = Angles.TwoPi - 0.2f;
        buffer.Push(2, "Malak", At(0f, facing: justBelowFull), 1000);
        buffer.Push(2, "Malak", At(0f, facing: 0.2f), 1100);

        IReadOnlyList<RemotePeer> peers = buffer.Sample(1150);
        float facing = peers[0].State.Facing;

        // Halfway between -0.2 and +0.2 rad is 0, not pi. A naive lerp would spin the
        // avatar the long way round.
        bool nearZero = facing < 0.05f || facing > Angles.TwoPi - 0.05f;
        Assert.True(nearZero, $"expected facing near 0, got {facing}");
    }

    [Fact]
    public void OutOfOrderPacketsAreDiscarded()
    {
        SnapshotBuffer buffer = NewBuffer();

        buffer.Push(2, "Malak", At(0f), 1000);
        buffer.Push(2, "Malak", At(10f), 1100);
        buffer.Push(2, "Malak", At(999f), 1050);

        IReadOnlyList<RemotePeer> peers = buffer.Sample(5000);

        Assert.Equal(10f, peers[0].State.Position.X, 3);
    }

    [Fact]
    public void ImplausibleStatesAreIgnored()
    {
        SnapshotBuffer buffer = NewBuffer();

        // What a wrong offset profile produces. It must never reach the render path.
        buffer.Push(2, "Malak", new PlayerState(new Vector3f(float.NaN, 0f, 0f), 0f, "m", 1, 0), 1000);

        Assert.Equal(0, buffer.PeerCount);
    }

    [Fact]
    public void StalePeersArePruned()
    {
        SnapshotBuffer buffer = NewBuffer();

        buffer.Push(2, "Malak", At(0f), 1000);
        Assert.Equal(1, buffer.PeerCount);

        buffer.PruneStale(nowMs: 1000 + 6000, timeoutMs: 5000);
        Assert.Equal(0, buffer.PeerCount);
    }

    [Fact]
    public void TracksMultiplePeersIndependently()
    {
        SnapshotBuffer buffer = NewBuffer();

        buffer.Push(2, "Malak", At(0f), 1000);
        buffer.Push(3, "Bastila", At(50f), 1000);
        buffer.Push(2, "Malak", At(10f), 1100);
        buffer.Push(3, "Bastila", At(60f), 1100);

        Dictionary<uint, RemotePeer> peers = buffer.Sample(1150).ToDictionary(p => p.PeerId);

        Assert.Equal(2, peers.Count);
        Assert.Equal(5f, peers[2].State.Position.X, 3);
        Assert.Equal(55f, peers[3].State.Position.X, 3);
    }
}
