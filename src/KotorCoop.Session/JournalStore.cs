using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// The session's shared quest journal: quest tag -&gt; state any player last authoritatively
/// set it to, or a tombstone if it was removed.
///
/// ⚠ PROTOCOL 31: this used to be a bare max-merge — "the highest state wins, a lower one is
/// refused" — because the engine's own <c>CSWSJournal::SetState</c> normally only moves a
/// quest forward. But KOTOR's own scripts do not stop there: `RemoveJournalQuestEntry` takes
/// a tag out of the journal entirely (12 scripts found by docs/research/quest-sync.md's real
/// module scan — e.g. `danm13/k_pdan_trig1303`, `ebo_m41aa/k_pebn_remove`), and a script can
/// re-add a tag at an earlier stage with the engine's own override flag. A bare max-merge
/// refuses both forever: a removed quest stays in every peer's journal, and a legitimate
/// push-back is silently dropped.
///
/// The fix is the same per-key Lamport clock <see cref="EventFlagStore"/> now uses: a local
/// change (<see cref="Merge"/>, <see cref="Remove"/>) mints the store's next serial, which
/// always outranks anything this instance has originated or observed; a remote entry
/// (<see cref="ApplyRemote"/>, <see cref="MergeSnapshot"/>) is adopted only when its
/// (Serial, OriginPeerId) is strictly newer, ties favouring the host. A removal is a
/// TOMBSTONE, not a deletion: the tag's last-known (Serial, OriginPeerId) has to survive the
/// removal, or a stale re-add replayed afterwards (nothing left to compare it against) would
/// resurrect the very entry that was just removed — exactly the "stale replay must never
/// regress" property the old scheme leaned on the value-only max for.
///
/// The version is the divergence tripwire carried in the world snapshot, exactly like the
/// flag and item stores.
/// </summary>
public sealed class JournalStore
{
    /// <summary>One tag's last-known state plus the (Serial, OriginPeerId) that won it.
    /// <see cref="Removed"/> tags stay in the dictionary as tombstones — see the class
    /// remarks — but are excluded from <see cref="Snapshot"/>, <see cref="Count"/> and the
    /// digest.</summary>
    private readonly record struct Entry(int State, uint Serial, uint OriginPeerId, bool Removed);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private uint _clock;
    private ulong _digest = EntryDigest.Empty;

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

    /// <summary>Order-independent content fingerprint, beside <see cref="Version"/> for the
    /// same reason <see cref="EventFlagStore.Digest"/> exists — see <see cref="EntryDigest"/>.
    /// A removed tag contributes nothing, matching an entry that never existed: two stores
    /// that agree "this tag is gone" must digest identically to two stores that never heard
    /// of it.</summary>
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

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private void ReplaceEntry(string questTag, Entry entry)
    {
        if (_entries.TryGetValue(questTag, out Entry existing) && !existing.Removed)
        {
            _digest ^= EntryDigest.Journal(questTag, existing.State);
        }

        _entries[questTag] = entry;

        if (!entry.Removed)
        {
            _digest ^= EntryDigest.Journal(questTag, entry.State);
        }
    }

    private static bool IsNewer(uint challengerSerial, uint challengerOrigin, Entry incumbent) =>
        challengerSerial > incumbent.Serial
        || (challengerSerial == incumbent.Serial && challengerOrigin < incumbent.OriginPeerId);

    /// <summary>Live (non-removed) entry count.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                int count = 0;
                foreach (Entry entry in _entries.Values)
                {
                    if (!entry.Removed)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    public bool TryGet(string questTag, out int state)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(questTag)
                && _entries.TryGetValue(questTag, out Entry entry)
                && !entry.Removed)
            {
                state = entry.State;
                return true;
            }

            state = 0;
            return false;
        }
    }

    /// <summary>The (Serial, OriginPeerId) currently stamped on <paramref name="questTag"/>
    /// (present whether the entry is live or a tombstone), for a caller
    /// (<c>CoopClient.SetJournalEntry</c>/<c>RemoveJournalEntry</c>) that just wrote the entry
    /// via <see cref="Merge"/>/<see cref="Remove"/> and needs its freshly-minted serial to
    /// build the outbound <see cref="JournalOpPacket"/>.</summary>
    public bool TryGetStamp(string questTag, out uint serial, out uint originPeerId)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(questTag) && _entries.TryGetValue(questTag, out Entry entry))
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
    /// Sets an entry to the given state as a LOCAL, deliberate change — this instance's own
    /// player, or its own engine, just made this so. Always wins, exactly like
    /// <see cref="EventFlagStore.Set"/>: mints the next Lamport serial, which outranks
    /// anything this store has originated or observed. Returns true (and bumps the version)
    /// only if this actually changed the store — the caller uses that to decide whether the
    /// change is worth announcing, and to stop echo loops.
    /// </summary>
    public bool Merge(string questTag, int state, uint originPeerId = 0)
    {
        if (string.IsNullOrEmpty(questTag))
        {
            return false;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(questTag, out Entry existing)
                && !existing.Removed && existing.State == state)
            {
                return false;
            }

            _clock++;
            ReplaceEntry(questTag, new Entry(state, _clock, originPeerId, Removed: false));
            return true;
        }
    }

    /// <summary>
    /// Takes an entry out of the journal as a LOCAL, deliberate change — the engine's own
    /// <c>RemoveJournalQuestEntry</c> fired here. Tombstones rather than deletes, per the
    /// class remarks. Returns false if the tag was never live in this store to begin with
    /// (never merged, or already removed) — nothing changed, nothing to announce, and no
    /// tombstone is minted for a tag this store never actually held.
    /// </summary>
    public bool Remove(string questTag, uint originPeerId = 0)
    {
        if (string.IsNullOrEmpty(questTag))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(questTag, out Entry existing) || existing.Removed)
            {
                return false;
            }

            _clock++;
            ReplaceEntry(questTag, new Entry(0, _clock, originPeerId, Removed: true));
            return true;
        }
    }

    /// <summary>
    /// Applies one remote op (add/advance/decrease OR remove) received over the wire,
    /// adopting it only when its (Serial, OriginPeerId) is strictly newer than what this
    /// store already holds for the tag — the same last-writer-wins rule
    /// <see cref="EventFlagStore.ApplyOp"/> uses. Observing the serial always advances this
    /// store's own clock past it, win or lose, so this instance's own subsequent local
    /// changes keep outranking anything it has seen.
    ///
    /// Unlike <see cref="EventFlagStore.ApplyOp"/> this does not keep a separate
    /// (OriginPeerId, OpSequence) dedup set: the (Serial, OriginPeerId) comparison is already
    /// exactly idempotent under an exact retransmit (the second delivery compares equal-not-
    /// greater and is silently absorbed), so a second bookkeeping layer would only be
    /// deduplicating what the comparison already deduplicates for free. Journal traffic is
    /// also rare enough (a handful of changes per session) that the dedup set's cost was never
    /// about scale here — it just is not needed for correctness.
    /// </summary>
    public bool ApplyRemote(string questTag, int state, uint serial, uint originPeerId, bool removed)
    {
        if (string.IsNullOrEmpty(questTag))
        {
            return false;
        }

        lock (_gate)
        {
            if (serial > _clock)
            {
                _clock = serial;
            }

            if (_entries.TryGetValue(questTag, out Entry existing)
                && !IsNewer(serial, originPeerId, existing))
            {
                return false;
            }

            ReplaceEntry(questTag, new Entry(state, serial, originPeerId, removed));
            return true;
        }
    }

    /// <summary>Merges a snapshot (or snapshot part) from the host, tombstones included —
    /// entries apply last-writer-wins by (Serial, OriginPeerId), exactly like
    /// <see cref="EventFlagStore.MergeSnapshot"/>, so a multi-part snapshot converges no
    /// matter the arrival order and a genuine removal in the snapshot overrides a stale local
    /// copy rather than being silently omitted (omission would be indistinguishable from
    /// "this snapshot part did not need to mention it").</summary>
    public void MergeSnapshot(IReadOnlyList<JournalSnapshotEntry> entries, uint version)
    {
        lock (_gate)
        {
            foreach (JournalSnapshotEntry entry in entries)
            {
                if (entry.QuestTag.Length == 0)
                {
                    continue;
                }

                if (entry.Serial > _clock)
                {
                    _clock = entry.Serial;
                }

                if (!_entries.TryGetValue(entry.QuestTag, out Entry existing)
                    || IsNewer(entry.Serial, entry.OriginPeerId, existing))
                {
                    ReplaceEntry(
                        entry.QuestTag,
                        new Entry(entry.State, entry.Serial, entry.OriginPeerId, entry.Removed));
                }
            }

            if (version > _clock)
            {
                _clock = version;
            }
        }
    }

    /// <summary>Live entries only — what CoopAgent re-declares to the engine, and what a
    /// caller means by "the journal" in the ordinary case.</summary>
    public List<KeyValuePair<string, int>> Snapshot()
    {
        lock (_gate)
        {
            List<KeyValuePair<string, int>> list = [];
            foreach (KeyValuePair<string, Entry> kv in _entries)
            {
                if (!kv.Value.Removed)
                {
                    list.Add(new KeyValuePair<string, int>(kv.Key, kv.Value.State));
                }
            }

            return list;
        }
    }

    /// <summary>Tags this store currently holds as REMOVED — for the agent to tell the local
    /// engine to take them out too (<c>IGameBridge.TryRemoveJournalEntry</c>), the removal
    /// twin of re-declaring <see cref="Snapshot"/> to apply every live entry.</summary>
    public List<string> RemovedTags()
    {
        lock (_gate)
        {
            List<string> list = [];
            foreach (KeyValuePair<string, Entry> kv in _entries)
            {
                if (kv.Value.Removed)
                {
                    list.Add(kv.Key);
                }
            }

            return list;
        }
    }

    /// <summary>The wire shape of the whole store, tombstones included, for
    /// <see cref="JournalSnapshotPacket"/> — a resync must be able to tell a peer "this tag
    /// was removed", not merely omit it (see <see cref="MergeSnapshot"/>'s remarks).</summary>
    public List<JournalSnapshotEntry> SnapshotEntries()
    {
        lock (_gate)
        {
            List<JournalSnapshotEntry> list = new(_entries.Count);
            foreach (KeyValuePair<string, Entry> kv in _entries)
            {
                list.Add(new JournalSnapshotEntry(
                    kv.Key, kv.Value.State, kv.Value.Serial, kv.Value.OriginPeerId, kv.Value.Removed));
            }

            return list;
        }
    }
}
