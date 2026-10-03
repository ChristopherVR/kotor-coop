using System.Text;

namespace KotorCoop.Plugin;

internal sealed unsafe partial class SharedBlockView
{
    public void BeginLocalPartyPublication() => *U32(SharedBlock.OffsetLocalPartyOccupants) |= 1u;
    public void PublishLocalPartyOccupants(uint packedRoster)
    {
        int bank = SharedBlock.OffsetLocalPartyOccupants;
        uint sequence = *U32(bank);
        *U32(bank) = (sequence + 1) | 1;
        *U32(bank + 4) = *U32(SharedBlock.OffsetLivenessNativeEpoch);
        *U32(bank + 8) = *U32(SharedBlock.OffsetWorldLoadSerial);
        *U32(bank + 16) = packedRoster;
        for (int slot = 0; slot < 9; slot++)
        {
            int row = bank + SharedBlock.PartyOccupantRowsOffset + slot * SharedBlock.PartyOccupantStride;
            *U32(row) = PartyOccupantHook.Captured.Generation(slot);
            *U32(row + 4) = PartyOccupantHook.Captured.Replaced(slot) ? 1u : 0u;
            PartyOccupantHook.Captured.CopyTemplate(slot, new Span<byte>(baseAddress + row + 8,16));
        }
        *U32(bank) = ((sequence + 2) & ~1u);
    }
    public bool HostPartyOccupantsCurrent()
    {
        int bank = SharedBlock.OffsetHostPartyOccupants;
        uint sequence = *U32(bank);
        return (sequence & 1) == 0 && sequence != 0
            && *U32(bank + 4) == *U32(SharedBlock.OffsetLivenessNativeEpoch)
            && *U32(bank + 8) == *U32(SharedBlock.OffsetWorldLoadSerial)
            && *U32(bank + 16) == *U32(SharedBlock.OffsetHostPartyRoster);
    }
    public bool TryGetHostPartyOccupant(int slot, out uint generation, out string template)
    {
        generation = 0; template = "";
        if ((uint)slot >= 9 || !HostPartyOccupantsCurrent()) return false;
        int bank = SharedBlock.OffsetHostPartyOccupants;
        uint sequence = *U32(bank);
        int row = bank + SharedBlock.PartyOccupantRowsOffset + slot * SharedBlock.PartyOccupantStride;
        generation = *U32(row);
        ReadOnlySpan<byte> bytes = new(baseAddress + row + 8,16);
        int length = bytes.IndexOf((byte)0); if (length < 0) length = 16;
        if (length > 0 && PartyOccupantTable.ValidTemplate(bytes[..length])) template = Encoding.ASCII.GetString(bytes[..length]);
        return sequence == *U32(bank) && HostPartyOccupantsCurrent();
    }
    public bool HostPartyOccupantRequiresBinding(int slot) => TryGetHostPartyOccupant(slot, out uint generation, out string template)
        && generation != 0 && (HostPartyOccupantReplaced(slot)
            || (!(slot == 0 && template.Equals("end_trask", StringComparison.OrdinalIgnoreCase))
                && (!PartyRosterTemplateTable.TryGetTemplate(slot, out string stock)
                    || !template.Equals(stock, StringComparison.OrdinalIgnoreCase))));
    public bool HostPartyOccupantReplaced(int slot) => (uint)slot < 9 && (baseAddress[SharedBlock.OffsetHostPartyOccupants + 240 + slot] & 1) != 0;
    public bool HostPartyOccupantAdopted(int slot) => (uint)slot < 9 && HostPartyOccupantsCurrent()
        && (baseAddress[SharedBlock.OffsetHostPartyOccupants + 240 + slot] & 2) != 0
        && *U32(SharedBlock.OffsetPartyAdoptedLocalGenerations + slot * 4) == PartyOccupantHook.Captured.Generation(slot);
    public uint HostPartyOccupantSession => *U32(SharedBlock.OffsetHostPartyOccupants + 12);
    public uint HostPartyOccupantSequence => *U32(SharedBlock.OffsetHostPartyOccupants);
    public uint HostPartyOccupantGeneration(int slot) => (uint)slot < 9 && HostPartyOccupantsCurrent()
        ? *U32(SharedBlock.OffsetHostPartyOccupants + SharedBlock.PartyOccupantRowsOffset + slot * SharedBlock.PartyOccupantStride) : 0;
    public bool CanMirrorPartyOccupant(int slot, uint publishedGeneration = uint.MaxValue)
        => TryGetPartyOccupantMirrorProof(slot, publishedGeneration, out _);

    public bool TryGetPartyOccupantMirrorProof(int slot, uint publishedGeneration, out uint session)
    {
        session = 0;
        if ((uint)slot >= 9 || !HostPartyOccupantsCurrent()) return false;
        int bank = SharedBlock.OffsetHostPartyOccupants;
        int row = bank + SharedBlock.PartyOccupantRowsOffset + slot * SharedBlock.PartyOccupantStride;
        uint sequence = *U32(bank);
        if ((sequence & 1) != 0 || sequence == 0) return false;
        uint acceptedSession = *U32(bank + 12);
        uint generation = *U32(row), snapshotGeneration = publishedGeneration == uint.MaxValue ? *U32(row + 4) : publishedGeneration;
        bool valid = *U32(bank + 20) == *U32(bank + 4) && (!HostPartyOccupantRequiresBinding(slot) || HostPartyOccupantAdopted(slot)) && generation == snapshotGeneration && ((generation == 0 && PartyOccupantHook.Captured.FixedStockIdentity(slot))
            || (baseAddress[row + 8] != 0 && PartyOccupantReconcileState.IsApplied(slot, acceptedSession, generation)));
        System.Threading.Thread.MemoryBarrier();
        if (!valid || sequence != *U32(bank) || !HostPartyOccupantsCurrent()) return false;
        session = acceptedSession;
        return true;
    }
    public bool TryCopyCompanionEquipment(int index, in PartyPresenceView expected, Span<byte> destination)
    {
        if ((uint)index >= SharedBlock.PartyPresenceCapacity || destination.Length < 48) return false;
        ref uint sequence = ref *U32(SharedBlock.OffsetPartyPresenceSlots + index * SharedBlock.PartyPresenceStride + 0x24);
        uint before = System.Threading.Volatile.Read(ref sequence);
        if ((before & 1) != 0 || !TryGetPartyPresence(index,out PartyPresenceView current) || current != expected) return false;
        new ReadOnlySpan<byte>(GetCompanionEquipResref(index,0),48).CopyTo(destination);
        System.Threading.Thread.MemoryBarrier();
        return before == System.Threading.Volatile.Read(ref sequence)
            && TryGetPartyPresence(index,out current) && current == expected
            && CanMirrorPartyOccupant((int)expected.RosterSlotPlusOne - 1, expected.OccupantGeneration);
    }
}
