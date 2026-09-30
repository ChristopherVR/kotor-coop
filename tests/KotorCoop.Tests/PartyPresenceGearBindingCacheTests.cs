using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PartyPresenceGearBindingCacheTests
{
    [Fact]
    public void ProofGapAndInvalidBodyDoNotLoseSuccessfulEquipment()
    {
        var cache = new PartyPresenceGearBindingCache(4);
        Assert.True(cache.Observe(0, 12, 7, 0x1B4));
        // No Observe is made while publication is busy or proof is unavailable.
        Assert.False(cache.Observe(0, 99, 99, 0));
        Assert.False(cache.Observe(0, 99, 99, 0x7F000000));
        Assert.False(cache.Observe(0, 12, 7, 0x1B4));
    }

    [Theory]
    [InlineData(13u, 7u, 0x1B4u)]
    [InlineData(12u, 8u, 0x1B4u)]
    [InlineData(12u, 7u, 0x1B5u)]
    public void VerifiedReplacementResetsOnce(uint session, uint generation, uint body)
    {
        var cache = new PartyPresenceGearBindingCache(4);
        Assert.True(cache.Observe(0, 12, 7, 0x1B4));
        Assert.True(cache.Observe(0, session, generation, body));
        Assert.False(cache.Observe(0, session, generation, body));
        Assert.True(cache.Observe(1, session, generation, body));
        Assert.False(cache.Observe(0, session, generation, body));
    }

    [Fact]
    public void DisconnectOrBodyResolutionInvalidationForcesOneReapply()
    {
        var cache = new PartyPresenceGearBindingCache(4);
        Assert.True(cache.Observe(0, 12, 7, 0x1B4));
        cache.Forget(0);
        Assert.True(cache.Observe(0, 12, 7, 0x1B4));
        Assert.False(cache.Observe(0, 12, 7, 0x1B4));
    }
    [Fact]
    public void ReusedIdRequiresCurrentEngineBodyAndFreshGear()
    {
        Assert.False(PartyPresenceResolveCacheRule.CanTrustCurrentCreature(0x1000, 0x2000, 0x1B4, 0x1B4, true));
        Assert.False(PartyPresenceResolveCacheRule.CanTrustCurrentCreature(0x1000, 0, 0x1B4, 0x1B4, true));
        Assert.True(PartyPresenceResolveCacheRule.CanTrustCurrentCreature(0x2000, 0x2000, 0x1B4, 0x1B4, true));
        var cache = new PartyPresenceGearBindingCache(4);
        Assert.True(cache.Observe(0, 12, 7, 0x1B4));
        cache.Forget(0); // Resolve rejected the old pointer before accepting its replacement.
        Assert.True(cache.Observe(0, 12, 7, 0x1B4));
        Assert.False(cache.Observe(0, 12, 7, 0x1B4));
    }
}
