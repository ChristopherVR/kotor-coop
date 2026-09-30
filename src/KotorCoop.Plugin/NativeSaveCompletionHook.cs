using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace KotorCoop.Plugin;

/// <summary>GOG stall-save completion, with real byte-count, fflush and fclose outcomes.
/// The stall and archive helper return values are deliberately not treated as success.</summary>
internal static unsafe class NativeSaveCompletionHook
{
    internal static nint Block = 0;
    private static bool _installed;
    private static readonly NativeSaveIoWatch Io = new();
    private static readonly byte[] Rows = new byte[9 * 24];
    private static string _directory = "";
    private static readonly byte[] ObjectIds=new byte[36];
    private static nint _table;
    private static int _control;
    private static uint _epoch, _serial, _packed;
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private sealed record Site(nint Address, byte[] Original, byte[] Arguments, nint Callback, int RelativeCall = -1);
    public static bool Install(out string diagnostic)
    {
        if (_installed) { diagnostic = "already installed"; return true; }
        Site[] sites =
        [
            new(0x4B327C, [0x89,0x74,0x24,0x24,0xE8,0x0B,0xFF,0x0F,0], [0x57,0x56], (nint)(delegate* unmanaged[Cdecl]<nint,nint,void>)&Begin, 4),
            new(0x4B3898, [0xA1,0xE0,0x39,0x7A,0], [], (nint)(delegate* unmanaged[Cdecl]<void>)&Complete),
            new(0x5EBDF3, [0x8B,0xC6,0x5E,0x5D,0x5B], [0x53,0x56,0x57], (nint)(delegate* unmanaged[Cdecl]<nint,uint,uint,void>)&Write),
            new(0x5EBE01, [0x5F,0x83,0xC4,0x0C,0xC2,0x0C,0], [], (nint)(delegate* unmanaged[Cdecl]<void>)&Fail),
            // fflush's EAX survives the CRT SEH epilogue. EBP+8 is its FILE* argument.
            new(0x6FD79E, [0xE8,0xA4,0x18,0,0], [0x50,0xFF,0x75,0x08], (nint)(delegate* unmanaged[Cdecl]<nint,int,void>)&Flush, 0),
            // fclose's sole shared result exit: EBP-1C is its retained result.
            new(0x6FAD0D, [0x8B,0x45,0xE4,0xE8,0x32,0x43,0,0], [0xFF,0x75,0xE4,0xFF,0x75,0x08], (nint)(delegate* unmanaged[Cdecl]<nint,int,void>)&Close, 3)
        ];
        foreach (Site site in sites)
            if (!NativeMethods.IsReadable(site.Address, (nuint)site.Original.Length)
                || !new ReadOnlySpan<byte>((void*)site.Address,site.Original.Length).SequenceEqual(site.Original))
            { diagnostic = $"save completion bytes refused at {site.Address:X}"; return false; }
        foreach (Site site in sites)
        {
            nint stub = NativeMethods.VirtualAlloc(0,128,0x3000,0x40);
            if (stub == 0) { diagnostic = "save completion allocation refused"; return false; }
            int written=Emit(new Span<byte>((void*)stub,128),stub,site.Address,site.Callback,site.Original,site.Arguments,site.RelativeCall);
            if(!NativeMethods.FlushInstructionCache(-1,stub,(nuint)written))
            { diagnostic="save completion stub cache flush refused"; return false; }
            if (!NativeMethods.VirtualProtect(site.Address,(nuint)site.Original.Length,0x40,out uint old))
            { diagnostic = "save completion protection refused"; return false; }
            byte* p = (byte*)site.Address; p[0] = 0xE9; *(int*)(p+1) = (int)(stub-site.Address-5);
            for(int i=5;i<site.Original.Length;i++) p[i]=0x90;
            NativeMethods.VirtualProtect(site.Address,(nuint)site.Original.Length,old,out _);
            if(!NativeMethods.FlushInstructionCache(-1,site.Address,(nuint)site.Original.Length))
            { diagnostic="save completion patch cache flush refused"; return false; }
        }
        _installed = true; diagnostic = "native save completion and required-file I/O capture installed"; return true;
    }
    internal static int Emit(Span<byte> bytes,nint stub,nint site,nint callback,ReadOnlySpan<byte> original,ReadOnlySpan<byte> arguments,int relativeCall)
    {
        int n=0; bytes[n++]=0x9C; bytes[n++]=0x60;
        arguments.CopyTo(bytes[n..]); n+=arguments.Length;
        bytes[n++]=0xE8; BitConverter.TryWriteBytes(bytes.Slice(n,4),(int)(callback-stub-n-4)); n+=4;
        int argc=0; for(int i=0;i<arguments.Length;i++) { argc++; if(arguments[i]==0xFF)i+=2; }
        if(argc>0) { bytes[n++]=0x83; bytes[n++]=0xC4; bytes[n++]=(byte)(argc*4); }
        bytes[n++]=0x61; bytes[n++]=0x9D;
        int copied=n; original.CopyTo(bytes[n..]); n+=original.Length;
        if(relativeCall>=0)
        {
            long destination=(long)site+relativeCall+5+BitConverter.ToInt32(original.Slice(relativeCall+1,4));
            BitConverter.TryWriteBytes(bytes.Slice(copied+relativeCall+1,4),(int)(destination-(long)stub-copied-relativeCall-5));
        }
        bytes[n++]=0xE9; BitConverter.TryWriteBytes(bytes.Slice(n,4),(int)(site+original.Length-stub-n-4)); return n+4;
    }
    private static string ReadString(nint exo,int maximum)
    {
        if(!NativeMethods.IsReadable(exo,8)) return "";
        nint text=*(nint*)exo; uint length=*(uint*)(exo+4);
        if(length==0 || length>maximum || !NativeMethods.IsReadable(text,length)) return "";
        ReadOnlySpan<byte> bytes=new((void*)text,(int)length);
        if(bytes[^1]==0)bytes=bytes[..^1];
        foreach(byte b in bytes)if(b is <32 or >126)return "";
        return Encoding.ASCII.GetString(bytes);
    }
    private static bool ReadPacked(nint table,out uint packed,out int control)
    {
        packed=0;control=0;
        if(!NativeMethods.IsReadable(table,0xF4))return false;
        uint available=0,active=0;int count=*(int*)table;
        if(count is <0 or >2)return false;
        for(int i=0;i<count;i++){uint slot=*(uint*)(table+4+i*4);if(slot>=9)return false;active|=1u<<(int)slot;}
        for(int slot=0;slot<9;slot++)if(*(int*)(table+0x30+slot*4)!=0)available|=1u<<slot;
        control=*(int*)(table+0xF0);
        packed=SharedBlock.PartyRosterValid|(available<<SharedBlock.PartyRosterAvailableShift)|(active<<SharedBlock.PartyRosterPartyShift);
        return true;
    }
    internal static bool ContextCurrent(uint epoch,uint serial,uint packed,int control,
        uint currentEpoch,uint currentSerial,uint currentPacked,int currentControl) =>
        epoch!=0 && epoch==currentEpoch && serial==currentSerial && packed==currentPacked
        && control==-1 && currentControl==-1;
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static void Begin(nint stall,nint server)
    {
        try
        {
            // Re-check on the actual serialization pass. SaveGame only arms a
            // stall event; controllers may have resumed since its request or
            // save-flush reissue. The later 4B36C5 party walk cannot tolerate
            // a member which GetCharacter no longer resolves.
            if (_installed && Odyssey.TryBoundClientPartyCountByResolvable(
                out int before, out int resolved, out string entries))
                PluginHost.Log($"party count: bounded {before} -> {resolved} at save serialization "
                    + $"(entries {entries}; StallEventSaveGame walks the array without a null check)");
            if(!_installed || Block==0 || !PartyOccupantHook.Installed)return;
            string path=ReadString(stall+0x18,128).Replace('\\','/');
            int colon=path.LastIndexOf(':'); if(colon>=0)path=path[(colon+1)..];
            _directory=path.Trim('/');
            Io.Begin(GetCurrentThreadId(),_directory);
            _epoch=*(uint*)(Block+SharedBlock.OffsetLivenessNativeEpoch); _serial=*(uint*)(Block+SharedBlock.OffsetWorldLoadSerial);
            nint table=server+0x1B770; _table=table;
            if(_epoch==0 || !NativeMethods.IsReadable(table,0xF4)) { Io.Fail(GetCurrentThreadId()); return; }
            if(!ReadPacked(table,out _packed,out _control) || _control!=-1){Io.Fail(GetCurrentThreadId());return;}
            Rows.AsSpan().Clear();
            new ReadOnlySpan<byte>((void*)(table+0x0C),36).CopyTo(ObjectIds);
            for(int slot=0;slot<9;slot++)
            {
                BitConverter.TryWriteBytes(Rows.AsSpan(slot*24,4),PartyOccupantHook.Captured.Generation(slot));
                BitConverter.TryWriteBytes(Rows.AsSpan(slot*24+4,4),PartyOccupantHook.Captured.Replaced(slot)?1u:0u);
                PartyOccupantHook.Captured.CopyTemplate(slot,Rows.AsSpan(slot*24+8,16));
            }
        } catch { Io.Fail(GetCurrentThreadId()); }
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static void Write(nint file,uint actual,uint expected)
    { try { if(_installed && Io.Active && NativeMethods.IsReadable(file,12))Io.Write(GetCurrentThreadId(),*(nint*)file,ReadString(file+4,512),actual,expected); } catch {Io.Fail(GetCurrentThreadId());} }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])] private static void Fail(){try{if(_installed)Io.Fail(GetCurrentThreadId());}catch{}}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])] private static void Flush(nint stream,int result){try{if(_installed)Io.Flush(GetCurrentThreadId(),stream,result);}catch{}}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])] private static void Close(nint stream,int result){try{if(_installed)Io.Close(GetCurrentThreadId(),stream,result);}catch{}}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static void Complete()
    {
        try
        {
            if(!_installed || !Io.Complete(GetCurrentThreadId()) || Block==0
                || !ReadPacked(_table,out uint packed,out int control)
                || !ContextCurrent(_epoch,_serial,_packed,_control,*(uint*)(Block+SharedBlock.OffsetLivenessNativeEpoch),*(uint*)(Block+SharedBlock.OffsetWorldLoadSerial),packed,control)
                || !NativeMethods.IsReadable(_table+0x0C,36) || !new ReadOnlySpan<byte>((void*)(_table+0x0C),36).SequenceEqual(ObjectIds))return;
            Span<byte> template=stackalloc byte[16];
            for(int slot=0;slot<9;slot++)
            {
                PartyOccupantHook.Captured.CopyTemplate(slot,template);
                if(PartyOccupantHook.Captured.Generation(slot)!=BitConverter.ToUInt32(Rows,slot*24)
                    || !template.SequenceEqual(Rows.AsSpan(slot*24+8,16)))return;
            }
            byte* bank=(byte*)Block+SharedBlock.OffsetNativeSaveCompletion;
            uint seq=*(uint*)bank; *(uint*)bank=(seq+1)|1u;
            *(uint*)(bank+4)=_epoch; *(uint*)(bank+8)=_serial; *(uint*)(bank+12)=_packed;
            new Span<byte>(bank+16,96).Clear(); Encoding.ASCII.GetBytes(_directory,new Span<byte>(bank+16,96));
            Rows.CopyTo(new Span<byte>(bank+112,Rows.Length)); ObjectIds.CopyTo(new Span<byte>(bank+328,36)); *(uint*)bank=(seq+2)&~1u;
        } catch { }
    }
}
