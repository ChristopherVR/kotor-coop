namespace KotorCoop.Plugin;

/// <summary>
/// The single-authority damage gate for party-placed mines (<see cref="PlacedMineFireRule"/>,
/// docs/research/mines.md §14.4): on a CLIENT, a mine placed by the party (<c>+0x314 != 0</c>)
/// must never run its trap script, because the creatures that can set it off are host-owned
/// enemies whose real copies live on the host, where the host's copy of the same mine fires.
///
/// One <b>call-site redirect</b>, the same shape as <see cref="MinimapPeerHook"/>: the only call
/// to <c>CSWSTrigger::GetCanFireMineOnObject 0x0058D4A0</c> in the image (VERIFIED with
/// <c>tools/xrefs.py 0x58d4a0</c>: one direct call, no address literal) is the
/// <c>call</c> at <c>0x0058D59E</c> inside <c>CSWSTrigger::OnEnterMine 0x0058D570</c>, reached
/// with <c>ecx</c> = the trigger (<c>mov ecx, esi</c> at 0x0058D59C) and two stack arguments
/// (creature id, flag; the callee ends <c>ret 8</c>). The caller treats eax == 0 as "do not
/// fire" and jumps straight to its epilogue (0x0058D5A5 <c>je 0x58d6f2</c>) — before the key
/// lookup, the trap script, and the event-11 self-destruction — so a refused mine simply stays.
///
/// The stub is pure machine code, no managed transition on the engine's event path:
/// <code>
///   cmp  dword [flag], 0          ; the controller's per-frame SuppressPartyMines answer
///   je   original
///   cmp  dword [ecx+0x314], 0     ; placed by the party? (SetCreator 0x0058D741)
///   je   original
///   inc  dword [counter]          ; diagnostic, read back by PlacedMineController
///   xor  eax, eax
///   ret  8                        ; pop the two arguments exactly as the callee would
/// original:
///   jmp  0x0058D4A0               ; return address and arguments untouched
/// </code>
/// No push, so esp is unchanged on both paths; <c>ecx</c> reaches the real callee intact.
/// Verify-bytes-or-refuse: the five bytes must be an E8 whose rel32 resolves to 0x0058D4A0.
/// </summary>
internal static unsafe class PlacedMineFireGateHook
{
    private const nint CallSite = 0x0058D59E;
    private const nint GetCanFireMineOnObject = 0x0058D4A0;
    private const int TriggerPlacedByParty = 0x314;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    private const int FlagOffset = 0x30;
    private const int CounterOffset = 0x34;

    private static nint _stub;

    public static bool Installed => _stub != 0;

    /// <summary>Written every frame by <see cref="PlacedMineController"/>.</summary>
    public static bool SuppressPartyMines
    {
        get => _stub != 0 && *(int*)(_stub + FlagOffset) != 0;
        set
        {
            if (_stub != 0)
            {
                *(int*)(_stub + FlagOffset) = value ? 1 : 0;
            }
        }
    }

    /// <summary>How many fire checks the stub refused since install.</summary>
    public static uint Refusals => _stub == 0 ? 0 : *(uint*)(_stub + CounterOffset);

    /// <summary>Verify-bytes-or-refuse. MAIN THREAD, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_stub != 0)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)CallSite;
        if (!NativeMethods.IsReadable(CallSite, 5) || site[0] != 0xE8)
        {
            diagnostic = $"expected a call at 0x{CallSite:X}; not gating party-placed mines";
            return false;
        }

        nint target = CallSite + 5 + *(int*)(site + 1);
        if (target != GetCanFireMineOnObject)
        {
            diagnostic = $"call at 0x{CallSite:X} goes to 0x{target:X}, not GetCanFireMineOnObject; not hooking";
            return false;
        }

        nint stub = NativeMethods.VirtualAlloc(0, 64, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;
        uint flag = (uint)(stub + FlagOffset);
        uint counter = (uint)(stub + CounterOffset);

        // cmp dword ptr [flag], 0
        s[n++] = 0x83; s[n++] = 0x3D;
        *(uint*)(s + n) = flag; n += 4;
        s[n++] = 0x00;

        // je original (patched below)
        s[n++] = 0x74;
        int jeFlag = n++;

        // cmp dword ptr [ecx+0x314], 0
        s[n++] = 0x83; s[n++] = 0xB9;
        *(int*)(s + n) = TriggerPlacedByParty; n += 4;
        s[n++] = 0x00;

        // je original
        s[n++] = 0x74;
        int jeParty = n++;

        // inc dword ptr [counter]
        s[n++] = 0xFF; s[n++] = 0x05;
        *(uint*)(s + n) = counter; n += 4;

        // xor eax, eax ; ret 8
        s[n++] = 0x33; s[n++] = 0xC0;
        s[n++] = 0xC2; s[n++] = 0x08; s[n++] = 0x00;

        int original = n;
        s[jeFlag] = (byte)(original - (jeFlag + 1));
        s[jeParty] = (byte)(original - (jeParty + 1));

        // jmp GetCanFireMineOnObject
        s[n++] = 0xE9;
        *(int*)(s + n) = (int)(GetCanFireMineOnObject - (stub + n + 4));
        n += 4;

        if (n > FlagOffset)
        {
            diagnostic = $"stub overran its data ({n} bytes)";
            return false;
        }

        *(int*)(stub + FlagOffset) = 0;
        *(uint*)(stub + CounterOffset) = 0;

        if (!NativeMethods.VirtualProtect(CallSite, 5, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        *(int*)(site + 1) = (int)(stub - (CallSite + 5));
        NativeMethods.VirtualProtect(CallSite, 5, old, out _);

        _stub = stub;
        diagnostic = $"party-placed mine fire gate at 0x{CallSite:X} (stub 0x{stub:X}, {n} bytes)";
        return true;
    }
}
