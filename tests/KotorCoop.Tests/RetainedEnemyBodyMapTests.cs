using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class RetainedEnemyBodyMapTests
{
    private const uint HostJolee = 0x120;
    private const uint LocalJolee = 0x245;

    [Fact]
    public void TranslationIsBoundToTheCurrentRosterSlotIdentity()
    {
        RetainedEnemyBodyMap map = new(4);
        Assert.True(map.Register(HostJolee, LocalJolee, rosterSlot: 4, now: 10));

        Assert.Equal(LocalJolee, map.ToLocal(HostJolee, _ => LocalJolee));
        Assert.Equal(HostJolee, map.ToOwner(LocalJolee, _ => LocalJolee));
        Assert.Equal(0u, map.ToLocal(HostJolee, _ => 0x246));
        Assert.Equal(0u, map.ToOwner(LocalJolee, _ => LocalJolee));
        Assert.Equal(0, map.Count);
    }

    [Fact]
    public void SweepPrunesReusedRosterSlotsWithoutClearingOtherRetainedBodies()
    {
        RetainedEnemyBodyMap map = new(4);
        Assert.True(map.Register(HostJolee, LocalJolee, rosterSlot: 4, now: 0));
        Assert.True(map.Register(0x121, 0x246, rosterSlot: 5, now: 0));

        Assert.Equal(1, map.PruneInvalidSlots(slot => slot == 4 ? 0x247u : 0x246u));
        Assert.Equal(1, map.Count);
        Assert.Equal(0u, map.ToLocal(HostJolee, slot => slot == 4 ? 0x247u : 0x246u));
        Assert.Equal(0x246u, map.ToLocal(0x121, slot => slot == 4 ? 0x247u : 0x246u));
    }

    [Fact]
    public void StaleSweepForgetsOnlyExpiredAliases()
    {
        RetainedEnemyBodyMap map = new(4);
        Assert.True(map.Register(HostJolee, LocalJolee, rosterSlot: 4, now: 1));

        map.Seen(HostJolee, now: 8);
        Assert.Equal(0, map.Sweep(now: 12, staleSeconds: 5));
        Assert.Equal(1, map.Count);
        Assert.Equal(1, map.Sweep(now: 13.1, staleSeconds: 5));
        Assert.Equal(0, map.Count);
    }

    [Fact]
    public void CurrentProofPruneImmediatelyDropsAliasesWhenEnemyRowOrRosterProofDisappears()
    {
        RetainedEnemyBodyMap map = new(4);
        Assert.True(map.Register(HostJolee, LocalJolee, rosterSlot: 4, now: 10));
        Assert.True(map.Register(0x121, 0x246, rosterSlot: 5, now: 10));

        Assert.Equal(1, map.PruneUnproven(entry => entry.OwnerId == HostJolee));
        Assert.Equal(1, map.Count);
        Assert.Equal(0u, map.ToOwner(0x246, slot => slot == 4 ? LocalJolee : 0x246));
        Assert.Equal(HostJolee, map.ToOwner(LocalJolee, _ => LocalJolee));
    }

    [Fact]
    public void RevokedOwnerAliasLeavesRawIdGuardUntilSessionRelease()
    {
        RetainedEnemyBodyMap map = new(4);
        Assert.True(map.Register(HostJolee, LocalJolee, rosterSlot: 4, now: 10));
        Assert.Equal(1, map.PruneUnproven(_ => false));

        Assert.True(map.IsOwnerTombstoned(HostJolee));
        Assert.False(map.TryGetOwnerEntry(HostJolee, out _));
        map.ReleaseSession();
        Assert.False(map.IsOwnerTombstoned(HostJolee));
    }

    [Fact]
    public void DisablingRoutingClearsAliasesButKeepsOwnerAndLocalGuards()
    {
        RetainedEnemyBodyMap map = new(4);
        Assert.True(map.Register(HostJolee, LocalJolee, rosterSlot: 4, now: 1));
        Assert.True(map.QuarantineLocal(LocalJolee, rosterSlot: 4));

        Assert.Equal(1, map.ClearAliasesKeepGuards());
        Assert.Equal(0, map.Count);
        Assert.True(map.IsOwnerTombstoned(HostJolee));
        Assert.True(map.IsQuarantinedLocal(LocalJolee));
    }

    [Fact]
    public void SessionReleaseDropsRetainedAliasesWithoutReportingDestroyableBodies()
    {
        RetainedEnemyBodyMap map = new(4);
        Assert.True(map.Register(HostJolee, LocalJolee, rosterSlot: 4, now: 0));

        Assert.Equal(1, map.ReleaseSession());
        Assert.Equal(0, map.Count);
        Assert.Equal(0u, map.ToOwner(LocalJolee, _ => LocalJolee));
    }

    [Fact]
    public void OwnerRemovalAndModuleInvalidationOnlyDropAliases()
    {
        RetainedEnemyBodyMap map = new(4);
        Assert.True(map.Register(HostJolee, LocalJolee, rosterSlot: 4, now: 0));

        map.ForgetOwner(HostJolee);
        Assert.Equal(0, map.Count);
        Assert.True(map.Register(HostJolee, LocalJolee, rosterSlot: 4, now: 0));

        map.Clear();
        Assert.Equal(0, map.Count);
    }

    [Fact]
    public void QuarantineBlocksLocalIdFallbackWithoutCreatingOwnerTranslation()
    {
        RetainedEnemyBodyMap map = new(4);
        Assert.True(map.QuarantineLocal(LocalJolee, rosterSlot: 4));

        Assert.True(map.IsQuarantinedLocal(LocalJolee));
        Assert.False(map.IsQuarantinedLocal(HostJolee));
        Assert.False(map.TryGetOwnerEntry(LocalJolee, out _));
        Assert.Equal(0, map.Count);

        map.Clear();
        Assert.False(map.IsQuarantinedLocal(LocalJolee));
    }

    [Fact]
    public void QuarantineTombstoneSurvivesRosterSlotReuseUntilModuleClear()
    {
        RetainedEnemyBodyMap map = new(4);
        Assert.True(map.QuarantineLocal(LocalJolee, rosterSlot: 4));
        Assert.Equal(0, map.PruneInvalidSlots(slot => slot == 4 ? 0x246u : 0u));

        Assert.True(map.IsQuarantinedLocal(LocalJolee));
        map.Clear();
        Assert.False(map.IsQuarantinedLocal(LocalJolee));
    }

    [Theory]
    [InlineData(0, 0, 4)]
    [InlineData(0x7F000000, 0x245, 4)]
    [InlineData(0x120, 0x7F000000, 4)]
    [InlineData(0x120, 0x245, 3)]
    [InlineData(0x120, 0x245, 6)]
    public void InvalidIdentityOrSlotIsRejected(uint owner, uint local, int slot)
    {
        RetainedEnemyBodyMap map = new(4);
        Assert.False(map.Register(owner, local, slot, now: 0));
        Assert.Equal(0, map.Count);
    }
}
