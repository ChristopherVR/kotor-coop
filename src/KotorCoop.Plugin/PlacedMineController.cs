using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// The plugin half of party-placed floor-mine replication (docs/research/mines.md §14,
/// coop.ini <c>placed_mine_sync</c>, default OFF, NEEDS-LIVE). Runs on the game thread from the
/// frame hook, and owns this instance's <see cref="PartyPlacedMineIdentityMap"/>.
///
/// <list type="number">
/// <item><b>Capture</b> — scans the local area's trigger list (area+0x12C, the list the engine's
/// own party-mine cap counts) for a party-placed mine (+0x2BC and +0x314 set) this controller
/// does not know yet: that is a placement by a party member here. Its record goes to the agent
/// through the capture ring; its local id never leaves the process. Chosen over a detour on
/// <c>AIActionSetMine</c> because it is read-only, catches every placing path (the player, a
/// companion's AI, a script), and needs no mid-function splice in a 0x1000-byte routine.
/// Mines already present when a scope begins (loaded from a save, or placed before the session)
/// are baselined and never published.</item>
/// <item><b>Gone</b> — a mapped (or still-pending) mine that stops validating for two stable
/// scans was detonated, disarmed or recovered here by the engine itself; the serial (or the
/// capture) is reported through the gone ring.</item>
/// <item><b>Roster</b> — a mapped serial the agent's roster no longer lists is retired and
/// destroyed through the engine's own DestroyObject, once.</item>
/// <item><b>Spawn</b> — one mailbox request per frame: build a replica through
/// <see cref="Odyssey.TrySpawnPartyPlacedMine"/>, or register this instance's own captured mine
/// under the host's serial (never destroyed and rebuilt: the placer's mine is the engine's own
/// object with the engine's own item bookkeeping behind it).</item>
/// <item><b>Fire gate</b> — publishes <see cref="PlacedMineFireRule.SuppressPartyMines"/> to
/// <see cref="PlacedMineFireGateHook"/> every frame.</item>
/// </list>
/// </summary>
internal sealed class PlacedMineController(SharedBlockView block)
{
    /// <summary>coop.ini <c>placed_mine_sync</c>. Three readers agree on the default (OFF):
    /// this, <c>CoopConfig.PlacedMineSync</c> and <c>AgentComposition</c>'s fallback.</summary>
    public static readonly bool Enabled = PluginConfig.ReadBool("placed_mine_sync", false);

    /// <summary>Frames between scans (~6 a second at 60 fps).</summary>
    private const int ScanIntervalFrames = 10;

    private readonly PartyPlacedMineIdentityMap _map = new();
    private readonly PlacedMineVanishTracker _vanish = new();

    /// <summary>Own captures still waiting for the host's serial: capture serial -> local id.</summary>
    private readonly Dictionary<uint, uint> _pendingCaptures = [];

    /// <summary>Own captures that vanished before their serial arrived.</summary>
    private readonly HashSet<uint> _vanishedCaptures = [];

    /// <summary>Every local party-mine id this scope already accounts for: baselined, captured,
    /// replica or registered. The capture scan publishes only an id outside this set.</summary>
    private readonly HashSet<uint> _knownIds = [];

    /// <summary>Ids built here as replicas (for <c>minestate</c>).</summary>
    private readonly HashSet<uint> _replicaIds = [];

    /// <summary>Retired serials whose DestroyObject the VM refused; retried each frame.</summary>
    private readonly List<(ulong Serial, uint LocalId, int Tries)> _destroyRetries = [];

    private ulong _sessionGeneration;
    private bool _sessionWasActive;
    private ulong _epoch;
    private nint _scopeArea;
    private bool _baselined;
    private int _frame;
    private uint _handledSpawnRequest;
    private bool _handledSeeded;
    private uint _loggedRefusals;
    private bool _loggedSuppress;
    private int _logBudget = 400;

    public void Invalidate()
    {
        _map.Invalidate();
        _vanish.Clear();
        _pendingCaptures.Clear();
        _vanishedCaptures.Clear();
        _knownIds.Clear();
        _replicaIds.Clear();
        _destroyRetries.Clear();
        _epoch = 0;
        _scopeArea = 0;
        _baselined = false;
        block.PlacedMineMappedCount = 0;
    }

    public void OnFrame()
    {
        bool sessionActive = block.Command == SharedBlock.CommandSpawnAndDrive;
        UpdateFireGate(sessionActive);

        if (!Enabled)
        {
            AnswerDisabled();
            return;
        }

        if (sessionActive && !_sessionWasActive)
        {
            // A new session (or a reconnect): serials from the previous one mean nothing now.
            _sessionGeneration++;
            Invalidate();
        }

        _sessionWasActive = sessionActive;
        if (!sessionActive || !Odyssey.TryGetWorldEpoch(out ulong epoch))
        {
            return;
        }

        if (epoch != _epoch)
        {
            Invalidate();
            _epoch = epoch;
        }

        PartyPlacedMineScope scope = new(_sessionGeneration, _epoch);
        if (!_map.BeginScope(scope))
        {
            return;
        }

        nint area = Odyssey.GetPlacedMineArea();
        if (area == 0)
        {
            return;
        }

        if (_scopeArea == 0)
        {
            _scopeArea = area;
        }

        RetryDestroys();

        if (!_baselined)
        {
            Baseline(area);
            return;
        }

        HandleSpawnRequest(scope);

        if (++_frame % ScanIntervalFrames != 0)
        {
            return;
        }

        bool worldStable = area == _scopeArea && Odyssey.GetPlayerCreaturePointer() != 0;
        ScanCaptures(area);
        ScanVanished(scope, worldStable);
        ReconcileRoster(scope);
        block.PlacedMineMappedCount = (uint)_map.Count;
    }

    // ---- fire gate -------------------------------------------------------------------------

    private void UpdateFireGate(bool sessionActive)
    {
        bool roleKnown = block.TryGetLocalSessionRole(out bool isHost);
        bool suppress = PlacedMineFireRule.SuppressPartyMines(Enabled, sessionActive, roleKnown, isHost);
        PlacedMineFireGateHook.SuppressPartyMines = suppress;
        if (suppress != _loggedSuppress)
        {
            _loggedSuppress = suppress;
            Log(suppress
                ? "placed mines: this instance is a client - party-placed mines here are inert; the host's copies own every detonation"
                : "placed mines: party-placed mines fire normally on this instance");
        }

        uint refusals = PlacedMineFireGateHook.Refusals;
        if (refusals != _loggedRefusals)
        {
            _loggedRefusals = refusals;
            block.PlacedMineFireSuppressed = refusals;
            Log($"placed mines: fire gate refused a party-placed mine on this client (total {refusals}); the host's copy decides");
        }
    }

    private void AnswerDisabled()
    {
        uint request = block.PlacedMineSpawnRequest;
        if (request != 0 && request != block.PlacedMineSpawnApplied)
        {
            block.AckPlacedMineSpawn(request, PlacedMineSpawnStatus.Disabled, 0);
        }
    }

    // ---- baseline and capture --------------------------------------------------------------

    private void Baseline(nint area)
    {
        Span<uint> ids = stackalloc uint[64];
        int count = Odyssey.ListPartyPlacedMines(area, ids);
        for (int i = 0; i < count; i++)
        {
            _knownIds.Add(ids[i]);
        }

        _baselined = true;
        if (count > 0)
        {
            Log($"placed mines: {count} party-placed mine(s) already in this area baselined - they stay local");
        }
    }

    private void ScanCaptures(nint area)
    {
        Span<uint> ids = stackalloc uint[64];
        int count = Odyssey.ListPartyPlacedMines(area, ids);
        for (int i = 0; i < count; i++)
        {
            uint id = ids[i];
            if (_knownIds.Contains(id) || !Odyssey.IsLivePartyPlacedMine(id, out nint trigger))
            {
                continue;
            }

            _knownIds.Add(id);
            PlacedMineSpec spec = Odyssey.ReadPartyPlacedMine(trigger);
            if (!spec.IsValid)
            {
                Log($"placed mines: new party mine 0x{id:X} has an unusable record (row {spec.TrapType}); left local");
                continue;
            }

            uint capture = block.PublishPlacedMineCapture(block.WorldLoadSerial, id, spec);
            _pendingCaptures[capture] = id;
            Log($"placed mines: captured local placement #{capture} (local 0x{id:X}, creator 0x{Odyssey.ReadPlacedMineCreator(trigger):X}, "
                + $"row {spec.TrapType}, DC {spec.DetectDc}/{spec.DisarmDc}, faction {spec.Faction}, "
                + $"at {spec.X:F2},{spec.Y:F2},{spec.Z:F2}, orient {spec.OrientX:F2},{spec.OrientY:F2})");
        }
    }

    // ---- local vanish ----------------------------------------------------------------------

    private const ulong CaptureKeyFlag = 1UL << 63;

    private void ScanVanished(PartyPlacedMineScope scope, bool worldStable)
    {
        List<(ulong Key, bool Alive)> tracked = [];
        foreach ((ulong serial, uint id) in _map.Snapshot())
        {
            tracked.Add((serial, Odyssey.IsLivePartyPlacedMine(id, out _)));
        }

        foreach ((uint capture, uint id) in _pendingCaptures)
        {
            tracked.Add((CaptureKeyFlag | capture, Odyssey.IsLivePartyPlacedMine(id, out _)));
        }

        foreach (ulong key in _vanish.Pass(tracked, worldStable))
        {
            if ((key & CaptureKeyFlag) != 0)
            {
                uint capture = (uint)(key & 0xFFFFFFFF);
                _pendingCaptures.Remove(capture);
                _vanishedCaptures.Add(capture);
                block.PublishPlacedMineGone(block.WorldLoadSerial, 0, capture);
                Log($"placed mines: own capture #{capture} is gone here before its serial arrived - reported");
                continue;
            }

            if (_map.TryGetLocalObjectId(scope, key, out uint localId) && _map.Retire(scope, key))
            {
                block.PublishPlacedMineGone(block.WorldLoadSerial, (uint)key, 0);
                Log($"placed mines: mine #{key} (local 0x{localId:X}) is gone here (the engine removed it) - reported to the session");
            }
        }
    }

    // ---- roster ----------------------------------------------------------------------------

    private void ReconcileRoster(PartyPlacedMineScope scope)
    {
        Span<uint> serials = stackalloc uint[SharedBlock.PlacedMineRosterCapacity];
        if (!block.TryReadPlacedMineRoster(serials, out int count, out uint highest, out uint worldSerial, out _))
        {
            return;
        }

        ReadOnlySpan<uint> listed = serials[..count];
        uint current = block.WorldLoadSerial;
        foreach ((ulong serial, uint localId) in _map.Snapshot())
        {
            if (!PlacedMineRosterRule.ShouldRemove(serial, worldSerial, current, highest, listed))
            {
                continue;
            }

            // Retire FIRST: from here the serial names nothing, so the vanish scan can never
            // echo the removal back as a local detonation.
            _map.Retire(scope, serial);
            _vanish.Forget(serial);
            DestroyLocal(serial, localId, 0);
        }
    }

    private void DestroyLocal(ulong serial, uint localId, int tries)
    {
        if (!Odyssey.IsLivePartyPlacedMine(localId, out _))
        {
            return; // already gone here
        }

        if (Odyssey.DestroyObjectById(localId, out int failure))
        {
            Log($"placed mines: mine #{serial} (local 0x{localId:X}) removed on the session's authority (engine DestroyObject)");
            return;
        }

        if (tries < 30)
        {
            _destroyRetries.Add((serial, localId, tries + 1));
        }
        else
        {
            Log($"placed mines: DestroyObject kept refusing mine #{serial} (local 0x{localId:X}, failure {failure}); giving up");
        }
    }

    private void RetryDestroys()
    {
        if (_destroyRetries.Count == 0)
        {
            return;
        }

        List<(ulong Serial, uint LocalId, int Tries)> due = [.. _destroyRetries];
        _destroyRetries.Clear();
        foreach ((ulong serial, uint localId, int tries) in due)
        {
            DestroyLocal(serial, localId, tries);
        }
    }

    // ---- spawn mailbox ---------------------------------------------------------------------

    private void HandleSpawnRequest(PartyPlacedMineScope scope)
    {
        uint request = block.PlacedMineSpawnRequest;
        if (!_handledSeeded)
        {
            // A block that outlived an earlier session in this process may hold an old request.
            _handledSeeded = true;
            _handledSpawnRequest = block.PlacedMineSpawnApplied;
        }

        if (request == 0 || request == _handledSpawnRequest || request == block.PlacedMineSpawnApplied)
        {
            return;
        }

        uint serial = block.PlacedMineSpawnSerial;
        uint capture = block.PlacedMineSpawnCapture;
        PlacedMineSpawnStatus status;
        uint localId = 0;

        if (block.PlacedMineSpawnWorldSerial != block.WorldLoadSerial)
        {
            status = PlacedMineSpawnStatus.StaleWorld;
        }
        else if (_map.TryGetLocalObjectId(scope, serial, out localId))
        {
            status = capture != 0 ? PlacedMineSpawnStatus.Registered : PlacedMineSpawnStatus.Spawned;
        }
        else if (capture != 0)
        {
            status = RegisterOwn(scope, serial, capture, out localId);
        }
        else
        {
            status = SpawnReplica(scope, serial, out localId);
        }

        if (status == PlacedMineSpawnStatus.Retry)
        {
            // Leave the request unhandled; the agent's timeout/retry decides when to ask again.
            block.AckPlacedMineSpawn(request, status, 0);
            _handledSpawnRequest = request;
            return;
        }

        _handledSpawnRequest = request;
        block.AckPlacedMineSpawn(request, status, localId);
        block.PlacedMineMappedCount = (uint)_map.Count;
    }

    private PlacedMineSpawnStatus RegisterOwn(PartyPlacedMineScope scope, uint serial, uint capture, out uint localId)
    {
        localId = 0;
        if (_vanishedCaptures.Contains(capture))
        {
            Log($"placed mines: host named own capture #{capture} mine #{serial}, but it is already gone here");
            return PlacedMineSpawnStatus.OwnCaptureGone;
        }

        if (!_pendingCaptures.TryGetValue(capture, out uint id))
        {
            Log($"placed mines: host named own capture #{capture} mine #{serial}, but this world never captured it");
            return PlacedMineSpawnStatus.Refused;
        }

        if (!_map.Register(scope, serial, id))
        {
            Log($"placed mines: identity map refused own capture #{capture} as mine #{serial} (order/capacity)");
            return PlacedMineSpawnStatus.Refused;
        }

        _pendingCaptures.Remove(capture);
        _vanish.Forget(CaptureKeyFlag | capture);
        localId = id;
        Log($"placed mines: own placement #{capture} (local 0x{id:X}) is session mine #{serial} - kept, not rebuilt");
        return PlacedMineSpawnStatus.Registered;
    }

    private PlacedMineSpawnStatus SpawnReplica(PartyPlacedMineScope scope, uint serial, out uint localId)
    {
        localId = 0;
        if (!_map.CanRegisterNew(scope, serial))
        {
            Log($"placed mines: mine #{serial} refused before building (out of order, retired, or {PartyPlacedMineIdentityMap.MaxActiveMines} already active)");
            return PlacedMineSpawnStatus.Refused;
        }

        PlacedMineSpec spec = block.ReadPlacedMineSpawnSpec();
        uint creator = Odyssey.GetPlayerObjectId();
        if (!Odyssey.TrySpawnPartyPlacedMine(spec, creator, $"COOP_PMINE_{serial}", out uint id, out string diagnostic))
        {
            Log($"placed mines: mine #{serial} not built - {diagnostic}");
            return diagnostic.StartsWith("no local player", StringComparison.Ordinal) || diagnostic.StartsWith("traps.2da is not", StringComparison.Ordinal)
                ? PlacedMineSpawnStatus.Retry
                : PlacedMineSpawnStatus.Refused;
        }

        // Known BEFORE the next capture scan, so the replica is never mistaken for a placement.
        _knownIds.Add(id);
        _replicaIds.Add(id);
        if (!_map.Register(scope, serial, id))
        {
            Log($"placed mines: built mine #{serial} as 0x{id:X} but the identity map refused it; removing the orphan");
            Odyssey.DestroyObjectById(id, out _);
            return PlacedMineSpawnStatus.Refused;
        }

        localId = id;
        Log($"placed mines: replica of session mine #{serial} spawned - {diagnostic}, tag COOP_PMINE_{serial}");
        return PlacedMineSpawnStatus.Spawned;
    }

    // ---- transition flush ------------------------------------------------------------------

    /// <summary>
    /// On a CLIENT, destroys one replica before the outgoing module is stored, so a replica is
    /// never baked into the client's saved area as a permanent (inert) party mine — the same
    /// "everything this plugin spawned must be gone before the area is stored" rule the phantom
    /// and item flushes follow. The serial is retired first, so the vanish scan never reports
    /// the flush as a detonation. The host keeps its copies: they are the canonical world, and
    /// a client's own placed mine is the engine's own object. True while one was destroyed.
    /// </summary>
    public bool FlushOneReplica()
    {
        if (!Enabled || !PlacedMineFireGateHook.SuppressPartyMines || _replicaIds.Count == 0)
        {
            return false;
        }

        PartyPlacedMineScope scope = _map.Scope;
        foreach ((ulong serial, uint id) in _map.Snapshot())
        {
            if (!_replicaIds.Contains(id))
            {
                continue;
            }

            _map.Retire(scope, serial);
            _vanish.Forget(serial);
            _replicaIds.Remove(id);
            if (Odyssey.IsLivePartyPlacedMine(id, out _) && Odyssey.DestroyObjectById(id, out _))
            {
                Log($"placed mines: replica #{serial} (local 0x{id:X}) flushed before the module is stored");
                return true;
            }
        }

        _replicaIds.Clear();
        return false;
    }

    // ---- dev verbs -------------------------------------------------------------------------

    /// <summary>
    /// <c>placemine</c>: builds a party-placed mine at the local player's feet through the same
    /// construction sequence, as if a party member here had placed it. It is NOT recorded as a
    /// replica, so the next capture scan publishes it exactly like a real placement — that is
    /// the point: the verb exercises capture, serial, peer spawn and the fire gate without the
    /// radial menu. It consumes no item (a real placement's item stays the engine's business).
    /// </summary>
    public string PlaceTestMine(byte trapType, short detectDc, short disarmDc, out bool ok)
    {
        ok = false;
        if (!Odyssey.TryBuildLocalPlacedMineSpec(trapType, detectDc, disarmDc, out PlacedMineSpec spec, out uint creator))
        {
            return "placemine: no local player";
        }

        if (!Odyssey.TrySpawnPartyPlacedMine(spec, creator, null, out uint id, out string diagnostic))
        {
            return $"placemine: refused - {diagnostic}";
        }

        ok = true;
        string sync = Enabled ? "the next scan captures it" : "placed_mine_sync is off, so it stays local";
        return $"placemine: {diagnostic}; {sync}";
    }

    /// <summary>Resolves a session serial to this instance's local id (the placedmineaction verb).</summary>
    public bool TryResolveSerial(uint serial, out uint localId) =>
        _map.TryGetLocalObjectId(_map.Scope, serial, out localId);

    /// <summary><c>minestate</c>: every mapping, capture and counter, one line each to the log;
    /// a one-line summary as the answer.</summary>
    public string Describe()
    {
        StringBuilder log = new();
        List<(ulong Serial, uint LocalObjectId)> pairs = _map.Snapshot();
        bool route = Odyssey.VerifyPlacedMineRoute(out string routeDiagnostic);
        Log($"minestate: enabled={Enabled} scope=({_map.Scope.SessionGeneration},{_map.Scope.WorldEpoch:X}) world={block.WorldLoadSerial} "
            + $"mapped={pairs.Count} pending={_pendingCaptures.Count} known={_knownIds.Count} suppress={PlacedMineFireGateHook.SuppressPartyMines} "
            + $"gate={(PlacedMineFireGateHook.Installed ? "installed" : "absent")} refusals={PlacedMineFireGateHook.Refusals} route={routeDiagnostic}");
        foreach ((ulong serial, uint id) in pairs)
        {
            bool alive = Odyssey.IsLivePartyPlacedMine(id, out nint trigger);
            string where = alive
                ? $"{Odyssey.ReadPartyPlacedMine(trigger).X:F2},{Odyssey.ReadPartyPlacedMine(trigger).Y:F2}"
                : "gone";
            string kind = _replicaIds.Contains(id) ? "replica" : "own";
            Log($"minestate:   #{serial} -> local 0x{id:X} ({kind}, {where})");
            log.Append(log.Length == 0 ? string.Empty : " ").Append($"#{serial}=0x{id:X}{(kind == "replica" ? "r" : "o")}");
        }

        foreach ((uint capture, uint id) in _pendingCaptures)
        {
            Log($"minestate:   capture #{capture} -> local 0x{id:X} (awaiting the host's serial)");
        }

        return $"minestate: {(Enabled ? "on" : "off")}, {pairs.Count} mapped [{log}], {_pendingCaptures.Count} pending, "
            + $"gate {(PlacedMineFireGateHook.SuppressPartyMines ? "inert (client)" : "armed")}, refusals {PlacedMineFireGateHook.Refusals}, "
            + $"route {(route ? "ok" : "REFUSED")}";
    }

    private void Log(string message)
    {
        if (_logBudget > 0 || message.StartsWith("minestate", StringComparison.Ordinal))
        {
            _logBudget--;
            PluginHost.Log(message);
        }
    }
}
