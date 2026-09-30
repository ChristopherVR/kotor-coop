using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    private const int BlockLocalPartyOccupants = 0xBC00, BlockHostPartyOccupants = 0xBD00;
    private uint _partyPresenceWorldEpoch;
    private const int PartyOccupantRows = 24, PartyOccupantStride = 24;
    private bool TryReadPartyOccupants(uint packed, out PartyOccupantIdentity[] occupants, out uint serial, out uint epoch)
    {
        occupants = new PartyOccupantIdentity[9]; serial = 0; epoch = 0;
        nint bank = _pluginBlock + BlockLocalPartyOccupants;
        if (!_memory.TryReadUInt32(bank, out uint sequence) || (sequence & 1) != 0 || sequence == 0
            || !_memory.TryReadUInt32(bank + 4, out epoch) || epoch == 0
            || !_memory.TryReadUInt32(bank + 8, out serial)
            || !_memory.TryReadUInt32(bank + 16, out uint capturedMask) || capturedMask != packed) return false;
        for (int slot = 0; slot < 9; slot++)
        {
            nint row = bank + PartyOccupantRows + slot * PartyOccupantStride;
            if (!_memory.TryReadUInt32(row, out uint generation)
                || !_memory.TryReadAsciiString(row + 8,16,out string template)) return false;
            if (!_memory.TryReadUInt32(row + 4, out uint replaced) || replaced > 1) return false;
            occupants[slot] = new(generation, template, replaced != 0);
        }
        return _memory.TryReadUInt32(bank, out uint after) && after == sequence
            && TryReadLivenessEpoch(out uint currentEpoch) && currentEpoch == epoch
            && TryReadWorldLoadSerial(out uint currentSerial) && currentSerial == serial;
    }
    private uint ReadPartyOccupantGeneration(int slot)
    {
        if (_pluginBlock == 0 || (uint)slot >= 9) return 0;
        _memory.TryReadUInt32(_pluginBlock + BlockLocalPartyOccupants + PartyOccupantRows + slot * PartyOccupantStride, out uint generation);
        return generation;
    }
    private bool PartySlotSideChannelHeld(int slot)
    {
        if (_pluginBlock == 0 || (uint)slot >= 9) return false;
        nint row = _pluginBlock + BlockLocalPartyOccupants + PartyOccupantRows + slot * PartyOccupantStride;
        if (!_memory.TryReadUInt32(row, out uint generation)) return true;
        if (generation == 0) return false;
        return !_memory.TryReadUInt32(row + 4, out uint replaced) || replaced != 0
            || !_memory.TryReadUInt32(row + 8, out uint firstTemplateBytes) || firstTemplateBytes == 0;
    }
}
