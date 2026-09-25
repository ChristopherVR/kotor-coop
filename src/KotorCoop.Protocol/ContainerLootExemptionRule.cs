namespace KotorCoop.Protocol;

/// <summary>
/// Containers the object-id-keyed container-loot relay must leave alone because each engine
/// fills them with its OWN party's items at runtime, so equal object ids on the two sides do
/// not name the same item.
///
/// The Leviathan party locker <c>lev40_ptylocker</c> (lev_m40aa) is filled by each engine's own
/// <c>k_plev_escsetup</c> run with that engine's stripped party gear, and retail returns the gear
/// only by the player looting it by hand (docs/research/leviathan.md §10: the only script that
/// could return it, <c>k_plev_killbc</c>, is unreachable). With the relay applied, the first
/// player to loot their own gear could delete the same-numbered items from the other player's
/// locker before they ever opened it.
/// </summary>
public static class ContainerLootExemptionRule
{
    private static readonly (string ModulePrefix, string Tag)[] PerEngineContainers =
    [
        ("lev_m40", "lev40_ptylocker"),
    ];

    public static bool IsExempt(string module, string? containerTag)
    {
        if (string.IsNullOrEmpty(module) || string.IsNullOrEmpty(containerTag))
        {
            return false;
        }

        foreach ((string modulePrefix, string tag) in PerEngineContainers)
        {
            if (module.StartsWith(modulePrefix, StringComparison.OrdinalIgnoreCase)
                && string.Equals(containerTag, tag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
