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
/// Kill switch: <c>liveness_sync=0</c> in coop.ini. ON by default: the channel was live-verified
/// on 2026-09-03 (the design doc's checklist) and the template line was flipped to 1 the same
/// day, but the code fallback here stayed <c>false</c> and the template line was later removed
/// by the "player settings only" cleanup, so every shipped install silently ran with it OFF
/// until the code default itself was flipped on 2026-09-18
/// (docs/review-seamless-2026-09-18.md). The agent-side digest default in CoopConfig.cs must
/// agree with this one, or a client that sets 1 is overridden back to 0 by the host.
/// </summary>
internal sealed unsafe class LivenessReconcileController(SharedBlockView block, CombatController combat)
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
        "liveness_sync", SharedBlock.OverrideBitLivenessSync, PluginConfig.ReadBool("liveness_sync", true));

    /// <summary>Owner ids this instance has already asked the engine to destroy once, so a
    /// pending-but-not-yet-completed destroy (see <see cref="LivenessRule.Decide"/>'s own doc
    /// comment on why <c>DestroyObjectById</c> is not synchronous) is not re-dispatched every
    /// throttled pass. Sized like <see cref="CombatController"/>'s own <c>_killed</c> array —
    /// bounded by <see cref="SharedBlock.LivenessCapacity"/>, which this document's own module
    /// survey (docs/research/creature-liveness.md) never found a reachable population to
    /// exceed by more than a factor of three.</summary>
    private readonly uint[] _destroyRequested = new uint[SharedBlock.LivenessCapacity];
    private int _destroyRequestedCount;

    private int _countdown;
    private bool _offLogged;

    /// <summary>One log budget PER OUTCOME, not one shared one. Live 2026-09-03 03:18 (client,
    /// tar_m10aa, liveness_sync=1): the shared 20-line cap had been spent entirely on the
    /// anomaly line (the counter stood at 148) before the first genuine destroy-to-match ever
    /// ran, so the checklist's own "exactly ONE destroy line" evidence was silently unloggable —
    /// the destroy itself worked (the creature resolved to 0x0 within 15 s), the line did not.</summary>
    private int _killsLogged;
    private int _destroysLogged;
    private int _anomaliesLogged;
    private const int LogBudgetPerOutcome = 20;

    /// <summary>Forgets every destroy-request latch on a module change — same reasoning as
    /// <see cref="CombatController.Invalidate"/>: ids are reassigned per module, so a
    /// remembered request would silently suppress a legitimate destroy for a completely
    /// different creature that happens to reuse the id.</summary>
    public void Invalidate()
    {
        Array.Clear(_destroyRequested);
        _destroyRequestedCount = 0;
    }

    public void OnFrame()
    {
        if (block.LivenessNativeEpoch == 0) { block.BeginLivenessEpoch(); return; }
        if (!_livenessSync.Resolve(block))
        {
            if (!_offLogged)
            {
                _offLogged = true;
                PluginHost.Log("liveness: disabled by coop.ini (liveness_sync=0; the default has been ON since 2026-09-18)");
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
        //
        // Except a creature the host has REMOVED that stands outside the conversation: it can
        // fight the scene's own actors here for as long as the scene lasts. On the Star Forge
        // (2026-10-10) the scene's script removes the captive Jedi on both games, the joiner's
        // guard refused it against a stale census, and the Jedi left standing on the joiner
        // killed its Carth mid-conversation; they were removed only when the scene ended, 35 s
        // later. Those removals still go through; kills and anything in a conversation wait.
        bool inConversation = Odyssey.IsConversationActive;

        uint sequence = block.LivenessPublishSequence;
        if (!BankCurrent(sequence)) return;
        uint count = Math.Min(block.LivenessCount, (uint)SharedBlock.LivenessCapacity);
        if (!BankCurrent(sequence)) return;
        if (count == 0)
        {
            return;
        }

        int actionsLeft = MaxActionsPerPass;

        for (int i = 0; i < count && actionsLeft > 0; i++)
        {
            LivenessView entry = block.GetLiveness(i);
            if (!BankCurrent(sequence)) return;
            if (Apply(entry, sequence, inConversation))
            {
                actionsLeft--;
            }
        }
    }

    private bool BankCurrent(uint sequence) => LivenessBankRule.IsSamePublication(sequence, block.LivenessPublishSequence,
            block.LivenessExpectedWorldSerial, block.WorldLoadSerial,
            block.LivenessExpectedEpoch, block.LivenessNativeEpoch);

    /// <summary>Returns true when this entry spent a dispatch (kill or destroy) from the
    /// pass's budget; false for every other outcome — nothing to do, or an anomaly, which is
    /// a log line and a counter bump, never an engine call.</summary>
    private bool Apply(LivenessView entry, uint sequence, bool inConversation = false)
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
        bool retainedCandidate = MaterialisedEnemies.IsRetainedOwnerCandidate(entry.ObjectId, block);
        bool retainedAlias = MaterialisedEnemies.IsRetainedOwner(entry.ObjectId);
        uint mapped;
        if (retainedCandidate || retainedAlias)
        {
            if (!MaterialisedEnemies.TryGetCurrentRetainedLocal(entry.ObjectId, block, out mapped))
            {
                // A dead or vanished owner body has no hostile row left to prove the live alias
                // (CombatController.KillOnce's note), so a death verdict uses the death form.
                if (hostState == LivenessHostState.Alive
                    || !MaterialisedEnemies.TryGetRetainedLocalForDeath(entry.ObjectId, out mapped))
                {
                    return false; // defer kills/destroys until the current owner/local proof is restored
                }
            }

            // A retained body is a roster companion's own creature: kill it like the owner
            // did rather than destroy an object the party table still names (live 2026-09-25:
            // Jolee vanished on the host while the live proof still held and was destroyed here).
            if (hostState == LivenessHostState.Gone)
            {
                hostState = LivenessHostState.Dead;
            }
        }
        else
        {
            mapped = MaterialisedEnemies.ToLocal(entry.ObjectId);
        }

        if (mapped == 0 && MaterialisedEnemies.ToOwner(entry.ObjectId) != 0)
        {
            return false;
        }

        if (mapped == 0)
        {
            mapped = MaterialisedEnemies.StoryAliasFor(entry.ObjectId);
        }

        uint effectiveId = mapped != 0 ? mapped : entry.ObjectId;

        // The same number on this game names a story actor this game built itself, not the
        // host's creature (see ScriptCreateObjectHook.IsLocalStoryActor).
        if (mapped == 0 && ScriptCreateObjectHook.IsLocalStoryActor(effectiveId))
        {
            return false;
        }

        nint creature = Odyssey.ResolveServerObject(effectiveId);
        if (creature != 0)
        {
            bool peerPhantom = false;
            for (int slot = 0; slot < SharedBlock.SlotCapacity; slot++)
                peerPhantom |= block.GetPhantomId(slot) == effectiveId;
            // A same-module load can reuse a departed creature's id for a map
            // waypoint or peer body. Never send it through creature virtual calls.
            if (!LivenessRule.CanReconcileBody((uint)*(nint*)creature, *(uint*)(creature + 4),
                effectiveId, effectiveId == Odyssey.GetPlayerObjectId(), peerPhantom)) return false;
        }
        if (inConversation && (hostState != LivenessHostState.Gone || creature == 0 || Odyssey.IsInAnyConversation(creature)))
        {
            return false;
        }

        bool localResolved = creature != 0;
        bool localDead = localResolved && Odyssey.IsCreatureDead(creature);
        bool localPlot = localResolved && Odyssey.IsPlot(creature);
        bool destroyAlreadyRequested = AlreadyRequestedDestroy(entry.ObjectId);

        LivenessAction action = LivenessRule.Decide(
            hostState, localResolved, localDead, localPlot, destroyAlreadyRequested);

        switch (action)
        {
            case LivenessAction.Kill:
                if (!BankCurrent(sequence)) return false;
                // The owner's own gate for a plot creature (module rule: un-killable) is
                // enforced above via localPlot, exactly like Apply's plot check does for the
                // ordinary census path — LivenessRule.Decide already folds that in.
                combat.KillForLivenessReconcile(entry.ObjectId);
                if (_killsLogged < LogBudgetPerOutcome)
                {
                    _killsLogged++;
                    PluginHost.Log($"liveness: 0x{entry.ObjectId:X} is a corpse on the owner and was still "
                        + "alive here - killing to match");
                }

                return true;

            case LivenessAction.Destroy:
                if (!BankCurrent(sequence)) return false;
                if (Odyssey.DestroyObjectById(effectiveId, out int failure))
                {
                    // Remember it on SUCCESS only, mirroring CombatController.KillOnce's own
                    // failure-retries-next-pass symmetry: a transient refusal (the VM mid-call,
                    // failure 4 — see PhantomController.Lifecycle.cs's own note on this) should
                    // still be retried, but a request the engine accepted must not be re-issued
                    // while its own delayed removal is still pending.
                    RememberDestroyRequested(entry.ObjectId);
                    if (_destroysLogged < LogBudgetPerOutcome)
                    {
                        _destroysLogged++;
                        PluginHost.Log($"liveness: 0x{entry.ObjectId:X} no longer exists on the owner and "
                            + $"still did here - destroying to match (local 0x{effectiveId:X})");
                    }
                }
                else if (_destroysLogged < LogBudgetPerOutcome)
                {
                    _destroysLogged++;
                    PluginHost.Log($"liveness: destroy refused for 0x{entry.ObjectId:X} (failure {failure})");
                }

                return true;

            case LivenessAction.Anomaly:
                block.LivenessAnomalyCount++;
                if (_anomaliesLogged < LogBudgetPerOutcome)
                {
                    _anomaliesLogged++;
                    PluginHost.Log($"liveness ANOMALY: owner says 0x{entry.ObjectId:X} is alive, this "
                        + $"instance already has it {(localResolved ? "dead" : "gone")} - not resurrecting "
                        + "(the fix belongs upstream, see docs/research/creature-liveness.md)");
                }

                return false;

            default:
                return false;
        }
    }

    private bool AlreadyRequestedDestroy(uint ownerObjectId)
    {
        for (int i = 0; i < _destroyRequestedCount; i++)
        {
            if (_destroyRequested[i] == ownerObjectId)
            {
                return true;
            }
        }

        return false;
    }

    private void RememberDestroyRequested(uint ownerObjectId)
    {
        if (_destroyRequestedCount < _destroyRequested.Length)
        {
            _destroyRequested[_destroyRequestedCount++] = ownerObjectId;
        }
    }
}
