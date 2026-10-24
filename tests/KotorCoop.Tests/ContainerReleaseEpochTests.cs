using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ContainerReleaseEpochTests
{
    private const string Module = "end_m01aa";
    private const uint Chest = 0xD1;

    private static InteractionClaimPacket Claim(uint peer, bool active, uint serial = 0) =>
        new(peer, Module, string.Empty, InteractionKind.Container, active, Chest, serial);

    [Fact]
    public void ReorderedDuplicateCloseCannotReleaseAReopenedContainer()
    {
        InteractionLockStore store = new();
        Assert.True(store.Claim(Claim(2, true, 1), 1000));
        store.Claim(Claim(2, false, 1), 1010);
        Assert.True(store.Claim(Claim(2, true, 2), 1020));

        // The live failure: the old closed level keeps arriving while grant 2 is in
        // flight. An ordinary active refresh must not erase its reservation epoch either.
        foreach (long now in new[] { 1021L, 1022L, 1040L })
        {
            store.Claim(Claim(2, false, 1), now);
            store.Claim(Claim(2, true), now);
            Assert.False(store.Claim(Claim(1, true, 1), now));
            Assert.Equal(2u, store.HolderOfContainer(Module, Chest, now));
        }

        store.Claim(Claim(2, false, 2), 1050);
        Assert.True(store.Claim(Claim(1, true, 2), 1051));
    }

    [Fact]
    public void InitialZeroSerialCloseCannotCancelAnExplicitReservation()
    {
        InteractionLockStore store = new();
        store.Claim(Claim(2, true, 1), 1000);
        store.Claim(Claim(2, false), 1001);
        Assert.False(store.Claim(Claim(1, true, 1), 1002));
        store.Claim(Claim(2, false, 1), 1003);
        Assert.True(store.Claim(Claim(1, true, 2), 1004));
    }

    [Fact]
    public void ReentrantRequestAdvancesEpochAndDelayedOlderRequestCannotRollItBack()
    {
        InteractionLockStore store = new();
        Assert.True(store.Claim(Claim(2, true, 7), 1000));
        Assert.True(store.Claim(Claim(2, true, 8), 1001));
        Assert.False(store.Claim(Claim(2, true, 7), 1002));
        store.Claim(Claim(2, false, 7), 1003);
        Assert.Equal(2u, store.HolderOfContainer(Module, Chest, 1003));
        store.Claim(Claim(2, false, 8), 1004);
        Assert.Equal(0u, store.HolderOfContainer(Module, Chest, 1004));
    }

    [Fact]
    public void DeniedRequestCannotReleaseTheWinnersReservation()
    {
        InteractionLockStore store = new();
        Assert.True(store.Claim(Claim(2, true, 4), 1000));
        Assert.False(store.Claim(Claim(3, true, 4), 1001));
        store.Claim(Claim(3, false, 4), 1002);
        Assert.Equal(2u, store.HolderOfContainer(Module, Chest, 1002));
        store.Claim(Claim(2, false, 4), 1003);
        Assert.True(store.Claim(Claim(3, true, 5), 1004));
    }

    [Fact]
    public void AbandonedOrLostGrantExpiresDespiteDuplicateOldCloses()
    {
        InteractionLockStore store = new(leaseMs: 100);
        store.Claim(Claim(2, true, 2), 1000);
        store.Claim(Claim(2, false, 1), 1099);
        Assert.False(store.Claim(Claim(3, true, 1), 1100));
        Assert.True(store.Claim(Claim(3, true, 2), 1101));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(42u)]
    public void UnarbitratedClaimsStillCloseImmediately(uint closeSerial)
    {
        InteractionLockStore store = new();
        // Arbitration explicitly disabled: the ordinary level creates a zero-epoch lease.
        // Either a legacy or upgraded sender may close it.
        Assert.True(store.Claim(Claim(2, true), 1000));
        store.Claim(Claim(2, false, closeSerial), 1001);
        Assert.True(store.Claim(Claim(3, true, 1), 1002));
    }

    [Fact]
    public void WrappedRequestSerialStillProtectsItsOwnClose()
    {
        InteractionLockStore store = new();
        Assert.True(store.Claim(Claim(2, true, uint.MaxValue), 1000));
        Assert.True(store.Claim(Claim(2, true, 1), 1001));
        store.Claim(Claim(2, false, uint.MaxValue), 1002);
        Assert.Equal(2u, store.HolderOfContainer(Module, Chest, 1002));
        store.Claim(Claim(2, false, 1), 1003);
        Assert.Equal(0u, store.HolderOfContainer(Module, Chest, 1003));
    }

    [Fact]
    public void ReleaseEpochRemainsAssociatedWithItsModuleAndContainer()
    {
        ContainerClaimEpochs epochs = new();
        Assert.Equal(0u, epochs.ForRelease(Module, Chest));
        epochs.Record(Module, Chest, 1);
        epochs.Record(Module, Chest + 1, 2);
        epochs.Record("danm13", Chest, 3);
        Assert.Equal(1u, epochs.ForRelease("END_M01AA", Chest));
        Assert.Equal(2u, epochs.ForRelease(Module, Chest + 1));
        Assert.Equal(3u, epochs.ForRelease("danm13", Chest));
    }
}
