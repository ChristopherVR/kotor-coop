using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure text half of item 14's deferred-hostiles HUD line
/// (docs/roadmap-seamless.md Tier 2): what <c>local_combat_deferred_notice</c> shows for a given
/// deferred count.
/// </summary>
public class LocalCombatDeferredNoticeRuleTests
{
    [Fact]
    public void ZeroDeferred_ShowsNothing()
    {
        Assert.Null(LocalCombatDeferredNoticeRule.Text(0));
    }

    [Fact]
    public void NegativeDeferred_ShowsNothing()
    {
        // Defensive: the caller should never produce a negative count, but the text rule must
        // not crash or show a nonsensical line if it ever did.
        Assert.Null(LocalCombatDeferredNoticeRule.Text(-1));
    }

    [Fact]
    public void OneDeferred_UsesSingular()
    {
        Assert.Equal("1 more enemy engaged - mirrored", LocalCombatDeferredNoticeRule.Text(1));
    }

    [Fact]
    public void TwoDeferred_UsesPlural()
    {
        Assert.Equal("2 more enemies engaged - mirrored", LocalCombatDeferredNoticeRule.Text(2));
    }

    [Fact]
    public void ManyDeferred_UsesPlural()
    {
        Assert.Equal("5 more enemies engaged - mirrored", LocalCombatDeferredNoticeRule.Text(5));
    }
}
