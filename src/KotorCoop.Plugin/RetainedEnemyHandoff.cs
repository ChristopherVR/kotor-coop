namespace KotorCoop.Plugin;

internal enum RetainedEnemyHandoffResult
{
    NoCurrentProof,
    FactionWriteFailed,
    MappingFailed,
    Committed,
}

/// <summary>Orders the campaign handoff's irreversible edges: fresh proof, hostile faction,
/// routable owner/local alias, and only then release of plugin-owned party protection.</summary>
internal static class RetainedEnemyHandoff
{
    public static RetainedEnemyHandoffResult TryCommit(
        bool currentProof,
        Func<bool> setHostileFaction,
        Func<bool> registerAlias,
        Action releaseOwnedPartyProtection)
    {
        if (!currentProof)
        {
            return RetainedEnemyHandoffResult.NoCurrentProof;
        }

        if (!setHostileFaction())
        {
            return RetainedEnemyHandoffResult.FactionWriteFailed;
        }

        if (!registerAlias())
        {
            return RetainedEnemyHandoffResult.MappingFailed;
        }

        releaseOwnedPartyProtection();
        return RetainedEnemyHandoffResult.Committed;
    }
}
