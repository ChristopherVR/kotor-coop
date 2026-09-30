using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>Verified K1 AddNPC/RemoveAvailableNPC splices. Callbacks only record metadata.</summary>
internal static unsafe class PartyOccupantHook
{
    public static readonly PartyOccupantTable Captured = new();
    private static bool _installed;
    public static bool Install(out string diagnostic)
    {
        if (_installed) { diagnostic = "already installed"; return true; }
        // AddNPC_1 accepted the slot and body; direct-object recruitment has unknown UTC.
        bool unknown = Patch(0x564350, [0x57,0x56], [0xC7,0x44,0xAB,0x30,1,0,0,0], 0,
            (nint)(delegate* unmanaged[Cdecl]<int,void>)&OnUnknown);
        // AddNPC_2 has returned from AddNPC_1. EAX=success, EDI=slot, [ESP+30]=CExoString*.
        bool known = Patch(0x5646A6, [0x8B,0xCB,0xE8,0x5A,0xFC,0xFF,0xFF], [0x85,0xF6,0x8B,0xF8,0x74,0x08], 1,
            (nint)(delegate* unmanaged[Cdecl]<int,nint,uint,void>)&OnTemplate);
        // This branch is reached only after slot bounds and availability have passed.
        bool removed = Patch(0x5646EA, [0x33,0xC0,0xC2,0x04,0x00], [0xC7,0x44,0x81,0x30,0,0,0,0], 2,
            (nint)(delegate* unmanaged[Cdecl]<int,void>)&OnRemoved);
        _installed = unknown && known && removed;
        diagnostic = $"recruit capture unknown={unknown} template={known} remove={removed}";
        return _installed;
    }
    internal static int Emit(Span<byte> bytes, nint stub, nint callback, nint site, ReadOnlySpan<byte> original, int kind)
    {
        int n = 0;
        bytes[n++] = 0x9C; bytes[n++] = 0x60; // pushfd; pushad (36 bytes)
        if (kind == 1)
        {
            bytes[n++] = 0x8B; bytes[n++] = 0x54; bytes[n++] = 0x24; bytes[n++] = 0x54; // original ESP+30
            bytes[n++] = 0x50; bytes[n++] = 0x52; bytes[n++] = 0x57; // success,string,slot
        }
        else bytes[n++] = kind == 0 ? (byte)0x55 : (byte)0x50; // EBP slot / EAX slot
        bytes[n++] = 0xE8;
        BitConverter.TryWriteBytes(bytes.Slice(n,4), (int)(callback - (stub + n + 4))); n += 4;
        bytes[n++] = 0x83; bytes[n++] = 0xC4; bytes[n++] = kind == 1 ? (byte)12 : (byte)4;
        bytes[n++] = 0x61; bytes[n++] = 0x9D; // popad; popfd
        if (kind == 1)
        {
            original[..4].CopyTo(bytes[n..]); n += 4;
            bytes[n++] = 0x0F; bytes[n++] = 0x84; // relocate original conditional branch
            BitConverter.TryWriteBytes(bytes.Slice(n,4), (int)(0x5646B4 - (stub + n + 4))); n += 4;
        }
        else { original.CopyTo(bytes[n..]); n += original.Length; }
        bytes[n++] = 0xE9;
        BitConverter.TryWriteBytes(bytes.Slice(n,4), (int)(site + original.Length - (stub + n + 4))); n += 4;
        return n;
    }
    private static bool Patch(nint siteAddress, ReadOnlySpan<byte> anchor, ReadOnlySpan<byte> original, int kind, nint callback)
    {
        if (!NativeMethods.IsReadable(siteAddress - anchor.Length, (nuint)(anchor.Length + original.Length))) return false;
        byte* site = (byte*)siteAddress;
        if (!new ReadOnlySpan<byte>(site - anchor.Length, anchor.Length).SequenceEqual(anchor)
            || !new ReadOnlySpan<byte>(site, original.Length).SequenceEqual(original)) return false;
        nint stub = NativeMethods.VirtualAlloc(0, 96, 0x3000, 0x40);
        if (stub == 0) return false;
        Emit(new Span<byte>((void*)stub, 96), stub, callback, siteAddress, original, kind);
        if (!NativeMethods.VirtualProtect(siteAddress, (nuint)original.Length, 0x40, out uint old)) return false;
        site[0] = 0xE9; *(int*)(site + 1) = (int)(stub - (siteAddress + 5));
        for (int i = 5; i < original.Length; i++) site[i] = 0x90;
        NativeMethods.VirtualProtect(siteAddress, (nuint)original.Length, old, out _);
        return true;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUnknown(int slot)
    {
        try { Captured.ObserveAdmission(slot); } catch { }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRemoved(int slot)
    {
        try { Captured.ObserveRemoval(slot); } catch { }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnTemplate(int slot, nint exoString, uint success)
    {
        try
        {
            if (success == 0 || !NativeMethods.IsReadable(exoString, 8)) return;
            byte* text = *(byte**)exoString;
            uint length = *(uint*)(exoString + 4);
            // Some CExoStrings include their terminal NUL in the length; trim exactly one.
            if (length is 0 or > 17 || !NativeMethods.IsReadable((nint)text, length)) return;
            if (text[length - 1] == 0) length--;
            Captured.ObserveTemplate(slot, new ReadOnlySpan<byte>(text, (int)length), true);
        }
        catch { } // No fault may unwind into the engine.
    }
}
