using KotorCoop.Plugin;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins the magnitude bound both position screens share (<see cref="Vector3f.IsWorldPlausible"/>
/// on the agent side, <see cref="Vec3.IsWorldPlausible"/> in the plugin). The live hang that
/// added it is in docs/research/session-save.md (2026-09-02): a finite garbage position read
/// mid-teardown reached the phantom spawner and the engine searched for a walkmesh point
/// next to it forever.
/// </summary>
public class WorldPlausibilityTests
{
    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(102.5f, -33.25f, 4.0f)]
    [InlineData(-9999f, 9999f, -9999f)]
    public void OrdinaryAreaPointsAreAccepted(float x, float y, float z)
    {
        Assert.True(new Vector3f(x, y, z).IsWorldPlausible);
        Assert.True(Vec3.IsWorldPlausible(new Vec3 { X = x, Y = y, Z = z }));
    }

    [Theory]
    [InlineData(-4.15e34f, 0f, -1.06e37f)]   // the live read that hung the client
    [InlineData(10_000f, 0f, 0f)]
    [InlineData(0f, -10_000f, 0f)]
    [InlineData(0f, 0f, 1e9f)]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.PositiveInfinity, 0f)]
    public void OutOfWorldPointsAreRejectedOnBothSides(float x, float y, float z)
    {
        Assert.False(new Vector3f(x, y, z).IsWorldPlausible);
        Assert.False(Vec3.IsWorldPlausible(new Vec3 { X = x, Y = y, Z = z }));
    }

    [Fact]
    public void TheTwoBoundsAgree()
    {
        Assert.Equal(Vector3f.MaxWorldCoordinate, Vec3.MaxWorldCoordinate);
    }

    [Fact]
    public void PlayerStateUsesTheBoundedScreen()
    {
        PlayerState state = new(new Vector3f(-4.15e34f, 0f, -1.06e37f), 0f, "end_m01aa", 20, 0);
        Assert.False(state.IsPlausible);
    }

    /// <summary>
    /// <see cref="Vec3.IsOrigin"/> is a NARROWER, deliberately different question than
    /// <see cref="Vec3.IsWorldPlausible"/> — the exact-zero vector passes plausibility (a
    /// creature can genuinely stand at an area's origin) but is exactly what a resolved object
    /// that was never really placed reads back as. Regression for the live 2026-09-02
    /// `movenear` bug (docs/research/shared-state-audit.md §2 round 36's fix): the verb used to
    /// snap whatever <c>ResolveServerObject</c> handed back with no type check, and a wrongly
    /// resolved object read back (0,0,0) — indistinguishable from a real snap unless something
    /// specifically calls that out.
    /// </summary>
    [Fact]
    public void OnlyTheExactZeroVectorIsTheOrigin()
    {
        Assert.True(Vec3.IsOrigin(new Vec3 { X = 0f, Y = 0f, Z = 0f }));
        Assert.False(Vec3.IsOrigin(new Vec3 { X = 0.001f, Y = 0f, Z = 0f }));
        Assert.False(Vec3.IsOrigin(new Vec3 { X = 29.0f, Y = 143.5f, Z = 0f }));
        Assert.False(Vec3.IsOrigin(new Vec3 { X = -0.5f, Y = 0f, Z = 0f }));
    }

    /// <summary>
    /// The wire-side twin of <see cref="OnlyTheExactZeroVectorIsTheOrigin"/> —
    /// <see cref="Vector3f.IsOrigin"/> added 2026-09-25 (docs/research/walkmesh-guard.md §3)
    /// for the publish/receive-side gates (KotorGameBridge.Player.cs, SnapshotBuffer.Push) that
    /// stop a joiner's phantom being driven at the exact origin during a host's own module load.
    /// </summary>
    [Fact]
    public void VectorAndVec3AgreeOnWhatCountsAsTheOrigin()
    {
        Assert.True(new Vector3f(0f, 0f, 0f).IsOrigin);
        Assert.False(new Vector3f(0.001f, 0f, 0f).IsOrigin);
        Assert.False(new Vector3f(29.0f, 143.5f, 0f).IsOrigin);
        Assert.False(new Vector3f(-0.5f, 0f, 0f).IsOrigin);

        // IsWorldPlausible deliberately still ACCEPTS the exact origin on both sides — IsOrigin
        // is a narrower, separate question a caller opts into, not a change to that screen.
        Assert.True(new Vector3f(0f, 0f, 0f).IsWorldPlausible);
        Assert.True(Vec3.IsWorldPlausible(new Vec3 { X = 0f, Y = 0f, Z = 0f }));
    }
}
