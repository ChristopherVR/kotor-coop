using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The `spawncreature <template> [count] [x y [z]]` console verb's pure parse
/// (docs/research/creature-liveness.md's 2026-09-03 checklist item 6). See
/// <see cref="SpawnCreatureVerbRule"/>'s own doc comment for why count must precede any
/// coordinates rather than being independently optional.
/// </summary>
public class SpawnCreatureVerbRuleTests
{
    [Fact]
    public void TemplateOnly_DefaultsToOneNearThePlayer()
    {
        bool ok = SpawnCreatureVerbRule.TryParse(["g_vulkar001"], out SpawnCreatureVerbRule.Result result, out string error);

        Assert.True(ok);
        Assert.Equal(string.Empty, error);
        Assert.Equal("g_vulkar001", result.Template);
        Assert.Equal(1, result.Count);
        Assert.False(result.IsExplicitPosition);
        Assert.Equal(0f, result.X);
        Assert.Equal(0f, result.Y);
        Assert.Equal(0f, result.Z);
    }

    [Fact]
    public void TemplateAndCount_ParsesTheCountWithNoExplicitPosition()
    {
        bool ok = SpawnCreatureVerbRule.TryParse(["n_sithsoldier001", "5"], out SpawnCreatureVerbRule.Result result, out _);

        Assert.True(ok);
        Assert.Equal("n_sithsoldier001", result.Template);
        Assert.Equal(5, result.Count);
        Assert.False(result.IsExplicitPosition);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public void CountAtEitherBoundIsAccepted(int count)
    {
        bool ok = SpawnCreatureVerbRule.TryParse(
            ["g_vulkar001", count.ToString()], out SpawnCreatureVerbRule.Result result, out _);

        Assert.True(ok);
        Assert.Equal(count, result.Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("33")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void OutOfRangeOrUnparseableCountIsInvalid(string countToken)
    {
        bool ok = SpawnCreatureVerbRule.TryParse(["g_vulkar001", countToken], out _, out string error);

        Assert.False(ok);
        Assert.Contains("spawn count", error);
    }

    [Fact]
    public void CountThenCoordinatePair_IsAnExplicitPositionWithZDefaultingToZero()
    {
        bool ok = SpawnCreatureVerbRule.TryParse(
            ["g_vulkar001", "3", "12.5", "-30.0"], out SpawnCreatureVerbRule.Result result, out _);

        Assert.True(ok);
        Assert.Equal(3, result.Count);
        Assert.True(result.IsExplicitPosition);
        Assert.Equal(12.5f, result.X);
        Assert.Equal(-30.0f, result.Y);
        Assert.Equal(0f, result.Z);
    }

    [Fact]
    public void CountThenCoordinateTriple_CarriesAnExplicitHeight()
    {
        bool ok = SpawnCreatureVerbRule.TryParse(
            ["g_vulkar001", "1", "12.5", "-30.0", "5.0"], out SpawnCreatureVerbRule.Result result, out _);

        Assert.True(ok);
        Assert.True(result.IsExplicitPosition);
        Assert.Equal(12.5f, result.X);
        Assert.Equal(-30.0f, result.Y);
        Assert.Equal(5.0f, result.Z);
    }

    [Fact]
    public void NoArgumentsIsInvalid()
    {
        bool ok = SpawnCreatureVerbRule.TryParse([], out _, out string error);

        Assert.False(ok);
        Assert.Equal(SpawnCreatureVerbRule.Usage, error);
    }

    [Fact]
    public void ThreeTrailingTokensWithoutAnIntegerCount_IsRejectedAtTheCountCheck()
    {
        // Three bare numbers after the template would read as "x y z" if count were optional -
        // this rule always treats the first trailing token as count instead (see
        // SpawnCreatureVerbRule's own doc comment for why), so "12.5" here is rejected as a bad
        // count rather than silently accepted as an x-coordinate.
        bool ok = SpawnCreatureVerbRule.TryParse(["g_vulkar001", "12.5", "-30.0", "5.0"], out _, out string error);

        Assert.False(ok);
        Assert.Contains("spawn count", error);
    }

    [Fact]
    public void TooManyTrailingTokensIsInvalid()
    {
        bool ok = SpawnCreatureVerbRule.TryParse(
            ["g_vulkar001", "1", "12.5", "-30.0", "5.0", "extra"], out _, out string error);

        Assert.False(ok);
        Assert.Equal(SpawnCreatureVerbRule.Usage, error);
    }

    [Fact]
    public void BadCoordinatePairIsInvalid()
    {
        bool ok = SpawnCreatureVerbRule.TryParse(
            ["g_vulkar001", "1", "notanumber", "-30.0"], out _, out string error);

        Assert.False(ok);
        Assert.Contains("coordinate pair", error);
    }

    [Fact]
    public void BadHeightIsInvalid()
    {
        bool ok = SpawnCreatureVerbRule.TryParse(
            ["g_vulkar001", "1", "12.5", "-30.0", "notaheight"], out _, out string error);

        Assert.False(ok);
        Assert.Contains("height", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("bad!name")]
    [InlineData("this_template_resref_is_far_too_long")]
    public void InvalidTemplateShapeIsRejected(string template)
    {
        bool ok = SpawnCreatureVerbRule.TryParse([template], out _, out string error);

        Assert.False(ok);
        Assert.Contains("template resref", error);
    }

    [Fact]
    public void MaxLengthTemplateIsAccepted()
    {
        string template = new('a', SpawnCreatureVerbRule.MaxTemplateLength);

        bool ok = SpawnCreatureVerbRule.TryParse([template], out SpawnCreatureVerbRule.Result result, out _);

        Assert.True(ok);
        Assert.Equal(template, result.Template);
    }

    [Fact]
    public void UnderscoresAndDigitsAreAllowedInATemplate()
    {
        bool ok = SpawnCreatureVerbRule.TryParse(["n_sithsoldier001"], out SpawnCreatureVerbRule.Result result, out _);

        Assert.True(ok);
        Assert.Equal("n_sithsoldier001", result.Template);
    }
}
