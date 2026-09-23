namespace KotorCoop.Plugin;

/// <summary>
/// Identity of one local engine world for party-placed mine replication. Both fields are
/// local lifecycle tokens: session generation changes on reconnect/session replacement, and
/// world epoch changes when the engine replaces its module world. Neither value is a native
/// object id or may be sent as one.
/// </summary>
internal readonly record struct PartyPlacedMineScope(ulong SessionGeneration, ulong WorldEpoch)
{
    public bool IsValid => SessionGeneration != 0 && WorldEpoch != 0;
}

/// <summary>
/// Maps the host's session-wide mine serial to this engine's own dynamic object id. Each game
/// instance owns one map. A serial has no meaning outside its exact session/world scope; a
/// reconnect or world replacement starts an empty map before any new event can resolve.
///
/// This is the identity boundary for the future party-mine transport. It deliberately does not
/// create mines, infer the dynamic-id range, or validate native pointers. The consumer must
/// validate the resolved id as a live party-placed trigger before invoking an engine action.
/// </summary>
internal sealed class PartyPlacedMineIdentityMap
{
    /// <summary>
    /// Retail KOTOR refuses player-party mine placement once the area's count reaches 15
    /// (CSWSArea::GetPlayerCanSetMines, docs/research/mines.md §2). Keep the active identity
    /// table to that same bound; retired ids need no tombstone because authority serials are
    /// strictly increasing within a scope and <see cref="_highestSerial"/> fences reuse.
    /// </summary>
    public const int MaxActiveMines = 15;

    private readonly Dictionary<ulong, uint> _objectIdBySerial = [];
    private readonly Dictionary<uint, ulong> _serialByObjectId = [];
    private PartyPlacedMineScope _scope;
    private ulong _highestSerial;

    public PartyPlacedMineScope Scope => _scope;

    /// <summary>
    /// Starts tracking identities for a loaded world. A changed session generation or world
    /// epoch clears both directions. Call this from the mine subsystem's existing lifecycle
    /// owner before it accepts spawn/remove events; repeated calls for the same scope are safe.
    /// </summary>
    public bool BeginScope(PartyPlacedMineScope scope)
    {
        if (!scope.IsValid)
        {
            return false;
        }

        if (scope == _scope)
        {
            return true;
        }

        _objectIdBySerial.Clear();
        _serialByObjectId.Clear();
        _scope = scope;
        _highestSerial = 0;
        return true;
    }

    /// <summary>
    /// Invalidates every identity while no valid local world/session scope is available, such
    /// as the teardown frame where the engine epoch is zero.
    /// </summary>
    public void Invalidate()
    {
        _objectIdBySerial.Clear();
        _serialByObjectId.Clear();
        _scope = default;
        _highestSerial = 0;
    }

    /// <summary>
    /// Binds one authoritative spawn serial to the dynamic id returned by this engine's
    /// successful spawn. The authority must allocate serials monotonically and the consumer must
    /// register spawn events in serial order. The same active pair is idempotent; serial reuse,
    /// out-of-order serials, conflicting serial or local-id ownership, and capacity overflow are
    /// rejected. IDs are treated as opaque local identities: only zero is rejected here because
    /// the native dynamic-id range is intentionally not assumed.
    /// </summary>
    public bool Register(PartyPlacedMineScope scope, ulong serial, uint localObjectId)
    {
        if (scope != _scope || !scope.IsValid || serial == 0 || localObjectId == 0)
        {
            return false;
        }

        if (_objectIdBySerial.TryGetValue(serial, out uint existingObjectId))
        {
            return existingObjectId == localObjectId;
        }

        // This also rejects a duplicate spawn delivered after retirement. A scalar high-water
        // mark gives the no-resurrection guarantee without retaining every retired serial.
        if (serial <= _highestSerial || _objectIdBySerial.Count >= MaxActiveMines)
        {
            return false;
        }

        if (_serialByObjectId.TryGetValue(localObjectId, out ulong existingSerial))
        {
            return existingSerial == serial;
        }

        _objectIdBySerial.Add(serial, localObjectId);
        _serialByObjectId.Add(localObjectId, serial);
        _highestSerial = serial;
        return true;
    }

    /// <summary>Resolves a canonical serial only inside the currently active local scope.</summary>
    public bool TryGetLocalObjectId(PartyPlacedMineScope scope, ulong serial, out uint localObjectId)
    {
        if (scope == _scope && scope.IsValid && serial != 0
            && _objectIdBySerial.TryGetValue(serial, out localObjectId))
        {
            return true;
        }

        localObjectId = 0;
        return false;
    }

    /// <summary>Resolves a local engine id back to its canonical serial in the active scope.</summary>
    public bool TryGetSerial(PartyPlacedMineScope scope, uint localObjectId, out ulong serial)
    {
        if (scope == _scope && scope.IsValid && localObjectId != 0
            && _serialByObjectId.TryGetValue(localObjectId, out serial))
        {
            return true;
        }

        serial = 0;
        return false;
    }

    /// <summary>
    /// Retires a mine after its local engine object is confirmed gone. The reverse entry is
    /// removed only when it still belongs to this serial, so a delayed retirement cannot erase
    /// another mine that later reused the local dynamic id.
    /// </summary>
    public bool Retire(PartyPlacedMineScope scope, ulong serial)
    {
        if (scope != _scope || !scope.IsValid || serial == 0
            || !_objectIdBySerial.Remove(serial, out uint localObjectId))
        {
            return false;
        }

        if (_serialByObjectId.TryGetValue(localObjectId, out ulong currentSerial)
            && currentSerial == serial)
        {
            _serialByObjectId.Remove(localObjectId);
        }

        return true;
    }
}
