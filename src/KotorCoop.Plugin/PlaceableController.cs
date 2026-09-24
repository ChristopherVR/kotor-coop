namespace KotorCoop.Plugin;

/// <summary>
/// Applies the session's placeable truth (chests, corpses, switches, containers) to this
/// instance, on the game's thread. The sibling of <see cref="DoorController"/>: placeable
/// object ids match across instances (same save, deterministic GIT order), and — since the
/// door-relock model was extended here (docs/research/door-relock.md §6) — open/closed and
/// locked/unlocked both apply in EITHER direction now, ordered by the same Lamport flag store
/// doors use, with the same no-oscillation guarantee: <see cref="_desired"/> only ever applies
/// a genuinely NEW (open, unlocked) tuple, so a stale re-published value cannot fight a local
/// story change (or the closing player's own panel) out from under itself.
///
/// Simpler than a door: a placeable has no walkmesh coupling, so lock/unlock is a plain field
/// write either direction (<see cref="Odyssey.UnlockPlaceable"/> / <see cref="Odyssey.LockPlaceable"/>).
/// Open/close plays the same VERIFIED animation pair the container-sync interaction-lock mirror
/// already uses (<see cref="OdysseyPlaceables.PlayContainerOpenAnimation"/> /
/// <see cref="OdysseyPlaceables.PlayContainerCloseAnimation"/>, docs/research/container-sync.md
/// §1.2) alongside the field write, so a peer sees the lid move rather than just the flag
/// changing under it.
/// </summary>
internal sealed class PlaceableController(SharedBlockView block)
{
    /// <summary>Frames between passes; placeable state changes are rare and never urgent.</summary>
    private const int IntervalFrames = 40;

    private int _countdown;
    private int _logged;

    /// <summary>Floor-mine removals share this slot list (kind byte +7 == 1) — SharedBlock.Mines.cs.</summary>
    private readonly MineController _mines = new();

    /// <summary>Dedupes applies to a genuinely new (open, unlocked) tuple per placeable id — the
    /// placeable twin of <c>DoorController</c>'s own tracker; see
    /// <see cref="PlaceableDesiredStateTracker"/>'s remarks for why this replaces the old
    /// <c>ContainerCloseHook.WasEverClosed</c> permanent latch.</summary>
    private readonly PlaceableDesiredStateTracker _desired = new();

    /// <summary>Forgets everything on a module change; ids now name different placeables.</summary>
    public void Invalidate()
    {
        block.PlaceableCount = 0;
        _mines.Invalidate();
        _desired.Clear();
    }

    public void OnFrame()
    {
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

        uint count = Math.Min(block.PlaceableCount, (uint)SharedBlock.PlaceableCapacity);

        for (int i = 0; i < count; i++)
        {
            if (block.GetPlaceableSlotKind(i) == SharedBlock.PlaceableSlotKindMine)
            {
                _mines.Apply(block.GetPlaceable(i).ObjectId);
                continue;
            }

            Apply(block.GetPlaceable(i));
        }
    }

    private void Apply(PlaceableView desired)
    {
        if (desired.ObjectId == 0 || desired.ObjectId == Odyssey.ObjectInvalid)
        {
            return;
        }

        nint placeable = Odyssey.ResolvePlaceable(desired.ObjectId);
        if (placeable == 0)
        {
            return;
        }

        // Disarm is unaffected by this pass and stays forward-only — nothing shipped ever
        // re-arms a trap, so there is no "desired.Disarmed == false" branch to add.
        if (desired.Disarmed && Odyssey.IsPlaceableTrapped(placeable))
        {
            Odyssey.DisarmPlaceable(placeable);
            LogFew($"placeable 0x{desired.ObjectId:X}: trap disarmed on the session's authority");
        }

        // Do not keep asserting an unchanged (open, unlocked) snapshot: a local story script (or
        // the closing player's own panel) may have just changed this placeable, and the external
        // scanner needs a chance to publish that newer state before we re-apply the stale one —
        // the exact guarantee DoorDesiredStateTracker gives doors (DoorController.Apply). This
        // also replaces the old ContainerCloseHook.WasEverClosed latch: that blocked a re-open
        // of this id FOREVER once any real close was seen, which would have refused a genuinely
        // later re-open; this only blocks re-applying the SAME tuple.
        if (!_desired.IsNew(desired.ObjectId, desired.Open, desired.Unlocked))
        {
            return;
        }

        if (desired.Unlocked)
        {
            Odyssey.UnlockPlaceable(placeable);
        }
        else
        {
            Odyssey.LockPlaceable(placeable);
        }

        _desired.Applied(desired.ObjectId, desired.Open, desired.Unlocked);

        bool currentlyOpen = Odyssey.IsPlaceableOpen(placeable);
        if (desired.Open == currentlyOpen)
        {
            return;
        }

        if (desired.Open)
        {
            OdysseyPlaceables.PlayContainerOpenAnimation(placeable);
            Odyssey.SetPlaceableOpen(placeable);
            LogFew($"placeable 0x{desired.ObjectId:X}: opened on the session's authority");
        }
        else
        {
            OdysseyPlaceables.PlayContainerCloseAnimation(placeable);
            OdysseyPlaceables.ClearPlaceableOpenFlag(placeable);
            LogFew($"placeable 0x{desired.ObjectId:X}: closed on the session's authority");
        }
    }

    private void LogFew(string message)
    {
        if (_logged < 20)
        {
            _logged++;
            PluginHost.Log(message);
        }
    }
}
