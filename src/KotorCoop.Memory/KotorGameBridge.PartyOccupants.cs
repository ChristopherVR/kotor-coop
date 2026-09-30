using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    private const int BlockLocalPartyOccupants = 0xBC00, BlockHostPartyOccupants = 0xBD00;
    private uint _partyPresenceWorldEpoch;
    private PartyAdoptionProof? _partyAdoptionProof;
    private PartyRoster _publishedHostPartyRoster;
    private PartyRoster _hostPartyRefreshProof;
    public void RecordHostPartyRefreshProof(PartyRoster host) => _hostPartyRefreshProof = host;
    public void RecordPartyAdoptionProof(PartyRoster host, IReadOnlyList<string> transferred, uint adoptedSaveIdentity)
    {
        _partyAdoptionProof = null;
        if (TryReadWorldLoadSerial(out uint serial) && TryReadLivenessEpoch(out uint epoch) && epoch != 0
            && TryReadLocalPartyRoster(out PartyRoster local) && local.HostLoadSerial == serial && local.HostNativeEpoch == epoch)
            {
            _partyAdoptionProof = new(host, transferred, serial, epoch, adoptedSaveIdentity,
                Enumerable.Range(0, 9).Select(slot => local.OccupantFor(slot).Generation).ToArray());
            PartyOccupantIdentity[] proven = Enumerable.Range(0,9).Select(slot =>
                _partyAdoptionProof.Allows(slot, host, serial, epoch, local.OccupantFor(slot).Generation)
                    ? host.OccupantFor(slot) : new PartyOccupantIdentity(0, "")).ToArray();
            TryRestoreSavedPartyOccupants(proven, local);
        }
    }
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
        if (_pluginBlock == 0 || (uint)slot >= 9
            || !_memory.TryReadUInt32(_pluginBlock + BlockLocalPartyOccupants, out uint sequence)
            || sequence == 0 || (sequence & 1) != 0) return uint.MaxValue;
        _memory.TryReadUInt32(_pluginBlock + BlockLocalPartyOccupants + PartyOccupantRows + slot * PartyOccupantStride, out uint generation);
        return generation;
    }
    private bool PartySlotSideChannelHeld(int slot)
    {
        if (_pluginBlock == 0 || (uint)slot >= 9) return false;
        nint row = _pluginBlock + BlockLocalPartyOccupants + PartyOccupantRows + slot * PartyOccupantStride;
        if (!_memory.TryReadUInt32(_pluginBlock + BlockLocalPartyOccupants, out uint localSequence)
            || localSequence == 0 || (localSequence & 1) != 0
            || !_memory.TryReadUInt32(row, out uint generation)) return true;
        bool StillCurrent() => _memory.TryReadUInt32(_pluginBlock + BlockLocalPartyOccupants, out uint after) && after == localSequence;
        if (generation == 0) return !StillCurrent();
        if (TryReadWorldLoadSerial(out uint hostSerial) && TryReadLivenessEpoch(out uint hostEpoch)
            && _hostPartyRefreshProof.HostNativeEpoch == hostEpoch && _hostPartyRefreshProof.HostLoadSerial == hostSerial
            && _hostPartyRefreshProof.OccupantFor(slot).Generation == generation
            && _hostPartyRefreshProof.OccupantFor(slot).Template.Length != 0) return !StillCurrent();
        if (TryReadWorldLoadSerial(out uint serial) && TryReadLivenessEpoch(out uint epoch)
            && (_partyAdoptionProof?.Allows(slot, _publishedHostPartyRoster, serial, epoch, generation) ?? false)) return !StillCurrent();
        bool held = !_memory.TryReadUInt32(row + 4, out uint replaced) || replaced != 0
            || !_memory.TryReadUInt32(row + 8, out uint firstTemplateBytes) || firstTemplateBytes == 0;
        return held || !StillCurrent();
    }
}
