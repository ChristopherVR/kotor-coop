using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PartyFacingSmootherTests
{
    [Fact]
    public void MovingTurnTakesTheShortArcAcrossZero()
    {
        var smoother = new KotorCoop.Plugin.PartyFacingSmoother(1, 0.15f);
        smoother.Snap(0, Angles.TwoPi - 0.2f);

        Assert.True(smoother.TryStep(0, 0.2f, 0.15, out float facing));

        Assert.InRange(facing, 0f, 0.2f);
    }

    [Fact]
    public void StationarySnapKeepsExactOwnerHeading()
    {
        var smoother = new KotorCoop.Plugin.PartyFacingSmoother(1, 0.15f);
        smoother.Snap(0, 0.1f);

        smoother.Snap(0, 2.4f);

        Assert.True(smoother.TryStep(0, 2.4f, 0.0, out float facing));
        Assert.Equal(2.4f, facing, 5);
    }

    [Fact]
    public void NonfiniteFacingIsRejected()
    {
        var smoother = new KotorCoop.Plugin.PartyFacingSmoother(1, 0.15f);

        Assert.False(smoother.TryStep(0, float.NaN, 0.0, out _));
        Assert.False(smoother.TryStep(0, float.PositiveInfinity, 0.1, out _));
    }
}
