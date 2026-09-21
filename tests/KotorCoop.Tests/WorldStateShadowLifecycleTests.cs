using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

public sealed class WorldStateShadowLifecycleTests
{
    [Fact]
    public void AReloadWithTheSameGlobalsCatalogueRetainsTheOldShadowForTheFirstDeltaScan()
    {
        // TryResolveGlobals runs again after a load.  The table catalogue is still the same
        // table shape, but the live values underneath it may have changed true -> false.  A
        // fresh shadow here would make CollectChangedWorldState report nothing and the host's
        // restored save would be overwritten by its stale session value.
        Assert.True(KotorGameBridge.RetainWorldStateShadow(
            hadShadow: true, previousCatalogueDigest: 0x1234, catalogueDigest: 0x1234));
        Assert.False(KotorGameBridge.RetainWorldStateShadow(
            hadShadow: false, previousCatalogueDigest: 0x1234, catalogueDigest: 0x1234));
        Assert.False(KotorGameBridge.RetainWorldStateShadow(
            hadShadow: true, previousCatalogueDigest: 0x1234, catalogueDigest: 0x5678));
    }
}
