using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The per-creature offset arithmetic <c>spawncreature</c> uses to spread a multi-creature spawn
/// out from its anchor (the local player's position, or an explicit destination) instead of
/// stacking every body on one point. See <see cref="SpawnCreatureGridRule"/>'s own doc comment.
/// </summary>
public class SpawnCreatureGridRuleTests
{
    [Fact]
    public void FirstCreature_MatchesMoveNearsOwnDefaultOffset()
    {
        Vec3 anchor = new() { X = 10f, Y = 20f, Z = 3f };

        Vec3 point = SpawnCreatureGridRule.OffsetFor(anchor, index: 0);

        Assert.Equal(anchor.X + MoveNearDestinationRule.DefaultOffsetX, point.X);
        Assert.Equal(anchor.Y, point.Y);
        Assert.Equal(anchor.Z, point.Z);
    }

    [Fact]
    public void SecondCreature_IsOneSpacingFartherAlongX()
    {
        Vec3 anchor = default;

        Vec3 point = SpawnCreatureGridRule.OffsetFor(anchor, index: 1);

        Assert.Equal(SpawnCreatureGridRule.Spacing * 2, point.X);
        Assert.Equal(0f, point.Y);
    }

    [Fact]
    public void LastCreatureInARow_IsStillOnRowZero()
    {
        Vec3 anchor = default;

        Vec3 point = SpawnCreatureGridRule.OffsetFor(anchor, index: SpawnCreatureGridRule.RowWidth - 1);

        Assert.Equal(SpawnCreatureGridRule.Spacing * SpawnCreatureGridRule.RowWidth, point.X);
        Assert.Equal(0f, point.Y);
    }

    [Fact]
    public void FirstCreatureOfTheSecondRow_WrapsBackToColumnZero()
    {
        Vec3 anchor = default;

        Vec3 point = SpawnCreatureGridRule.OffsetFor(anchor, index: SpawnCreatureGridRule.RowWidth);

        Assert.Equal(SpawnCreatureGridRule.Spacing, point.X);
        Assert.Equal(SpawnCreatureGridRule.Spacing, point.Y);
    }

    [Fact]
    public void EveryPointInTheGridSharesTheAnchorsHeight()
    {
        Vec3 anchor = new() { X = -55f, Y = 12.5f, Z = 0.25f };

        for (int index = 0; index < 32; index++)
        {
            Vec3 point = SpawnCreatureGridRule.OffsetFor(anchor, index);
            Assert.Equal(anchor.Z, point.Z);
        }
    }

    [Fact]
    public void NoTwoOfTheMaximumThirtyTwoCreaturesShareAPoint()
    {
        Vec3 anchor = new() { X = 1f, Y = 2f, Z = 3f };
        HashSet<(float X, float Y)> seen = [];

        for (int index = 0; index < 32; index++)
        {
            Vec3 point = SpawnCreatureGridRule.OffsetFor(anchor, index);
            Assert.True(seen.Add((point.X, point.Y)), $"index {index} collided with an earlier point");
        }
    }
}
