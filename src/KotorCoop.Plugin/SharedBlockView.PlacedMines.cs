namespace KotorCoop.Plugin;

// Party-placed floor-mine replication (shared block version 78, region 0x5A00..0x5BB8). See
// SharedBlock.PlacedMines.cs for the layout and docs/research/mines.md §14 for the design.
internal sealed unsafe partial class SharedBlockView
{
    // ---- capture ring (plugin -> agent) ----------------------------------------------------

    public uint PlacedMineCaptureHead => *U32(SharedBlock.OffsetPlacedMineCaptureHead);

    /// <summary>Publishes one captured placement: fields first, the entry's serial next, the
    /// head LAST, so the agent never pairs a new head with a half-written entry.</summary>
    public uint PublishPlacedMineCapture(uint worldLoadSerial, uint localObjectId, in PlacedMineSpec spec)
    {
        uint serial = PlacedMineCaptureHead + 1;
        byte* entry = baseAddress + SharedBlock.OffsetPlacedMineCaptureRing
            + ((int)((serial - 1) % SharedBlock.PlacedMineCaptureCapacity) * SharedBlock.PlacedMineCaptureStride);

        *(uint*)(entry + SharedBlock.PlacedMineCaptureSerialOffset) = 0;
        *(uint*)(entry + SharedBlock.PlacedMineCaptureWorldSerialOffset) = worldLoadSerial;
        *(uint*)(entry + SharedBlock.PlacedMineCaptureLocalIdOffset) = localObjectId;
        *(float*)(entry + SharedBlock.PlacedMineCaptureXOffset) = spec.X;
        *(float*)(entry + SharedBlock.PlacedMineCaptureYOffset) = spec.Y;
        *(float*)(entry + SharedBlock.PlacedMineCaptureZOffset) = spec.Z;
        *(float*)(entry + SharedBlock.PlacedMineCaptureOrientXOffset) = spec.OrientX;
        *(float*)(entry + SharedBlock.PlacedMineCaptureOrientYOffset) = spec.OrientY;
        *(short*)(entry + SharedBlock.PlacedMineCaptureDetectDcOffset) = spec.DetectDc;
        *(short*)(entry + SharedBlock.PlacedMineCaptureDisarmDcOffset) = spec.DisarmDc;
        *(entry + SharedBlock.PlacedMineCaptureTrapTypeOffset) = spec.TrapType;
        *(ushort*)(entry + SharedBlock.PlacedMineCaptureFactionOffset) = spec.Faction;
        *(uint*)(entry + SharedBlock.PlacedMineCaptureSerialOffset) = serial;
        *U32(SharedBlock.OffsetPlacedMineCaptureHead) = serial;
        return serial;
    }

    // ---- gone ring (plugin -> agent) -------------------------------------------------------

    public void PublishPlacedMineGone(uint worldLoadSerial, uint mineSerial, uint captureSerial)
    {
        uint sequence = *U32(SharedBlock.OffsetPlacedMineGoneHead) + 1;
        byte* entry = baseAddress + SharedBlock.OffsetPlacedMineGoneRing
            + ((int)((sequence - 1) % SharedBlock.PlacedMineGoneCapacity) * SharedBlock.PlacedMineGoneStride);

        *(uint*)(entry + SharedBlock.PlacedMineGoneSequenceOffset) = 0;
        *(uint*)(entry + SharedBlock.PlacedMineGoneWorldSerialOffset) = worldLoadSerial;
        *(uint*)(entry + SharedBlock.PlacedMineGoneMineSerialOffset) = mineSerial;
        *(uint*)(entry + SharedBlock.PlacedMineGoneCaptureOffset) = captureSerial;
        *(uint*)(entry + SharedBlock.PlacedMineGoneSequenceOffset) = sequence;
        *U32(SharedBlock.OffsetPlacedMineGoneHead) = sequence;
    }

    // ---- roster level (agent -> plugin) ----------------------------------------------------

    /// <summary>
    /// Reads the agent's roster under its seqlock. False while the agent is mid-write, when
    /// there has never been a roster, or when it is torn; the caller simply tries next pass.
    /// </summary>
    public bool TryReadPlacedMineRoster(Span<uint> serials, out int count, out uint highest, out uint worldSerial, out uint generation)
    {
        count = 0;
        highest = 0;
        worldSerial = 0;
        generation = Volatile.Read(ref *U32(SharedBlock.OffsetPlacedMineRosterGeneration));
        if (generation == 0 || (generation & 1) != 0)
        {
            return false;
        }

        worldSerial = *U32(SharedBlock.OffsetPlacedMineRosterWorldSerial);
        highest = *U32(SharedBlock.OffsetPlacedMineRosterHighest);
        uint rawCount = *U32(SharedBlock.OffsetPlacedMineRosterCount);
        if (rawCount > SharedBlock.PlacedMineRosterCapacity || rawCount > (uint)serials.Length)
        {
            return false;
        }

        for (int i = 0; i < (int)rawCount; i++)
        {
            serials[i] = *U32(SharedBlock.OffsetPlacedMineRosterSerials + (i * sizeof(uint)));
        }

        if (Volatile.Read(ref *U32(SharedBlock.OffsetPlacedMineRosterGeneration)) != generation)
        {
            return false;
        }

        count = (int)rawCount;
        return true;
    }

    // ---- spawn mailbox (agent -> plugin) ---------------------------------------------------

    public uint PlacedMineSpawnRequest => *U32(SharedBlock.OffsetPlacedMineSpawnRequest);

    public uint PlacedMineSpawnWorldSerial => *U32(SharedBlock.OffsetPlacedMineSpawnWorldSerial);

    public uint PlacedMineSpawnSerial => *U32(SharedBlock.OffsetPlacedMineSpawnSerial);

    public uint PlacedMineSpawnCapture => *U32(SharedBlock.OffsetPlacedMineSpawnCapture);

    public PlacedMineSpec ReadPlacedMineSpawnSpec() => new(
        *(baseAddress + SharedBlock.OffsetPlacedMineSpawnTrapType),
        *F32(SharedBlock.OffsetPlacedMineSpawnX),
        *F32(SharedBlock.OffsetPlacedMineSpawnY),
        *F32(SharedBlock.OffsetPlacedMineSpawnZ),
        0f,
        0f,
        *(short*)(baseAddress + SharedBlock.OffsetPlacedMineSpawnDetectDc),
        *(short*)(baseAddress + SharedBlock.OffsetPlacedMineSpawnDisarmDc),
        *(ushort*)(baseAddress + SharedBlock.OffsetPlacedMineSpawnFaction));

    public uint PlacedMineSpawnApplied => *U32(SharedBlock.OffsetPlacedMineSpawnApplied);

    /// <summary>Acks a request: status and diagnostic id first, the echo LAST.</summary>
    public void AckPlacedMineSpawn(uint request, PlacedMineSpawnStatus status, uint localObjectId)
    {
        *U32(SharedBlock.OffsetPlacedMineSpawnStatus) = (uint)status;
        *U32(SharedBlock.OffsetPlacedMineSpawnLocalId) = localObjectId;
        *U32(SharedBlock.OffsetPlacedMineSpawnApplied) = request;
    }

    public uint PlacedMineFireSuppressed
    {
        get => *U32(SharedBlock.OffsetPlacedMineFireSuppressed);
        set => *U32(SharedBlock.OffsetPlacedMineFireSuppressed) = value;
    }

    public uint PlacedMineMappedCount
    {
        get => *U32(SharedBlock.OffsetPlacedMineMappedCount);
        set => *U32(SharedBlock.OffsetPlacedMineMappedCount) = value;
    }
}
