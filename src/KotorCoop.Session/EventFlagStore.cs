using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// The world progression channel. Two players seeing each other's avatars is the easy
/// half of co-op; agreeing on which doors are open, which bosses are dead and which
/// quest steps have fired is the half that actually takes the time.
///
/// Every change bumps <see cref="Version"/>. The host stamps that version into each
/// world snapshot, so a client detects divergence from a 4-byte field rather than by
/// shipping the whole flag set every tick.
/// </summary>
public sealed class EventFlagStore
{
    private const int MaxRememberedOps = 4096;

    private readonly Dictionary<uint, sbyte> _flags = [];
    private readonly HashSet<(uint Origin, uint Sequence)> _appliedOps = [];
    private readonly Queue<(uint Origin, uint Sequence)> _opHistory = new();
    private readonly Lock _gate = new();

    public uint Version { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _flags.Count;
            }
        }
    }

    public bool TryGet(uint flagId, out sbyte value)
    {
        lock (_gate)
        {
            return _flags.TryGetValue(flagId, out value);
        }
    }

    /// <summary>Applies a change. Returns true only when the value actually moved.</summary>
    public bool Set(uint flagId, sbyte value)
    {
        lock (_gate)
        {
            if (_flags.TryGetValue(flagId, out sbyte existing) && existing == value)
            {
                return false;
            }

            _flags[flagId] = value;
            Version++;
            return true;
        }
    }

    /// <summary>
    /// Merges a value the way KOTOR's own world state behaves: upwards only.
    ///
    /// The game's globals are overwhelmingly monotonic — booleans are latches
    /// (<c>*_DONE</c>, <c>*_DEAD</c>) and numbers are stage counters — and the engine
    /// itself refuses to lower a journal quest state. Taking the maximum makes the merge a
    /// join: order-independent and idempotent, so a reordered or replayed packet can never
    /// walk a quest backwards, and a script part-way through a sequence locally cannot be
    /// dragged back by a peer that is behind.
    /// </summary>
    public bool Merge(uint flagId, sbyte value)
    {
        lock (_gate)
        {
            if (_flags.TryGetValue(flagId, out sbyte existing) && existing >= value)
            {
                return false;
            }

            _flags[flagId] = value;
            Version++;
            return true;
        }
    }

    /// <summary>
    /// Seeds the store without bumping the version once per entry. Populating a whole
    /// KOTOR catalogue through <see cref="Set"/> would raise the version a thousand times
    /// and make every client immediately demand a full resync.
    /// </summary>
    public void Seed(IEnumerable<KeyValuePair<uint, sbyte>> flags)
    {
        lock (_gate)
        {
            foreach (KeyValuePair<uint, sbyte> flag in flags)
            {
                _flags[flag.Key] = flag.Value;
            }
        }
    }

    /// <summary>
    /// Applies a remote op exactly once. UDP means the same op can arrive several times
    /// through retransmission, and a flag toggle applied twice is a flag toggle undone.
    /// </summary>
    public bool ApplyOp(FlagOpPacket op)
    {
        lock (_gate)
        {
            (uint, uint) key = (op.OriginPeerId, op.OpSequence);

            if (!_appliedOps.Add(key))
            {
                return false;
            }

            _opHistory.Enqueue(key);
            while (_opHistory.Count > MaxRememberedOps)
            {
                _appliedOps.Remove(_opHistory.Dequeue());
            }

            // Merge upwards rather than overwrite, for the reason in Merge: world state
            // only ever advances, and a replayed or reordered op must not undo a beat.
            if (_flags.TryGetValue(op.FlagId, out sbyte existing) && existing >= op.Value)
            {
                return false;
            }

            _flags[op.FlagId] = op.Value;
            Version++;
            return true;
        }
    }

    /// <summary>Replaces the whole set from an authoritative host snapshot.</summary>
    public void ReplaceAll(IReadOnlyList<KeyValuePair<uint, sbyte>> flags, uint version)
    {
        lock (_gate)
        {
            _flags.Clear();
            foreach (KeyValuePair<uint, sbyte> flag in flags)
            {
                _flags[flag.Key] = flag.Value;
            }

            Version = version;
        }
    }

    public List<KeyValuePair<uint, sbyte>> Snapshot()
    {
        lock (_gate)
        {
            return [.. _flags];
        }
    }

    public Dictionary<uint, sbyte> ToDictionary()
    {
        lock (_gate)
        {
            return new Dictionary<uint, sbyte>(_flags);
        }
    }
}
