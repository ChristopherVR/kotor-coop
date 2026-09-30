using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>Capture safety for an explicit reload of the host's saved world.</summary>
public static class ManualWorldRefreshRule
{
    public static bool IsFreshSave(DateTime stampUtc, DateTime nowUtc) =>
        stampUtc != DateTime.MinValue && stampUtc <= nowUtc
        && nowUtc - stampUtc <= TimeSpan.FromSeconds(30);

    public static bool IsSafe(PlayerState state) =>
        !string.IsNullOrEmpty(state.Module) && !Pumps.ModuleNameGuard.IsFallbackModuleName(state.Module)
        && !state.IsAway && !state.IsInCombat && !state.IsInDialogue && !state.IsInMinigame
        && !state.IsDowned && !state.IsPartyWiped && !state.IsMenuGuarded;

    public static bool IsSameWorld(string moduleBefore, uint serialBefore, PlayerState state, uint serialNow) =>
        serialBefore == serialNow
        && string.Equals(moduleBefore, state.Module, StringComparison.OrdinalIgnoreCase)
        && IsSafe(state);
}
