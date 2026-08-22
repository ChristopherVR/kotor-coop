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

        if (desired.Unlocked && Odyssey.IsPlaceableLocked(placeable))
        {
            Odyssey.UnlockPlaceable(placeable);
            LogFew($"placeable 0x{desired.ObjectId:X}: unlocked on the session's authority");
        }

        if (desired.Disarmed && Odyssey.IsPlaceableTrapped(placeable))
        {
            Odyssey.DisarmPlaceable(placeable);
            LogFew($"placeable 0x{desired.ObjectId:X}: trap disarmed on the session's authority");
        }

        // LIVE-PASS ADDITION (2026-08-22): the WasEverClosed latch. This channel is
        // deliberately monotonic (PlaceableFlags.cs: "a placeable only ever advances ... never
        // the reverse") and the agent polls every placeable's own +0x338 about once a second
        // (KotorGameBridge.CollectChangedPlaceableState) — so once ANY player has ever really
        // opened a container, this Apply pass would otherwise re-assert it open forever. Live-
        // confirmed to clobber a fresh real close within ~100ms of it happening, on every
        // instance, permanently — and confirmed AGAIN that a time-boxed defence is not enough
        // (the reassertion pressure is continuous, so any timeout eventually loses the race; see
        // ContainerCloseHook's own remarks for that measurement). Deferring here — rather than
        // making the channel itself expressly "closed" (a wire/merge change out of scope for
        // this pass) — is the smallest fix that lets a real close actually stick.
        if (desired.Open && !Odyssey.IsPlaceableOpen(placeable) && !ContainerCloseHook.WasEverClosed(desired.ObjectId))
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
