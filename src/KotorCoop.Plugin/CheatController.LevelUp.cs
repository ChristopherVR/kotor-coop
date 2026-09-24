namespace KotorCoop.Plugin;

// The `levelup`/`stats` dev verbs (docs/research/class-and-power-grants.md §9): make the local
// player a REAL Jedi through the engine's own AutoLevelup, then read back class levels, HP, FP
// and known powers so a live test (and the orchestrator watching the console) can confirm it.
// See Odyssey.LevelUp.cs for the full route and why levelup grants no new power knowledge on
// its own (that stays grantpower's job, effect-sync.md §14).
internal sealed unsafe partial class CheatController
{
    private void LevelUp(nint player, uint playerId)
    {
        int classArg = block.CheatAmount;
        int count = (int)block.CheatItemCharges;

        Odyssey.TryReadLocalClassList(out int countBefore, out int class0Before, out int level0Before,
            out int class1Before, out int level1Before);
        string before = $"{countBefore}:[{class0Before}@{level0Before},{class1Before}@{level1Before}]";

        bool ran = Odyssey.TryAutoLevelUp(
            playerId, classArg, count,
            out int levelsGranted, out int slot, out int classId, out int finalLevel,
            out string stopReason, out int failure);

        if (!ran)
        {
            string reason = failure switch
            {
                1 => "local player does not resolve",
                2 => "local player has no stats block",
                3 => "local player has no class at all (classes 0)",
                4 => $"'{classArg}' does not name the LAST class slot (slot {slot}, class {classId}) - "
                    + "AutoLevelup only ever levels that one (see 'addclass'/'stats')",
                5 => "level-up handler bytes do not match the verified build",
                _ => $"refused (failure {failure})",
            };

            PluginHost.Log($"levelup: class {classArg} count {count} refused - {reason} (classes {before})");
            Answer(SharedBlock.CheatStatusEngineRefused, $"levelup {classArg}: {reason} (classes {before})");
            return;
        }

        Odyssey.TryReadLocalClassList(out int countAfter, out int class0After, out int level0After,
            out int class1After, out int level1After);
        string after = $"{countAfter}:[{class0After}@{level0After},{class1After}@{level1After}]";

        string stop = stopReason.Length == 0 ? "" : $" ({stopReason})";
        PluginHost.Log($"levelup: slot {slot} class {classId} granted {levelsGranted}/{count} level(s), "
            + $"now level {finalLevel}{stop}; classes {before} -> {after}; "
            + $"hp {Odyssey.GetCurrentHitPoints(player)} fp {Odyssey.GetCurrentForcePoints(player)}");

        int status = levelsGranted > 0 ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusRefused;
        Answer(status, $"classes {before} -> {after}, granted {levelsGranted}/{count}{stop}");
    }

    private void Stats(nint player)
    {
        Odyssey.TryReadLocalClassList(out int count, out int class0, out int level0, out int class1, out int level1);

        short hp = Odyssey.GetCurrentHitPoints(player);
        int maxHp = Odyssey.GetEffectiveMaxHitPoints(player);
        short fp = Odyssey.GetCurrentForcePoints(player);
        short maxFp = Odyssey.GetMaxForcePoints(player);

        Span<int> known0 = stackalloc int[16];
        Span<int> known1 = stackalloc int[16];
        int read0 = 0, total0 = 0, read1 = 0, total1 = 0;

        if (count >= 1)
        {
            read0 = Odyssey.ReadKnownSpells(player, 0, known0, out total0);
        }

        if (count >= 2)
        {
            read1 = Odyssey.ReadKnownSpells(player, 1, known1, out total1);
        }

        string powers0 = read0 == 0 ? "-" : string.Join(",", known0[..read0].ToArray());
        string powers1 = read1 == 0 ? "-" : string.Join(",", known1[..read1].ToArray());

        PluginHost.Log($"stats: classes {count}:[{class0}@{level0},{class1}@{level1}] "
            + $"hp {hp}/{maxHp} fp {fp}/{maxFp} "
            + $"known0[{total0}]={powers0} known1[{total1}]={powers1}");

        // The console reply is one 64-byte line (SharedBlock.CheatDetailLength); the full
        // known-power lists for BOTH slots are always in the log above, matching the
        // `items`/`item` precedent (console-cheats.md) of "the rows are in the agent log, not
        // the reply". The reply shows only the LAST (most recently leveled) slot's powers.
        string lastPowers = count >= 2 ? powers1 : powers0;
        string more = read0 < total0 || read1 < total1 ? " +more(log)" : "";

        Answer(SharedBlock.CheatStatusApplied,
            $"cls {count}:[{class0}@{level0},{class1}@{level1}] hp {hp}/{maxHp} fp {fp}/{maxFp} pw:{lastPowers}{more}");
    }
}
