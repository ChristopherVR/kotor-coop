using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// A DIAGNOSTIC, default-OFF (<c>coop.ini gui_probe=1</c>) read-only splice on
/// <c>CGuiInGame::ShowSWInGameGui 0x0062C9B0</c> and <c>HideSWInGameGui 0x0062CBA0</c>, the
/// only two runtime writers of <c>CGuiInGame+0x30</c> (a capstone sweep of every function in
/// tools/kotor1_0_3.db, 2026-09-03: the constructor and <c>CSWGuiPartySelection::OnDone</c>
/// are the other two, neither reachable from play). Records WHO called each one — the return
/// address, <c>this</c>, the panel-index argument and the field's value at entry — into a
/// preallocated ring, drained into the plugin log from the frame hook.
///
/// Built for the galaxy-map relay's one open question (docs/research/
/// galaxy-map-and-party-select.md, 2026-09-03): on the host, <c>ShowGalaxyMapGui</c> leaves
/// <c>+0x30</c> at 1 synchronously, though it contains no write to the field and none of its
/// callees do. ANSWERED the same day, and not by this probe: it recorded nothing, which is
/// what sent the search to <see cref="FieldWriteWatch"/> — the writer is
/// <c>CClientExoAppInternal::SetInputClass</c> 0x006200E0 (see
/// <c>Odyssey.Store.cs</c>, <c>ClientInternalInputClassOffset</c>). Kept as the cheap first
/// probe for the next "who opened / closed the menu screen" question.
///
/// Same discipline as <see cref="DelayEventProbeHook"/>: verify bytes before patching, do
/// nothing inside the stub but record, re-execute the relocated prologue verbatim.
/// </summary>
internal static unsafe class ShowInGameGuiProbeHook
{
    private const int Capacity = 64;

    private static readonly byte[] Kind = new byte[Capacity];
    private static readonly uint[] Caller = new uint[Capacity];
    private static readonly uint[] This = new uint[Capacity];
    private static readonly uint[] Arg = new uint[Capacity];
    private static readonly uint[] Busy = new uint[Capacity];
    private static readonly uint[] Serials = new uint[Capacity];

    private static int _write;
    private static uint _sequence;
    private static uint _drained;
    private static bool _installed;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x1000 | 0x2000;

    /// <summary>ShowSWInGameGui opens with the SEH frame setup <c>mov eax, fs:[0]</c> — six
    /// position-independent bytes, re-executed verbatim in the stub.</summary>
    private static ReadOnlySpan<byte> ShowPrologue => [0x64, 0xA1, 0x00, 0x00, 0x00, 0x00];

    /// <summary>HideSWInGameGui: <c>push esi</c> / <c>mov esi,ecx</c> / <c>mov eax,[esi+0x108]</c>
    /// — nine bytes, because the five a jmp needs would split the third instruction.</summary>
    private static ReadOnlySpan<byte> HidePrologue => [0x56, 0x8B, 0xF1, 0x8B, 0x86, 0x08, 0x01, 0x00, 0x00];

    private const nint ShowEntry = 0x0062C9B0;
    private const nint HideEntry = 0x0062CBA0;

    public static bool IsInstalled => _installed;

    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        nint showCb = (nint)(delegate* unmanaged[Cdecl]<uint, uint, uint, void>)&OnShow;
        nint hideCb = (nint)(delegate* unmanaged[Cdecl]<uint, uint, uint, void>)&OnHide;
        if (!Splice(ShowEntry, ShowPrologue, showCb, out string a))
        {
            diagnostic = $"ShowSWInGameGui: {a}";
            return false;
        }

        if (!Splice(HideEntry, HidePrologue, hideCb, out string b))
        {
            diagnostic = $"HideSWInGameGui: {b}";
            return false;
        }

        _installed = true;
        diagnostic = "in-game GUI show/hide probe armed";
        return true;
    }

    /// <summary>Stub: <c>pushad</c>; push <c>[esp+0x24]</c> (the one stack argument (Show only; Hide has none, the slot is whatever the caller had there) — the
    /// return address sits at <c>[esp+0x20]</c> once pushad has run, the argument right above
    /// it); push <c>[esp+0x24]</c> again (the same displacement now reaches the return address,
    /// because the first push moved esp); push <c>ecx</c>; call; <c>add esp,12</c>;
    /// <c>popad</c>; relocated prologue; jmp back.</summary>
    private static bool Splice(nint site, ReadOnlySpan<byte> prologue, nint callback, out string diagnostic)
    {
        byte* target = (byte*)site;
        for (int i = 0; i < prologue.Length; i++)
        {
            if (target[i] != prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} (0x{target[i]:X2})";
                return false;
            }
        }

        nint stub = NativeMethods.VirtualAlloc(0, 64, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;
        s[n++] = 0x60;                                              // pushad
        s[n++] = 0xFF; s[n++] = 0x74; s[n++] = 0x24; s[n++] = 0x24; // push [esp+0x24]  (arg)
        s[n++] = 0xFF; s[n++] = 0x74; s[n++] = 0x24; s[n++] = 0x24; // push [esp+0x24]  (return address)
        s[n++] = 0x51;                                              // push ecx         (this)
        s[n++] = 0xE8;                                              // call callback(this, caller, arg)
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;
        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x0C;                // add esp, 12
        s[n++] = 0x61;                                              // popad
        for (int i = 0; i < prologue.Length; i++)
        {
            s[n++] = prologue[i];
        }

        s[n++] = 0xE9;                                              // jmp resume
        *(int*)(s + n) = (int)((site + prologue.Length) - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(site, (nuint)prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        target[0] = 0xE9;
        *(int*)(target + 1) = (int)(stub - (site + 5));
        for (int i = 5; i < prologue.Length; i++)
        {
            target[i] = 0x90;
        }

        NativeMethods.VirtualProtect(site, (nuint)prologue.Length, old, out _);

        diagnostic = $"stub 0x{stub:X}";
        return true;
    }

    private static void Record(byte kind, uint self, uint caller, uint arg)
    {
        uint busy = 0xFFFFFFFF;
        if (self is > 0x10000 and < 0x7FFF0000)
        {
            busy = *(uint*)(self + 0x30);
        }

        int slot = _write;
        Kind[slot] = kind;
        This[slot] = self;
        Caller[slot] = caller;
        Arg[slot] = arg;
        Busy[slot] = busy;
        Serials[slot] = ++_sequence;
        _write = (slot + 1) % Capacity;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnShow(uint self, uint caller, uint arg)
    {
        try
        {
            Record(1, self, caller, arg);
        }
        catch
        {
            // Never let a managed fault unwind into the engine.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnHide(uint self, uint caller, uint arg)
    {
        try
        {
            Record(2, self, caller, arg);
        }
        catch
        {
        }
    }

    /// <summary>Frame hook: turn what the stubs recorded into log lines, where file I/O is safe.</summary>
    public static void Drain()
    {
        if (!_installed)
        {
            return;
        }

        while (_drained < _sequence)
        {
            uint serial = _drained + 1;
            int slot = (int)((serial - 1) % Capacity);
            if (Serials[slot] != serial)
            {
                // Overwritten before we got to it (more than Capacity calls in one frame).
                _drained = serial;
                continue;
            }

            string what = Kind[slot] == 1 ? "ShowSWInGameGui" : "HideSWInGameGui";
            PluginHost.Log($"gui-probe: {what}(this=0x{This[slot]:X}, arg=0x{Arg[slot]:X}) from 0x{Caller[slot]:X}, "
                + $"+0x30 was {Busy[slot]}");
            _drained = serial;
        }
    }
}
