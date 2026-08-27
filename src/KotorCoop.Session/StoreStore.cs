using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// What this instance believes every merchant store's stock holds: a running quantity per
/// <see cref="StoreStackKey"/> (which already carries the store's object id — see that type's
/// own remarks), built up by applying add/remove ops from every peer (including this instance's
/// own, published exactly like a peer's — see <c>CoopHost.Store.cs</c>/<c>CoopClient.Store.cs</c>).
///
/// Same shape as <see cref="PartyPoolStore"/> — <c>Version</c>/<c>Digest</c> tripwire, op-sequence
/// dedup so a UDP retransmit is applied exactly once, an aggregate multiset rather than a set
/// (a store's stock genuinely grows AND shrinks, repeatedly, from either instance — see
/// docs/research/store-sync.md §2.1/§2.2 for why this rules out the container-loot "set of
/// removed pairs" shape and reuses the party pool's own instead).
/// </summary>
public sealed class StoreStore
{
    private const int MaxRememberedOps = 4096;

    private readonly Dictionary<StoreStackKey, int> _totals = [];
    private readonly HashSet<(uint Origin, uint Sequence)> _appliedOps = [];
    private readonly Queue<(uint Origin, uint Sequence)> _opHistory = new();
    private readonly Lock _gate = new();
    private ulong _digest = EntryDigest.Empty;

    public uint Version { get; private set; }

    /// <summary>Order-independent content fingerprint, beside <see cref="Version"/> for the same
    /// reason <see cref="PartyPoolStore.Digest"/> exists.</summary>
    public ulong Digest
    {
        get
        {
            lock (_gate)
            {
                return _digest;
            }
        }
    }

    /// <summary>Distinct (store, stack) keys currently tracked, across every store — exactly the
    /// size of a <see cref="Snapshot"/>.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _totals.Count;
            }
        }
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private bool Mutate(StoreOpKind kind, StoreStackKey key, uint countDelta)
    {
        if (countDelta == 0 && kind != StoreOpKind.Set)
        {
            return false;
        }

        int before = _totals.GetValueOrDefault(key);
        int after = kind switch
        {
            StoreOpKind.Add => before + (int)countDelta,
            StoreOpKind.Remove => Math.Max(0, before - (int)countDelta),

            // Set is ABSOLUTE — see StoreOpKind.Set's own doc. Used only for a host-authored
            // resync, where replaying history as more Adds could double-count a peer that
            // already holds a different total for this key.
            _ => (int)countDelta,
        };

        if (after == before)
        {
            return false;
        }

        if (before == 0 && after > 0 && _totals.Count >= ProtocolConstants.MaxStoreStacks)
        {
            // Cap only refuses a BRAND NEW key — an existing one can still change quantity
            // (including shrinking to zero, which frees a slot for the next new key).
            return false;
        }

        if (before > 0)
        {
            _digest ^= EntryDigest.StoreEntry(key, before);
        }

        if (after > 0)
        {
            _totals[key] = after;
            _digest ^= EntryDigest.StoreEntry(key, after);
        }
        else
        {
            _totals.Remove(key);
        }

        Version++;
        return true;
    }

    /// <summary>
    /// Records a locally-originated change (this instance's own scan detected it, or the host's
    /// own add/remove is about to be broadcast) — same mutation <see cref="ApplyOp"/> performs,
    /// without the op-sequence dedup a self-originated change does not need. Returns true only
    /// when the total actually changed (a cap refusal or a zero delta returns false).
    /// </summary>
    public bool Record(StoreOpKind kind, StoreStackKey key, uint countDelta)
    {
        lock (_gate)
        {
            return Mutate(kind, key, countDelta);
        }
    }

    /// <summary>
    /// Applies a remote op exactly once. UDP means the same op can arrive several times through
    /// retransmission, and — unlike a container removal — applying the same add twice would be a
    /// genuinely different (wrong) total, not a harmless no-op.
    /// </summary>
    public bool ApplyOp(StoreOpPacket op)
    {
        lock (_gate)
        {
            (uint, uint) opKey = (op.OriginPeerId, op.OpSequence);

            if (!_appliedOps.Add(opKey))
            {
                return false;
            }

            _opHistory.Enqueue(opKey);
            while (_opHistory.Count > MaxRememberedOps)
            {
                _appliedOps.Remove(_opHistory.Dequeue());
            }

            return Mutate(op.Kind, op.Key, op.CountDelta);
        }
    }

    /// <summary>The current quantity of one stack key, for tests and for the pump's own
    /// expectation bookkeeping.</summary>
    public int Get(StoreStackKey key)
    {
        lock (_gate)
        {
            return _totals.GetValueOrDefault(key);
        }
    }

    /// <summary>Every (store, stack) key currently held, with its quantity — what a resync
    /// offers.</summary>
    public Dictionary<StoreStackKey, int> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<StoreStackKey, int>(_totals);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _totals.Clear();
            _appliedOps.Clear();
            _opHistory.Clear();
            _digest = EntryDigest.Empty;
            Version = 0;
        }
    }
}
