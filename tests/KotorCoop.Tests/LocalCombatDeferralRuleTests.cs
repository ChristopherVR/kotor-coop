using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure set arithmetic behind item 14's "one fight at a time" surfacing
/// (docs/roadmap-seamless.md Tier 2): given every id that cleared
/// <see cref="LocalFightCandidateRule.Evaluate"/>'s gates this scan and whichever id currently
/// holds the single local-combat exclusion slot, which of them were deferred, and did that set
/// actually change since the last scan.
/// </summary>
public class LocalCombatDeferralRuleTests
{
    [Fact]
    public void ComputeDeferred_ExcludesTheHolder_KeepsEveryoneElse()
    {
        uint[] eligible = [0x10, 0x11, 0x12];
        uint[] deferred = new uint[8];

        int count = LocalCombatDeferralRule.ComputeDeferred(eligible, eligible.Length, holderOwnerId: 0x11, deferred);

        Assert.Equal(2, count);
        Assert.Equal(0x10u, deferred[0]);
        Assert.Equal(0x12u, deferred[1]);
    }

    [Fact]
    public void ComputeDeferred_NoHolder_EveryEligibleIdIsDeferred()
    {
        uint[] eligible = [0x10, 0x11];
        uint[] deferred = new uint[8];

        int count = LocalCombatDeferralRule.ComputeDeferred(eligible, eligible.Length, holderOwnerId: 0, deferred);

        Assert.Equal(2, count);
    }

    [Fact]
    public void ComputeDeferred_NothingEligible_IsZero()
    {
        uint[] eligible = new uint[8];
        uint[] deferred = new uint[8];

        int count = LocalCombatDeferralRule.ComputeDeferred(eligible, eligibleCount: 0, holderOwnerId: 0x11, deferred);

        Assert.Equal(0, count);
    }

    [Fact]
    public void ComputeDeferred_OnlyTheHolderWasEligible_DeferredIsEmpty()
    {
        // The common case: one hostile engaged, nothing else nearby yet.
        uint[] eligible = [0x11];
        uint[] deferred = new uint[8];

        int count = LocalCombatDeferralRule.ComputeDeferred(eligible, eligible.Length, holderOwnerId: 0x11, deferred);

        Assert.Equal(0, count);
    }

    [Fact]
    public void ComputeDeferred_NeverOverflowsTheCallersBuffer()
    {
        uint[] eligible = [0x1, 0x2, 0x3, 0x4, 0x5];
        uint[] deferred = new uint[2];

        int count = LocalCombatDeferralRule.ComputeDeferred(eligible, eligible.Length, holderOwnerId: 0, deferred);

        Assert.Equal(2, count);
    }

    [Fact]
    public void HasChanged_SameSetDifferentOrder_IsFalse()
    {
        // The census re-sorts every publish — enumeration order is not stable frame to frame
        // even when the exact same creatures are eligible.
        uint[] previous = [0x10, 0x11, 0x12];
        uint[] current = [0x12, 0x10, 0x11];

        Assert.False(LocalCombatDeferralRule.HasChanged(previous, 3, current, 3));
    }

    [Fact]
    public void HasChanged_DifferentCount_IsTrue()
    {
        uint[] previous = [0x10, 0x11];
        uint[] current = [0x10, 0x11, 0x12];

        Assert.True(LocalCombatDeferralRule.HasChanged(previous, 2, current, 3));
    }

    [Fact]
    public void HasChanged_SameCountDifferentMembers_IsTrue()
    {
        uint[] previous = [0x10, 0x11];
        uint[] current = [0x10, 0x12];

        Assert.True(LocalCombatDeferralRule.HasChanged(previous, 2, current, 2));
    }

    [Fact]
    public void HasChanged_BothEmpty_IsFalse()
    {
        uint[] previous = new uint[4];
        uint[] current = new uint[4];

        Assert.False(LocalCombatDeferralRule.HasChanged(previous, 0, current, 0));
    }
}
