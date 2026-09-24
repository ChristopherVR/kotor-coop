namespace KotorCoop.Plugin;

// The `mgkill` live-test primitive (docs/research/minigames.md): finishes a forced
// turret/gunnery minigame ambush (e.g. the Ebon Hawk Sith-fighter sequence, module m12ab) that
// the unattended rig cannot play by actually shooting down the enemy fighters, by running the
// engine's own SWMG_AdjustFollowerHitPoints VM routine (Odyssey.TryKillMiniGameFighter) against
// each currently-alive fighter, so the shipped OnDeath scripts (e.g. k_pebo_sthdeath2..7) fire
// exactly as they would for a real kill.
internal sealed unsafe partial class CheatController
{
    private void MgKill()
    {
        int requested = block.CheatAmount; // -1 = all, else a positive count

        Odyssey.MiniGameFighter[] fighters = Odyssey.ListMiniGameFighters();
        if (fighters.Length == 0)
        {
            Answer(
                SharedBlock.CheatStatusNoTarget,
                "mgkill: no minigame enemy fighters found - not in a turret/gunnery minigame "
                + "with live fighters right now?");
            return;
        }

        List<Odyssey.MiniGameFighter> alive = [];
        int deadAlready = 0;
        foreach (Odyssey.MiniGameFighter fighter in fighters)
        {
            if (fighter.Dead)
            {
                deadAlready++;
            }
            else
            {
                alive.Add(fighter);
            }
        }

        if (alive.Count == 0)
        {
            Answer(
                SharedBlock.CheatStatusApplied,
                $"mgkill: 0 alive of {fighters.Length} tracked fighters - nothing to kill");
            return;
        }

        int toAttempt = requested < 0 ? alive.Count : Math.Min(requested, alive.Count);
        int killed = 0;
        string firstRefusalReason = "";
        for (int i = 0; i < toAttempt; i++)
        {
            Odyssey.MiniGameFighter fighter = alive[i];
            if (Odyssey.TryKillMiniGameFighter(fighter.Slot, out int failure))
            {
                killed++;
                PluginHost.Log(
                    $"mgkill: slot {fighter.Slot} (hp {fighter.CurrentHp}/{fighter.MaxHp}) "
                    + "killed via SWMG_AdjustFollowerHitPoints");
                continue;
            }

            string reason = failure switch
            {
                1 => "no VM available",
                2 => "no routine table",
                3 => "routine table entry does not match the verified handler (build mismatch?)",
                4 => "handler bytes do not match the verified build",
                5 => "a script currently owns the VM stack - try again next frame",
                6 => "VM stack push refused",
                7 => "the engine refused the call (slot no longer valid?)",
                _ => $"engine call refused (failure {failure})",
            };

            if (firstRefusalReason.Length == 0)
            {
                firstRefusalReason = reason;
            }

            PluginHost.Log($"mgkill: slot {fighter.Slot} refused - {reason}");
        }

        int stillAlive = alive.Count - killed;
        string detail = $"mgkill: killed {killed}/{toAttempt} requested "
            + $"({alive.Count} were alive, {fighters.Length} tracked total, {deadAlready} already "
            + $"dead); {stillAlive} still alive"
            + (killed < toAttempt ? $"; first refusal: {firstRefusalReason}" : "");

        Answer(killed > 0 ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused, detail);
    }
}
