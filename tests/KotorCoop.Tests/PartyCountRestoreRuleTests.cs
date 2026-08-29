using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The rule behind the 2026-08-29 HOST warp crash
/// (<c>CSWGuiInGameEquip::UpdatePortraits+0x48E</c>, deterministic 6/6 on any relocating
/// <c>warp</c> — docs/research/transition-crashes.md's host-warp-crash section,
/// shared-inventory-v2.md §15.6).
///
/// Every case checks the same single property: <c>CSWParty+0x00</c> is an ARRAY BOUND, so the
/// restore may never write a value naming a member slot that does not currently resolve — no
/// matter what the party TABLE's own arithmetic says the population should be. The party table
/// survives a module change intact while <c>CSWParty</c>'s array does not, and that gap is the
/// whole crash: count 2 over an array whose member[1] is still the empty-slot sentinel, walked by
/// the incoming module's own GUI construction.
///
/// <see cref="PartyTogetherClampRuleTests"/> pins the same field's DOWNWARD write.
/// </summary>
public class PartyCountRestoreRuleTests
{
    private const int Capacity = 3;

    /// <summary>The crash, in one line: the table says two members, the engine has populated
    /// one, and the pre-fix code wrote 2 anyway.</summary>
    [Fact]
    public void ARestoreIsRefusedWhileTheIncomingModuleHasOnlyOneResolvableMember()
    {
        Assert.Equal(0, PartyCountRestoreRule.SafeRestoreCount(
            currentCount: 1, desiredCount: 2, resolvableMembers: 1, Capacity));
    }

    /// <summary>...and is allowed the moment the engine has genuinely populated the member the
    /// count would name. This is what makes the rule epoch-aware without a timer: the same call
    /// answers "not yet" and then "now" as the module finishes loading.</summary>
    [Fact]
    public void TheSameRestoreIsAllowedOnceThatMemberResolves()
    {
        Assert.Equal(2, PartyCountRestoreRule.SafeRestoreCount(
            currentCount: 1, desiredCount: 2, resolvableMembers: 2, Capacity));
    }

    /// <summary>An empty client party (nothing resolves at all — the first frames of a module
    /// load) can never be raised, whatever the table claims.</summary>
    [Fact]
    public void NothingResolvableMeansNoWriteAtAll()
    {
        Assert.Equal(0, PartyCountRestoreRule.SafeRestoreCount(
            currentCount: 0, desiredCount: 3, resolvableMembers: 0, Capacity));
    }

    /// <summary>The ceiling binds even when the table is asking for less than capacity: a
    /// desired 3 over a 2-deep resolvable run yields 2, never 3.</summary>
    [Fact]
    public void TheResolvableRunIsAHardCeilingNotAHint()
    {
        Assert.Equal(2, PartyCountRestoreRule.SafeRestoreCount(
            currentCount: 1, desiredCount: 3, resolvableMembers: 2, Capacity));
    }

    /// <summary>Round 101's actual purpose — a clamped-down count hiding a companion that IS
    /// present — still works exactly as before.</summary>
    [Fact]
    public void AClampedCountOverAFullyPopulatedArrayIsStillRestored()
    {
        Assert.Equal(3, PartyCountRestoreRule.SafeRestoreCount(
            currentCount: 1, desiredCount: 3, resolvableMembers: 3, Capacity));
    }

    /// <summary>Never DOWN: lowering this field is <c>party_together_fix</c>'s decision and its
    /// alone (<see cref="PartyTogetherClampRule"/>), and this path must not fight it.</summary>
    [Theory]
    [InlineData(3, 2, 3)]
    [InlineData(2, 2, 2)]
    [InlineData(3, 1, 3)]
    public void ARestoreNeverLowersTheCount(int currentCount, int desiredCount, int resolvableMembers)
    {
        Assert.Equal(0, PartyCountRestoreRule.SafeRestoreCount(
            currentCount, desiredCount, resolvableMembers, Capacity));
    }

    /// <summary>A count this code does not recognise as a count is not one it may write — the
    /// same "refuses to touch a struct it was not verified to understand" discipline
    /// <c>TryClampClientPartyCount</c> already applies to the other direction.</summary>
    [Theory]
    [InlineData(-1, 2, 2, Capacity)]
    [InlineData(1, -1, 2, Capacity)]
    [InlineData(1, 9, 9, Capacity)]
    [InlineData(1, 2, -1, Capacity)]
    [InlineData(1, 2, 2, 0)]
    public void NonsenseInputsWriteNothing(
        int currentCount, int desiredCount, int resolvableMembers, int capacity)
    {
        Assert.Equal(0, PartyCountRestoreRule.SafeRestoreCount(
            currentCount, desiredCount, resolvableMembers, capacity));
    }
}
