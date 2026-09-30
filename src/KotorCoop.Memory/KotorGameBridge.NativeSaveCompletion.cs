using System.Text;
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    public bool TryReadNativeSaveCompletion(out uint sequence,out string directory,out PartyRoster party)
    {
        sequence=0; directory=""; party=default;
        byte[] bytes=new byte[364];
        if(_pluginBlock==0 || !_memory.TryRead(_pluginBlock+0xC000,bytes))return false;
        uint observed=BitConverter.ToUInt32(bytes,0), epoch=BitConverter.ToUInt32(bytes,4), serial=BitConverter.ToUInt32(bytes,8);
        if(observed==0 || (observed&1)!=0 || epoch==0)return false;
        ReadOnlySpan<byte> name=bytes.AsSpan(16,96); int end=name.IndexOf((byte)0);
        if(end<0)return false;
        directory=Encoding.ASCII.GetString(name[..end]);
        if(directory.Length<10 || directory.Contains('/') || directory.Contains('\\') || directory.Contains(':') || directory.Contains('.'))return false;
        PartyOccupantIdentity[] occupants=new PartyOccupantIdentity[9];
        for(int slot=0;slot<9;slot++)
        {
            int row=112+slot*24; ReadOnlySpan<byte> template=bytes.AsSpan(row+8,16); int length=template.IndexOf((byte)0); if(length<0)length=16;
            string resref=Encoding.ASCII.GetString(template[..length]);
            occupants[slot]=new(BitConverter.ToUInt32(bytes,row),resref,BitConverter.ToUInt32(bytes,row+4)!=0);
        }
        uint packed=BitConverter.ToUInt32(bytes,12);
        uint[] ids=Enumerable.Range(0,9).Select(slot=>BitConverter.ToUInt32(bytes,328+slot*4)).ToArray();
        party=new PartyRoster((ushort)(packed&0x1FF),(ushort)((packed>>16)&0x1FF),ObjectIds:ids,HostLoadSerial:serial,HostNativeEpoch:epoch,Occupants:occupants);
        if(!_memory.TryReadUInt32(_pluginBlock+0xC000,out uint final) || final!=observed)return false;
        sequence=observed; return true;
    }
}
