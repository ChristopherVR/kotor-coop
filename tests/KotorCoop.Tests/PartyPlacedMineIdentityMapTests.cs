using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Party-placed mines use dynamic ids local to each engine. These tests cover only the safe
/// serial-to-local identity boundary; native creation and transport remain separately gated.
/// </summary>
public sealed class PartyPlacedMineIdentityMapTests
{
    private static readonly PartyPlacedMineScope FirstScope = new(SessionGeneration: 1, WorldEpoch: 10);

    [Fact]
    public void SpawnSerialResolvesBothDirectionsWithinItsLocalScope()
    {
        PartyPlacedMineIdentityMap map = new();
        Assert.True(map.BeginScope(FirstScope));

        Assert.True(map.Register(FirstScope, serial: 42, localObjectId: 0x7F00001A));
        Assert.True(map.TryGetLocalObjectId(FirstScope, 42, out uint localObjectId));
        Assert.Equal(0x7F00001Au, localObjectId);
        Assert.True(map.TryGetSerial(FirstScope, localObjectId, out ulong serial));
        Assert.Equal(42ul, serial);
    }

    [Fact]
    public void SameAuthoritySerialCanMapToDifferentIdsInSeparateEngines()
    {
        PartyPlacedMineIdentityMap host = new();
        PartyPlacedMineIdentityMap client = new();
        Assert.True(host.BeginScope(FirstScope));
        Assert.True(client.BeginScope(FirstScope));

        Assert.True(host.Register(FirstScope, serial: 42, localObjectId: 0x7F00001A));
        Assert.True(client.Register(FirstScope, serial: 42, localObjectId: 0x7F000003));

        Assert.True(host.TryGetLocalObjectId(FirstScope, 42, out uint hostId));
        Assert.True(client.TryGetLocalObjectId(FirstScope, 42, out uint clientId));
        Assert.Equal(0x7F00001Au, hostId);
        Assert.Equal(0x7F000003u, clientId);
    }

    [Fact]
    public void SameRegistrationIsIdempotentButConflictingOwnershipIsRefused()
    {
        PartyPlacedMineIdentityMap map = new();
        Assert.True(map.BeginScope(FirstScope));
        Assert.True(map.Register(FirstScope, serial: 42, localObjectId: 0x7F00001A));

        Assert.True(map.Register(FirstScope, serial: 42, localObjectId: 0x7F00001A));
        Assert.False(map.Register(FirstScope, serial: 42, localObjectId: 0x7F00001B));
        Assert.False(map.Register(FirstScope, serial: 43, localObjectId: 0x7F00001A));

        Assert.True(map.TryGetLocalObjectId(FirstScope, 42, out uint localObjectId));
        Assert.Equal(0x7F00001Au, localObjectId);
        Assert.False(map.TryGetLocalObjectId(FirstScope, 43, out _));
        Assert.False(map.TryGetSerial(FirstScope, 0x7F00001B, out _));
    }

    [Theory]
    [InlineData(2, 10)]
    [InlineData(1, 11)]
    public void NewSessionOrWorldEpochClearsMappingsAndRejectsOldEvents(ulong sessionGeneration, ulong worldEpoch)
    {
        PartyPlacedMineIdentityMap map = new();
        Assert.True(map.BeginScope(FirstScope));
        Assert.True(map.Register(FirstScope, serial: 42, localObjectId: 0x7F00001A));
        PartyPlacedMineScope nextScope = new(sessionGeneration, worldEpoch);

        Assert.True(map.BeginScope(nextScope));
        Assert.False(map.TryGetLocalObjectId(FirstScope, 42, out _));
        Assert.False(map.TryGetSerial(FirstScope, 0x7F00001A, out _));
        Assert.False(map.Register(FirstScope, serial: 42, localObjectId: 0x7F00001B));
        Assert.False(map.Retire(FirstScope, 42));

        Assert.True(map.Register(nextScope, serial: 42, localObjectId: 0x7F00002A));
        Assert.True(map.TryGetLocalObjectId(nextScope, 42, out uint localObjectId));
        Assert.Equal(0x7F00002Au, localObjectId);
    }

    [Fact]
    public void RetiringOneMineAllowsItsLocalIdToBeReusedWithoutStaleReverseMapping()
    {
        PartyPlacedMineIdentityMap map = new();
        Assert.True(map.BeginScope(FirstScope));
        Assert.True(map.Register(FirstScope, serial: 42, localObjectId: 0x7F00001A));
        Assert.True(map.Retire(FirstScope, 42));

        Assert.False(map.TryGetLocalObjectId(FirstScope, 42, out _));
        Assert.False(map.TryGetSerial(FirstScope, 0x7F00001A, out _));
        Assert.False(map.Retire(FirstScope, 42));

        Assert.True(map.Register(FirstScope, serial: 43, localObjectId: 0x7F00001A));
        Assert.True(map.TryGetSerial(FirstScope, 0x7F00001A, out ulong serial));
        Assert.Equal(43ul, serial);
    }

    [Fact]
    public void RetiredSerialCannotResurrectAndStaleRetirementCannotRemoveReplacement()
    {
        PartyPlacedMineIdentityMap map = new();
        Assert.True(map.BeginScope(FirstScope));
        Assert.True(map.Register(FirstScope, serial: 42, localObjectId: 0x7F00001A));
        Assert.True(map.Retire(FirstScope, 42));

        // The native allocator may reuse its local id, but the authority serial must advance.
        Assert.False(map.Register(FirstScope, serial: 42, localObjectId: 0x7F00001A));
        Assert.True(map.Register(FirstScope, serial: 43, localObjectId: 0x7F00001A));

        Assert.False(map.Retire(FirstScope, 42));
        Assert.True(map.TryGetLocalObjectId(FirstScope, 43, out uint localObjectId));
        Assert.Equal(0x7F00001Au, localObjectId);
        Assert.True(map.TryGetSerial(FirstScope, localObjectId, out ulong serial));
        Assert.Equal(43ul, serial);
    }

    [Fact]
    public void ActiveMapUsesTheVerifiedFifteenMineCapacityAndRetirementFreesASlot()
    {
        PartyPlacedMineIdentityMap map = new();
        Assert.True(map.BeginScope(FirstScope));

        for (ulong serial = 1; serial <= PartyPlacedMineIdentityMap.MaxActiveMines; serial++)
        {
            Assert.True(map.Register(FirstScope, serial, localObjectId: 0x7F000000u + (uint)serial));
        }

        Assert.False(map.Register(FirstScope, serial: 16, localObjectId: 0x7F000020));
        Assert.True(map.Retire(FirstScope, serial: 5));
        Assert.True(map.Register(FirstScope, serial: 16, localObjectId: 0x7F000020));
        Assert.False(map.TryGetLocalObjectId(FirstScope, serial: 5, out _));
    }

    [Fact]
    public void InvalidScopeAndZeroIdentityValuesAreRejected()
    {
        PartyPlacedMineIdentityMap map = new();
        Assert.False(map.BeginScope(default));
        Assert.False(map.Register(default, serial: 1, localObjectId: 1));

        Assert.True(map.BeginScope(FirstScope));
        Assert.False(map.Register(FirstScope, serial: 0, localObjectId: 1));
        Assert.False(map.Register(FirstScope, serial: 1, localObjectId: 0));
        Assert.False(map.TryGetLocalObjectId(FirstScope, 0, out _));
        Assert.False(map.TryGetSerial(FirstScope, 0, out _));
    }

    [Fact]
    public void TeardownInvalidationDropsTheCurrentScopeAndAllMappings()
    {
        PartyPlacedMineIdentityMap map = new();
        Assert.True(map.BeginScope(FirstScope));
        Assert.True(map.Register(FirstScope, serial: 42, localObjectId: 0x7F00001A));

        map.Invalidate();

        Assert.False(map.Scope.IsValid);
        Assert.False(map.TryGetLocalObjectId(FirstScope, 42, out _));
        Assert.False(map.TryGetSerial(FirstScope, 0x7F00001A, out _));
        Assert.False(map.Register(FirstScope, serial: 43, localObjectId: 0x7F00001B));
    }
}
