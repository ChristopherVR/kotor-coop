namespace KotorCoop.Plugin;

/// <summary>
/// Reconciles this instance's own creatures against the enemy owner's liveness view, so a
/// body left alive on one side and dead or destroyed on the other converges — see
/// docs/research/creature-liveness.md for the design and USER-REPORTED CONCERN this answers
/// ("we don't want instances of NPCs alive in one host and not another").
///
/// Runs on the game's frame thread, throttled like <see cref="CombatController"/> — a kill or
/// destroy is a VM dispatch, and this channel is meant to be EVENTUALLY consistent (the owner
/// republishes its full view every couple of seconds), not instantaneous.
///
/// The owner never has anything to reconcile against itself: <c>CoopAgent</c> only ever
/// writes the liveness region on the non-authority side, so <see cref="SharedBlockView.LivenessCount"/>
/// reads 0 on the owner and <see cref="OnFrame"/> does nothing there, every pass.
///
/// Kill switch: <c>liveness_sync=0</c> in coop.ini — the DEFAULT, until this channel has been
/// live-verified (see the NEEDS-LIVE checklist in the design doc).
/// </summary>
internal sealed class LivenessReconcileController(SharedBlockView block, CombatController combat)
{
    private const int IntervalFrames = 40;

    /// <summary>
    /// How many kill/destroy engine calls one pass may issue. A reconnect or a fresh module
    /// can hand this controller dozens of genuine mismatches at once — a returning client's
    /// FIRST liveness packet after (re)join is a full snapshot (docs/research/creature-liveness.md)
    /// — and dispatching them all in one frame is the VM-dispatch storm every other
    /// spawn/destroy path in this codebase already rate-limits against (MaterialisedEnemies'
    /// own spawn cooldown, the phantom spawner). The channel republishes every cycle, so
    /// anything not reached this pass is reached the next one.
    /// </summary>
    private const int MaxActionsPerPass = 4;

    /// <summary>coop.ini <c>liveness_sync</c>, overridable per session by the host — see
    /// <see cref="EffectiveBehaviourFlag"/>.</summary>
    private readonly EffectiveBehaviourFlag _livenessSync = new(
        "liveness_sync", SharedBlock.OverrideBitLivenessSync, PluginConfig.ReadBool("liveness_sync", false));

    private int _countdown;
    private bool _offLogged;
    private int _logged;

    public void OnFrame()
    {
        if (!_livenessSync.Resolve(block))
        {
            if (!_offLogged)
            {
                _offLogged = true;
                PluginHost.Log("liveness: disabled by coop.ini (liveness_sync=0, default off until live-verified)");
            }

            return;
        }

        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;

        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        // The dialogue system owns positions and action queues for the duration of a local
        // conversation, and every other combat-adjacent controller here holds its own
        // publication or action for the same reason (CombatController's per-enemy pin,
        // EnemyPresenceController's whole pass) — a kill/destroy dispatched through the VM
        // while a script holds the stage is exactly the collision those guards exist to
        // avoid. Nothing is lost: the owner republishes its full view every cycle, so the
        // next clean pass catches up.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        uint count = Math.Min(block.LivenessCount, (uint)SharedBlock.LivenessCapacity);
        if (count == 0)
        {
            return;
        }

        int actionsLeft = MaxActionsPerPass;

        for (int i = 0; i < count && actionsLeft > 0; i++)
        {
            if (Apply(block.GetLiveness(i)))
            {
                actionsLeft--;
            }
        }
    }

    /// <summary>Returns true when this entry spent a dispatch (kill or destroy) from the
    /// pass's budget; false for every other outcome — nothing to do, or an anomaly, which is
    /// a log line and a counter bump, never an engine call.</summary>
    private bool Apply(LivenessView entry)
    {
        if (entry.ObjectId == 0 || entry.ObjectId == Odyssey.ObjectInvalid)
        {
            return false;
        }

        LivenessHostState hostState = entry.State switch
        {
            SharedBlock.LivenessStateDead => LivenessHostState.Dead,
            SharedBlock.LivenessStateGone => LivenessHostState.Gone,
            _ => LivenessHostState.Alive,
        };

        // Same collision guard every other consumer of MaterialisedEnemies applies: an
        // unmapped owner id that numerically equals one of our own stand-ins' local ids must
        // never be resolved as if it were GIT-identical — that would reconcile the wrong body.
        uint mapped = MaterialisedEnemies.ToLocal(entry.ObjectId);
        if (mapped == 0 && MaterialisedEnemies.ToOwner(entry.ObjectId) != 0)
        {
            return false;
        }

        uint effectiveId = mapped != 0 ? mapped : entry.ObjectId;
        nint creature = Odyssey.ResolveServerObject(effectiveId);
        bool localResolved = creature != 0;
        bool localDead = localResolved && Odyssey.IsCreatureDead(creature);
        bool localPlot = localResolved && Odyssey.IsPlot(creature);

        LivenessAction action = LivenessRule.Decide(hostState, localResolved, localDead, localPlot);

        switch (action)
        {
            case LivenessAction.Kill:
                // The owner's own gate for a plot creature (module rule: un-killable) is
                // enforced above via localPlot, exactly like Apply's plot check does for the
                // ordinary census path — LivenessRule.Decide already folds that in.
                combat.KillForLivenessReconcile(entry.ObjectId);
                if (_logged < 20)
                {
                    _logged++;
                    PluginHost.Log($"liveness: 0x{entry.ObjectId:X} is a corpse on the owner and was still "
                        + "alive here - killing to match");
                }

                return true;

            case LivenessAction.Destroy:
                if (Odyssey.DestroyObjectById(effectiveId, out int failure))
                {
                    if (_logged < 20)
                    {
                        _logged++;
                        PluginHost.Log($"liveness: 0x{entry.ObjectId:X} no longer exists on the owner and "
                            + $"still did here - destroying to match (local 0x{effectiveId:X})");
                    }
                }
                else if (_logged < 20)
                {
                    _logged++;
                    PluginHost.Log($"liveness: destroy refused for 0x{entry.ObjectId:X} (failure {failure})");
                }

                return true;

            case LivenessAction.Anomaly:
                block.LivenessAnomalyCount++;
                if (_logged < 20)
                {
                    _logged++;
                    PluginHost.Log($"liveness ANOMALY: owner says 0x{entry.ObjectId:X} is alive, this "
                        + $"instance already has it {(localResolved ? "dead" : "gone")} - not resurrecting "
                        + "(the fix belongs upstream, see docs/research/creature-liveness.md)");
                }

                return false;

            default:
                return false;
        }
    }
}
