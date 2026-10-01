namespace KotorCoop.Plugin;

// What happens when a co-op session connects/releases, and when the world changes under
// every controller at once. Split out of PluginHost.Frame.cs because it is a distinct
// concern from the per-frame SCHEDULE (this is what PumpSessionLifecycle/DrainSessionSpawns/
// InvalidateAll do once they are reached, not where they sit in it) — the call sites for all
// three stay in PluginHost.Frame.cs's OnFrame, in the same order as before.
internal static unsafe partial class PluginHost
{
    /// <summary>Whether a session was driving this instance on the previous frame; the falling
    /// edge is a disconnect (or a session the agent tore down for any other reason).</summary>
    private static bool _sessionActive;

    /// <summary>
    /// THE ONE PLACE A SESSION IS UNDONE.
    ///
    /// ⚠ THIS EXISTS BECAUSE PER-FIELD TEARDOWN KEPT MISSING ONE. Reported from play: after a
    /// client disconnected, Trask was back in the party but clicking him did nothing — the
    /// agent's control-swap suppression was still raised in the block, so DeathController kept
    /// arming the byte patch that turns <c>ChangeCharacter</c> (Tab, every party portrait, and
    /// the death auto-swap) into an immediate return. Beside it, mirrored companions were left
    /// in the Neutral faction and pinned at min-1-hp, the spectate camera could stay bolted to
    /// a phantom the very next controller was about to destroy, and the party portraits stayed
    /// parked off-screen. Each was a separate omission of the same shape.
    ///
    /// So the rule is now structural: <c>block.Command</c> is the session's presence, and its
    /// FALLING edge releases every controller that holds engine or GUI state, in one ordered
    /// list. Anything a controller does to the world on connect belongs in its
    /// <c>ReleaseSession</c> — not in a second copy of this list somewhere else.
    ///
    /// The agent lowers the command LAST when it clears the block
    /// (KotorGameBridge.SessionScopedBlockLevels), so by the time this runs every other level
    /// is already down and a controller's release reads a clean block.
    /// </summary>
    private static void PumpSessionLifecycle()
    {
        bool active = _view is { } view && view.Command == SharedBlock.CommandSpawnAndDrive;
        if (active == _sessionActive)
        {
            return;
        }

        _sessionActive = active;

        if (active)
        {
            Log("session: a co-op session is driving this instance");
            PluginEvents.RaiseSessionConnected();
            return;
        }

        Log("session: released — undoing what it did to this game");
        PluginEvents.RaiseSessionReleased();

        // Camera first, and before PhantomController's despawn pass: the follow behaviour
        // holds a raw client-creature pointer, so a camera left on a phantom outlives the
        // body by however long the give-back takes. The solo-segment spectate camera is the
        // same hazard as the down-spectate one, plus a clamped commandable flag on the LOCAL
        // player's own creature that must not survive a disconnect either.
        _spectate?.ReleaseSession();
        _soloSegmentSpectate?.ReleaseSession();
        _minigameLock?.ReleaseSession();
        _controlReturn?.ReleaseSession();

        // Then the bodies: faction and the min-1-hp pin, which are SAVED state.
        _partyPresence?.ReleaseSession();

        // Then everything that was holding the player's own game hostage.
        _death?.ReleaseSession();
        _interactionLocks?.ReleaseSession();
        _partyRoster?.ReleaseSession();
        _enemyEffects?.ReleaseSession();
        _portraits?.ReleaseSession();
        _soloSegment?.ReleaseSession();

        // And last, the BODIES the session put in the world. Armed rather than done here,
        // because a destroy can be refused; DrainSessionSpawns finishes the job.
        _spawnDrainPasses = SpawnDrainPasses;
        _spawnsAtRelease = -1;
    }

    /// <summary>
    /// Passes the spawn drain is allowed. One destroy attempt per body per pass, and a
    /// refusal is written off after three (see PhantomController.MaxDestroyAttempts), so this
    /// only has to outlast that — the rest is headroom for a busy script VM.
    /// </summary>
    private const int SpawnDrainPasses = 12;

    private static int _spawnDrainPasses;

    /// <summary>How many bodies the session had standing when it was released; -1 until the
    /// first drain pass has counted them.</summary>
    private static int _spawnsAtRelease;

    /// <summary>
    /// Destroys everything the session SPAWNED — peer phantoms, replicated ground items,
    /// materialised enemies and map pins — and keeps at it until the world is clean.
    ///
    /// ⚠ THIS IS NOT BELT AND BRACES; IT IS THE THIRD LEAK OF THE TEARDOWN CLASS. Measured on
    /// the rig after the suppression fix landed: forty seconds past a disconnect, with
    /// `remotes 0` and `command 0`, a `coop_phantom` was still standing in the client's game
    /// and the readout still said `phantoms 1`. Two causes, both of the shape this pass
    /// exists to remove. PhantomController's idle branch was gated on a DERIVED count it
    /// never recomputed, so the gate lied; and every one of the four spawners cleared its
    /// slot whether the engine's destroy succeeded or REFUSED — and a destroy is refused
    /// whenever the script VM is mid-call, which a disconnect landing on an arbitrary frame
    /// routinely is. One declined call orphaned the body forever.
    ///
    /// Why it matters more than a stray body: the disconnected player is left with the other
    /// player's corpse-shaped creature standing in their single-player game, and if they
    /// save, StoreCurrentModule serialises it in permanently. That is the "baked phantom" the
    /// cinematics work is separately hunting, whose CLEAN criterion is literally "no
    /// coop_phantom with the session down".
    ///
    /// Runs before every world controller, so nothing acts on a body that is going away.
    /// </summary>
    private static void DrainSessionSpawns()
    {
        if (_spawnDrainPasses <= 0)
        {
            return;
        }

        _spawnDrainPasses--;

        int standing = (_controller?.ReleaseSession() ?? 0)
            + (_items?.ReleaseSession() ?? 0)
            + MaterialisedEnemies.ReleaseSession()
            + (_mapMarkers?.ReleaseSession() ?? 0);

        (SpawnDrainOutcome outcome, _spawnsAtRelease) =
            SpawnDrainRule.Decide(standing, _spawnsAtRelease, _spawnDrainPasses);

        switch (outcome)
        {
            case SpawnDrainOutcome.NothingWasStanding:
                _spawnDrainPasses = 0;

                // The required NEGATIVE report: silence here is indistinguishable from a
                // drain that never ran, and that ambiguity is what cost the last rig round.
                Log("session: nothing it spawned was left standing");
                break;

            case SpawnDrainOutcome.Cleaned:
                _spawnDrainPasses = 0;
                Log($"session: removed {_spawnsAtRelease} object(s) it had spawned — "
                    + "nothing left standing");
                break;

            case SpawnDrainOutcome.Refused:
                Log($"session: {standing} of {_spawnsAtRelease} spawned object(s) REFUSED to be "
                    + "destroyed and are still standing — a save now would bake them into the module");
                break;

            case SpawnDrainOutcome.KeepDraining:
            default:
                break;
        }
    }

    private static void InvalidateAll(string reason)
    {
        Log($"epoch: {reason}; clearing phantom, item, combat and door state");
        if (FloatyTextNodeGuardHook.CreatesSkipped != 0 || FloatyTextNodeGuardHook.UpdatesSkipped != 0)
        {
            // The floaty guard's running totals, printed where a live session will look for
            // them (its own log lines are budgeted; the update tick is per-frame).
            Log($"floaty: guard totals so far - {FloatyTextNodeGuardHook.CreatesSkipped} floaty(ies) not added, "
                + $"{FloatyTextNodeGuardHook.UpdatesSkipped} tick(s) skipped");
        }

        // First, before any controller can act on them again: the agent-written world inputs
        // are stale (the agent's reads fail through a transition, so it cannot clear them
        // itself), and a controller acting on them spawns bodies for the wrong world.
        _view?.ClearAgentWorldInputs();

        // A world that has gone has taken every spawned body with it, so a drain still
        // running would report on slots the Invalidate calls below are about to forget —
        // and "removed them all" would be a claim about objects UnloadModule freed, not
        // about anything this plugin did.
        _spawnDrainPasses = 0;
        _spawnsAtRelease = -1;

        CinematicWatch.Invalidate();
        _controller?.Invalidate();
        _items?.Invalidate();
        _combat?.Invalidate();
        _liveness?.Invalidate();
        _enemyScaling?.Invalidate();
        _enemyPresence?.Invalidate();
        MaterialisedEnemies.Invalidate();
        _partyPresence?.Invalidate();
        _incomingDamage?.Invalidate();
        _peerEffects?.Invalidate();
        _peerDamage?.Invalidate();
        _companionDamage?.Invalidate();
        _testDrive?.Invalidate();
        _cheats?.Invalidate();
        PhantomHitHook.Invalidate();
        AnimationHook.InvalidateCreatureTable();
        Odyssey.InvalidateMoveGuardLog();
        _doors?.Invalidate();
        _placeables?.Invalidate();
        _localVars?.Invalidate();
        _loot?.Invalidate();
        _travel?.Invalidate();
        _pause?.Invalidate();
        _journal?.Invalidate();
        _mapMarkers?.Invalidate();
        _mapReveal?.Invalidate();
        _connectMenu?.Invalidate();
        _death?.Invalidate();
        _spectate?.Invalidate();
        _soloSegment?.Invalidate();
        _soloSegmentSpectate?.Invalidate();
        _ghostSweep?.Invalidate();
        _minigameLock?.Invalidate();
        _encounters?.Invalidate();
        _triggerRelay?.Invalidate();
        _movieRelay?.Invalidate();
        _galaxyDeparture?.Invalidate();
        _classGrant?.Invalidate();
        _jediChoicePrompt?.Invalidate();
        _enemyEffects?.Invalidate();
        _placedMines?.Invalidate();
        PendingMoviePlay.Cancel();
        _barks?.Invalidate();
        _itemTemplateSpawns?.Invalidate();
        _interactionLocks?.Invalidate();
        ContainerUseHook.Invalidate();
        ContainerCloseHook.Invalidate();
        _partyRoster?.Invalidate();
        _partySelectSuppression?.Invalidate();
        _partySelectHostRequest?.Invalidate();
        _hostConversationRequest?.Invalidate();
        _companionEquipSuppression?.Invalidate();

        // ⚠ THIS LINE WAS MISSING, so the spectate controller's ENTIRE teardown had never run
        // in the history of the mod — every other controller here was reset on a module change
        // and that one was not. A conversation's partner TAG then resolved to a different body
        // in the new module, and a published node index named an entry in a completely
        // different .dlg.
        //
        // The controller no longer depends on this: it watches the same world epoch PluginHost
        // gates its frame on and invalidates itself. Both are kept deliberately. The
        // self-invalidation is what makes it correct when this list is wrong again, and this
        // line is what makes the list mean what it says.
        _dialogueSpectate?.Invalidate();
    }
}
