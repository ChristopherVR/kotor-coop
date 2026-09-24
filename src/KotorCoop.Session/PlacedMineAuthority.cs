using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>What <see cref="PlacedMineAuthority.Place"/> did with one placement report.</summary>
public enum PlacedMinePlaceResult
{
    /// <summary>A new canonical mine; its serial is returned.</summary>
    Accepted,

    /// <summary>The same (owner, capture) was already accepted; its original serial is returned.</summary>
    Duplicate,

    /// <summary>The placement is not in the host's current module. The host's copy is the only
    /// one armed against enemies, so a mine the host cannot hold is not replicated.</summary>
    WrongModule,

    /// <summary>The module already holds <see cref="PlacedMineRosterPacket.MaxEntries"/> canonical
    /// mines (the retail per-area party-mine cap).</summary>
    Full,

    /// <summary>The host has not reported a world yet.</summary>
    NoWorld,

    /// <summary>The record failed validation.</summary>
    Invalid,
}

/// <summary>
/// The host's canonical set of party-placed floor mines (docs/research/mines.md §14.3). Pure and
/// thread-safe; <see cref="CoopHost"/> owns one.
///
/// Host snapshot + client intents, never a bidirectional op log (the shared-inventory lesson):
/// clients only ever REPORT ("I placed one", "serial S is gone here"), and this class alone
/// decides what exists. Serials are session-wide and strictly increasing — never reused, not
/// even across module changes — so a stale report or a late roster can never name a newer mine.
/// The set is scoped to the host's current world: a module change or a same-module reload on
/// the host drops every record (the host's engine copies went with the world), and the roster
/// generation moves so every client re-reads.
/// </summary>
public sealed class PlacedMineAuthority
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<uint, PlacedMineRosterEntry> _active = [];
    private readonly Dictionary<(uint Owner, uint Capture), uint> _serialByCapture = [];
    private uint _nextSerial = 1;
    private uint _generation = 1;
    private uint _moduleHash;
    private uint _worldGeneration;
    private bool _hasWorld;

    /// <summary>Changes on every accepted placement, removal and world change.</summary>
    public uint Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    /// <summary>
    /// The host's current world. A different module hash or world generation (the host plugin's
    /// world-load serial) drops every record. Returns true when records were dropped.
    /// </summary>
    public bool SetWorld(uint moduleHash, uint worldGeneration)
    {
        lock (_gate)
        {
            if (_hasWorld && moduleHash == _moduleHash && worldGeneration == _worldGeneration)
            {
                return false;
            }

            bool dropped = _active.Count > 0;
            _active.Clear();
            _serialByCapture.Clear();
            _moduleHash = moduleHash;
            _worldGeneration = worldGeneration;
            _hasWorld = true;
            _generation++;
            return dropped;
        }
    }

    public PlacedMinePlaceResult Place(uint ownerPeerId, uint captureSerial, uint moduleHash, PlacedMineRecord record, out uint serial)
    {
        serial = 0;
        lock (_gate)
        {
            if (!_hasWorld)
            {
                return PlacedMinePlaceResult.NoWorld;
            }

            if (captureSerial == 0 || !record.IsValid)
            {
                return PlacedMinePlaceResult.Invalid;
            }

            if (_serialByCapture.TryGetValue((ownerPeerId, captureSerial), out serial))
            {
                return PlacedMinePlaceResult.Duplicate;
            }

            if (moduleHash != _moduleHash)
            {
                return PlacedMinePlaceResult.WrongModule;
            }

            if (_active.Count >= PlacedMineRosterPacket.MaxEntries)
            {
                return PlacedMinePlaceResult.Full;
            }

            serial = _nextSerial++;
            _active[serial] = new PlacedMineRosterEntry(serial, ownerPeerId, captureSerial, record);
            _serialByCapture[(ownerPeerId, captureSerial)] = serial;
            _generation++;
            return PlacedMinePlaceResult.Accepted;
        }
    }

    /// <summary>Removes a canonical mine. False when it was not active (already removed, never
    /// existed, or from a world the host has since left) — idempotent by design.</summary>
    public bool Remove(uint serial)
    {
        lock (_gate)
        {
            if (!_active.Remove(serial))
            {
                return false;
            }

            _generation++;
            return true;
        }
    }

    /// <summary>The whole canonical set as a roster level, ascending by serial.</summary>
    public PlacedMineRosterPacket Snapshot()
    {
        lock (_gate)
        {
            return new PlacedMineRosterPacket(_generation, _moduleHash, _nextSerial - 1, [.. _active.Values]);
        }
    }

    /// <summary>Forget everything (the host session ended).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _active.Clear();
            _serialByCapture.Clear();
            _hasWorld = false;
            _generation++;
        }
    }
}
