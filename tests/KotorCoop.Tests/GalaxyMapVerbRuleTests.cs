using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The `galaxymap open|close|stats` console verb's pure parse (Tier 2 item 18 task 2's own
/// "Live test plan" follow-up, docs/research/galaxy-map-and-party-select.md's 2026-09-03
/// addendum). See <see cref="GalaxyMapVerbRule"/>'s own doc comment for why this exists as a
/// standalone rule rather than an inline check the way `partyselect ok` does it.
/// </summary>
public class GalaxyMapVerbRuleTests
{
    [Theory]
    [InlineData("open", GalaxyMapVerbRule.Verb.Open)]
    [InlineData("OPEN", GalaxyMapVerbRule.Verb.Open)]
    [InlineData("close", GalaxyMapVerbRule.Verb.Close)]
    [InlineData("Close", GalaxyMapVerbRule.Verb.Close)]
    [InlineData("stats", GalaxyMapVerbRule.Verb.Stats)]
    [InlineData("STATS", GalaxyMapVerbRule.Verb.Stats)]
    public void RecognisesEachSubcommandCaseInsensitively(string token, GalaxyMapVerbRule.Verb expected)
    {
        Assert.Equal(expected, GalaxyMapVerbRule.Parse([token]));
    }

    [Fact]
    public void NoArgumentsIsInvalid()
    {
        Assert.Equal(GalaxyMapVerbRule.Verb.Invalid, GalaxyMapVerbRule.Parse([]));
    }

    [Fact]
    public void MoreThanOneArgumentIsInvalid()
    {
        Assert.Equal(GalaxyMapVerbRule.Verb.Invalid, GalaxyMapVerbRule.Parse(["open", "now"]));
    }

    [Fact]
    public void AnUnrecognisedSubcommandIsInvalid()
    {
        Assert.Equal(GalaxyMapVerbRule.Verb.Invalid, GalaxyMapVerbRule.Parse(["confirm"]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(15)]
    public void SelectParsesThePlanetIndex(int index)
    {
        Assert.Equal(
            GalaxyMapVerbRule.Verb.Select,
            GalaxyMapVerbRule.Parse(["select", index.ToString()], out int planet));
        Assert.Equal(index, planet);
    }

    [Fact]
    public void SelectIsCaseInsensitive()
    {
        Assert.Equal(GalaxyMapVerbRule.Verb.Select, GalaxyMapVerbRule.Parse(["SELECT", "0"], out int planet));
        Assert.Equal(0, planet);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16)]
    public void SelectRefusesAnOutOfRangeIndex(int outOfRange)
    {
        GalaxyMapVerbRule.Verb result = GalaxyMapVerbRule.Parse(["select", outOfRange.ToString()], out int planet);
        Assert.Equal(GalaxyMapVerbRule.Verb.Invalid, result);
        Assert.Equal(-1, planet);
    }

    [Fact]
    public void SelectRefusesANonNumericIndex()
    {
        Assert.Equal(GalaxyMapVerbRule.Verb.Invalid, GalaxyMapVerbRule.Parse(["select", "danm13"], out _));
    }

    [Fact]
    public void SelectWithNoIndexIsInvalid()
    {
        Assert.Equal(GalaxyMapVerbRule.Verb.Invalid, GalaxyMapVerbRule.Parse(["select"], out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(15)]
    public void UnlockParsesThePlanetIndex(int index)
    {
        Assert.Equal(
            GalaxyMapVerbRule.Verb.Unlock,
            GalaxyMapVerbRule.Parse(["unlock", index.ToString()], out int planet));
        Assert.Equal(index, planet);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("16")]
    [InlineData("tat")]
    public void UnlockRefusesABadIndex(string bad)
    {
        Assert.Equal(GalaxyMapVerbRule.Verb.Invalid, GalaxyMapVerbRule.Parse(["unlock", bad], out int planet));
        Assert.Equal(-1, planet);
    }

    [Fact]
    public void UnlockWithNoIndexIsInvalid()
    {
        Assert.Equal(GalaxyMapVerbRule.Verb.Invalid, GalaxyMapVerbRule.Parse(["unlock"], out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(15)]
    public void DepartParsesThePlanetIndex(int index)
    {
        Assert.Equal(
            GalaxyMapVerbRule.Verb.Depart,
            GalaxyMapVerbRule.Parse(["depart", index.ToString()], out int planet));
        Assert.Equal(index, planet);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("16")]
    [InlineData("tatooine")]
    public void DepartRefusesABadIndex(string bad)
    {
        Assert.Equal(GalaxyMapVerbRule.Verb.Invalid, GalaxyMapVerbRule.Parse(["DEPART", bad], out int planet));
        Assert.Equal(-1, planet);
    }

    [Fact]
    public void AnUnknownTwoTokenSubcommandWithAValidIndexIsStillInvalid()
    {
        // The index parse is shared between select and unlock; the verb name must still gate.
        Assert.Equal(GalaxyMapVerbRule.Verb.Invalid, GalaxyMapVerbRule.Parse(["lock", "3"], out int planet));
        Assert.Equal(-1, planet);
    }

    [Fact]
    public void TheSingleArgumentOverloadDiscardsThePlanetIndex()
    {
        // The pre-existing one-argument Parse must keep working unchanged for callers (and
        // tests) that never cared about Select's own index - it delegates to the new
        // two-argument overload and simply discards the out parameter.
        Assert.Equal(GalaxyMapVerbRule.Verb.Select, GalaxyMapVerbRule.Parse(["select", "3"]));
    }
}
