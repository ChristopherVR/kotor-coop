// The agent's half of party-placed floor-mine replication (KotorCoop.Plugin's
// SharedBlock.PlacedMines.cs, shared block version 78, region 0x5A00..0x5BBC). See
// docs/research/mines.md §14.
using System.Runtime.InteropServices;
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ⚠ KEEP IN SYNC with SharedBlock.PlacedMines.cs (SharedBlockNewRegionOffsetMirrorTests).
    private const int BlockPlacedMineCaptureHead = 0x5A00;
    private const int BlockPlacedMineCaptureRing = 0x5A08;
    private const int BlockPlacedMineCaptureCapacity = 4;
    private const int BlockPlacedMineCaptureStride = 0x28;
    private const int BlockPlacedMineGoneHead = 0x5AA8;
    private const int BlockPlacedMineGoneRing = 0x5AB0;
    private const int BlockPlacedMineGoneCapacity = 8;
    private const int BlockPlacedMineGoneStride = 0x10;
    private const int BlockPlacedMineRosterGeneration = 0x5B30;
    private const int BlockPlacedMineRosterWorldSerial = 0x5B34;
    private const int BlockPlacedMineRosterHighest = 0x5B38;
    private const int BlockPlacedMineRosterCount = 0x5B3C;
    private const int BlockPlacedMineRosterSerials = 0x5B40;
    private const int BlockPlacedMineRosterCapacity = 15;
    private const int BlockPlacedMineSpawnRequest = 0x5B80;
    private const int BlockPlacedMineSpawnWorldSerial = 0x5B84;
    private const int BlockPlacedMineSpawnSerial = 0x5B88;
    private const int BlockPlacedMineSpawnCapture = 0x5B8C;
    private const int BlockPlacedMineSpawnX = 0x5B90;
    private const int BlockPlacedMineSpawnY = 0x5B94;
    private const int BlockPlacedMineSpawnZ = 0x5B98;
    private const int BlockPlacedMineSpawnDetectDc = 0x5B9C;
    private const int BlockPlacedMineSpawnDisarmDc = 0x5B9E;
    private const int BlockPlacedMineSpawnTrapType = 0x5BA0;
    private const int BlockPlacedMineSpawnFaction = 0x5BA2;
    private const int BlockPlacedMineSpawnApplied = 0x5BA4;
    private const int BlockPlacedMineSpawnStatus = 0x5BA8;
    private const int BlockPlacedMineSpawnLocalId = 0x5BAC;
    private const int BlockPlacedMineWorld = 0x5BB8;

    [StructLayout(LayoutKind.Explicit, Size = BlockPlacedMineCaptureStride)]
    private struct CaptureEntry
    {
        [FieldOffset(0x00)] public uint Serial;
        [FieldOffset(0x04)] public uint WorldSerial;
        [FieldOffset(0x08)] public uint LocalId;
        [FieldOffset(0x0C)] public float X;
        [FieldOffset(0x10)] public float Y;
        [FieldOffset(0x14)] public float Z;
        [FieldOffset(0x18)] public float OrientX;
        [FieldOffset(0x1C)] public float OrientY;
        [FieldOffset(0x20)] public short DetectDc;
        [FieldOffset(0x22)] public short DisarmDc;
        [FieldOffset(0x24)] public byte TrapType;
        [FieldOffset(0x26)] public ushort Faction;
    }

    [StructLayout(LayoutKind.Explicit, Size = BlockPlacedMineGoneStride)]
    private struct GoneEntry
    {
        [FieldOffset(0x00)] public uint Sequence;
        [FieldOffset(0x04)] public uint WorldSerial;
        [FieldOffset(0x08)] public uint MineSerial;
        [FieldOffset(0x0C)] public uint CaptureSerial;
    }

    /// <inheritdoc />
    public bool SupportsPlacedMineSync => _pluginBlock != 0;

    /// <inheritdoc />
    public bool TryReadPlacedMineWorld(out uint world)
    {
        world = 0;
        return _pluginBlock != 0
            && _memory.TryReadUInt32(_pluginBlock + BlockPlacedMineWorld, out world)
            && world != 0;
    }

    /// <inheritdoc />
    public bool TryReadPlacedMineHeads(out uint captureHead, out uint goneHead)
    {
        goneHead = 0;
        captureHead = 0;
        return _pluginBlock != 0
            && _memory.TryReadUInt32(_pluginBlock + BlockPlacedMineCaptureHead, out captureHead)
            && _memory.TryReadUInt32(_pluginBlock + BlockPlacedMineGoneHead, out goneHead);
    }

    /// <inheritdoc />
    public IReadOnlyList<PlacedMineCapture> ReadPlacedMineCaptures(ref uint cursor, out int lost)
    {
        lost = 0;
        if (_pluginBlock == 0 || !_memory.TryReadUInt32(_pluginBlock + BlockPlacedMineCaptureHead, out uint head)
            || head == cursor)
        {
            return [];
        }

        if (head < cursor)
        {
            // The block was re-created (a new plugin): start over from its head.
            cursor = head;
            return [];
        }

        uint first = cursor + 1;
        if (head - cursor > BlockPlacedMineCaptureCapacity)
        {
            lost = (int)(head - cursor - BlockPlacedMineCaptureCapacity);
            first = head - BlockPlacedMineCaptureCapacity + 1;
        }

        List<PlacedMineCapture> captures = [];
        for (uint serial = first; serial <= head; serial++)
        {
            nint entry = _pluginBlock + BlockPlacedMineCaptureRing
                + (int)((serial - 1) % BlockPlacedMineCaptureCapacity) * BlockPlacedMineCaptureStride;

            // The serial is the plugin's LAST write inside an entry; a mismatch before or after
            // the read means it is being rewritten, so stop here and pick it up next tick.
            if (!_memory.TryRead(entry, out CaptureEntry e) || e.Serial != serial
                || !_memory.TryReadUInt32(entry, out uint again) || again != serial)
            {
                break;
            }

            captures.Add(new PlacedMineCapture(serial, e.WorldSerial, new PlacedMineRecord(
                e.TrapType, e.X, e.Y, e.Z, e.OrientX, e.OrientY, e.DetectDc, e.DisarmDc, e.Faction)));
            cursor = serial;
        }

        return captures;
    }

    /// <inheritdoc />
    public IReadOnlyList<PlacedMineGone> ReadPlacedMineGone(ref uint cursor, out int lost)
    {
        lost = 0;
        if (_pluginBlock == 0 || !_memory.TryReadUInt32(_pluginBlock + BlockPlacedMineGoneHead, out uint head)
            || head == cursor)
        {
            return [];
        }

        if (head < cursor)
        {
            cursor = head;
            return [];
        }

        uint first = cursor + 1;
        if (head - cursor > BlockPlacedMineGoneCapacity)
        {
            lost = (int)(head - cursor - BlockPlacedMineGoneCapacity);
            first = head - BlockPlacedMineGoneCapacity + 1;
        }

        List<PlacedMineGone> gone = [];
        for (uint sequence = first; sequence <= head; sequence++)
        {
            nint entry = _pluginBlock + BlockPlacedMineGoneRing
                + (int)((sequence - 1) % BlockPlacedMineGoneCapacity) * BlockPlacedMineGoneStride;
            if (!_memory.TryRead(entry, out GoneEntry e) || e.Sequence != sequence
                || !_memory.TryReadUInt32(entry, out uint again) || again != sequence)
            {
                break;
            }

            gone.Add(new PlacedMineGone(sequence, e.WorldSerial, e.MineSerial, e.CaptureSerial));
            cursor = sequence;
        }

        return gone;
    }

    /// <inheritdoc />
    public bool TryWritePlacedMineRoster(uint world, uint highestSerial, IReadOnlyList<uint> serials)
    {
        if (_pluginBlock == 0 || serials.Count > BlockPlacedMineRosterCapacity
            || !_memory.TryReadUInt32(_pluginBlock + BlockPlacedMineRosterGeneration, out uint generation))
        {
            return false;
        }

        // Seqlock: odd while writing, the next even value when done.
        uint writing = (generation & 1) == 0 ? generation + 1 : generation;
        bool ok = _memory.TryWrite(_pluginBlock + BlockPlacedMineRosterGeneration, writing);
        ok &= _memory.TryWrite(_pluginBlock + BlockPlacedMineRosterWorldSerial, world);
        ok &= _memory.TryWrite(_pluginBlock + BlockPlacedMineRosterHighest, highestSerial);
        ok &= _memory.TryWrite(_pluginBlock + BlockPlacedMineRosterCount, (uint)serials.Count);
        for (int i = 0; i < serials.Count; i++)
        {
            ok &= _memory.TryWrite(_pluginBlock + BlockPlacedMineRosterSerials + (i * sizeof(uint)), serials[i]);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockPlacedMineRosterGeneration, writing + 1);
        return ok;
    }

    /// <inheritdoc />
    public uint RequestPlacedMineSpawn(uint world, uint mineSerial, uint captureSerial, PlacedMineRecord record)
    {
        if (_pluginBlock == 0 || mineSerial == 0 || !record.IsValid
            || !_memory.TryReadUInt32(_pluginBlock + BlockPlacedMineSpawnRequest, out uint request))
        {
            return 0;
        }

        bool ok = _memory.TryWrite(_pluginBlock + BlockPlacedMineSpawnWorldSerial, world);
        ok &= _memory.TryWrite(_pluginBlock + BlockPlacedMineSpawnSerial, mineSerial);
        ok &= _memory.TryWrite(_pluginBlock + BlockPlacedMineSpawnCapture, captureSerial);
        ok &= _memory.TryWrite(_pluginBlock + BlockPlacedMineSpawnX, record.X);
        ok &= _memory.TryWrite(_pluginBlock + BlockPlacedMineSpawnY, record.Y);
        ok &= _memory.TryWrite(_pluginBlock + BlockPlacedMineSpawnZ, record.Z);
        ok &= _memory.TryWrite(_pluginBlock + BlockPlacedMineSpawnDetectDc, record.DetectDc);
        ok &= _memory.TryWrite(_pluginBlock + BlockPlacedMineSpawnDisarmDc, record.DisarmDc);
        ok &= _memory.TryWrite(_pluginBlock + BlockPlacedMineSpawnTrapType, record.TrapType);
        ok &= _memory.TryWrite(_pluginBlock + BlockPlacedMineSpawnFaction, record.Faction);

        // The serial LAST, so the plugin never sees a half-written request.
        uint next = request + 1;
        if (next == 0)
        {
            next = 1;
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockPlacedMineSpawnRequest, next);
        return ok ? next : 0;
    }

    /// <inheritdoc />
    public bool TryReadPlacedMineSpawnAck(out uint applied, out uint status, out uint localId)
    {
        status = 0;
        localId = 0;
        applied = 0;
        return _pluginBlock != 0
            && _memory.TryReadUInt32(_pluginBlock + BlockPlacedMineSpawnApplied, out applied)
            && _memory.TryReadUInt32(_pluginBlock + BlockPlacedMineSpawnStatus, out status)
            && _memory.TryReadUInt32(_pluginBlock + BlockPlacedMineSpawnLocalId, out localId);
    }
}
