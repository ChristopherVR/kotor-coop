// Drains KotorCoop.Plugin's item-template-spawn ring (SharedBlock.OffsetItemTemplateSpawnRing,
// block version 59) into the SAME (object id -> real resref) cache
// KotorGameBridge.GroundItems.cs's own ground-item spawn tracking feeds, and — version 59's own
// addition — a second (object id -> was this OUR OWN code, observed when) fact that
// KotorGameBridge.PartyPool.cs's scan uses to suppress an ambient script's own party-pool
// deposit from being counted as sync-relevant content. See docs/research/item-identity.md §13
// for the resref-cache design and docs/research/party-pool-sync.md §20 for the cheat-flag one;
// ItemTemplateSpawnHook.cs (plugin side) for what populates the ring and exactly which
// instruction it hooks.
namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    /// <summary>
    /// One drained item-template-spawn ring entry's own creation fact: whether THIS instance's
    /// own code made the <c>CreateItemOnObject</c> call (see
    /// <c>KotorCoop.Plugin.ItemTemplateSpawnHook.NextSpawnIsCheat</c>'s doc comment), and the
    /// agent's own wall-clock time it learned that — used as a faithful proxy for "when the
    /// engine created it," since <see cref="DrainItemTemplateSpawns"/> runs unconditionally
    /// every tick (docs/research/item-identity.md §13.2) and this fact is therefore observed
    /// within one tick of the real creation, never meaningfully later.
    /// </summary>
    private readonly record struct ItemCreationObservation(bool IsCheat, long ObservedAtMs);

    /// <summary>
    /// objectId -&gt; how/when this session learned it was created — fed by
    /// <see cref="DrainItemTemplateSpawns"/> for EVERY ring entry (cheat or not), unlike
    /// <see cref="_spawnedItemResrefs"/> which only cares about the resref. Bounded and cleared
    /// on module change for the identical reason that cache is (an object id is only meaningful
    /// within the module that minted it).
    /// </summary>
    private readonly Dictionary<uint, ItemCreationObservation> _itemCreationObservations = [];

    /// <summary>Insertion order for <see cref="_itemCreationObservations"/>, FIFO-trimmed the
    /// same way <see cref="_spawnedItemResrefOrder"/> is.</summary>
    private readonly Queue<uint> _itemCreationObservationOrder = new();

    /// <summary>Same reasoning as <see cref="SpawnedItemResrefCacheCapacity"/>: generous past
    /// anything one session's own item-creation churn produces.</summary>
    private const int ItemCreationObservationCacheCapacity = 256;

    /// <summary>internal (not private) only so <c>PartyPoolAmbientSuppressionTests</c>
    /// (KotorCoop.Tests, granted access via <c>InternalsVisibleTo</c>) can seed a creation fact
    /// directly and pin <see cref="IsAmbientPartyPoolNoise"/>'s decision without needing a real
    /// plugin block or a memory scan — the identical seam
    /// <see cref="RememberSpawnedItemResref"/> already grants for its own cache.</summary>
    internal void RememberItemCreation(uint objectId, bool isCheat, long nowMs)
    {
        if (objectId is 0 or ObjectInvalid)
        {
            return;
        }

        if (!_itemCreationObservations.ContainsKey(objectId))
        {
            _itemCreationObservationOrder.Enqueue(objectId);
        }

        // Unlike RememberSpawnedItemResref's "first writer wins", a REPEAT report about the
        // SAME id (the ring re-reporting a slot, or — vanishingly unlikely — a reused id) always
        // overwrites: this fact is about "the freshest thing this session has learned," not an
        // immutable blueprint identity, so the newest observation is always the right one to
        // keep.
        _itemCreationObservations[objectId] = new ItemCreationObservation(isCheat, nowMs);

        while (_itemCreationObservationOrder.Count > ItemCreationObservationCacheCapacity)
        {
            _itemCreationObservations.Remove(_itemCreationObservationOrder.Dequeue());
        }
    }

    /// <summary>
    /// True when this session has a creation fact for <paramref name="objectId"/> — regardless
    /// of age or cheat/script origin; the caller (<c>TryScanPartyPoolTotals</c>) applies its own
    /// recency window. See <see cref="_itemCreationObservations"/>'s own remarks.
    /// </summary>
    internal bool TryGetItemCreationObservation(uint objectId, out bool isCheat, out long observedAtMs)
    {
        if (_itemCreationObservations.TryGetValue(objectId, out ItemCreationObservation observation))
        {
            isCheat = observation.IsCheat;
            observedAtMs = observation.ObservedAtMs;
            return true;
        }

        isCheat = false;
        observedAtMs = 0;
        return false;
    }

    private void ClearItemCreationObservationCache()
    {
        _itemCreationObservations.Clear();
        _itemCreationObservationOrder.Clear();
    }
    /// <summary>
    /// The highest ring cursor value this session has already consumed. 0 before the first
    /// drain, and — unlike <see cref="_spawnedItemResrefs"/> — deliberately NOT reset on a
    /// module change: the plugin's own cursor (<c>SharedBlock.OffsetItemTemplateSpawnCursor</c>)
    /// counts every entry EVER written for the life of the plugin, module boundaries included,
    /// so resetting this half back to 0 would make <see cref="ItemTemplateSpawnRing.ComputeDrainRange"/>
    /// re-read (harmlessly idempotent, since <see cref="RememberSpawnedItemResref"/> already
    /// no-ops on a repeat id) or, worse, spuriously report as LOST every entry this session had
    /// already correctly drained before the transition. The cache the drain actually populates
    /// (<see cref="_spawnedItemResrefs"/>) is what module change needs to clear — an object id
    /// is only meaningful within the module that minted it — not the cursor bookkeeping that
    /// gets it there.
    /// </summary>
    private uint _itemTemplateSpawnLastSeenCursor;

    /// <summary>Cumulative count of ring entries this session's own drain arrived too late for
    /// — more than <see cref="BlockItemTemplateSpawnRingCapacity"/> writes landed between two
    /// drains. Exposed for diagnostics; a nonzero count here means the drain cadence (or the
    /// ring's capacity) needs revisiting, not that anything crashed or corrupted state — a lost
    /// entry simply leaves <see cref="TryGetItemResref"/> falling back to the Tag proxy for
    /// that one item, exactly as it did before this channel existed.</summary>
    public uint LastItemTemplateSpawnLostCount { get; private set; }

    /// <summary>
    /// Reads whatever is new in the plugin's item-template-spawn ring since the last call and
    /// remembers each (object id -&gt; real resref) fact the SAME way
    /// <see cref="RememberSpawnedItemResref"/> already does for a body this session spawned
    /// onto the floor — so <see cref="TryGetItemResref"/> needs no new lookup order, only a
    /// fuller cache to consult.
    ///
    /// Cheap when nothing is new: one dword read (the cursor) and nothing else. Safe to call
    /// every tick regardless of which session capabilities are on, the same way the tag/address
    /// caches are always kept warm — this has no wire cost of its own and everything downstream
    /// of <see cref="TryGetItemResref"/> already tolerates an empty answer.
    /// </summary>
    void IItemBridge.DrainItemTemplateSpawns() => DrainItemTemplateSpawns();

    public void DrainItemTemplateSpawns()
    {
        if (_pluginBlock == 0)
        {
            return;
        }

        if (!_memory.TryReadUInt32(_pluginBlock + BlockItemTemplateSpawnCursor, out uint currentCursor))
        {
            return;
        }

        ItemTemplateSpawnDrainRange range = ItemTemplateSpawnRing.ComputeDrainRange(
            _itemTemplateSpawnLastSeenCursor, currentCursor, BlockItemTemplateSpawnRingCapacity);

        if (range.Lost > 0)
        {
            LastItemTemplateSpawnLostCount += range.Lost;
        }

        for (int i = 0; i < range.Count; i++)
        {
            uint cursorValue = range.StartCursor + (uint)i;
            int slot = ItemTemplateSpawnRing.SlotFor(cursorValue, BlockItemTemplateSpawnRingCapacity);

            nint entry = _pluginBlock + BlockItemTemplateSpawnRing + (slot * BlockItemTemplateSpawnEntryStride);

            if (!_memory.TryReadUInt32(entry + BlockItemTemplateSpawnIdOffset, out uint objectId)
                || objectId == 0 || objectId >= ObjectInvalid)
            {
                continue;
            }

            // Recorded for EVERY entry, cheat or not — TryScanPartyPoolTotals decides what to
            // do with the fact; this drain only reports it. A read failure defaults to
            // isCheat=false, the SAFE direction: an un-derivable flag is treated as "might be
            // ambient noise," never silently trusted as a player action. (The signature scan
            // this agent used to attach already guarantees the plugin's block is this exact
            // version — see PluginSignature's own remarks — so a stride/offset mismatch against
            // an older plugin cannot happen here.)
            bool isCheat = _memory.TryReadUInt32(entry + BlockItemTemplateSpawnIsCheatOffset, out uint rawFlags)
                && (rawFlags & BlockItemTemplateSpawnIsCheatBit) != 0;
            RememberItemCreation(objectId, isCheat, Environment.TickCount64);

            if (!_memory.TryReadAsciiString(
                    entry + BlockItemTemplateSpawnResrefOffset, BlockItemTemplateSpawnResrefLength,
                    out string raw))
            {
                continue;
            }

            // Same trim/lowercase discipline every other resref this bridge reports goes
            // through (RebuildItemTagCache's own tag read) — defensive here too, even though
            // TryReadAsciiString already refused anything outside printable ASCII.
            string resref = SanitiseResref(raw);
            if (resref.Length > 0)
            {
                RememberSpawnedItemResref(objectId, resref);
            }
        }

        _itemTemplateSpawnLastSeenCursor = range.NextLastSeenCursor;
    }
}
