using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>Completed native load of exact transferred companion bytes. Runtime HP and
/// equipment changes never recompute this identity; a new world or generation revokes it.</summary>
public sealed class PartyAdoptionProof
{
    private readonly PartyRoster _host;
    private readonly uint _localSerial, _localEpoch;
    private readonly uint[] _localGenerations = new uint[9];
    private readonly bool[] _proven = new bool[9];

    public PartyAdoptionProof(PartyRoster host, IReadOnlyList<string> transferred,
        uint localSerial, uint localEpoch, uint adoptedSaveIdentity, IReadOnlyList<uint>? localGenerations = null)
    {
        _host = host with { Occupants = Enumerable.Range(0, 9).Select(host.OccupantFor).ToArray() };
        _localSerial = localSerial; _localEpoch = localEpoch;
        for (int slot = 0; slot < 9; slot++)
        {
            PartyOccupantIdentity occupant = _host.OccupantFor(slot);
            _localGenerations[slot] = localGenerations is not null && slot < localGenerations.Count ? localGenerations[slot] : 0;
            _proven[slot] = host.HostSessionId != 0 && host.HostNativeEpoch != 0 && localEpoch != 0
                && host.RefreshRevision != 0 && host.RefreshSaveIdentity != 0 && adoptedSaveIdentity == host.RefreshSaveIdentity
                && occupant.Generation != 0 && ModSupportProfile.IsResref(occupant.Template)
                && PartyCacheFingerprint.IsValid(occupant.CacheFingerprint) && slot < transferred.Count
                && occupant.CacheFingerprint.Equals(transferred[slot], StringComparison.Ordinal);
        }
    }

    public uint LocalGeneration(int slot) => (uint)slot < 9 ? _localGenerations[slot] : uint.MaxValue;

    public bool Allows(int slot, PartyRoster host, uint localSerial, uint localEpoch, uint localGeneration = 0) =>
        (uint)slot < 9 && _proven[slot] && _localGenerations[slot] == localGeneration && _localSerial == localSerial && _localEpoch == localEpoch
        && host.HostSessionId == _host.HostSessionId && host.HostLoadSerial == _host.HostLoadSerial
        && host.RefreshSaveIdentity == _host.RefreshSaveIdentity && host.RefreshRevision == _host.RefreshRevision
        && host.HostNativeEpoch == _host.HostNativeEpoch && host.OccupantFor(slot) == _host.OccupantFor(slot);
}
