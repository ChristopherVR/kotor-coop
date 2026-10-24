using KotorCoop.Plugin;
using Xunit;
using Metrics = KotorCoop.Plugin.EnhancedMenuFontRule.Metrics;

namespace KotorCoop.Tests;

public sealed class EnhancedMenuFontRuleTests
{
    [Theory]
    [InlineData(127)]
    [InlineData(255)]
    [InlineData(256)]
    public void OriginalMetadataSupportsEverySuppliedCharacterCount(int characters)
    {
        string text = $"binary texture tail\nnumchars {characters}\r\nFONTHEIGHT 0.16\r\nbaselineheight 0.14\r\ntexturewidth 1.28\r\nspacingR 0\r\nspacingB 0\r\nupperleftcoords {characters}\n0 0 0\n";
        Assert.True(EnhancedMenuFontRule.TryParse(text, out int parsed, out Metrics metrics));
        Assert.Equal(characters, parsed);
        Assert.Equal(new Metrics(.16f, .14f, 1.28f, 0, 0), metrics);
    }

    [Fact]
    public void AbsoluteScalingRestoresOriginalWithoutCumulativeDrift()
    {
        Metrics original = new(.16f, .14f, 2.56f, .01f, .02f);
        Assert.True(EnhancedMenuFontRule.HasUniformScale(original, original.Scale(3)));
        Assert.Equal(original, original.Scale(1));
        Assert.Equal(.48f, original.Scale(3).Height, 5);
        Assert.Equal(.42f, original.Scale(3).Baseline, 5);
        Assert.Equal(7.68f, original.Scale(3).Width, 5);
        Assert.False(EnhancedMenuFontRule.HasUniformScale(original, original.Scale(3) with { Width = 2.56f }));
    }

    [Theory]
    [InlineData("fontheight NaN")]
    [InlineData("fontheight Infinity")]
    [InlineData("fontheight -1")]
    [InlineData("fontheight 0")]
    [InlineData("fontheight 9")]
    public void UnusableFontMetricsAreRejected(string field)
    {
        Assert.False(EnhancedMenuFontRule.TryParse($"numchars 256\n{field}\nbaselineheight .14\ntexturewidth 2.56\n", out _, out _));
    }

    [Fact]
    public void IncompleteMetadataIsNotTreatedAsAnUnscaledBaseline()
    {
        Assert.False(EnhancedMenuFontRule.TryParse("numchars 256\nfontheight .16\ntexturewidth 2.56\n", out _, out _));
        Assert.False(EnhancedMenuFontRule.HasUniformScale(new(.16f, .14f, 2.56f, 0, 0), new(.16f, .14f, 2.56f, .1f, 0)));
    }
}
