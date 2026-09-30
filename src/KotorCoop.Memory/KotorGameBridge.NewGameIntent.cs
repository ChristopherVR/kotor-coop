namespace KotorCoop.Memory;

public sealed record NewGameIntent(uint Generation,uint WorldLoadSerialAtRequest,uint NativeEpochAtRequest,long RequestedAtTick);

public sealed partial class KotorGameBridge
{
    public bool TryReadNewGameIntent(out NewGameIntent? intent)
    {
        intent=null;
        if(_pluginBlock==0 || !_memory.TryReadUInt32(_pluginBlock+0xC400,out uint generation)
            || (generation&1)!=0)return false;
        if(generation==0)return true;
        byte[] bytes=new byte[24];
        if(!_memory.TryRead(_pluginBlock+0xC400,bytes)
            || BitConverter.ToUInt32(bytes,0)!=generation
            || !_memory.TryReadUInt32(_pluginBlock+0xC400,out uint after) || after!=generation)return false;
        intent=new(generation,BitConverter.ToUInt32(bytes,4),BitConverter.ToUInt32(bytes,8),BitConverter.ToInt64(bytes,16));
        return true;
    }
}
