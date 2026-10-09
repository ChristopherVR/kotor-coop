namespace KotorCoop.Plugin;

/// <summary>
/// The local player's half of a duel (docs/research/pvp-plan.md): pin at 1 hp minimum while it
/// lasts, then put hit points and Force points back as they were. Every decision is
/// <see cref="PvpDuelMachine"/>'s; this is the engine glue around it.
///
/// WHY A PIN AND NOT A REAL FIGHT TO THE DEATH. A duellist who reaches 0 hp goes "temp dead",
/// and that state belongs to the campaign: the control auto-swap, the game-over screen and the
/// wipe vote all key on it (docs/research/death-handling.md). With the engine's own
/// min-1-hit-point flag, the one god mode and the Taris duel ring use, damage floors at 1 and
/// none of that ever runs; the host ends the round at 25% instead. The pin is a persistent
/// creature field that rides a save, so <see cref="Holding"/> also tells the save hook to refuse
/// while it is up.
///
/// The hostile flip of the OPPONENT's stand-in is not here: it is one more case in
/// <c>PhantomController.ApplyTargetable</c>, next to the stealth and menu-guard holds it
/// already owns.
///
/// MAIN THREAD ONLY (frame hook).
/// </summary>
internal sealed unsafe class PvpDuelController(SharedBlockView block)
{
    private readonly PvpDuelMachine _machine = new();
    private uint _lastPhase;
    private bool _gaveUpLogged;

    /// <summary>True while the local player carries the duel's pin or is still being restored.
    /// <see cref="PluginHost.OnSaveGameRequested"/> refuses any save in that window.</summary>
    public bool Holding => _machine.Holding;

    public void OnFrame()
    {
        uint phase = block.PvpPhase;

        // A level, not an edge: the hit hook reads it from the damage choke point.
        PhantomHitHook.SetPvpLive(phase == SharedBlock.PvpPhaseLive);

        if (phase == 0 && !_machine.Holding)
        {
            _lastPhase = 0;
            return;
        }

        nint player = Odyssey.GetPlayerCreaturePointer();
        bool resolvable = player != 0 && Odyssey.IsPlayerCharacter(player);

        PvpFacts facts = resolvable
            ? new PvpFacts(
                phase, PlayerResolvable: true,
                Pinned: Odyssey.GetMinOneHitPoint(player),
                Downed: Odyssey.IsDowned(player),
                HitPoints: Odyssey.GetCurrentHitPoints(player),
                MaxHitPoints: Odyssey.GetEffectiveMaxHitPoints(player),
                ForcePoints: Odyssey.GetCurrentForcePoints(player),
                MaxForcePoints: Odyssey.GetMaxForcePoints(player))
            : new PvpFacts(phase, false, false, false, 0, 0, 0, 0);

        PvpStep step = _machine.Step(facts);

        if (phase != _lastPhase)
        {
            PluginHost.Log(phase switch
            {
                SharedBlock.PvpPhaseArmed => $"duel: round {block.PvpRound} armed against peer {block.PvpOpponentPeer} - pinned at 1 hp minimum, "
                    + $"snapshot hp {facts.HitPoints} fp {facts.ForcePoints}",
                SharedBlock.PvpPhaseLive => $"duel: round {block.PvpRound} is live; peer {block.PvpOpponentPeer}'s stand-in is hostile",
                _ => $"duel: over - restoring hp {_machine.SnapshotHitPoints} fp {_machine.SnapshotForcePoints} and taking the pin off",
            });
            _lastPhase = phase;
        }

        if (!resolvable || step.Actions == PvpAction.None)
        {
            LogGaveUp();
            return;
        }

        if (step.Has(PvpAction.SetPin))
        {
            Odyssey.SetMinOneHitPoint(player, true);
        }

        if (step.Has(PvpAction.ClearPin))
        {
            Odyssey.SetMinOneHitPoint(player, false);
        }

        if (step.Has(PvpAction.Revive))
        {
            uint id = Odyssey.GetObjectId(player);
            if (!Odyssey.ReviveCreature(id, out int failure))
            {
                PluginHost.Log($"duel: standing the player up after the duel was refused (failure {failure}); will retry");
            }
        }

        if (step.Has(PvpAction.WriteHitPoints))
        {
            Odyssey.SetCurrentHitPoints(player, step.HitPoints);
        }

        if (step.Has(PvpAction.WriteForcePoints))
        {
            Odyssey.SetCurrentForcePoints(player, (short)step.ForcePoints);
        }

        LogGaveUp();
    }

    private void LogGaveUp()
    {
        if (_machine.GaveUp && !_gaveUpLogged)
        {
            _gaveUpLogged = true;
            PluginHost.Log("duel: could not read the snapshot hit points or Force points back after the duel - left as the engine has them");
        }
        else if (!_machine.GaveUp)
        {
            _gaveUpLogged = false;
        }
    }

    /// <summary>The world changed. The snapshot and the "I set the pin" memory deliberately
    /// survive: the same character is on the other side, and the pin rode the change with it.</summary>
    public void Invalidate()
    {
    }
}
