using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Where a stand-in stands during a scripted scene: beside the anchor, facing the same way, on
/// the first spot clear of other bodies (the Star Forge ceremony showed neither player the other).
/// </summary>
public class SceneStagingRuleTests
{
    private static Vec3 At(float x, float y, float z = 0f) => new() { X = x, Y = y, Z = z };

    [Fact]
    public void OnlyDuringAScene()
    {
        Assert.False(SceneStagingRule.ShouldStage(false, At(0, 0), At(0, 0)));
        Assert.True(SceneStagingRule.ShouldStage(true, At(0, 0), At(0, 0)));
    }

    [Fact]
    public void OnlyWhenThePeerIsOnTheSameMark()
    {
        Assert.True(SceneStagingRule.ShouldStage(true, At(2f, 0), At(0, 0)));
        Assert.False(SceneStagingRule.ShouldStage(true, At(3f, 0), At(0, 0)));
    }

    [Fact]
    public void TheLeftCandidateFacesTheSameWay()
    {
        (Vec3 spot, float facing) = SceneStagingRule.Spot(At(10, 20, 5), At(1, 0), 2);

        Assert.Equal(10f, spot.X, 3);
        Assert.Equal(20f + SceneStagingRule.SideOffset, spot.Y, 3);
        Assert.Equal(5f, spot.Z, 3);
        Assert.Equal(0f, facing, 3);
    }

    [Fact]
    public void TheRightCandidateMirrorsIt()
    {
        (Vec3 spot, _) = SceneStagingRule.Spot(At(10, 20), At(1, 0), 3);

        Assert.Equal(10f, spot.X, 3);
        Assert.Equal(20f - SceneStagingRule.SideOffset, spot.Y, 3);
    }

    [Fact]
    public void TheFirstCandidateSitsBehindTheAnchor()
    {
        (Vec3 spot, _) = SceneStagingRule.Spot(At(0, 0), At(1, 0), 0);

        Assert.True(spot.X < 0f);
        Assert.True(spot.Y > 0f);
    }

    [Fact]
    public void FollowsTheAnchorsFacing()
    {
        (Vec3 spot, float facing) = SceneStagingRule.Spot(At(0, 0), At(0, -2), 2);

        Assert.Equal(SceneStagingRule.SideOffset, spot.X, 3);
        Assert.Equal(0f, spot.Y, 3);
        Assert.Equal(-MathF.PI / 2f, facing, 3);
    }

    [Fact]
    public void EveryCandidateIsAwayFromTheAnchor()
    {
        for (int i = 0; i < SceneStagingRule.CandidateCount; i++)
        {
            (Vec3 spot, _) = SceneStagingRule.Spot(At(0, 0), At(1, 0), i);
            Assert.True(MathF.Sqrt((spot.X * spot.X) + (spot.Y * spot.Y)) >= SceneStagingRule.Clearance);
        }
    }

    [Fact]
    public void ClearanceRejectsASpotOnAnotherBody()
    {
        Assert.False(SceneStagingRule.IsClear(At(0, 0), [At(0.7f, 0)]));
        Assert.True(SceneStagingRule.IsClear(At(0, 0), [At(1.2f, 0), At(0, -1.5f)]));
    }

    [Fact]
    public void DegenerateOrientationFacesPlusX()
    {
        (Vec3 spot, float facing) = SceneStagingRule.Spot(At(0, 0), At(0, 0), 2);

        Assert.Equal(0f, spot.X, 3);
        Assert.Equal(SceneStagingRule.SideOffset, spot.Y, 3);
        Assert.Equal(0f, facing, 3);
    }
}
