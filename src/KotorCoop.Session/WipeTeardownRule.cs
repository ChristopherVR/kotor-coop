using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>Distinguishes a witnessed game-over teardown from a selected character load.</summary>
public static class WipeTeardownRule
{
    public static bool MaySuppressOwnLoad(bool wipeWitnessed, PlayerState? local,
        uint? previousRequestGeneration, uint? currentRequestGeneration,
        bool explicitLoadPending, bool protectedOwnLoad)
        => wipeWitnessed
            && (local is null || local.Value.IsAway || local.Value.Module.Length == 0)
            && previousRequestGeneration.HasValue
            && currentRequestGeneration.HasValue
            && previousRequestGeneration == currentRequestGeneration
            && !explicitLoadPending && !protectedOwnLoad;
}
