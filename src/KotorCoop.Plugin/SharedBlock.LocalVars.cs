namespace KotorCoop.Plugin;

// Per-object local-variable replication (coop.ini local_var_sync, version 77, region
// 0x5800..0x5A00, TotalSize grown to 0x5A00). docs/research/quest-sync.md §8.3.
//
// Two one-way channels:
//   * plugin -> agent: a 16-entry capture ring LocalVarSyncHook fills from inside the engine's
//     own SetLocalBoolean/SetLocalNumber VM command, for allowlisted (module, object, slot)
//     writes only. Cursor written LAST, each row's ticket written after its payload.
//   * agent -> plugin: the session's current values for this module's allowlisted keys, under
//     a seqlock serial (odd while the agent is writing), stamped with the plugin generation the
//     agent built it for and the capture cursor it had drained, so LocalVarController never
//     applies a list from an earlier world or overwrites a local write the agent has not seen.
//
// Entry word layout is LocalVarSyncRule.PackEntry: GIT object id (16) | slot (7) | kind (1) |
// value byte (8). Mirrored by KotorGameBridge.LocalVars.cs.
internal partial struct SharedBlock
{
    /// <summary>Plugin -&gt; agent. Count of captures EVER written; the Nth capture (1-based)
    /// lives in row <c>(N - 1) % LocalVarCaptureCapacity</c>. Written last.</summary>
    public const int OffsetLocalVarCaptureCursor = 0x5800;

    /// <summary>Plugin -&gt; agent. Bit 0 = capture hook installed, bit 1 = local_var_sync on.</summary>
    public const int OffsetLocalVarStatus = 0x5804;

    /// <summary>Plugin -&gt; agent. The plugin's world generation, advanced on every
    /// InvalidateAll (module change, world unload). Never zero once the controller exists.</summary>
    public const int OffsetLocalVarGeneration = 0x5808;

    /// <summary>Plugin -&gt; agent. Diagnostic: writes to a watched (kind, slot) that the full
    /// allowlist match then refused (tag, id or module differed). Never reset.</summary>
    public const int OffsetLocalVarRefused = 0x580C;

    /// <summary>Plugin -&gt; agent. The capture ring: 16 rows of 16 bytes.</summary>
    public const int OffsetLocalVarCaptureRing = 0x5810;

    public const int LocalVarCaptureCapacity = 16;
    public const int LocalVarCaptureStride = 0x10;

    /// <summary>Row +0: FNV-1a hash of the module the write happened in.</summary>
    public const int LocalVarCaptureModuleOffset = 0x00;

    /// <summary>Row +4: <c>LocalVarSyncRule.PackEntry</c> word.</summary>
    public const int LocalVarCaptureEntryOffset = 0x04;

    /// <summary>Row +8: <see cref="OffsetLocalVarGeneration"/> at the moment of the write.</summary>
    public const int LocalVarCaptureGenerationOffset = 0x08;

    /// <summary>Row +C: the 1-based cursor value this row was written for; written after the
    /// payload, so a row mid-rewrite never carries the ticket the reader expects.</summary>
    public const int LocalVarCaptureTicketOffset = 0x0C;

    /// <summary>Agent -&gt; plugin. Seqlock serial: odd while the agent is rewriting the list.</summary>
    public const int OffsetLocalVarApplySerial = 0x5910;

    /// <summary>Agent -&gt; plugin. Rows valid in <see cref="OffsetLocalVarApplyEntries"/>.</summary>
    public const int OffsetLocalVarApplyCount = 0x5914;

    /// <summary>Agent -&gt; plugin. The <see cref="OffsetLocalVarGeneration"/> the agent read
    /// when it built this list; the plugin ignores a list stamped with any other generation.</summary>
    public const int OffsetLocalVarApplyGeneration = 0x5918;

    /// <summary>Agent -&gt; plugin. The capture cursor the agent had drained when it built this
    /// list. A key captured locally after that point is skipped until the list catches up.</summary>
    public const int OffsetLocalVarApplyDrainedCursor = 0x591C;

    /// <summary>Agent -&gt; plugin. 28 rows of (u32 module hash, u32 entry word).</summary>
    public const int OffsetLocalVarApplyEntries = 0x5920;

    public const int LocalVarApplyCapacity = 28;
    public const int LocalVarApplyStride = 0x08;

    public const uint LocalVarStatusHookInstalled = 1u << 0;
    public const uint LocalVarStatusEnabled = 1u << 1;
}
