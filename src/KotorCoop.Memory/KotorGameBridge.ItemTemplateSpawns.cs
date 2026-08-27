// Drains KotorCoop.Plugin's item-template-spawn ring (SharedBlock.OffsetItemTemplateSpawnRing,
// block version 58) into the SAME (object id -> real resref) cache
// KotorGameBridge.GroundItems.cs's own ground-item spawn tracking feeds. See
// docs/research/item-identity.md §13 for the full design and ItemTemplateSpawnHook.cs (plugin
// side) for what populates the ring and exactly which instruction it hooks.
namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
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
