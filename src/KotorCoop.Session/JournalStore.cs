namespace KotorCoop.Session;

/// <summary>
/// The session's shared quest journal: quest tag -&gt; highest state any player has
/// reached. States only ever rise — the engine itself refuses to lower a quest entry —
/// so the merge is a max, which makes it order-independent and idempotent: a reordered
/// or replayed packet cannot walk a quest backwards.
///
/// The version is the divergence tripwire carried in the world snapshot, exactly like
/// the flag and item stores.
/// </summary>
public sealed class JournalStore
{
    private readonly Dictionary<string, int> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private uint _version;

    public uint Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Raises an entry to the given state. Returns true (and bumps the version) only if
    /// this actually advanced the store — the caller uses that to decide whether the
    /// change is worth announcing, and to stop echo loops.
    /// </summary>
    public bool Merge(string questTag, int state)
    {
        if (string.IsNullOrEmpty(questTag))
        {
            return false;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(questTag, out int existing) && existing >= state)
            {
                return false;
            }

            _entries[questTag] = state;
            _version++;
            return true;
        }
    }

    /// <summary>Merges a snapshot (or snapshot part) from the host. Entries merge upward;
    /// the version is adopted if newer, so a multi-part snapshot converges no matter the
    /// arrival order.</summary>
    public void MergeSnapshot(IReadOnlyList<KeyValuePair<string, int>> entries, uint version)
    {
        lock (_gate)
        {
            foreach (KeyValuePair<string, int> entry in entries)
            {
                if (entry.Key.Length == 0)
                {
                    continue;
                }

                if (!_entries.TryGetValue(entry.Key, out int existing) || existing < entry.Value)
                {
                    _entries[entry.Key] = entry.Value;
                }
            }

            if (version > _version)
            {
                _version = version;
            }
        }
    }

    public List<KeyValuePair<string, int>> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }
}
