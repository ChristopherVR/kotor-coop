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
}
