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
///
/// ⚠ PROTOCOL 31: this used to merge by VALUE — "the higher number wins, ties keep what is
/// already here" — which is what a KOTOR global usually does (a `*_DONE` latch, a stage
/// counter) but is not what one ALWAYS does: a kill counter can legitimately tick down, a
/// `*_DONE` boolean can be cleared back to false by a script, and the game data has real
/// examples (docs/research/quest-sync.md's enumeration). Under the old policy a genuine
/// decrease was silently refused forever — worse, on the apply-back path
/// (<c>CoopAgent.PumpWorldState</c>) the STALE higher value the store still held was written
/// straight back into the local engine every tick, fighting the player's own game.
///
/// The fix is a per-key Lamport clock. <see cref="Version"/> now doubles as this store's own
/// logical clock: a local change (<see cref="Set"/>) stamps the entry with the NEXT clock
/// value, guaranteed higher than anything this instance has originated OR observed, because
/// observing ANY serial — via <see cref="ApplyOp"/> or <see cref="MergeSnapshot"/>, win or
/// lose — advances the clock past it first. A remote entry is adopted only when its
/// (Serial, OriginPeerId) is strictly newer than what is already held; ties favour the lower
/// peer id, i.e. the host (<c>CoopHost.HostPeerId == 1</c>). This keeps every property the
/// old max-merge had — order-independent, idempotent, a stale replay can never regress a
/// newer value (proven by <c>ReplayedOpCannotUndoABeat</c> and its serial-aware siblings) —
/// while finally letting a value go down when that is what genuinely happened.
///
/// KEYS ARE 64-BIT (protocol 53). A global variable's id lives in the low 32 bits; a door or
/// placeable key (<c>KotorCoop.Memory.ObjectKeyedFlags</c>) carries a type tag, the FULL
/// 32-bit module-name hash and the object id, because object ids restart on every module load
/// and the old one-byte module fold let two modules share an entry over a long game. The store
/// itself is key-agnostic; <see cref="Prune"/> is the hook a caller with module knowledge uses
/// to drop the object-keyed entries of a module it KNOWS no peer will need again.
///
/// ⚠ NOTHING PRUNES AUTOMATICALLY ON A MODULE CHANGE, AND THAT IS DELIBERATE. Peers can be in
/// different modules (solo segments, split parties, a joiner still catching up), and the ONLY
/// way a host-side door/chest change reaches a peer that was not there to see it is this
/// store's snapshot, applied by the bridge the moment that peer enters the module
/// (<c>TryWriteDoorStates</c> holds back other modules' keys until then). A "current module
/// only" prune on the host would silently drop exactly those entries. With collision-free keys
/// the store's growth over a whole playthrough is a few thousand 17-byte entries, which the
/// paged snapshot already carries.
/// </summary>
public sealed class EventFlagStore
{
    private const int MaxRememberedOps = 4096;

    /// <summary>One flag's current value plus the (Serial, OriginPeerId) that won it.</summary>
    private readonly record struct Entry(sbyte Value, uint Serial, uint OriginPeerId);

    private readonly Dictionary<ulong, Entry> _flags = [];
    private readonly HashSet<(uint Host, uint Load, uint Revision, uint Origin, uint Sequence, ulong Flag, uint Serial)> _appliedOps = [];
    private readonly Queue<(uint Host, uint Load, uint Revision, uint Origin, uint Sequence, ulong Flag, uint Serial)> _opHistory = new();
    private readonly Lock _gate = new();
    private ulong _digest = EntryDigest.Empty;

    /// <summary>Caller must hold <see cref="_gate"/>. This store's Lamport clock: the serial
    /// stamped on the NEXT local change, and the high-water mark of every serial this
    /// instance has ever observed (locally minted or received).</summary>
    private uint _clock;

    public uint Version
    {
        get
        {
            lock (_gate)
            {
                return _clock;
            }
        }
    }

    /// <summary>
    /// An order-independent content fingerprint, maintained incrementally beside
    /// <see cref="Version"/> so a resync tripwire can catch two stores that diverged by the
    /// same change COUNT — a tie the counter alone cannot see. See <see cref="EntryDigest"/>.
    /// </summary>
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

    /// <summary>Caller must hold <see cref="_gate"/>. Replaces one entry's contribution to
    /// <see cref="_digest"/>: XORs out the old value's hash (if the key was present) and XORs
    /// in the new one. The digest depends only on VALUE, not on Serial/OriginPeerId — two
    /// stores that reached the same content by different local/remote paths still agree on
    /// what they hold, which is the only thing the digest needs to answer.</summary>
    private void ReplaceEntry(ulong flagId, Entry entry)
    {
        if (_flags.TryGetValue(flagId, out Entry existing))
        {
            _digest ^= EntryDigest.Flag(flagId, existing.Value);
        }

        _flags[flagId] = entry;
        _digest ^= EntryDigest.Flag(flagId, entry.Value);
    }

    /// <summary>True when <paramref name="challenger"/> should replace <paramref name="incumbent"/>:
    /// a strictly higher serial wins outright; an equal serial (two instances minting their
    /// Nth local change independently, before either heard of the other) is broken by peer id,
    /// lower wins — the host is always peer 1, so a genuine tie favours the host.</summary>
    private static bool IsNewer(uint challengerSerial, uint challengerOrigin, Entry incumbent) =>
        challengerSerial > incumbent.Serial
        || (challengerSerial == incumbent.Serial && challengerOrigin < incumbent.OriginPeerId);

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

    public bool TryGet(ulong flagId, out sbyte value)
    {
        lock (_gate)
        {
            if (_flags.TryGetValue(flagId, out Entry entry))
            {
                value = entry.Value;
                return true;
            }

            value = 0;
            return false;
        }
    }

    /// <summary>The (Serial, OriginPeerId) currently stamped on <paramref name="flagId"/>, for
    /// a caller (<c>CoopClient.SetFlag</c>) that just wrote the entry via <see cref="Set"/> and
    /// needs its freshly-minted serial to build the outbound <see cref="FlagOpPacket"/>.</summary>
    public bool TryGetStamp(ulong flagId, out uint serial, out uint originPeerId)
    {
        lock (_gate)
        {
            if (_flags.TryGetValue(flagId, out Entry entry))
            {
                serial = entry.Serial;
                originPeerId = entry.OriginPeerId;
                return true;
            }

            serial = 0;
            originPeerId = 0;
            return false;
        }
    }

    /// <summary>
    /// Applies a LOCAL, deliberate change — this instance's own player, or its own engine,
    /// just made this so. Always wins: it mints the next Lamport serial, which by
    /// construction outranks anything this store has originated or observed so far, so a
    /// genuine local advance OR decrease always overrides whatever a peer announced earlier.
    /// Returns true only when the value actually moved (nothing to mint or announce
    /// otherwise).
    /// </summary>
    public bool Set(ulong flagId, sbyte value, uint originPeerId = 0)
    {
        lock (_gate)
        {
            if (_flags.TryGetValue(flagId, out Entry existing) && existing.Value == value)
            {
                return false;
            }

            _clock++;
            ReplaceEntry(flagId, new Entry(value, _clock, originPeerId));
            return true;
        }
    }

    /// <summary>
    /// Seeds the store without bumping the clock once per entry. Populating a whole
    /// KOTOR catalogue through <see cref="Set"/> would raise the version a thousand times
    /// and make every client immediately demand a full resync. Seeded entries carry
    /// Serial 0 / OriginPeerId 0 — nobody's — so ANY genuine local or remote change (which
    /// always mints Serial &gt;= 1) trivially outranks the baseline.
    ///
    /// The digest is NOT exempted the same way: it is a content fingerprint, not a change
    /// count, so it has to reflect whatever the store actually holds regardless of how that
    /// content arrived.
    /// </summary>
    public void Seed(IEnumerable<KeyValuePair<ulong, sbyte>> flags)
    {
        lock (_gate)
        {
            foreach (KeyValuePair<ulong, sbyte> flag in flags)
            {
                ReplaceEntry(flag.Key, new Entry(flag.Value, 0, 0));
            }
        }
    }

    /// <summary>
    /// Applies a remote op. UDP means the same op can arrive several times through
    /// retransmission; the authenticated world namespace plus (OriginPeerId, OpSequence) catches an EXACT duplicate
    /// cheaply, but it is not the correctness guarantee — a stale value replayed under a
    /// FRESH sequence (a reconnecting peer re-announcing what it knew before it dropped)
    /// must also be refused, and that is what the (Serial, OriginPeerId) comparison
    /// guarantees regardless of sequence numbers. Observing the op's serial — win or lose —
    /// always advances this store's own clock past it, per the class remarks.
    /// </summary>
    public bool ApplyOp(FlagOpPacket op)
    {
        lock (_gate)
        {
            var key = (op.HostSessionId, op.HostLoadSerial, op.WorldRevision, op.OriginPeerId, op.OpSequence, op.FlagId, op.Serial);

            if (!_appliedOps.Add(key))
            {
                return false;
            }

            _opHistory.Enqueue(key);
            while (_opHistory.Count > MaxRememberedOps)
            {
                _appliedOps.Remove(_opHistory.Dequeue());
            }

            if (op.Serial > _clock)
            {
                _clock = op.Serial;
            }

            if (_flags.TryGetValue(op.FlagId, out Entry existing)
                && !IsNewer(op.Serial, op.OriginPeerId, existing))
            {
                return false;
            }

            ReplaceEntry(op.FlagId, new Entry(op.Value, op.Serial, op.OriginPeerId));
            return true;
        }
    }

    /// <summary>
    /// Merges a host snapshot (or snapshot part) the same way a live op applies: per entry,
    /// last-writer-wins by (Serial, OriginPeerId). A resync fires when the versions
    /// disagree, which is exactly the state a client can be in while holding a LOCAL change
    /// the host has not applied yet — the old value-only "upward merge" got this right by
    /// accident (a lower host value could never beat a higher local one); the serial
    /// comparison gets it right on purpose, including the case the old policy could not: the
    /// host's snapshot carries a genuine DECREASE that is newer than this client's own stale
    /// copy, and that must win too.
    /// </summary>
    public void MergeSnapshot(IReadOnlyList<FlagSnapshotEntry> flags, uint version)
    {
        lock (_gate)
        {
            foreach (FlagSnapshotEntry flag in flags)
            {
                if (flag.Serial > _clock)
                {
                    _clock = flag.Serial;
                }

                if (!_flags.TryGetValue(flag.FlagId, out Entry existing)
                    || IsNewer(flag.Serial, flag.OriginPeerId, existing))
                {
                    ReplaceEntry(flag.FlagId, new Entry(flag.Value, flag.Serial, flag.OriginPeerId));
                }
            }

            // Adopt the host's version as a floor so the tripwire stops firing even for a
            // flag this snapshot did not need to touch. Anything this client still holds
            // that disagrees with the host is, by construction, an op the reliable sender
            // is still carrying — and it is never abandoned, so it lands, the host's clock
            // moves, and the next snapshot disagrees again until the two really agree.
            if (version > _clock)
            {
                _clock = version;
            }
        }
    }

    /// <summary>Replaces the whole set from an authoritative source. Destructive — nothing in
    /// this codebase calls it (the resync path uses <see cref="MergeSnapshot"/> instead,
    /// deliberately, per its own remarks); kept for callers (tests, a future admin tool) that
    /// genuinely want a hard reset. Every entry is stamped with the given version as its
    /// serial and origin 0, so any subsequent real change (local or remote, serial &gt;= 1
    /// over its own clock) still outranks it correctly.</summary>
    public void ReplaceAll(IReadOnlyList<KeyValuePair<ulong, sbyte>> flags, uint version)
    {
        lock (_gate)
        {
            _flags.Clear();
            _digest = EntryDigest.Empty;
            foreach (KeyValuePair<ulong, sbyte> flag in flags)
            {
                ReplaceEntry(flag.Key, new Entry(flag.Value, version, 0));
            }

            _clock = version;
        }
    }

    /// <summary>
    /// Drops every entry whose key <paramref name="shouldDrop"/> accepts, keeping the digest
    /// consistent (each removed entry's contribution is XOR-ed back out, so the result equals a
    /// store freshly built from the surviving entries). The Lamport clock is NOT lowered — it is
    /// a high-water mark of everything ever observed, and lowering it would let a later local
    /// change mint a serial a peer has already seen. <see cref="Version"/> therefore does not
    /// change either: a prune is local housekeeping, not a world change to announce. Returns the
    /// number of entries removed.
    ///
    /// Meant for per-module housekeeping with the <c>ObjectKeyedFlags.BelongsToModule</c>
    /// predicate, by a caller that knows every peer is done with that module — see the class
    /// remarks for why no caller does this on a plain transition.
    /// </summary>
    public int Prune(Func<ulong, bool> shouldDrop)
    {
        lock (_gate)
        {
            List<ulong> doomed = [];
            foreach (KeyValuePair<ulong, Entry> kv in _flags)
            {
                if (shouldDrop(kv.Key))
                {
                    doomed.Add(kv.Key);
                }
            }

            foreach (ulong key in doomed)
            {
                _digest ^= EntryDigest.Flag(key, _flags[key].Value);
                _flags.Remove(key);
            }

            return doomed.Count;
        }
    }

    public List<KeyValuePair<ulong, sbyte>> Snapshot()
    {
        lock (_gate)
        {
            List<KeyValuePair<ulong, sbyte>> list = new(_flags.Count);
            foreach (KeyValuePair<ulong, Entry> kv in _flags)
            {
                list.Add(new KeyValuePair<ulong, sbyte>(kv.Key, kv.Value.Value));
            }

            return list;
        }
    }

    /// <summary>The wire shape of <see cref="Snapshot"/>: every entry with the
    /// (Serial, OriginPeerId) that won it, for <see cref="FlagSnapshotPacket"/>.</summary>
    public List<FlagSnapshotEntry> SnapshotEntries()
    {
        lock (_gate)
        {
            List<FlagSnapshotEntry> list = new(_flags.Count);
            foreach (KeyValuePair<ulong, Entry> kv in _flags)
            {
                list.Add(new FlagSnapshotEntry(kv.Key, kv.Value.Value, kv.Value.Serial, kv.Value.OriginPeerId));
            }

            return list;
        }
    }

    public Dictionary<ulong, sbyte> ToDictionary()
    {
        lock (_gate)
        {
            Dictionary<ulong, sbyte> result = new(_flags.Count);
            foreach (KeyValuePair<ulong, Entry> kv in _flags)
            {
                result[kv.Key] = kv.Value.Value;
            }

            return result;
        }
    }
}
