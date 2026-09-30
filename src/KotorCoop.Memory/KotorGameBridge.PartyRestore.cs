using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    private const int BlockPartyRestore = 0xBEB0;
    /// <summary>Unknown is held too: a save must never assume a script-controlled party
    /// became safe merely because another channel temporarily lost its solo flag.</summary>
    public bool IsPartyScriptControlled
    {
        get
        {
            int slot = 0;
            bool readable = TryGetPartyTable(out nint table) && _memory.TryReadInt32(table + 0xF0, out slot);
            return !PartyScriptSaveSafetyRule.CanSave(readable, readable ? slot : 0);
        }
    }
    public bool TryRestoreSavedPartyOccupants(IReadOnlyList<PartyOccupantIdentity> verified, PartyRoster captured)
    {
        if (_pluginBlock == 0 || !TryReadLocalPartyRoster(out PartyRoster current) || current != captured
            || !TryReadLivenessEpoch(out uint epoch) || epoch == 0 || epoch != captured.HostNativeEpoch
            || !TryReadWorldLoadSerial(out uint serial) || serial != captured.HostLoadSerial
            || !_memory.TryReadUInt32(_pluginBlock + BlockPartyRestore, out uint sequence)) return false;
        nint bank = _pluginBlock + BlockPartyRestore;
        if (!_memory.TryWrite(bank, (sequence + 1) | 1u)) return false;
        bool ok = _memory.TryWrite(bank + 4, epoch);
        ok &= _memory.TryWrite(bank + 8, serial);
        for (int slot = 0; slot < 9; slot++)
        {
            nint row = bank + 16 + slot * 24;
            PartyOccupantIdentity occupant = slot < verified.Count ? verified[slot] : new(0, "");
            ok &= _memory.TryWrite(row, captured.OccupantFor(slot).Generation);
            uint body = captured.ObjectIdFor(slot);
            ok &= _memory.TryWrite(row + 4, body == 0 ? 0x7F000000u : body);
            ok &= _memory.TryWriteAsciiString(row + 8,
                ModSupportProfile.IsResref(occupant.Template) && PartyCacheFingerprint.IsValid(occupant.CacheFingerprint) ? occupant.Template : "", 16);
        }
        return ok && TryReadLocalPartyRoster(out current) && current == captured
            && _memory.TryWrite(bank, (sequence + 2) & ~1u);
    }
}
