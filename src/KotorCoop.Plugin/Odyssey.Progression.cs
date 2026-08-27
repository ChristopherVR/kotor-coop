// Odyssey.Progression.cs - a creature's own experience, read/write. Staging-cheat use only
// (CheatController.Progression.cs's GrantXp) — the companion_xp_sync channel itself never
// calls into the plugin for this field; it is read/written out-of-process by the agent
// (KotorGameBridge.Party.cs), exactly like the PC's own progression channel
// (docs/research/progression-sync.md). This file exists only so the GrantXp staging cheat can
// simulate a script-granted XP award IN-PROCESS, on whichever creature the cheat names.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>CSWSCreatureStats::experience — u32. VERIFIED (progression-sync.md,
    /// companion-state-sync.md §1.1/§1.4), reused verbatim here.</summary>
    private const int StatsExperience = 0x068;

    /// <summary>Reads a creature's current XP through <see cref="CreStats"/>. Returns 0 for a
    /// creature with no stats block (should not happen for a live <c>CSWSCreature</c>, but the
    /// read is guarded rather than trusted, matching every other Odyssey read's own contract).
    /// MAIN THREAD ONLY.</summary>
    public static uint GetExperience(nint creature)
    {
        nint stats = Deref(creature + CreStats);
        return stats == 0 ? 0 : *(uint*)(stats + StatsExperience);
    }

    /// <summary>
    /// Writes a creature's XP directly — a plain field poke, no derived state to rebuild
    /// afterward (unlike appearance/equipment; companion-state-sync.md §1.1 makes the same
    /// claim for the agent's own out-of-process write of this exact field). MAIN THREAD ONLY.
    /// </summary>
    public static void SetExperience(nint creature, uint experience)
    {
        nint stats = Deref(creature + CreStats);
        if (stats != 0)
        {
            *(uint*)(stats + StatsExperience) = experience;
        }
    }
}
