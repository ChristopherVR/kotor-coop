using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

public sealed partial class CoopCommandDispatcher
{
    private sealed record NativePartyReload(NativePartyLoadProof Native,string Directory,PartySaveProvenance.Snapshot Saved);
    private NativePartyReload? _nativePartyReload;
    private uint _nativePartyLoadSeen;
    private void MaintainNativePartyLoadProof()
    {
        if(_kotor is null || !_kotor.TryReadNativePartyLoadProof(out uint generation,out NativePartyLoadProof? native))return;
        if(generation!=_nativePartyLoadSeen)
        {
            _nativePartyLoadSeen=generation;_nativePartyReload=null;
            if(native is not null && _partyReload is null && !string.IsNullOrEmpty(gameDirectory)
                && NativePartyLoadProofRule.Recent(native.RequestedAtTick,Environment.TickCount64))
            {
                string directory=Path.Combine(gameDirectory,"saves",native.Source.DirectoryLeaf);
                if(LocalSaveReadProofReader.TryCapture(directory,out LocalSaveReadProof? current) && current==native.Source
                    && PartySaveProvenance.TryRead(directory,out PartySaveProvenance.Snapshot? saved)
                    && saved is not null && saved.SaveSha256==native.Source.SaveSha256 && saved.SaveLength==native.Source.SaveLength)
                    _nativePartyReload=new(native,directory,saved);
            }
        }
        if(_nativePartyReload is not { } pending)return;
        if(!NativePartyLoadProofRule.Recent(pending.Native.RequestedAtTick,Environment.TickCount64))
        {_nativePartyReload=null;return;}
        if(!bridge.TryReadWorldLoadSerial(out uint serial) || !bridge.TryReadLivenessEpoch(out uint epoch)
            || !NativePartyLoadProofRule.CanRestore(pending.Native.RequestedAtTick,Environment.TickCount64,
                pending.Native.RequestLoadSerial,pending.Native.RequestNativeEpoch,serial,epoch)
            || string.IsNullOrEmpty(pending.Saved.Module) || !bridge.TryReadLocalState(out PlayerState state)
            || !state.Module.Equals(pending.Saved.Module,StringComparison.OrdinalIgnoreCase)
            || !bridge.TryReadLocalPartyRoster(out PartyRoster local) || local.HostLoadSerial!=serial || local.HostNativeEpoch!=epoch)return;
        if(!LocalSaveReadProofReader.TryCapture(pending.Directory,out LocalSaveReadProof? currentSource) || currentSource!=pending.Native.Source)
        {_nativePartyReload=null;Log?.Invoke("party provenance: native-selected source changed; restoration refused");return;}
        if(!_kotor.TryReadNativePartyLoadProof(out uint finalGeneration,out NativePartyLoadProof? finalProof)
            || finalGeneration!=pending.Native.Generation || finalProof!=pending.Native)
        {_nativePartyReload=null;return;}
        if(_kotor.TryRestoreSavedPartyOccupants(pending.Saved.Occupants,local))
        {
            _nativePartyReload=null;
            Log?.Invoke("party provenance: exact native-selected pre-load bytes submitted for completed-load occupant validation");
        }
    }
}
