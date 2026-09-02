using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The destination arithmetic <c>movenear</c> uses now that its default target is
/// module-agnostic (measured live 2026-09-02 in tar_m10aa: the old hardcoded END_M01AA point
/// teleported a live Vulkar off the map). No explicit destination -&gt; next to the local
/// player, a fixed +X offset; an explicit, non-origin destination always wins.
/// </summary>
public class MoveNearDestinationRuleTests
{
    [Fact]
    public void NoExplicitDestination_DefaultsToPlayerPositionOffsetAlongX()
    {
        Vec3 player = new() { X = 10f, Y = 20f, Z = 3f };
        Vec3 explicitPosition = default; // origin sentinel = "nothing sent"

        Vec3 destination = MoveNearDestinationRule.Resolve(player, explicitPosition, out bool isExplicit);

        Assert.False(isExplicit);
        Assert.Equal(player.X + MoveNearDestinationRule.DefaultOffsetX, destination.X);
        Assert.Equal(player.Y, destination.Y);
        Assert.Equal(player.Z, destination.Z);
    }

    [Fact]
    public void ExplicitDestination_OverridesTheDefault()
    {
        Vec3 player = new() { X = 10f, Y = 20f, Z = 3f };
        Vec3 explicitPosition = new() { X = -55f, Y = 12.5f, Z = 0.25f };

        Vec3 destination = MoveNearDestinationRule.Resolve(player, explicitPosition, out bool isExplicit);

        Assert.True(isExplicit);
        Assert.Equal(explicitPosition.X, destination.X);
        Assert.Equal(explicitPosition.Y, destination.Y);
        Assert.Equal(explicitPosition.Z, destination.Z);
    }

    [Fact]
    public void ExplicitDestinationAtOrigin_IsIndistinguishableFromNoneSent()
    {
        // Same tradeoff tp/walkto already accept: a real explicit destination is never exactly
        // (0,0,0), so the origin doubles as "nothing sent" rather than needing a separate flag.
        Vec3 player = new() { X = 10f, Y = 20f, Z = 3f };

        Vec3 destination = MoveNearDestinationRule.Resolve(player, default, out bool isExplicit);

        Assert.False(isExplicit);
        Assert.Equal(player.X + MoveNearDestinationRule.DefaultOffsetX, destination.X);
    }

    [Fact]
    public void PlayerAtOrigin_DefaultDestinationIsStillOffsetAlongX()
    {
        Vec3 player = default;

        Vec3 destination = MoveNearDestinationRule.Resolve(player, default, out bool isExplicit);

        Assert.False(isExplicit);
        Assert.Equal(MoveNearDestinationRule.DefaultOffsetX, destination.X);
        Assert.Equal(0f, destination.Y);
        Assert.Equal(0f, destination.Z);
    }
}
