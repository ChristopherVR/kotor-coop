using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PartnerHealMirrorRuleTests
{
    [Theory]
    [InlineData(10u)]
    [InlineData(28u)]
    public void LocalHealOrCureOnPartyBodyIsEligible(uint spellId)
    {
        Assert.True(PartnerHealMirrorRule.ShouldMirror(
            engineType: 39,
            creatorId: 0x7FFFFFFF,
            localPlayerId: 0x7FFFFFFF,
            spellId: spellId,
            targetId: 0x41,
            localTargets: new uint[] { 0x7FFFFFFF, 0x41 },
            targetIsWatchedPhantom: false,
            magnitude: 30));
    }

    [Theory]
    [InlineData(38u)]
    [InlineData(39u)]
    [InlineData(0u)]
    public void CreatorAndEngineTypeAreRequired(uint creatorId)
    {
        Assert.False(PartnerHealMirrorRule.ShouldMirror(
            engineType: 39,
            creatorId: creatorId,
            localPlayerId: 0x7FFFFFFF,
            spellId: 28,
            targetId: 0x41,
            localTargets: new uint[] { 0x41 },
            targetIsWatchedPhantom: false,
            magnitude: 30));

        Assert.False(PartnerHealMirrorRule.ShouldMirror(
            engineType: 38,
            creatorId: 0x7FFFFFFF,
            localPlayerId: 0x7FFFFFFF,
            spellId: 28,
            targetId: 0x41,
            localTargets: new uint[] { 0x41 },
            targetIsWatchedPhantom: false,
            magnitude: 30));
    }

    [Fact]
    public void AStandInOrNonPartyTargetIsNeverMirrored()
    {
        Assert.False(PartnerHealMirrorRule.ShouldMirror(
            39, 0x7FFFFFFF, 0x7FFFFFFF, 28, 0x115,
            new uint[] { 0x41 }, targetIsWatchedPhantom: true, magnitude: 30));
        Assert.False(PartnerHealMirrorRule.ShouldMirror(
            39, 0x7FFFFFFF, 0x7FFFFFFF, 28, 0x99,
            new uint[] { 0x41 }, targetIsWatchedPhantom: false, magnitude: 30));
    }

    [Fact]
    public void UnsupportedSpellAndZeroMagnitudeAreRefused()
    {
        Assert.False(PartnerHealMirrorRule.IsSupportedSpell(129));
        Assert.False(PartnerHealMirrorRule.ShouldMirror(
            39, 0x7FFFFFFF, 0x7FFFFFFF, 28, 0x41,
            new uint[] { 0x41 }, targetIsWatchedPhantom: false, magnitude: 0));
    }

    [Theory]
    [InlineData(15f, false)]
    [InlineData(15.01f, false)]
    public void PartnerRangeUsesTheRetailStrictBoundary(float x, bool expected)
    {
        Vec3 caster = new() { X = 0, Y = 0, Z = 0 };
        Vec3 phantom = new() { X = x, Y = 0, Z = 0 };
        Assert.Equal(expected, PartnerHealMirrorRule.IsWithinPartnerRange(caster, phantom));
    }

    [Fact]
    public void OneCastClaimsEachPhantomOnceAcrossRosterEffects()
    {
        PartnerHealDedupRule dedup = new(2);

        Assert.True(dedup.TryClaim(0, generation: 1, spellId: 28, phantomId: 0x115));
        Assert.False(dedup.TryClaim(0, generation: 1, spellId: 28, phantomId: 0x115));
        Assert.True(dedup.TryClaim(1, generation: 1, spellId: 28, phantomId: 0x116));
    }

    [Fact]
    public void ANewCastOrReusedSlotGetsAFreshClaim()
    {
        PartnerHealDedupRule dedup = new(1);

        Assert.True(dedup.TryClaim(0, 1, 28, 0x115));
        Assert.True(dedup.TryClaim(0, 2, 28, 0x115));
        Assert.True(dedup.TryClaim(0, 2, 10, 0x115));
        Assert.True(dedup.TryClaim(0, 2, 28, 0x117));
        Assert.False(dedup.TryClaim(0, 2, 28, 0x117));
    }
}
