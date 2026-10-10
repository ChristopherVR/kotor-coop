using KotorCoop.Session.Pumps;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>A joiner holds a new conversation's claim for a moment so the host's copy of a world
/// scene wins the token (Star Forge, 2026-10-10).</summary>
public sealed class DialogueClaimGraceTests
{
    [Fact]
    public void TheHostClaimsAtOnce()
    {
        DialogueClaimGrace grace = new();
        Assert.True(grace.AllowClaim(isWorldAuthority: true, localActive: true, weHoldToken: false, nowMs: 100));
    }

    [Fact]
    public void AJoinerWaitsOutTheGraceBeforeClaiming()
    {
        DialogueClaimGrace grace = new();
        Assert.False(grace.AllowClaim(false, true, false, 1000));
        Assert.False(grace.AllowClaim(false, true, false, 1000 + DialogueClaimGrace.GraceMs - 1));
        Assert.True(grace.AllowClaim(false, true, false, 1000 + DialogueClaimGrace.GraceMs));
    }

    [Fact]
    public void AJoinerThatHoldsTheTokenKeepsClaimingIt()
    {
        DialogueClaimGrace grace = new();
        Assert.True(grace.AllowClaim(false, true, weHoldToken: true, nowMs: 5));
    }

    [Fact]
    public void ANewConversationStartsANewGrace()
    {
        DialogueClaimGrace grace = new();
        long g = DialogueClaimGrace.GraceMs;
        Assert.False(grace.AllowClaim(false, true, false, 0));
        Assert.True(grace.AllowClaim(false, true, false, g));

        Assert.False(grace.AllowClaim(false, false, false, g + 1000));     // it ended
        Assert.False(grace.AllowClaim(false, true, false, g + 2000));      // the next one waits again
        Assert.True(grace.AllowClaim(false, true, false, (2 * g) + 2000));
    }

    [Fact]
    public void AScriptForcedConversationWaitsLonger()
    {
        DialogueClaimGrace grace = new();
        Assert.False(grace.AllowClaim(false, true, false, 0, forced: true));
        Assert.False(grace.AllowClaim(false, true, false, 2000, forced: true)); // the host was 2 s behind
        Assert.True(grace.AllowClaim(false, true, false, DialogueClaimGrace.ForcedGraceMs, forced: true));
    }

    [Fact]
    public void NoConversationIsNeverAClaim()
    {
        DialogueClaimGrace grace = new();
        Assert.False(grace.AllowClaim(true, false, true, 0));
    }
}
