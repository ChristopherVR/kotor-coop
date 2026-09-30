using System.Text;
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed record NativePartyLoadProof(uint Generation,uint RequestLoadSerial,uint RequestNativeEpoch,
    long RequestedAtTick,LocalSaveReadProof Source);

public sealed partial class KotorGameBridge
{
    /// <summary>True includes an invalidated source: a later failed capture must retire
    /// earlier proof. Both this bank and the legacy native request must be coherent.</summary>
    public bool TryReadNativePartyLoadProof(out uint generation,out NativePartyLoadProof? proof)
    {
        generation=0;proof=null;byte[] bytes=new byte[224];
        if(_pluginBlock==0 || !_memory.TryReadUInt32(_pluginBlock+0x4E50,out uint request)
            || request==0 || (request&1)!=0 || !_memory.TryRead(_pluginBlock+0xC200,bytes))return false;
        uint observed=BitConverter.ToUInt32(bytes,0);
        if(observed!=request || !_memory.TryReadUInt32(_pluginBlock+0xC200,out uint after) || after!=observed
            || !_memory.TryReadUInt32(_pluginBlock+0x4E50,out uint finalRequest) || finalRequest!=request)return false;
        generation=observed;
        if(BitConverter.ToUInt32(bytes,12)!=1)return true;
        ReadOnlySpan<byte> text=bytes.AsSpan(120,96);int end=text.IndexOf((byte)0);
        if(end<0)return true;
        string directory=Encoding.ASCII.GetString(text[..end]);long length=BitConverter.ToInt64(bytes,16);
        if(!LocalSaveReadProofReader.ValidLeaf(directory) || length<=0 || length>1_073_741_824)return true;
        LocalSaveReadProof source=new(directory,Convert.ToHexString(bytes.AsSpan(24,32)),length,
            Convert.ToHexString(bytes.AsSpan(56,32)),Convert.ToHexString(bytes.AsSpan(88,32)));
        proof=new(observed,BitConverter.ToUInt32(bytes,4),BitConverter.ToUInt32(bytes,8),BitConverter.ToInt64(bytes,216),source);
        return true;
    }
}
