namespace KotorCoop.Plugin;

/// <summary>What <see cref="CarriedPinReleaseController"/> should do this poll, decided by
/// <see cref="CarriedPinReleaseRule.Decide"/>.</summary>
public enum CarriedPinReleaseAction
{
    /// <summary>The world is not readable enough yet (no player creature, no module name) —
    /// spend one poll and try again.</summary>
    NotReady,

    /// <summary>The module we came up in pins the PC from its own OnEnter, so whatever the
    /// flag reads is the module's business — stop watching this load.</summary>
    LeaveModulePin,

    /// <summary>The player carries no pin — nothing to do for this load.</summary>
    NothingToRelease,

    /// <summary>The player came up pinned in a module that never pins: release it.</summary>
    Release,
}

/// <summary>
/// Pure decision half of <see cref="CarriedPinReleaseController"/>, split out so it can be
/// unit-tested without a live game (the same split <see cref="PostAdoptEquipRepairRule"/> uses).
/// </summary>
public static class CarriedPinReleaseRule
{
    /// <summary>
    /// Modules whose own area script pins the PC at 1 hit point from OnEnter, so a pin read at
    /// world-up there is the module's and must be left alone. Only the Endar Spire command deck
    /// does this at entry (`k_pend_area01`, docs/research/min-one-hitpoint.md §2); the Taris
    /// duel ring pins around each duel, never at entry, so it is deliberately NOT listed.
    /// </summary>
    public static readonly string[] ModulesThatPinAtEntry = ["end_m01aa"];

    /// <param name="playerResolved">Whether the engine's own player lookup returned a creature.</param>
    /// <param name="module">The module the world came up in, or empty while unreadable.</param>
    /// <param name="pinned">The player's current min-1-hp flag (meaningful only when
    /// <paramref name="playerResolved"/>).</param>
    public static CarriedPinReleaseAction Decide(bool playerResolved, string module, bool pinned)
    {
        if (!playerResolved || string.IsNullOrEmpty(module))
        {
            return CarriedPinReleaseAction.NotReady;
        }

        foreach (string pinning in ModulesThatPinAtEntry)
        {
            if (string.Equals(pinning, module, StringComparison.OrdinalIgnoreCase))
            {
                return CarriedPinReleaseAction.LeaveModulePin;
            }
        }

        return pinned ? CarriedPinReleaseAction.Release : CarriedPinReleaseAction.NothingToRelease;
    }
}
