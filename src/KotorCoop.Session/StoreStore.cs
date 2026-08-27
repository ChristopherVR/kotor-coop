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
///
/// <b>The "known" set (2026-08-28 fix, docs/research/store-sync.md's "Bug 4" write-up).</b>
/// <see cref="_totals"/> prunes a key the moment its quantity reaches zero — necessary so this
/// dictionary does not grow forever, but it means "absent from <see cref="_totals"/>" is
/// genuinely ambiguous between "nobody has ever transacted this key" (a store's pristine,
/// never-synced .utm stock — must NEVER be reconciled toward zero) and "this key was
/// legitimately depleted to exactly zero by a real op" (a fact that MUST still propagate to
/// every peer, including one who never locally scanned the depleted item). <see cref="_known"/>
/// resolves the ambiguity: every key that has EVER been the target of a real
/// <see cref="Record"/>/<see cref="ApplyOp"/> call is added here and never removed (short of
/// <see cref="Clear"/>) — including one whose total is currently (or was always) zero. A
/// consumer reconciling toward this store's wire truth (<c>Pumps.StorePump</c>'s step 2) must
/// only ever touch a key present in this set; one absent here is unknown, not zero, and must be
/// left alone.
/// </summary>
public sealed class StoreStore
{
    private const int MaxRememberedOps = 4096;

    private readonly Dictionary<StoreStackKey, int> _totals = [];
    private readonly HashSet<StoreStackKey> _known = [];
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

            // No floor here (2026-08-28 fix, docs/research/store-sync.md). `before` may be an
            // UNSEEDED zero for a key whose first-ever transaction is a BUY (Remove) — flooring
            // at zero would silently report "no change" (before==after==0) for a perfectly real
            // delta, and Record's caller (PublishStoreOp) would then never even send the packet.
            // See Record's own <c>localBaselineIfUnknown</c> remarks for the primary fix (seeding
            // a real baseline before this runs); this NO-FLOOR arithmetic is the defence-in-depth
            // half — even an UNSEEDED first Remove still produces a genuine `after != before`
            // (0 vs a negative number) and therefore still returns true below. A non-positive
            // `after` is pruned by the branch below exactly like a legitimately-zeroed key
            // already was — negative and zero are digest/Snapshot-equivalent: "not present".
            StoreOpKind.Remove => before - (int)countDelta,

            // Set is ABSOLUTE — see StoreOpKind.Set's own doc. Used only for a host-authored
            // resync, where replaying history as more Adds could double-count a peer that
            // already holds a different total for this key.
            _ => (int)countDelta,
        };

        if (before == 0 && after > 0 && _totals.Count >= ProtocolConstants.MaxStoreStacks)
        {
            // Cap only refuses a BRAND NEW key — an existing one can still change quantity
            // (including shrinking to zero, which frees a slot for the next new key). A
            // cap-refused key is deliberately NOT added to _known below: this session never
            // actually recorded anything real for it, so pretending otherwise would let a later
            // reconcile wipe whatever a local engine scan legitimately shows for it.
            return false;
        }

        // Mark known now — a genuine op is about to be processed for this key, REGARDLESS of
        // whether it ends up changing the total (a Set restating an already-known fact, e.g. a
        // resync's Set(key, 0) for a key already at zero, is still real knowledge the caller
        // must retain even though the early-return below fires).
        _known.Add(key);

        if (after == before)
        {
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
    ///
    /// <paramref name="localBaselineIfUnknown"/> (2026-08-28 fix, docs/research/store-sync.md):
    /// when this is the FIRST-EVER op recorded for <paramref name="key"/> (absent from
    /// <see cref="_totals"/>), seeds it with this value BEFORE applying <paramref name="kind"/> —
    /// the local engine total <c>Pumps.StorePump</c>'s own step 1 observed THIS key at,
    /// immediately before the just-detected delta. Without this, the first transaction on a
    /// store's pristine stock would be recorded against a false zero baseline, and every
    /// subsequent <see cref="SnapshotKnown"/>-driven resync would report a wildly wrong absolute
    /// total for that key. Zero (the default) means "no hint available" — the key is left
    /// genuinely unseeded, matching every call site that predates this parameter.
    /// </summary>
    public bool Record(StoreOpKind kind, StoreStackKey key, uint countDelta, int localBaselineIfUnknown = 0)
    {
        lock (_gate)
        {
            if (localBaselineIfUnknown > 0 && !_totals.ContainsKey(key))
            {
                if (_totals.Count >= ProtocolConstants.MaxStoreStacks)
                {
                    // Same cap Mutate's own before==0 branch enforces — checked here too since
                    // the seed below inserts a brand new key before Mutate ever sees it.
                    return false;
                }

                _totals[key] = localBaselineIfUnknown;
                _digest ^= EntryDigest.StoreEntry(key, localBaselineIfUnknown);
            }

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

    /// <summary>Every (store, stack) key currently held with a NONZERO quantity. NOT the full
    /// resync offer — see <see cref="SnapshotKnown"/> for that; a key legitimately depleted to
    /// zero is absent here by construction (see <see cref="_known"/>'s own remarks), which is
    /// exactly right for this method's OTHER consumers (<c>CoopHost</c>/<c>CoopClient</c>'s own
    /// <c>StoreTotals</c> — "what does this session currently believe a store holds", where an
    /// absent key correctly means "zero, as far as any CURRENT total goes").</summary>
    public Dictionary<StoreStackKey, int> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<StoreStackKey, int>(_totals);
        }
    }

    /// <summary>Every (store, stack) key this session has EVER recorded a real op for, mapped to
    /// its CURRENT quantity (zero for one since pruned from <see cref="_totals"/>) — the full
    /// resync offer <see cref="CoopHost.ResyncStore"/> sends, so a rejoining peer learns BOTH
    /// "this key is at N" and "this key is known, and currently zero" (the latter is silently
    /// missing from <see cref="Snapshot"/>, which is exactly the ambiguity <see cref="_known"/>'s
    /// own remarks describe). A store nobody has ever transacted contributes NOTHING here — its
    /// keys were never added to <see cref="_known"/> — matching the design rule that an untouched
    /// store's resync offer must be empty, not a "set everything to what I currently see".</summary>
    public Dictionary<StoreStackKey, int> SnapshotKnown()
    {
        lock (_gate)
        {
            Dictionary<StoreStackKey, int> result = new(_known.Count);
            foreach (StoreStackKey key in _known)
            {
                result[key] = _totals.GetValueOrDefault(key);
            }

            return result;
        }
    }

    /// <summary>Every (store, stack) key this session has ever recorded a real op for — the
    /// predicate <c>Pumps.StorePump</c>'s step 2 gates reconciliation on: a key absent here is
    /// unknown (never transacted by anyone), not zero, and must never be pushed toward the wire's
    /// (necessarily silent) belief about it. See <see cref="_known"/>'s own remarks.</summary>
    public IReadOnlySet<StoreStackKey> KnownKeys
    {
        get
        {
            lock (_gate)
            {
                return new HashSet<StoreStackKey>(_known);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _totals.Clear();
            _known.Clear();
            _appliedOps.Clear();
            _opHistory.Clear();
            _digest = EntryDigest.Empty;
            Version = 0;
        }
    }
}
