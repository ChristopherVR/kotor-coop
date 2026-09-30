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
    private readonly Dictionary<uint, uint> _applyGenerations = [];

    /// <summary>Forgets everything on a module change; ids now name different placeables.</summary>
    public void Invalidate()
    {
        block.PlaceableCount = 0;
        _mines.Invalidate();
        _desired.Clear();
        block.ClearPlaceableAcknowledgements();
        PlaceableLocalChanges.Clear(block);
    }

    public void OnFrame()
    {
        // Capture confirmed genuine interaction edges before a stale mirror can
        // overwrite them; the external collector runs only once per second.
        PlaceableLocalChanges.PublishConfirmed(block);
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

            Apply(i, block.GetPlaceable(i));
        }
    }

    private void Apply(int index, PlaceableView desired)
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

        // Applying a delayed close must not also erase an independent local
        // relock before its one-second scanner sees it. Do not acknowledge the
        // retained local tuple: that would prime the scanner shadow and swallow
        // the very change it still needs to publish.
        if (_desired.HasUnpublishedLockChange(desired.ObjectId,
                !Odyssey.IsPlaceableLocked(placeable), desired.Unlocked))
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
        if (desired.Open && !currentlyOpen)
        {
            OdysseyPlaceables.PlayContainerOpenAnimation(placeable);
            Odyssey.SetPlaceableOpen(placeable);
            LogFew($"placeable 0x{desired.ObjectId:X}: opened on the session's authority");
        }
        else if (!desired.Open && currentlyOpen)
        {
            OdysseyPlaceables.PlayContainerCloseAnimation(placeable);
            OdysseyPlaceables.ClearPlaceableOpenFlag(placeable);
            LogFew($"placeable 0x{desired.ObjectId:X}: closed on the session's authority");
        }

        uint generation = _applyGenerations.GetValueOrDefault(desired.ObjectId) + 1;
        if (generation == 0) generation = 1;
        _applyGenerations[desired.ObjectId] = generation;
        byte actual = (byte)((Odyssey.IsPlaceableOpen(placeable) ? 1 : 0)
            | (Odyssey.IsPlaceableLocked(placeable) ? 0 : 2)
            | (Odyssey.IsPlaceableTrapped(placeable) ? 4 : 0));
        block.AcknowledgePlaceable(index, desired.ObjectId, actual, generation);
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
