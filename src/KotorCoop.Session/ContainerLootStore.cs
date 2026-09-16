using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Every (container, item) pair anyone has looted, so the item is removed from this instance's
/// copy of each shared container and cannot be taken twice. Both sides keep one: a container is
/// a private copy in each process, so whoever empties theirs first has to tell everyone else.
///
/// Same shape as <see cref="GroundItemStore"/>'s op handling: a removal op is applied exactly
/// once (UDP retransmits the same op several times), and the accumulated set is what the agent
/// re-declares to the game each tick. Applying a removal in-game is idempotent — once the item
/// is gone from the container, re-writing the same removal resolves to a no-op — so keeping a
/// removal in the set after it has been applied is harmless.
///
/// The set is also what stops a removal echoing between the two instances. Applying a peer's
/// removal deletes the item from a container here, which this instance's own scanner then sees
/// as an item that left a container — indistinguishable from this player looting it. Because
/// <see cref="Add"/> reports a removal already in the set as not-new, the announce path drops
/// it and the echo dies at the first hop.
/// </summary>
public sealed class ContainerLootStore
{
    private const int MaxRememberedOps = 4096;

    /// <summary>How many removals to keep in the RE-DECLARE WINDOW (<see cref="Snapshot"/>).
    /// The oldest are long since applied on the game side, so dropping them past this bound
    /// loses nothing. It is not a bound on what the store KNOWS — see <see cref="Remember"/>
    /// for why the version and the digest must describe the whole set rather than this
    /// window.</summary>
    private const int MaxRemovals = 512;

    private readonly List<ContainerLootRemoval> _removals = [];
    private readonly HashSet<ContainerLootRemoval> _set = [];
    private readonly HashSet<(uint Origin, uint Sequence)> _appliedOps = [];
    private readonly Queue<(uint Origin, uint Sequence)> _opHistory = new();
    private readonly Lock _gate = new();
    private ulong _digest = EntryDigest.Empty;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _removals.Count;
            }
        }
    }

    /// <summary>
    /// Local-change counter, exactly like the other three world stores — bumped once per
    /// genuinely new removal. Before this pass this channel had NO version tripwire at all: a
    /// removal was announced once, reliably, and never mentioned again, so a straggler that
    /// missed it under sustained loss had nothing to repair it — see
    /// docs/research/disconnect-reconnect.md §4.8.
    /// </summary>
    public uint Version { get; private set; }

    /// <summary>Order-independent content fingerprint over every distinct removal this instance
    /// has ever been told about — NOT over the bounded re-declare window, which is ordered by
    /// arrival and is therefore not the same on two ends of a session (see
    /// <see cref="Remember"/>). Beside <see cref="Version"/> for the same reason
    /// <see cref="EventFlagStore.Digest"/> exists — see <see cref="EntryDigest"/>.</summary>
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

    /// <summary>
    /// Applies a removal op exactly once. Returns true only when this op was new AND it
    /// introduced a removal the set did not already hold.
    /// </summary>
    public bool ApplyOp(ContainerLootPacket op)
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

            return Remember(new ContainerLootRemoval(op.ContainerId, op.ItemId));
        }
    }

    /// <summary>
    /// Records a removal this instance discovered for itself — this player emptied a container
    /// here — rather than one a peer announced. Returns true only when the pair is new to the
    /// set, which is the whole anti-echo mechanism: a removal that arrived from a peer is
    /// already in the set by the time the scanner rediscovers it, so it is not announced back.
    /// </summary>
    public bool Add(ContainerLootRemoval removal)
    {
        lock (_gate)
        {
            return Remember(removal);
        }
    }

    /// <summary>Whether the set already holds this pair. Caller must hold <see cref="_gate"/>
    /// for a decision that has to stay true; this is for reporting only.</summary>
    public bool Contains(ContainerLootRemoval removal)
    {
        lock (_gate)
        {
            return _set.Contains(removal);
        }
    }

    private bool Remember(ContainerLootRemoval removal)
    {
        if (!_set.Add(removal))
        {
            return false;
        }

        _removals.Add(removal);
        _digest ^= EntryDigest.ContainerLootRemoval(removal.ContainerId, removal.ItemId);

        if (_removals.Count > MaxRemovals)
        {
            // ⚠ THE WINDOW SHRINKS; THE SET, THE VERSION AND THE DIGEST DO NOT.
            //
            // Eviction only trims the re-declare-to-the-game list — the entry was applied long
            // ago and nothing needs telling again. The digest used to be trimmed with it, on
            // the reasoning that it should describe the same window Snapshot() returns. That is
            // exactly what made two ends of a session disagree forever.
            //
            // The window is ordered BY ARRIVAL, and arrival order is not a shared fact: UDP
            // reorders, retransmits and resyncs, so once a session has passed MaxRemovals
            // distinct removals the host and each client are holding DIFFERENT 512-element
            // windows of the same set. A digest over the window therefore disagrees
            // permanently, even though both ends know precisely the same removals — and the
            // disagreement is not quiet. It is the loot tripwire, so the client asks for a
            // resync twice a second for the rest of the session, and each ask re-offers up to
            // 512 reliable ops (CoopHost.ResyncContainerLoot) that nothing can ever settle.
            //
            // A removal is a permanent fact about the world, so the version and the digest now
            // describe every distinct pair this instance has EVER been told about, which is a
            // SET: order-independent, eviction-independent, and identical on two ends that have
            // heard the same things. `_set` keeping its evicted members is what makes that
            // true, and it also stops an evicted pair re-entering the window and being
            // re-declared to the game as though it were news. The cost is one 8-byte struct per
            // container-item pair for the life of the session, which a whole playthrough
            // measures in tens of thousands.
            _removals.RemoveAt(0);
        }

        Version++;
        return true;
    }

    /// <summary>The removals to re-declare to the game, most recent last.</summary>
    public List<ContainerLootRemoval> Snapshot()
    {
        lock (_gate)
        {
            return [.. _removals];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _removals.Clear();
            _set.Clear();
            _appliedOps.Clear();
            _opHistory.Clear();
            _digest = EntryDigest.Empty;
            Version = 0;
        }
    }
}
