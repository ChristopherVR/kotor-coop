namespace KotorCoop.Plugin;

/// <summary>
/// Applies the session's placeable truth (chests, corpses, switches, containers) to this
/// instance, on the game's thread. The sibling of <see cref="DoorController"/>: placeable
/// object ids match across instances (same save, deterministic GIT order) and their state only
/// advances (closed -&gt; opened/searched, locked -&gt; unlocked), so the reliable max-merge flag
/// channel carries them with no new transport.
///
/// Simpler than a door: a placeable has no walkmesh coupling, so applying is a plain field
/// write — unlock is a write to the locked field, "opened" a write to the open field. There is
/// no swing animation to defer or commit. The cosmetic open pose (an engine vtable call) is
/// deliberately not done here; it is a NEEDS-LIVE guessed-ABI path and the field write desyncs
/// nothing structural without it.
/// </summary>
internal sealed class PlaceableController(SharedBlockView block)
{
    /// <summary>Frames between passes; placeable state changes are rare and never urgent.</summary>
    private const int IntervalFrames = 40;

    private int _countdown;
    private int _logged;

    /// <summary>Forgets everything on a module change; ids now name different placeables.</summary>
    public void Invalidate() => block.PlaceableCount = 0;

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
            Apply(block.GetPlaceable(i));
        }
    }

    private void Apply(DoorView desired)
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

        // DoorView.Unlocked carries the placeable's unlocked bit; OpenState carries "opened".
        if (desired.Unlocked && Odyssey.IsPlaceableLocked(placeable))
        {
            Odyssey.UnlockPlaceable(placeable);
            LogFew($"placeable 0x{desired.ObjectId:X}: unlocked on the session's authority");
        }

        if (desired.OpenState != 0 && !Odyssey.IsPlaceableOpen(placeable))
        {
            Odyssey.SetPlaceableOpen(placeable);
            LogFew($"placeable 0x{desired.ObjectId:X}: marked opened on the session's authority");
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
