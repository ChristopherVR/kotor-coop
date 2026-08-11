namespace KotorCoop.Plugin;

/// <summary>
/// Keeps this instance's combat state in step with the session's.
///
/// A client cannot work out for itself that a fight is happening.
/// <see cref="Odyssey.MarkAsMirroredEnemy"/> clears the attack-permitted bit on every
/// mirrored enemy, deliberately, so the client's copies never swing — and combat state is
/// derived from being fought, so the client's engine sits at "not in combat" through an
/// entire battle. Measured live: host creature +0x4E0 = 1, client 0, same room, same
/// second.
///
/// That is not a cosmetic gap. The in-combat dword is read from ~30 sites: combat round
/// timing, the HUD and combat music, auto-pause, whether abilities queue as combat actions,
/// and the party's own combat broadcast. A client that never enters combat is running a
/// different ruleset from the host.
///
/// So the state is replicated rather than re-derived: the agent publishes "a peer standing
/// in your module has its engine in combat", and this drives the local PC to match through
/// <c>CSWSCreature::SetCombatState</c> — the engine's own setter, the only writer of the
/// field on that path.
///
/// ⚠ THIS DOES NOT WORK YET, AND IT IS OFF BY DEFAULT. Measured 2026-08-11 on the live
/// pair: a written in-combat flag on a real player with no attackers is reverted by the
/// engine in <b>0–2 ms</b>, three trials —
///
///     trial 1 : reverted after 2 ms
///     trial 2 : reverted after 0 ms
///     trial 3 : reverted after 1 ms
///
/// — so re-asserting on any cadence a frame hook can manage holds the field for ~2 ms out
/// of every 60. The state is not a latch the engine consults; it is continuously re-derived
/// from whether the creature is actually being fought (there is a second writer inside
/// CSWSCreature::ProcessPendingCombatActions at 0x004F7C0E, past the one in SetCombatState).
///
/// The channel below is correct and stays: publishing "a peer here is fighting" is needed
/// whatever drives the state. What is wrong is the last step — poking the derived field.
///
/// THE REAL FIX is to give the client's engine a genuine reason to be in combat rather than
/// asserting the conclusion: stop clearing the attack-permitted bit in
/// <see cref="Odyssey.MarkAsMirroredEnemy"/> for enemies near the local player, so its own
/// copies swing at it and its own combat state rises for the same reason the host's does.
/// That is an ownership change, not a poke, and it needs its own pass with the damage
/// reconciliation thought through — a client whose mirrored enemies really attack must not
/// also apply that damage locally, or every hit lands twice.
///
/// Turn this on with coop.ini <c>combat_state_sync=1</c> to reproduce the measurement.
/// </summary>
internal sealed class CombatStateController(SharedBlockView block)
{
    /// <summary>Frames between passes; ~16 a second at the hook's ~317 Hz. Combat rounds are
    /// three seconds long, so this re-asserts many times per round without putting an engine
    /// call on the hot path.</summary>
    private const int IntervalFrames = 20;

    private int _countdown;

    /// <summary>True while the local combat state is being held up by the session rather
    /// than by this engine's own fight, so it is released when the session's fight ends and
    /// never when a local fight is still running.</summary>
    private bool _held;

    private int _logged;

    /// <summary>Off until the engine's re-derivation is dealt with — see the class remarks.
    /// Left wired so the measurement can be reproduced without rebuilding.</summary>
    private readonly bool _enabled = PluginConfig.ReadBool("combat_state_sync", false);

    /// <summary>A module change ends every fight; drop the claim so a stale one cannot
    /// hold the new world in combat.</summary>
    public void Invalidate()
    {
        _held = false;
    }

    public void OnFrame()
    {
        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;

        if (!_enabled || block.Command != SharedBlock.CommandSpawnAndDrive || !Odyssey.HasPlayer)
        {
            _held = false;
            return;
        }

        bool sessionFighting = block.SessionInCombat;

        if (sessionFighting)
        {
            // Re-assert every pass: the engine drops a client back out on its own schedule.
            if (Odyssey.SetPlayerCombatState(true))
            {
                LogFew("combat: entered combat because a peer here is fighting");
            }

            _held = true;
            return;
        }

        if (!_held)
        {
            return;
        }

        _held = false;

        // Only release what we imposed. If this engine has a fight of its own — the host's
        // case, or a client that somehow drew a real attacker — its own state stands.
        if (Odyssey.SetPlayerCombatState(false))
        {
            LogFew("combat: left combat; the session's fight is over");
        }
    }

    private void LogFew(string message)
    {
        if (_logged < 40)
        {
            _logged++;
            PluginHost.Log(message);
        }
    }
}
