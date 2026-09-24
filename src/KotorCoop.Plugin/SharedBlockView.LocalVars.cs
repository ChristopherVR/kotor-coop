namespace KotorCoop.Plugin;

// Readers/writers for SharedBlock.LocalVars.cs (local_var_sync, version 77).
internal sealed unsafe partial class SharedBlockView
{
    public uint LocalVarGeneration => *U32(SharedBlock.OffsetLocalVarGeneration);

    public uint LocalVarCaptureCursor => *U32(SharedBlock.OffsetLocalVarCaptureCursor);

    /// <summary>Advances the plugin's local-variable world generation, skipping zero (the
    /// "never set" value a fresh block and the agent both treat as "no world").</summary>
    public uint AdvanceLocalVarGeneration()
    {
        uint next = unchecked(LocalVarGeneration + 1);
        if (next == 0)
        {
            next = 1;
        }

        *U32(SharedBlock.OffsetLocalVarGeneration) = next;
        return next;
    }

    public void SetLocalVarStatus(bool hookInstalled, bool enabled) =>
        *U32(SharedBlock.OffsetLocalVarStatus) =
            (hookInstalled ? SharedBlock.LocalVarStatusHookInstalled : 0)
            | (enabled ? SharedBlock.LocalVarStatusEnabled : 0);

    public void CountLocalVarRefused() =>
        *U32(SharedBlock.OffsetLocalVarRefused) = unchecked(*U32(SharedBlock.OffsetLocalVarRefused) + 1);

    /// <summary>
    /// Appends one capture and returns the cursor value it was published under. Called from
    /// the engine hook on the game thread; plain stores only. Row payload first, then the
    /// row's ticket, then the global cursor — the agent trusts a row only when the cursor it
    /// read covers it AND the row's ticket equals that row's 1-based position.
    /// </summary>
    public uint PublishLocalVarCapture(uint moduleHash, uint entryWord, uint generation)
    {
        uint cursor = *U32(SharedBlock.OffsetLocalVarCaptureCursor);
        int row = (int)(cursor % SharedBlock.LocalVarCaptureCapacity);
        byte* entry = baseAddress + SharedBlock.OffsetLocalVarCaptureRing + (row * SharedBlock.LocalVarCaptureStride);

        *(uint*)(entry + SharedBlock.LocalVarCaptureTicketOffset) = 0;
        *(uint*)(entry + SharedBlock.LocalVarCaptureModuleOffset) = moduleHash;
        *(uint*)(entry + SharedBlock.LocalVarCaptureEntryOffset) = entryWord;
        *(uint*)(entry + SharedBlock.LocalVarCaptureGenerationOffset) = generation;

        uint published = unchecked(cursor + 1);
        *(uint*)(entry + SharedBlock.LocalVarCaptureTicketOffset) = published;
        *U32(SharedBlock.OffsetLocalVarCaptureCursor) = published;
        return published;
    }

    /// <summary>Reads the row published under 1-based cursor value <paramref name="published"/>;
    /// false once that row has been overwritten by a later capture.</summary>
    public bool TryReadLocalVarCapture(uint published, out uint moduleHash, out uint entryWord, out uint generation)
    {
        int row = (int)((published - 1) % SharedBlock.LocalVarCaptureCapacity);
        byte* entry = baseAddress + SharedBlock.OffsetLocalVarCaptureRing + (row * SharedBlock.LocalVarCaptureStride);
        moduleHash = *(uint*)(entry + SharedBlock.LocalVarCaptureModuleOffset);
        entryWord = *(uint*)(entry + SharedBlock.LocalVarCaptureEntryOffset);
        generation = *(uint*)(entry + SharedBlock.LocalVarCaptureGenerationOffset);
        return *(uint*)(entry + SharedBlock.LocalVarCaptureTicketOffset) == published;
    }

    /// <summary>
    /// Copies the agent's apply list under its seqlock. False when the agent is mid-write, the
    /// serial moved during the copy, or the list is empty; <paramref name="count"/> rows of
    /// (module hash, entry word) are valid in the spans otherwise.
    /// </summary>
    public bool TryReadLocalVarApplyList(
        Span<uint> moduleHashes, Span<uint> entryWords, out int count, out uint generation, out uint drainedCursor)
    {
        count = 0;
        generation = 0;
        drainedCursor = 0;

        uint before = *(volatile uint*)(baseAddress + SharedBlock.OffsetLocalVarApplySerial);
        if (before == 0 || (before & 1) != 0)
        {
            return false;
        }

        uint rows = *U32(SharedBlock.OffsetLocalVarApplyCount);
        generation = *U32(SharedBlock.OffsetLocalVarApplyGeneration);
        drainedCursor = *U32(SharedBlock.OffsetLocalVarApplyDrainedCursor);
        int n = (int)Math.Min(rows, (uint)Math.Min(SharedBlock.LocalVarApplyCapacity, Math.Min(moduleHashes.Length, entryWords.Length)));

        for (int i = 0; i < n; i++)
        {
            byte* row = baseAddress + SharedBlock.OffsetLocalVarApplyEntries + (i * SharedBlock.LocalVarApplyStride);
            moduleHashes[i] = *(uint*)row;
            entryWords[i] = *(uint*)(row + 4);
        }

        uint after = *(volatile uint*)(baseAddress + SharedBlock.OffsetLocalVarApplySerial);
        if (after != before)
        {
            return false;
        }

        count = n;
        return true;
    }
}
