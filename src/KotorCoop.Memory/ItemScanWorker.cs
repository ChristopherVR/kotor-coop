namespace KotorCoop.Memory;

/// <summary>
/// Runs vtable pattern sweeps of the game's committed memory on a background thread, so the
/// agent's pump thread never waits for one.
///
/// <para>Why. A sweep looks for a 4-byte vtable constant across every private committed region
/// of the game — about 180 MiB of <c>ReadProcessMemory</c> — and costs, measured on the rig
/// (2026-08-29, <c>perf_trace</c>), 180-660 ms per pass. Every sweep used to run inline on
/// <c>CoopAgent.PumpOnce</c>'s thread, which is the same thread that reads the local player and
/// writes the remote players' positions into the game. A pass therefore landed BETWEEN two
/// position writes and stopped the avatar stream dead for its whole duration: both players saw
/// the other freeze and then dash to catch up, which is exactly the "moves in bursts" report
/// this was built to chase. The two callers are the item tag/address cache
/// (<c>KotorGameBridge.ServiceItemTagCache</c>) and the world-object address cache
/// (<c>KotorGameBridge.CachedScan</c> — doors, placeables, encounters).</para>
///
/// <para>Serving stale while refreshing is sound for both, and for the same reason each cache
/// already existed: these are addresses of objects that do not move while a module is loaded.
/// Only the address list ages; every field read through an address is still live.</para>
///
/// <para>Threading. A sweep is pure reads — <see cref="ProcessMemory.TryRead"/> is a
/// <c>ReadProcessMemory</c> call on a handle, safe to issue from several threads at once, and
/// <see cref="PatternScanner"/> holds no mutable state. The worker hands back a plain list of
/// addresses; the CALLER, on its own thread, does every follow-up read and every dictionary
/// write that turns those addresses into a cache. The expensive half is the half that is safe
/// to move, and the half that mutates bridge state stays exactly where it always ran.</para>
///
/// <para>⚠ ONE sweep at a time across all vtables. A second concurrent 180 MiB sweep would cost
/// the game more than the inline version this replaces — the point is to stop hammering the
/// target process, not to hammer it from somewhere else. Requests for other vtables queue
/// behind whichever is running.</para>
/// </summary>
internal sealed class ItemScanWorker(ProcessMemory memory)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, List<nint>> _ready = [];
    private readonly Queue<(uint Vtable, int Limit)> _pending = new();

    private Task? _running;
    private uint _runningVtable;

    /// <summary>Why the last sweep gave up, or null if it did not. Surfaced rather than
    /// swallowed: a sweep that throws every time would otherwise look exactly like a sweep that
    /// keeps finding nothing.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Asks for a sweep of <paramref name="vtable"/> if one is not already running, queued, or
    /// finished-and-uncollected. Cheap and idempotent — safe to call every tick.
    /// </summary>
    public void Request(uint vtable, int limit)
    {
        lock (_gate)
        {
            if (_ready.ContainsKey(vtable) || _runningVtable == vtable)
            {
                return;
            }

            foreach ((uint queued, _) in _pending)
            {
                if (queued == vtable)
                {
                    return;
                }
            }

            _pending.Enqueue((vtable, limit));
            StartNextLocked();
        }
    }

    /// <summary>
    /// Hands over one finished sweep's hits, exactly once, and returns false whenever there is
    /// nothing finished for this vtable — the normal answer on all but one tick in several
    /// hundred.
    /// </summary>
    public bool TryTakeHits(uint vtable, out List<nint> hits)
    {
        lock (_gate)
        {
            if (_ready.Remove(vtable, out List<nint>? found))
            {
                hits = found;
                return true;
            }

            hits = [];
            return false;
        }
    }

    /// <summary>Whether any sweep is currently running — so a caller can tell "the cache is
    /// stale and nothing is being done about it" from "a refresh is already on its way".</summary>
    public bool ScanInFlight
    {
        get
        {
            lock (_gate)
            {
                return _running is not null;
            }
        }
    }

    private void StartNextLocked()
    {
        if (_running is not null || _pending.Count == 0)
        {
            return;
        }

        (uint vtable, int limit) = _pending.Dequeue();
        _runningVtable = vtable;
        _running = Task.Run(() => Scan(vtable, limit));
    }

    private void Scan(uint vtable, int limit)
    {
        List<nint> hits;

        try
        {
            BytePattern pattern = BytePattern.FromBytes(BitConverter.GetBytes(vtable));
            hits = new PatternScanner(memory).ScanAll(pattern, ScanScope.PrivateCommitted, limit);
            LastError = null;
        }
        catch (Exception ex)
        {
            // A sweep racing a process that is exiting reads through a handle that is closing.
            // That is a normal end-of-session event, not a reason to tear anything down: report
            // it, publish nothing, and let the next Request try again.
            LastError = ex.Message;
            hits = [];
        }

        lock (_gate)
        {
            // An empty result from a failed sweep must NOT be published as "this vtable has no
            // objects" — every consumer would clear a perfectly good cache on the strength of a
            // read that never happened.
            if (hits.Count > 0 || LastError is null)
            {
                _ready[vtable] = hits;
            }

            _running = null;
            _runningVtable = 0;
            StartNextLocked();
        }
    }
}
