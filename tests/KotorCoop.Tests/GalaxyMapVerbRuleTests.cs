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
}
