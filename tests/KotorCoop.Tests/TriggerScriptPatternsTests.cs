using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The coop.ini allowlist gate for TriggerRelay, checked against real script resrefs pulled
/// from the early-game inventory in docs/research/trigger-relay.md — 69 story scripts the
/// default pattern must match and 14 non-story/no-op ones it must not.
/// </summary>
public class TriggerScriptPatternsTests
{
    [Theory]
    // Story cutscene scripts (69 of the 83 generic trigger placements across end_m01aa,
    // end_m01ab and the seven early Taris exteriors) — every one must match the default.
    [InlineData("k_pend_trig02")]
    [InlineData("k_pend_explode")]
    [InlineData("k_pend_bridge04")]
    [InlineData("k_pend_pctut")]
    [InlineData("k_pend_carth_t02")]
    [InlineData("k_ptar_homint_en")]
    [InlineData("k_ptar_npcext_en")]
    [InlineData("k_ptar_sithtalk")]
    [InlineData("k_ptar_transhelp")]
    public void TheDefaultPatternMatchesEveryStoryScript(string scriptName)
    {
        Assert.True(TriggerScriptPatterns.Matches(scriptName, TriggerScriptPatterns.Parse("k_p*_*")));
    }

    [Theory]
    // Non-story generic scripts and no-ops from the same inventory — none should match.
    [InlineData("k_trg_partyinit")]
    [InlineData("k_bant_trig")]
    [InlineData("k_trg_stealth")]
    [InlineData("")]
    public void TheDefaultPatternExcludesNonStoryScripts(string scriptName)
    {
        Assert.False(TriggerScriptPatterns.Matches(scriptName, TriggerScriptPatterns.Parse("k_p*_*")));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.True(TriggerScriptPatterns.Matches("K_PTAR_HOMINT_EN", ["k_p*_*"]));
    }

    [Fact]
    public void AnEmptyPatternListMatchesNothing()
    {
        Assert.False(TriggerScriptPatterns.Matches("k_ptar_homint_en", []));
    }

    [Fact]
    public void ANullOrEmptyScriptNameNeverMatches()
    {
        Assert.False(TriggerScriptPatterns.Matches(null, ["*"]));
        Assert.False(TriggerScriptPatterns.Matches("", ["*"]));
    }

    [Fact]
    public void APatternWithNoWildcardRequiresAnExactMatch()
    {
        Assert.True(TriggerScriptPatterns.Matches("k_bant_trig", ["k_bant_trig"]));
        Assert.False(TriggerScriptPatterns.Matches("k_bant_trig2", ["k_bant_trig"]));
    }

    [Fact]
    public void APlainStarMatchesEverything()
    {
        Assert.True(TriggerScriptPatterns.Matches("anything_at_all", ["*"]));
    }

    [Fact]
    public void MultipleWildcardsInOnePatternAllApply()
    {
        // k_p*_* requires "k_p", then any run, then a literal underscore, then any run.
        Assert.True(TriggerScriptPatterns.MatchesOne("k_ptar_homint_en", "k_p*_*"));
        // No underscore after "k_p" at all — the pattern's own required literal is missing.
        Assert.False(TriggerScriptPatterns.MatchesOne("k_pnounderscore", "k_p*_*"));
    }

    [Fact]
    public void ASecondPatternInTheListCanMatchWhenTheFirstDoesNot()
    {
        string[] patterns = TriggerScriptPatterns.Parse("k_p*_*, k_bant_trig");

        Assert.True(TriggerScriptPatterns.Matches("k_bant_trig", patterns));
        Assert.True(TriggerScriptPatterns.Matches("k_ptar_homint_en", patterns));
        Assert.False(TriggerScriptPatterns.Matches("k_trg_partyinit", patterns));
    }

    [Fact]
    public void ParseSplitsOnCommasAndTrimsWhitespace()
    {
        Assert.Equal(["k_p*_*", "k_bant_trig"], TriggerScriptPatterns.Parse(" k_p*_* , k_bant_trig "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseOfNothingProducesAnEmptyList(string? raw)
    {
        Assert.Empty(TriggerScriptPatterns.Parse(raw));
    }
}
