namespace KotorCoop.Plugin;

/// <summary>
/// Observes SetMoveToModulePending even when a script raises and the main loop drains
/// it between SwapBuffers callbacks. Polling alone misses those transitions and reports
/// a false Load Game edge. No managed callback or engine mutation beyond the retail store.
/// </summary>
internal static unsafe class ModuleMoveRequestHook
{
    private const nint Entry = 0x004AECC0;
    private static nint _counter;
    public static uint Serial => _counter == 0 ? 0 : *(uint*)_counter;

    public static bool Install(out string diagnostic)
    {
        if (_counter != 0)
        {
            diagnostic = "already installed";
            return true;
        }

        // GOG 1.0.3, disassembled 2026-09-22: mov eax,[ecx+4];
        // mov ecx,[esp+4]; mov [eax+10080h],ecx; ret 4.
        ReadOnlySpan<byte> expected =
            [0x8B, 0x41, 0x04, 0x8B, 0x4C, 0x24, 0x04,
             0x89, 0x88, 0x80, 0x00, 0x01, 0x00, 0xC2, 0x04, 0x00];
        byte* site = (byte*)Entry;
        for (int i = 0; i < expected.Length; i++)
        {
            if (site[i] != expected[i])
            {
                diagnostic = $"setter byte mismatch at +{i}; observer not installed";
                return false;
            }
        }

        nint stub = NativeMethods.VirtualAlloc(0, 32, 0x3000, 0x40);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        expected[..7].CopyTo(new Span<byte>(s, 7)); // whole, position-independent instructions
        s[7] = 0x9C;                              // pushfd
        s[8] = 0x85; s[9] = 0xC9;                // test ecx,ecx
        s[10] = 0x74; s[11] = 6;                 // jz popfd (clearing pending is not a request)
        s[12] = 0xFF; s[13] = 0x05;              // inc dword ptr [counter]
        *(int*)(s + 14) = (int)(stub + 28);
        s[18] = 0x9D;                             // popfd
        s[19] = 0xE9;                             // resume retail store and ret 4
        *(int*)(s + 20) = (int)((Entry + 7) - (stub + 24));
        *(uint*)(s + 28) = 0;
        if (!NativeMethods.VirtualProtect(Entry, 7, 0x40, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;
        *(int*)(site + 1) = (int)(stub - (Entry + 5));
        site[5] = 0x90; site[6] = 0x90;
        NativeMethods.VirtualProtect(Entry, 7, old, out _);
        _counter = stub + 28;
        diagnostic = $"module move request observer installed (stub 0x{stub:X})";
        return true;
    }
}
