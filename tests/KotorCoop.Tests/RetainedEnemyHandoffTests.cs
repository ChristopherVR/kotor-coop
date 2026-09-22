using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class RetainedEnemyHandoffTests
{
    private const uint HostJolee = 0x120;
    private const uint LocalJolee = 0x245;

    [Fact]
    public void CurrentProofIsRevokedByMissingCensusThenCanBeReestablishedBeforeCommit()
    {
        RetainedEnemyBodyMap aliases = new(2);
        Assert.True(aliases.Register(HostJolee, LocalJolee, rosterSlot: 4, now: 1));
        bool enemyRowPresent = true;

        Assert.Equal(0, aliases.PruneUnproven(entry => HasCurrentProof(entry, enemyRowPresent)));
        enemyRowPresent = false; // zero census / owner row absent this frame
        Assert.Equal(1, aliases.PruneUnproven(entry => HasCurrentProof(entry, enemyRowPresent)));
        Assert.Equal(0, aliases.Count);

        enemyRowPresent = true;
        RetainedEnemyBody proofCandidate = new(HostJolee, LocalJolee, 4, LastSeen: 0);
        Assert.True(HasCurrentProof(proofCandidate, enemyRowPresent));
        bool partyProtectionReleased = false;
        RetainedEnemyHandoffResult result = RetainedEnemyHandoff.TryCommit(
            currentProof: HasCurrentProof(proofCandidate, enemyRowPresent),
            setHostileFaction: () => true,
            registerAlias: () => aliases.Register(HostJolee, LocalJolee, 4, now: 2),
            releaseOwnedPartyProtection: () => partyProtectionReleased = true);

        Assert.Equal(RetainedEnemyHandoffResult.Committed, result);
        Assert.True(partyProtectionReleased);
        Assert.Equal(LocalJolee, aliases.ToLocal(HostJolee, _ => LocalJolee));
    }

    [Fact]
    public void CommittedAliasRemainsQuarantinedWhenCurrentProofIsRevokedBeforeNextSweep()
    {
        RetainedEnemyBodyMap aliases = new(2);
        Assert.True(aliases.Register(HostJolee, LocalJolee, rosterSlot: 4, now: 1));
        Assert.True(aliases.IsQuarantinedLocal(LocalJolee));

        // Models a host-row/roster update arriving after PartyPresence commits but before the
        // next EnemyPresence prune. The mapping is removed, while the hook still recognizes the
        // local id as a retained candidate and fails closed instead of forwarding its raw id.
        Assert.Equal(1, aliases.PruneUnproven(_ => false));
        Assert.Equal(0u, aliases.ToOwner(LocalJolee, _ => LocalJolee));
        Assert.True(aliases.IsQuarantinedLocal(LocalJolee));
    }

    [Fact]
    public void QuarantineReleasesOnlyAfterBothRostersProveSameBodyActiveAndPlayerFaction()
    {
        RetainedEnemyBodyMap aliases = new(2);
        Assert.True(aliases.Register(HostJolee, LocalJolee, rosterSlot: 4, now: 1));
        aliases.PruneUnproven(_ => false);

        uint activeSlot = 1u << 4;
        bool active = RetainedEnemyHandoffProof.IsCurrentActive(
            lehonModule: true,
            rosterSlot: 4,
            hostRosterId: HostJolee,
            localId: LocalJolee,
            sharedLocalRosterId: LocalJolee,
            engineLocalRosterId: LocalJolee,
            hostAvailable: activeSlot,
            hostParty: activeSlot,
            localAvailable: activeSlot,
            localParty: activeSlot,
            currentHostEnemyRow: false,
            localFaction: 0,
            playerFaction: 0);

        Assert.True(active);
        Assert.True(aliases.ReleaseQuarantineIfActive(LocalJolee, 4, active));
        Assert.False(aliases.IsQuarantinedLocal(LocalJolee));
    }

    [Theory]
    [InlineData(false, true, true, true, false, 0)]
    [InlineData(true, false, true, true, false, 0)]
    [InlineData(true, true, false, true, false, 0)]
    [InlineData(true, true, true, false, false, 0)]
    [InlineData(true, true, true, true, true, 0)]
    [InlineData(true, true, true, true, false, 1)]
    public void IncompleteOrHostileActiveProofKeepsQuarantineClosed(
        bool hostAvailable,
        bool hostParty,
        bool localAvailable,
        bool localParty,
        bool currentHostEnemyRow,
        int localFaction)
    {
        RetainedEnemyBodyMap aliases = new(2);
        Assert.True(aliases.Register(HostJolee, LocalJolee, rosterSlot: 4, now: 1));
        aliases.PruneUnproven(_ => false);
        uint slot = 1u << 4;

        bool active = RetainedEnemyHandoffProof.IsCurrentActive(
            lehonModule: true,
            rosterSlot: 4,
            hostRosterId: HostJolee,
            localId: LocalJolee,
            sharedLocalRosterId: LocalJolee,
            engineLocalRosterId: LocalJolee,
            hostAvailable: hostAvailable ? slot : 0,
            hostParty: hostParty ? slot : 0,
            localAvailable: localAvailable ? slot : 0,
            localParty: localParty ? slot : 0,
            currentHostEnemyRow,
            localFaction,
            playerFaction: 0);

        Assert.False(active);
        Assert.False(aliases.ReleaseQuarantineIfActive(LocalJolee, 4, active));
        Assert.True(aliases.IsQuarantinedLocal(LocalJolee));
    }

    [Fact]
    public void MissingCurrentProofPerformsNoFactionMappingOrProtectionWrites()
    {
        bool faction = false, mapped = false, released = false;

        RetainedEnemyHandoffResult result = RetainedEnemyHandoff.TryCommit(
            currentProof: false,
            setHostileFaction: () => faction = true,
            registerAlias: () => mapped = true,
            releaseOwnedPartyProtection: () => released = true);

        Assert.Equal(RetainedEnemyHandoffResult.NoCurrentProof, result);
        Assert.False(faction);
        Assert.False(mapped);
        Assert.False(released);
    }

    [Fact]
    public void FailedFactionWriteDoesNotPublishDamageAliasOrReleasePartyProtection()
    {
        bool mapped = false, released = false;

        RetainedEnemyHandoffResult result = RetainedEnemyHandoff.TryCommit(
            currentProof: true,
            setHostileFaction: () => false,
            registerAlias: () => mapped = true,
            releaseOwnedPartyProtection: () => released = true);

        Assert.Equal(RetainedEnemyHandoffResult.FactionWriteFailed, result);
        Assert.False(mapped);
        Assert.False(released);
    }

    [Fact]
    public void ProtectionReleasesOnlyAfterFactionAndAliasCommit()
    {
        List<string> order = [];

        RetainedEnemyHandoffResult result = RetainedEnemyHandoff.TryCommit(
            currentProof: true,
            setHostileFaction: () => { order.Add("faction"); return true; },
            registerAlias: () => { order.Add("mapping"); return true; },
            releaseOwnedPartyProtection: () => order.Add("release"));

        Assert.Equal(RetainedEnemyHandoffResult.Committed, result);
        Assert.Equal(new[] { "faction", "mapping", "release" }, order);
    }

    [Fact]
    public void FailedAliasRegistrationKeepsPartyProtectionInPlace()
    {
        bool released = false;

        RetainedEnemyHandoffResult result = RetainedEnemyHandoff.TryCommit(
            currentProof: true,
            setHostileFaction: () => true,
            registerAlias: () => false,
            releaseOwnedPartyProtection: () => released = true);

        Assert.Equal(RetainedEnemyHandoffResult.MappingFailed, result);
        Assert.False(released);
    }

    private static bool HasCurrentProof(RetainedEnemyBody entry, bool enemyRowPresent) =>
        RetainedEnemyHandoffProof.IsCurrent(
            lehonModule: true,
            entry.RosterSlot,
            entry.OwnerId,
            entry.LocalId,
            hostRosterId: entry.OwnerId,
            sharedLocalRosterId: entry.LocalId,
            engineLocalRosterId: entry.LocalId,
            hostAvailable: 0,
            hostParty: 0,
            localAvailable: 0,
            localParty: 0,
            currentHostEnemyRow: enemyRowPresent,
            hostFaction: 1,
            localFaction: 1,
            requireHostileLocalFaction: true);
}
