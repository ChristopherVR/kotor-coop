namespace KotorCoop.Plugin;

// The save-time flush (docs/research/save-ghosts.md §4): what SaveFlushHook's detour asks
// on every save request, and the frame branch that destroys the plugin's own spawns and
// re-issues the held save once the area is clean. The decisions live in SaveFlushRule; this
// file is the engine-facing glue around it, in the same shape as the door-transition flush
// in PluginHost.Frame.cs.
internal static unsafe partial class PluginHost
{
    private static readonly SaveFlushRule _saveFlush = new();

    /// <summary>The held request's arguments, replaced by a coalesced one (the latest wins).</summary>
    private static SaveFlushHook.Capture _heldSave;

    /// <summary>Set by the detour, consumed by the next frame: the "holding the save" line is
    /// logged from the frame, never from inside the engine's message handling.</summary>
    private static bool _saveFlushLogHoldPending;

    /// <summary>True only after a successful re-issue temporarily exposed the local PC's
    /// original commandable value. SaveFlush's quiet frames keep input-lock controllers from
    /// reasserting it until the native store has consumed the save.</summary>
    private static bool _saveInputLockSuspended;

    /// <summary>How many spawned bodies are standing right now, across the four registries the
    /// pre-store flush empties: peer stand-ins, replicated ground items, materialised enemy
    /// bodies and peer map pins. MAIN THREAD ONLY.</summary>
    internal static int CountSpawnedBodiesStanding() =>
        (_controller?.StandingCount ?? 0)
        + (_items?.StandingCount ?? 0)
        + MaterialisedEnemies.StandingCount
        + (_mapMarkers?.StandingCount ?? 0);

    /// <summary>The door-transition flush's exact sequence, one destroy per call; false once
    /// every registry reports empty.</summary>
    private static bool FlushOneSpawnedBody()
    {
        if (_controller is { } phantoms && phantoms.FlushOneBody())
        {
            return true;
        }

        if (_items is { } items && items.FlushOneBody())
        {
            return true;
        }

        if (MaterialisedEnemies.FlushOneBody())
        {
            return true;
        }

        return _mapMarkers is { } markers && markers.FlushOneBody();
    }

    /// <summary>
    /// <see cref="SaveFlushHook"/>'s question, asked at the true entry of
    /// <c>CServerExoAppInternal::SaveGame</c> on the game's own thread. True = let the engine
    /// save now; false = hold it (the detour answers the caller as accepted, and the frame
    /// re-issues it).
    ///
    /// Passes through, in order: when the splice is off; when the plugin has not ticked in
    /// the world the request is for (the epoch is unreadable mid-load, or differs from the one
    /// the frame last saw — the registries are then stale and nothing of ours can be standing
    /// in the new world, which is exactly the module-enter autosave's situation); when the
    /// request's name cannot be captured; and, through the rule, when nothing is standing and
    /// no plugin-owned player input lock needs a clean serialization window.
    /// </summary>
    internal static bool OnSaveGameRequested(nint server, int number, nint name, nint player, nint arg4)
    {
        if (!SaveFlushHook.Installed)
        {
            return true;
        }

        if (_epoch == 0 || !Odyssey.TryGetWorldEpoch(out ulong live) || live != _epoch)
        {
            return true;
        }

        int standing = CountSpawnedBodiesStanding();
        bool inputLockOwned = PlayerCommandableLock.IsOwned;
        if (_saveFlush.Phase == SaveFlushPhase.Idle && standing == 0 && !inputLockOwned)
        {
            return true;
        }

        if (!SaveFlushHook.TryCapture(server, number, name, player, arg4, out SaveFlushHook.Capture capture))
        {
            return true;
        }

        if (_saveFlush.OnSaveRequested(standing, inputLockOwned) == SaveFlushRequest.PassThrough)
        {
            return true;
        }

        _heldSave = capture;
        _saveFlushLogHoldPending = true;
        return false;
    }

    /// <summary>
    /// The frame branch. Returns true when the frame must stop here — a save is held and one
    /// body was just destroyed, or it was just re-issued and the engine has not stored yet —
    /// so nothing below re-spawns a body behind the flush. Sits after the door-transition and
    /// move-to-module blocks (a real transition outranks a held save, and the party-count
    /// crash guard must still run) and before the session-warp block.
    /// </summary>
    private static bool RunSaveFlushFrame()
    {
        if (_saveFlush.Phase == SaveFlushPhase.Idle)
        {
            return false;
        }

        if (_saveFlushLogHoldPending)
        {
            _saveFlushLogHoldPending = false;
            Log($"save flush: holding the save (slot {_heldSave.Number}, '{_heldSave.Name}') while "
                + $"{_saveFlush.StandingAtHold} body(ies) are destroyed"
                + (_saveFlush.Requests > 1 ? $" ({_saveFlush.Requests} requests coalesced)" : string.Empty));
        }

        bool flushed = _saveFlush.Phase == SaveFlushPhase.Holding && FlushOneSpawnedBody();

        switch (_saveFlush.OnFrame(flushed))
        {
            case SaveFlushStep.ReissueClean:
                ReissueHeldSave(clean: true);
                return true;

            case SaveFlushStep.ReissueUnclean:
                ReissueHeldSave(clean: false);
                return true;

            case SaveFlushStep.Release:
                // Stand-ins, materialised bodies and pins re-spawn on their own: each flush
                // cleared the slot it destroyed, which is the state a slot is in before its
                // first spawn. Replicated items do not — ItemController keeps the wanted item
                // id on a destroyed slot ("already reported gone") — so they get the same
                // nudge the aborted-transition path gives them.
                _items?.Invalidate();
                ResumeSaveInputLock();
                Log("save flush: released; the controllers rebuild what the flush destroyed");
                return false;

            case SaveFlushStep.KeepHolding:
            case SaveFlushStep.KeepQuiet:
                return true;

            default:
                return false;
        }
    }

    private static void ReissueHeldSave(bool clean)
    {
        int left = CountSpawnedBodiesStanding();
        int destroyed = Math.Max(0, _saveFlush.StandingAtHold - left);
        SaveFlushHook.Capture held = _heldSave;

        // The request may have been accepted while the player was attached, then cross a
        // world-settle edge during the flush window.  SaveGame's later StallEventSaveGame
        // path resolves the current player and immediately dereferences its area-localized
        // string; do not re-arm that retail path while the player has detached again.  Keep
        // this guard here as well as in RequestQuickSave because manual/native save requests
        // can enter SaveFlushHook without going through the console verb.
        if (!Odyssey.TryGetQuickSaveReadiness(out string readinessReason))
        {
            _saveFlush.Reset();
            ResumeSaveInputLock();
            Log($"save flush: refused to re-issue the held save (slot {held.Number}, '{held.Name}') - "
                + $"player world is not ready ({readinessReason}); the held save was dropped");
            return;
        }

        // A successful SaveGame call only arms StallEventSaveGame; serialization happens on
        // the next main-loop pass. Temporarily restore the value captured before the plugin
        // owned its input lock, and leave controller frames parked until that store window ends.
        _saveInputLockSuspended = PlayerCommandableLock.SuspendForSave();
        if (Odyssey.ReissueServerSaveGame(held.Server, held.Number, held.Name, held.Player, held.Arg4))
        {
            Log($"save flush: re-issued the save (slot {held.Number}, '{held.Name}') after "
                + $"{_saveFlush.HeldFrames} frame(s) - {destroyed} body(ies) destroyed, {left} left standing"
                + (clean
                    ? string.Empty
                    : "; the flush budget ran out, so whatever is left is baked in and the "
                      + "load-time ghost sweep removes it on the next load"));
            return;
        }

        // The server object is not the one the request was made against (the world went
        // away under the hold). Nothing to re-issue into; say so, loudly.
        _saveFlush.Reset();
        ResumeSaveInputLock();
        Log($"save flush: could NOT re-issue the held save (slot {held.Number}, '{held.Name}') - "
            + "the server the request was made against is gone; that save did not happen");
    }

    private static void ResumeSaveInputLock()
    {
        if (!_saveInputLockSuspended)
        {
            return;
        }

        _saveInputLockSuspended = false;
        PlayerCommandableLock.ResumeAfterSave();
    }

    /// <summary>The world changed while a save was held: the request was for a world that no
    /// longer exists, so it is dropped rather than re-issued into whatever is loading.</summary>
    private static void DropHeldSaveOnWorldChange()
    {
        if (_saveFlush.Phase == SaveFlushPhase.Idle)
        {
            return;
        }

        SaveFlushHook.Capture held = _heldSave;
        _saveFlush.Reset();
        _saveFlushLogHoldPending = false;
        // InvalidateAll runs immediately before this method on a world epoch change and
        // forgets world-owned lock pointers without dereferencing them. Clear only our local
        // bookkeeping; never write through a pointer into the world being unloaded.
        _saveInputLockSuspended = false;
        Log($"save flush: the module changed while the save (slot {held.Number}, '{held.Name}') was "
            + "held - dropped; the transition autosave covers this world change");
    }
}
