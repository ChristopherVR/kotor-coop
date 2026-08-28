namespace KotorCoop.Plugin;

// Ring writer for SharedBlock.OffsetItemTemplateSpawnRing. See SharedBlock.ItemTemplateSpawns.cs
// for the region layout and ItemTemplateSpawnHook.cs for what calls this and when.
internal sealed unsafe partial class SharedBlockView
{
    /// <summary>
    /// Plugin -&gt; agent. Appends one (object id, resref, cheat flag) triple the engine's own
    /// <c>CreateItemOnObject</c> handler just produced to the item-template-spawn ring, and
    /// advances the write cursor by one.
    ///
    /// <paramref name="isCheat"/> is version 59's own addition
    /// (<see cref="SharedBlock.ItemTemplateSpawnIsCheatOffset"/>, docs/research/
    /// party-pool-sync.md §20): true when this instance's own code made the call (see
    /// <c>ItemTemplateSpawnHook.NextSpawnIsCheat</c>'s doc comment), false for a vanilla
    /// script's own heartbeat/plot logic.
    ///
    /// Payload written BEFORE the cursor — the same "counter last" discipline
    /// <see cref="SetLocalBark"/>'s serial and <see cref="TryWriteCreatureLiveness"/>'s id
    /// already follow — so a reader that reads the cursor first and then only trusts entries
    /// covered by the value it read can never observe a half-written slot, even though this
    /// runs from inside an engine call rather than from an ordinary per-frame controller pass.
    /// </summary>
    public void PublishItemTemplateSpawn(uint objectId, ReadOnlySpan<byte> resref, bool isCheat)
    {
        uint cursor = *U32(SharedBlock.OffsetItemTemplateSpawnCursor);
        int slot = (int)(cursor % SharedBlock.ItemTemplateSpawnRingCapacity);

        byte* entry = baseAddress + SharedBlock.OffsetItemTemplateSpawnRing
            + (slot * SharedBlock.ItemTemplateSpawnEntryStride);

        *(uint*)(entry + SharedBlock.ItemTemplateSpawnIdOffset) = objectId;

        for (int i = 0; i < SharedBlock.ItemTemplateSpawnResrefLength; i++)
        {
            entry[SharedBlock.ItemTemplateSpawnResrefOffset + i] = i < resref.Length ? resref[i] : (byte)0;
        }

        *(uint*)(entry + SharedBlock.ItemTemplateSpawnIsCheatOffset) =
            isCheat ? SharedBlock.ItemTemplateSpawnIsCheatBit : 0;

        // Cursor last: the agent only trusts entries the cursor value it just read covers.
        *U32(SharedBlock.OffsetItemTemplateSpawnCursor) = cursor + 1;
    }
}
