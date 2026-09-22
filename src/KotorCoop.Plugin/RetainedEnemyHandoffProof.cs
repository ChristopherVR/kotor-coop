using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>Pure predicate shared by the party-side commit and enemy-side alias revocation.
/// The owner row must be a current hostile enemy on Lehon, and both machines must show the
/// same local/host roster slot dismissed before any native handoff is authorized.</summary>
internal static class RetainedEnemyHandoffProof
{
    public static bool IsCurrentActive(
        bool lehonModule,
        int rosterSlot,
        uint hostRosterId,
        uint localId,
        uint sharedLocalRosterId,
        uint engineLocalRosterId,
        uint hostAvailable,
        uint hostParty,
        uint localAvailable,
        uint localParty,
        bool currentHostEnemyRow,
        int localFaction,
        int playerFaction)
    {
        if (!lehonModule
            || rosterSlot is not (4 or 5)
            || !PartyCensusRule.IsRosterId(hostRosterId)
            || !PartyCensusRule.IsRosterId(localId)
            || sharedLocalRosterId != localId
            || engineLocalRosterId != localId
            || currentHostEnemyRow
            || localFaction != playerFaction)
        {
            return false;
        }

        uint bit = 1u << rosterSlot;
        return (hostAvailable & bit) != 0
            && (hostParty & bit) != 0
            && (localAvailable & bit) != 0
            && (localParty & bit) != 0;
    }

    public static bool IsCurrent(
        bool lehonModule,
        int rosterSlot,
        uint ownerId,
        uint localId,
        uint hostRosterId,
        uint sharedLocalRosterId,
        uint engineLocalRosterId,
        uint hostAvailable,
        uint hostParty,
        uint localAvailable,
        uint localParty,
        bool currentHostEnemyRow,
        int hostFaction,
        int localFaction,
        bool requireHostileLocalFaction)
    {
        if (!lehonModule
            || rosterSlot is not (4 or 5)
            || !PartyCensusRule.IsRosterId(ownerId)
            || !PartyCensusRule.IsRosterId(localId)
            || hostRosterId != ownerId
            || sharedLocalRosterId != localId
            || engineLocalRosterId != localId
            || !currentHostEnemyRow
            || hostFaction != DarkSideHostileCompanionRule.HostileFaction
            || (requireHostileLocalFaction
                && localFaction != DarkSideHostileCompanionRule.HostileFaction))
        {
            return false;
        }

        uint bit = 1u << rosterSlot;
        return (hostAvailable & bit) == 0
            && (hostParty & bit) == 0
            && (localAvailable & bit) == 0
            && (localParty & bit) == 0;
    }
}
