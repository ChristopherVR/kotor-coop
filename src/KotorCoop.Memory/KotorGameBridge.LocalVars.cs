// Per-object local-variable replication (coop.ini local_var_sync, shared block version 77,
// region 0x5800..0x5A00). Mirrors KotorCoop.Plugin's SharedBlock.LocalVars.cs; the pure
// decisions live in LocalVarBlockRules. See docs/research/quest-sync.md §8.3.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    private const int BlockLocalVarCaptureCursor = 0x5800;
    private const int BlockLocalVarStatus = 0x5804;
    private const int BlockLocalVarGeneration = 0x5808;
    private const int BlockLocalVarCaptureRing = 0x5810;
    private const int BlockLocalVarCaptureStride = 0x10;
    private const int BlockLocalVarApplySerial = 0x5910;
    private const int BlockLocalVarApplyCount = 0x5914;
    private const int BlockLocalVarApplyGeneration = 0x5918;
    private const int BlockLocalVarApplyDrainedCursor = 0x591C;
    private const int BlockLocalVarApplyEntries = 0x5920;
    private const int BlockLocalVarApplyStride = 0x08;

    /// <summary>Plugin status bit 1: its own coop.ini local_var_sync is on.</summary>
    private const uint LocalVarStatusEnabled = 1u << 1;

    private bool _localVarPrimed;
    private uint _localVarDrained;
    private uint _localVarApplySerial;
    private int _localVarApplyCursor;
    private ulong _localVarLastSignature;

    /// <summary>Rows the ring overwrote before a drain reached them, since attach.</summary>
    public uint LocalVarCapturesLost { get; private set; }

    public int LocalVarApplyCount { get; private set; }

    public bool SupportsLocalVarSync =>
        _pluginBlock != 0
        && _memory.TryReadUInt32(_pluginBlock + BlockLocalVarStatus, out uint status)
        && (status & LocalVarStatusEnabled) != 0;

    public List<KeyValuePair<ulong, sbyte>> CollectLocalVarCaptures()
    {
        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockLocalVarCaptureCursor, out uint cursor))
        {
            return [];
        }

        // First read after attach: whatever is already in the ring was written before this
        // agent was watching, possibly in a world this session never shared. A baseline, never a
        // republish — republishing would mint a fresh serial over newer peer values.
        if (!_localVarPrimed)
        {
            _localVarPrimed = true;
            _localVarDrained = cursor;
            return [];
        }

        if (cursor == _localVarDrained)
        {
            return [];
        }

        if (!EngineGlobals.TryReadModuleName(_memory, out string module)
            || !_memory.TryReadUInt32(_pluginBlock + BlockLocalVarGeneration, out uint generation))
        {
            // Leave the rows for the next pass: a world between lives reads no module.
            return [];
        }

        uint moduleHash = LocalVarSyncRule.HashModule(module);
        List<KeyValuePair<ulong, sbyte>> changes = LocalVarBlockRules.Drain(
            cursor, _localVarDrained, generation, moduleHash, ReadLocalVarRow, out uint lost);

        // The generation moving under the drain means a world edge crossed it: every row read
        // was stamped with the old generation or is being rewritten for the new one. Drop them.
        if (!_memory.TryReadUInt32(_pluginBlock + BlockLocalVarGeneration, out uint after) || after != generation)
        {
            changes.Clear();
        }

        LocalVarCapturesLost += lost;
        _localVarDrained = cursor;
        return changes;
    }

    private LocalVarCaptureRow ReadLocalVarRow(uint ticket)
    {
        nint row = _pluginBlock + BlockLocalVarCaptureRing
            + (int)((ticket - 1) % LocalVarBlockRules.CaptureCapacity) * BlockLocalVarCaptureStride;

        // Ticket first and last: a row the plugin rewrote while it was being read fails one of
        // the two reads (the plugin zeroes the ticket before touching the payload).
        if (!_memory.TryReadUInt32(row + 0x0C, out uint ticketBefore)
            || !_memory.TryReadUInt32(row + 0x00, out uint moduleHash)
            || !_memory.TryReadUInt32(row + 0x04, out uint word)
            || !_memory.TryReadUInt32(row + 0x08, out uint generation)
            || !_memory.TryReadUInt32(row + 0x0C, out uint ticketAfter)
            || ticketBefore != ticketAfter)
        {
            return default;
        }

        return new LocalVarCaptureRow(moduleHash, word, generation, ticketAfter);
    }

    public bool TryWriteLocalVarStates(IReadOnlyList<KeyValuePair<ulong, sbyte>> flags)
    {
        if (_pluginBlock == 0 || !_localVarPrimed
            || !EngineGlobals.TryReadModuleName(_memory, out string module)
            || !_memory.TryReadUInt32(_pluginBlock + BlockLocalVarGeneration, out uint generation)
            || generation == 0)
        {
            return false;
        }

        uint moduleHash = LocalVarSyncRule.HashModule(module);
        List<uint> words = LocalVarBlockRules.SelectApplyEntries(flags, moduleHash, ref _localVarApplyCursor);

        ulong signature = 14695981039346656037UL;
        void Mix(uint v) => signature = (signature ^ v) * 1099511628211UL;
        Mix(moduleHash);
        Mix(generation);
        Mix(_localVarDrained);
        Mix((uint)words.Count);
        foreach (uint word in words)
        {
            Mix(word);
        }

        if (signature == _localVarLastSignature)
        {
            return true;
        }

        // Seqlock: odd while rewriting, the next even value once complete. Resumed from the
        // block's own value after an agent restart so the serial never runs backwards.
        if (_localVarApplySerial == 0 && _memory.TryReadUInt32(_pluginBlock + BlockLocalVarApplySerial, out uint existing))
        {
            _localVarApplySerial = existing & ~1u;
        }

        uint writing = _localVarApplySerial + 1;
        bool ok = _memory.TryWrite(_pluginBlock + BlockLocalVarApplySerial, writing);
        for (int i = 0; i < words.Count; i++)
        {
            nint row = _pluginBlock + BlockLocalVarApplyEntries + (i * BlockLocalVarApplyStride);
            ok &= _memory.TryWrite(row, moduleHash);
            ok &= _memory.TryWrite(row + 4, words[i]);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockLocalVarApplyCount, (uint)words.Count);
        ok &= _memory.TryWrite(_pluginBlock + BlockLocalVarApplyGeneration, generation);
        ok &= _memory.TryWrite(_pluginBlock + BlockLocalVarApplyDrainedCursor, _localVarDrained);

        _localVarApplySerial = writing + 1;
        if (_localVarApplySerial == 0)
        {
            _localVarApplySerial = 2;
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockLocalVarApplySerial, _localVarApplySerial);
        if (ok)
        {
            _localVarLastSignature = signature;
            LocalVarApplyCount = words.Count;
        }

        return ok;
    }
}
