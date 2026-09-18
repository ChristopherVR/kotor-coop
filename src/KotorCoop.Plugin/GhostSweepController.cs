using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Removes co-op session spawns that a save baked into the module, the moment that save's
/// world comes up.
///
/// THE BUG THIS CLOSES (docs/review-seamless-2026-09-18.md blocker #7, confirmed live the same
/// day): the plugin destroys everything it spawned — peer stand-ins, materialised enemy bodies,
/// replicated ground items, map-note pins — in the "pre-store flush" window that a door
/// transition or a session-commanded warp opens (<c>PluginHost.Frame</c>), because
/// <c>StoreCurrentModule</c> serialises the area on the way out. A MANUAL save opens no such
/// window: the retail quicksave (F4 / <c>DoQuickSave</c>) and the save panel store the module
/// with everything standing. Measured 2026-09-18: a host quicksave with the joiner beside it
/// held 26 creatures against the same module's 25 (one <c>coop_phantom</c>, appearance 114) and
/// 59 waypoints against 58 (one untagged, enabled map note). Every later load of that save —
/// the host reloading it, a joiner adopting it — then carried a ghost NPC and a stray pin, and
/// the object-id counter had moved by two.
///
/// This sweep is the load-time half of the fix and the one that also heals saves ALREADY
/// written that way (any v0.10.0 install that saved with a partner present). It runs once per
/// world epoch, on the frame the epoch flips — <see cref="Odyssey.TryGetWorldEpoch"/> is true
/// only once the module is fully loaded, so every GIT object exists, and it runs before any
/// controller has spawned anything for the new world (the phantom/materialised registries were
/// just invalidated), so anything carrying the stand-in tag is a ghost by construction. Found
/// creatures are destroyed through the same <see cref="Odyssey.DestroyObjectById"/> the flush
/// uses, one per frame with bounded retries, because that call is refused while the script VM
/// is mid-call (module OnEnter scripts run exactly now). Ghost pins are destroyed synchronously
/// through <see cref="Odyssey.DestroyMapMarker"/>.
///
/// The other half — opening the flush window before a manual save so nothing is baked in the
/// first place — needs a hook on the quicksave and save-panel paths and is tracked in
/// docs/research/save-ghosts.md; until then a manual save still bakes spawns, and this sweep
/// makes that harmless.
///
/// MAIN THREAD ONLY, from the epoch-valid section of <c>PluginHost.OnFrame</c>.
/// </summary>
internal sealed unsafe class GhostSweepController
{
    private const int CreatureCapacity = 512;
    private const int MaxGhosts = 32;
    private const int MaxDestroyAttempts = 6;
    private const int RetryIntervalFrames = 10;
    private const int MaxMarkerDestroys = 16;

    private readonly uint[] _pending = new uint[MaxGhosts];
    private readonly int[] _attempts = new int[MaxGhosts];
    private int _pendingCount;
    private int _framesUntilRetry;
    private bool _armed;
    private int _destroyed;
    private int _writtenOff;

    /// <summary>Arms one sweep for the world that just came up. Called on the epoch flip.</summary>
    public void Arm()
    {
        _armed = true;
        Invalidate();
    }

    /// <summary>Forgets every queued ghost: the world that held them is gone.</summary>
    public void Invalidate()
    {
        _pendingCount = 0;
        _framesUntilRetry = 0;
        _destroyed = 0;
        _writtenOff = 0;
    }

    public void OnFrame()
    {
        if (_armed)
        {
            _armed = false;
            Sweep();
        }

        if (_pendingCount == 0)
        {
            return;
        }

        if (_framesUntilRetry > 0)
        {
            _framesUntilRetry--;
            return;
        }

        DestroyOnePending();
    }

    private void Sweep()
    {
        nint* creatures = stackalloc nint[CreatureCapacity];
        int count = CreatureFinder.CollectByVtable(CreatureFinder.CreatureVtable, creatures, CreatureCapacity);

        byte* tagBuf = stackalloc byte[64];
        int stale = 0;
        for (int i = 0; i < count && _pendingCount < MaxGhosts; i++)
        {
            nint creature = creatures[i];

            // ⚠ A vtable match is NOT a live object. The sweep reads every private committed
            // page, and a creature freed by the previous module's unload keeps its vtable dword
            // until the allocator reuses the block — its tag CExoString then points anywhere.
            // First live run (2026-09-18) crashed exactly there (CopyAsciiBounded via
            // ReadObjectTag, rva 0xB69AA). Only a candidate the engine's own object table maps
            // back to this very pointer is real; everything else is skipped untouched.
            uint objectId = *(uint*)(creature + 0x04);
            if (Odyssey.ResolveServerObject(objectId) != creature)
            {
                stale++;
                continue;
            }

            Odyssey.ReadObjectTag(creature, tagBuf, 64);
            string tag = ReadAscii(tagBuf, 64);
            if (!GhostSweepRule.IsGhostCreatureTag(tag))
            {
                continue;
            }

            _pending[_pendingCount] = objectId;
            _attempts[_pendingCount] = 0;
            _pendingCount++;
        }

        int markers = Odyssey.SweepGhostMapMarkers(MaxMarkerDestroys);

        if (_pendingCount == 0 && markers == 0)
        {
            return;
        }

        PluginHost.Log(
            $"ghost sweep: this save carried {_pendingCount} co-op stand-in creature(s) and "
            + $"{markers} stray map pin(s) baked in by a manual save - removing them "
            + $"({count} creature candidate(s) scanned, {stale} stale)");
    }

    private void DestroyOnePending()
    {
        int last = _pendingCount - 1;
        uint objectId = _pending[last];

        if (Odyssey.DestroyObjectById(objectId, out int failure))
        {
            _destroyed++;
            _pendingCount--;
            PluginHost.Log($"ghost sweep: destroyed baked-in stand-in 0x{objectId:X}");
        }
        else if (++_attempts[last] >= MaxDestroyAttempts)
        {
            _writtenOff++;
            _pendingCount--;
            PluginHost.Log(
                $"ghost sweep: GIVING UP on baked-in stand-in 0x{objectId:X} (failure {failure}) "
                + $"after {MaxDestroyAttempts} attempts - it stays until the next load");
        }
        else
        {
            _framesUntilRetry = RetryIntervalFrames;
            return;
        }

        if (_pendingCount == 0)
        {
            PluginHost.Log($"ghost sweep: done - {_destroyed} destroyed, {_writtenOff} written off");
        }
    }

    private static string ReadAscii(byte* buffer, int capacity)
    {
        int length = 0;
        while (length < capacity && buffer[length] != 0)
        {
            length++;
        }

        return length == 0 ? string.Empty : Encoding.ASCII.GetString(buffer, length);
    }
}
