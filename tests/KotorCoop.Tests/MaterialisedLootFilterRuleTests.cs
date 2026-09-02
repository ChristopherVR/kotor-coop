using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure decision behind the 2026-09-02 fix to docs/research/enemy-materialisation.md §6's
/// automatic wire: whether a materialised-loot give is even worth queueing, BEFORE it costs a
/// round trip on the single-slot giveloot cheat channel. <see cref="MaterialisedLootFilterRule"/>
/// itself does nothing but a set lookup; what matters is the tri-state contract — a null set
/// (unsure) always sends, a real set (however it reads) is trusted by membership.
/// </summary>
public class MaterialisedLootFilterRuleTests
{
    [Fact]
    public void ANullSetIsUnsureAndAlwaysSends()
    {
        // No plugin attached, or a bridge that does not carry this channel at all - the ONLY
        // case that means "don't know" (see IEnemyBridge.ReadMaterialisedOwnerIds' own doc).
        // The plugin's own refusal remains the correct backstop here.
        Assert.True(MaterialisedLootFilterRule.ShouldQueue(0xE, null));
    }

    [Fact]
    public void AnEmptySetIsConfidentAndFiltersEverything()
    {
        // Unlike null, an EMPTY set is a genuine answer: the plugin read the table and found
        // nothing mapped. Every give must be filtered, not just some - this is the ordinary
        // steady state for a peer holding a real, GIT-identical creature.
        HashSet<uint> knownEmpty = [];

        Assert.False(MaterialisedLootFilterRule.ShouldQueue(0xE, knownEmpty));
    }

    [Fact]
    public void AMappedOwnerIdIsQueued()
    {
        HashSet<uint> mapped = [0xE, 0x35];

        Assert.True(MaterialisedLootFilterRule.ShouldQueue(0xE, mapped));
        Assert.True(MaterialisedLootFilterRule.ShouldQueue(0x35, mapped));
    }

    [Fact]
    public void AnUnmappedOwnerIdAgainstAKnownSetIsSkipped()
    {
        HashSet<uint> mapped = [0x35];

        // 0xE is not in the set - a real, if partial, answer - so this give is skipped rather
        // than spending a doomed giveloot round trip.
        Assert.False(MaterialisedLootFilterRule.ShouldQueue(0xE, mapped));
    }
}
