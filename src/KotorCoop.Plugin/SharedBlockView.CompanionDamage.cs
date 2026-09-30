namespace KotorCoop.Plugin;

// Companion damage TAKEN — the companion sibling of SharedBlockView.Combat.cs's damage-ledger
// accessors. See SharedBlock.CompanionDamage.cs for the block layout and
// docs/research/companion-state-sync.md §13 for the design.
internal sealed unsafe partial class SharedBlockView
{
    /// <summary>
    /// Publishes what this instance's engine has actually applied to one currently-released
    /// companion, for the agent to forward to the host. The roster slot is written LAST, so
    /// the agent never pairs a cumulative total with the wrong companion's identity.
    /// </summary>
    public void SetCompanionDamageTaken(int index, uint rosterSlotPlusOne, uint cumulative, uint serial, uint occupantGeneration = 0)
    {
        byte* slot = baseAddress + SharedBlock.OffsetCompanionDamageSlots
            + (Clamp(index, SharedBlock.CompanionDamageSlotCapacity) * SharedBlock.CompanionDamageSlotStride);

        *(uint*)(slot + 0x04) = cumulative;
        *(uint*)(slot + 0x08) = serial;
        uint roster = KotorCoop.Protocol.CompanionDamageSlots.RosterSlotPlusOne(rosterSlotPlusOne);
        *U32(SharedBlock.OffsetCompanionTakenMetadata + 20 + index * 4) = roster == 0 ? 0 : occupantGeneration;
        *(uint*)(slot + 0x00) = rosterSlotPlusOne;
    }

    public uint CompanionDamageCount
    {
        get => *U32(SharedBlock.OffsetCompanionDamageCount);
        set => *U32(SharedBlock.OffsetCompanionDamageCount) = value;
    }

    public uint CompanionDamageGeneration
    {
        get => *U32(SharedBlock.OffsetCompanionDamageGeneration);
        set => *U32(SharedBlock.OffsetCompanionDamageGeneration) = value;
    }

    /// <summary>Agent-written: how much every peer has dealt to each of THIS host's own
    /// companions, keyed by NPC roster slot; zeroed by the plugin on a module transition like
    /// every other combat region.</summary>
    public uint CompanionDamageOwedCount
    {
        get => *U32(SharedBlock.OffsetCompanionDamageOwedCount);
        set => *U32(SharedBlock.OffsetCompanionDamageOwedCount) = value;
    }

    public void GetCompanionDamageOwed(int index, out uint rosterSlotPlusOne, out uint cumulative)
    {
        byte* slot = baseAddress + SharedBlock.OffsetCompanionDamageOwedSlots
            + (index * SharedBlock.CompanionDamageOwedStride);

        rosterSlotPlusOne = *(uint*)(slot + 0x00);
        cumulative = *(uint*)(slot + 0x04);
    }

    public uint CompanionDamageApplied
    {
        get => *U32(SharedBlock.OffsetCompanionDamageApplied);
        set => *U32(SharedBlock.OffsetCompanionDamageApplied) = value;
    }
    public uint BeginCompanionTakenPublish()
    {
        int bank = SharedBlock.OffsetCompanionTakenMetadata;
        uint sequence = *U32(bank);
        *U32(bank) = (sequence + 1) | 1;
        *U32(bank + 4) = *U32(SharedBlock.OffsetLivenessNativeEpoch);
        *U32(bank + 8) = *U32(SharedBlock.OffsetWorldLoadSerial);
        return sequence;
    }
    public void EndCompanionTakenPublish(uint sequence) => *U32(SharedBlock.OffsetCompanionTakenMetadata) = (sequence + 2) & ~1u;
    public bool CompanionOwedCurrent(out uint sequence)
    {
        int bank = SharedBlock.OffsetCompanionOwedMetadata;
        sequence = *U32(bank);
        return sequence != 0 && (sequence & 1) == 0
            && *U32(bank + 4) == *U32(SharedBlock.OffsetLivenessNativeEpoch)
            && *U32(bank + 8) == *U32(SharedBlock.OffsetWorldLoadSerial);
    }
    public uint CompanionOwedOccupant(int index) => *U32(SharedBlock.OffsetCompanionOwedMetadata + 16 + index * 4);
    public bool CompanionOwedStillCurrent(uint sequence) => CompanionOwedCurrent(out uint after) && sequence == after;
}
