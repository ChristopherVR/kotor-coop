using KotorCoop.Plugin;
using Xunit;
using Extent = KotorCoop.Plugin.MenuColumnRule.Extent;

namespace KotorCoop.Tests;

public sealed class EnhancedMenuLayoutRuleTests
{
    [Theory]
    [InlineData(1024, 768, 100)]
    [InlineData(1280, 720, 100)]
    [InlineData(1920, 1080, 150)]
    [InlineData(2560, 1440, 200)]
    [InlineData(3840, 2160, 300)]
    [InlineData(5120, 1440, 200)]
    [InlineData(800, 2160, 100)]
    [InlineData(1000, 899, 100)]
    [InlineData(1000, 900, 125)]
    [InlineData(1200, 1080, 150)]
    [InlineData(1400, 1260, 175)]
    [InlineData(2000, 1800, 250)]
    public void AutomaticScaleMatchesVendorTiers(int width, int height, int percent) =>
        Assert.Equal(percent, EnhancedMenuLayoutRule.TargetPercent(width, height));

    [Theory]
    [InlineData(3840, 2160, 125, 125)]
    [InlineData(3840, 2160, 174, 150)]
    [InlineData(3840, 2160, 450, 300)]
    [InlineData(3840, 2160, 1, 100)]
    [InlineData(1024, 768, 300, 125)]
    [InlineData(1280, 720, 300, 100)]
    [InlineData(1400, 1050, 300, 175)]
    [InlineData(3840, 2160, -1, 300)]
    public void ManualScaleRemainsQuantizedAndViewportGated(int width, int height, int manual, int expected) =>
        Assert.Equal(expected, EnhancedMenuLayoutRule.TargetPercent(width, height, manual));

    [Fact]
    public void CapturedMainMenuButtonRetainsRightAnchorAt720()
    {
        // Enhanced's captured NEWGAME at 1024x768 and cold 1280x720.
        Assert.Equal(new Extent(848, 340, 235, 24), EnhancedMenuLayoutRule.ResizeMainControl(
            new(592, 364, 235, 24), 1024, 768, 100, 1280, 720, 100));
    }

    [Fact]
    public void CapturedMainMenuButtonMatchesCold1440Scale()
    {
        Assert.Equal(new Extent(1696, 680, 470, 48), EnhancedMenuLayoutRule.ResizeMainControl(
            new(592, 364, 235, 24), 1024, 768, 100, 2560, 1440, 200));
    }

    [Fact]
    public void FullCanvasResizeKeepsEveryEdgeInsideNewViewport()
    {
        Assert.Equal(new Extent(0, 0, 1280, 720), EnhancedMenuLayoutRule.ResizeFullCanvasControl(
            new(0, 0, 1024, 768), 1024, 768, 1280, 720));
        // Captured options-main Graphics control: a 960x720 content canvas centers in 1280x720.
        Assert.Equal(new Extent(267, 366, 360, 60), EnhancedMenuLayoutRule.ResizeFullCanvasControl(
            new(114, 390, 384, 64), 1024, 768, 1280, 720));
    }

    [Fact]
    public void MainMenuNonButtonDecorationRemainsCentered()
    {
        Assert.Equal(new Extent(505, 124, 270, 472), EnhancedMenuLayoutRule.ResizeMainControl(
            new(377, 148, 270, 472), 1024, 768, 100, 1280, 720, 100, rightAnchored: false));
    }

    [Fact]
    public void DialogScalingPreservesSignedOffscreenCoordinates()
    {
        Assert.Equal(new Extent(-3, -5, 435, 131), EnhancedMenuLayoutRule.ResizeDialogControl(
            new(-2, -3, 290, 87), 100, 150));
    }

    [Fact]
    public void DialogRootMatchesVendorTemplateAfterChangingFractionalScale()
    {
        // A 290x87 template was rounded to 363x109 at 125%. Enhanced recognizes the
        // 150% root by exact template-derived dimensions, 435x131, rather than 436x131.
        Assert.Equal(new Extent(-3, -5, 435, 131), EnhancedMenuLayoutRule.ResizeDialogControl(
            new(-3, -4, 363, 109), 125, 150));
    }

    [Fact]
    public void BaselineCanRestoreExactOriginalAfterSeveralModeChanges()
    {
        Extent baseline = new(591, 425, 235, 24);
        _ = EnhancedMenuLayoutRule.ResizeMainControl(baseline, 1024, 768, 100, 3840, 2160, 300);
        _ = EnhancedMenuLayoutRule.ResizeMainControl(baseline, 1024, 768, 100, 1280, 720, 100);
        Assert.Equal(baseline, EnhancedMenuLayoutRule.ResizeMainControl(
            baseline, 1024, 768, 100, 1024, 768, 100));
    }

    [Fact]
    public void InvalidSourceDoesNotInventGeometry()
    {
        Extent original = new(40, 50, 60, 70);
        Assert.Equal(original, EnhancedMenuLayoutRule.ResizeFullCanvasControl(original, 0, 768, 1280, 720));
        Assert.Equal(original, EnhancedMenuLayoutRule.ResizeDialogControl(original, 0, 150));
        Assert.Equal(original, EnhancedMenuLayoutRule.ResizeMainControl(original, 1024, 768, 100, -1, 720, 100));
    }

    [Fact]
    public void ExtremeValuesSaturateWithoutWrappingOrNegativeSizes()
    {
        Assert.Equal(new Extent(int.MinValue, int.MaxValue, int.MaxValue, 0),
            EnhancedMenuLayoutRule.ResizeDialogControl(new(int.MinValue, int.MaxValue, int.MaxValue, -10), 1, int.MaxValue));
    }
}
