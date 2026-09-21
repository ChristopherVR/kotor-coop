namespace KotorCoop.Memory;

/// <summary>
/// KotorGameBridge — forgetting what this instance's world used to look like, when the world
/// underneath it has been REPLACED rather than travelled through.
/// </summary>
public sealed partial class KotorGameBridge
{
    /// <summary>
    /// Drops every agent-side diff shadow, so the next scan of each channel is a fresh SILENT
    /// baseline instead of a diff against a world that no longer exists.
    ///
    /// ⚠ THE BUG THIS FIXES, and it is subtle enough to be worth stating in full. Every
    /// world-state channel here is an EDGE publisher: it scans the module, compares against a
    /// shadow of the last scan, and announces what changed. Those shadows are cleared on one
    /// condition only — the module NAME changing (<c>CollectChangedDoorState</c>,
    /// <c>CollectChangedPlaceableState</c>) — because that is the only way a world used to be
    /// replaced. World adoption broke that assumption: it loads a DIFFERENT SAVE OF THE SAME
    /// MODULE.
    ///
    /// So a joiner who adopts the host's world while standing in the module they were already
    /// in keeps a shadow describing the world they left. The staged save has a door CLOSED
    /// (the host opened it after the save was taken); the shadow says OPEN; the next scan
    /// therefore reports a genuine local transition to CLOSED — and
    /// <c>EventFlagStore.Set</c> mints a fresh Lamport serial for it, which OUTRANKS the host's
    /// OPEN in both stores. The host's own engine stays visually open (the door channel only
    /// ever moves doors forward, and a closed door is never pushed at all), so the session's
    /// agreed truth becomes "closed" while the host is looking at an open door, permanently,
    /// with no further local change to correct it. Measured live: a door OPEN on the host and
    /// CLOSED on the client after a new-character join.
    ///
    /// ⚠ AND WHY THIS IS NOT FOLDED INTO <c>ClearCoopState</c>. That runs on every disconnect,
    /// including ones with no load at all, where the shadows are still an accurate description
    /// of a world nobody replaced — and re-baselining there would silently swallow a door the
    /// player opened during the outage. This is called only where the world really was
    /// swapped underneath the shadows: the adopt paths.
    ///
    /// Deliberately does NOT clear <see cref="_journalShadow"/>: the journal is keyed by quest
    /// tag rather than by object id, it is monotonic (an entry never regresses), and a
    /// re-baseline there would suppress a real quest advance rather than a phantom one.
    /// </summary>
    public void ForgetWorldShadows()
    {
        // Doors. Clearing the module name too is what forces the next scan to take the
        // "entered a new module" path in full, including the silent-baseline flag — clearing
        // only the dictionaries would leave _doorBaselined true and announce every door in the
        // module as a fresh change.
        _doorShadow.Clear();
        _doorModule = string.Empty;
        _doorBaselined = false;
        _nextDoorScanTicks = 0;

        _placeableShadow.Clear();
        _placeableBaselineLocked.Clear();
        _placeableBaselineTrapped.Clear();
        _placeableModule = string.Empty;
        _placeableBaselined = false;
        _nextPlaceableScanTicks = 0;

        // Floor mines: same edge-publisher shape, same reason.
        _mineTracker.Reset();
        _mineModule = string.Empty;
        _nextMineScanTicks = 0;

        // Globals. The shadow arrays are re-taken by TryResolveGlobals against the new world;
        // dropping them makes the next collect a baseline rather than a diff against the
        // previous save's values.
        _shadowBooleans = null;
        _shadowNumbers = null;
    }
}
