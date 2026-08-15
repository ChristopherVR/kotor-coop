namespace KotorCoop.Plugin;

/// <summary>
/// Shows each remote peer on the M-screen map as a live map-note pin. The engine's map
/// draw re-reads every map-note waypoint's position each frame, so a marker whose position
/// this controller updates per pass tracks its peer without any GUI code — the same
/// data-driven use of an engine surface the phantom makes of the creature system.
///
/// One marker per remote slot, spawned lazily beside the phantom (a slot with a phantom
/// has a peer worth pointing at) and destroyed in the pre-store flush window, because a
/// waypoint left in the area is serialised into the saved module exactly as a stray
/// creature would be.
/// </summary>
internal sealed class MapMarkerController(SharedBlockView block)
{
    /// <summary>Frames between passes. The pin only needs to keep up with the map's own
    /// draw, which the player sees at render rate, not the ~317 Hz hook rate.</summary>
    private const int IntervalFrames = 12;

    private readonly nint[] _markers = new nint[SharedBlock.SlotCapacity];
    private readonly uint[] _markerIds = new uint[SharedBlock.SlotCapacity];
    private int _countdown;
    private int _spawnCooldownFrames;
    private int _logged;

    /// <summary>
    /// Forgets every marker without touching the engine — the objects behind these
    /// pointers are freed by UnloadModule. The next stable frame respawns them.
    /// </summary>
    public void Invalidate()
    {
        for (int i = 0; i < _markers.Length; i++)
        {
            _markers[i] = 0;
            _markerIds[i] = 0;
        }
    }

    /// <summary>
    /// Destroys one live marker through the engine, for the pre-store flush. One per call
    /// keeps the flush cheap; returns false once nothing is left to destroy.
    /// </summary>
    public bool FlushOneBody()
    {
        for (int i = 0; i < _markers.Length; i++)
        {
            if (_markers[i] == 0)
            {
                continue;
            }

            Odyssey.DestroyMapMarker(_markers[i]);
            _markers[i] = 0;
            _markerIds[i] = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Destroys every peer pin this controller created and reports how many are left (always
    /// zero — <c>DestroyMapMarker</c> reports no failure, so there is nothing to retry). Part
    /// of the session release for the same reason as the phantoms: a waypoint the session
    /// created is still an object the module store would serialise.
    /// </summary>
    public int ReleaseSession()
    {
        while (FlushOneBody())
        {
            // One per call by construction; sweep them all, teardown has nothing waiting.
        }

        return 0;
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

        if (_spawnCooldownFrames > 0)
        {
            _spawnCooldownFrames--;
        }

        int wanted = (int)Math.Min(block.RemoteCount, (uint)SharedBlock.SlotCapacity);

        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            RemoteView remote = i < wanted ? block.GetRemote(i) : default;

            if (!remote.Active)
            {
                // The peer left this slot: drop its marker so a stale pin does not linger.
                if (_markers[i] != 0)
                {
                    Odyssey.DestroyMapMarker(_markers[i]);
                    _markers[i] = 0;
                    _markerIds[i] = 0;
                }

                continue;
            }

            if (_markers[i] == 0)
            {
                // Rate-limit spawns the same way the phantom does: an allocation-plus-
                // AddToArea is the heavy operation here, not the per-frame move.
                if (_spawnCooldownFrames > 0)
                {
                    continue;
                }

                uint id = Odyssey.SpawnMapMarker(remote.Position, out nint marker);
                _spawnCooldownFrames = 30;

                if (id == Odyssey.ObjectInvalid || marker == 0)
                {
                    if (_logged < 5)
                    {
                        _logged++;
                        PluginHost.Log($"map marker: spawn refused for slot {i}");
                    }

                    continue;
                }

                _markers[i] = marker;
                _markerIds[i] = id;

                if (_logged < 5)
                {
                    _logged++;
                    PluginHost.Log($"map marker: slot {i} pinned as 0x{id:X}");
                }

                continue;
            }

            Odyssey.MoveMapMarker(_markers[i], remote.Position);
        }
    }
}
