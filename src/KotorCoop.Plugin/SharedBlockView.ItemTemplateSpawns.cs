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
    /// <paramref name="stackCount"/> is version 64's addition
    /// (<see cref="SharedBlock.ItemTemplateSpawnStackCountShift"/>): the stack count the call
    /// asked for, packed into the same flags dword; 0 when the hook could not read it.
    ///
    /// Payload written BEFORE the cursor — the same "counter last" discipline
    /// <see cref="SetLocalBark"/>'s serial and <see cref="TryWriteCreatureLiveness"/>'s id
    /// already follow — so a reader that reads the cursor first and then only trusts entries
    /// covered by the value it read can never observe a half-written slot, even though this
    /// runs from inside an engine call rather than from an ordinary per-frame controller pass.
    /// </summary>
    public uint ItemTemplateSpawnGeneration => *U32(SharedBlock.OffsetItemTemplateSpawnGeneration);
    /// <summary>Invalidates aliases from every prior world/module epoch. Generation zero is
    /// reserved as the in-progress entry marker, so the live generation skips zero on wrap.</summary>
    public uint AdvanceItemTemplateSpawnGeneration()
    {
        uint next = unchecked(ItemTemplateSpawnGeneration + 1);
        if (next == 0)
        {
            next = 1;
        }

        *U32(SharedBlock.OffsetItemTemplateSpawnGeneration) = next;
        return next;
    }

    /// <summary>Publishes a capture with the generation sampled by ItemTemplateSpawnHook at
    /// the actual engine creation site. Never substitutes the current generation here: the
    /// controller may drain this entry after a world invalidation.</summary>
    public void PublishItemTemplateSpawn(
        uint objectId, ReadOnlySpan<byte> resref, bool isCheat, ushort stackCount,
        uint capturedGeneration, uint capturedWorldLoadSerial)
    {
        if (capturedGeneration == 0
            || capturedGeneration != ItemTemplateSpawnGeneration
            || capturedWorldLoadSerial != WorldLoadSerial)
        {
            return;
        }

        uint cursor = *U32(SharedBlock.OffsetItemTemplateSpawnCursor);
        int slot = (int)(cursor % SharedBlock.ItemTemplateSpawnRingCapacity);

        byte* entry = baseAddress + SharedBlock.OffsetItemTemplateSpawnRing
            + (slot * SharedBlock.ItemTemplateSpawnEntryStride);

        // Generation zero marks this slot as being rewritten. It cannot be confused with a
        // valid row because the controller starts generation at one.
        *U32(SharedBlock.OffsetItemTemplateSpawnGenerations + (slot * sizeof(uint))) = 0;
        *U32(SharedBlock.OffsetItemTemplateSpawnTickets + (slot * sizeof(uint))) = ~cursor;
        *U32(SharedBlock.OffsetItemTemplateSpawnWorldSerials + (slot * sizeof(uint))) = 0;

        *(uint*)(entry + SharedBlock.ItemTemplateSpawnIdOffset) = objectId;

        for (int i = 0; i < SharedBlock.ItemTemplateSpawnResrefLength; i++)
        {
            entry[SharedBlock.ItemTemplateSpawnResrefOffset + i] = i < resref.Length ? resref[i] : (byte)0;
        }

        *(uint*)(entry + SharedBlock.ItemTemplateSpawnIsCheatOffset) =
            (isCheat ? SharedBlock.ItemTemplateSpawnIsCheatBit : 0)
            | ((uint)stackCount << SharedBlock.ItemTemplateSpawnStackCountShift);

        *U32(SharedBlock.OffsetItemTemplateSpawnTickets + (slot * sizeof(uint))) = cursor;
        *U32(SharedBlock.OffsetItemTemplateSpawnWorldSerials + (slot * sizeof(uint))) = capturedWorldLoadSerial;
        *U32(SharedBlock.OffsetItemTemplateSpawnGenerations + (slot * sizeof(uint))) = capturedGeneration;

        // Cursor last: the agent only trusts entries the cursor value it just read covers.
        *U32(SharedBlock.OffsetItemTemplateSpawnCursor) = cursor + 1;
    }
}
