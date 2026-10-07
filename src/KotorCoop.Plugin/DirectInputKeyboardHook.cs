using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Lets the plugin type on the game's keyboard.
///
/// The engine reads its keyboard once per frame as BUFFERED DirectInput events
/// (<c>CExoRawInputInternal::GetKeyboardBuffer 0x005E3AE0</c> →
/// <c>IDirectInputDevice8::GetDeviceData</c>, docs/re/frame-hook.md §"Per frame"). Since this
/// DLL is the <c>dinput8.dll</c> the game loads, every DirectInput object the game ever
/// holds passes through <see cref="Exports.DirectInput8Create"/> first — so this class hands
/// the game a PROXY for the <c>IDirectInput8</c> it asked for, catches the keyboard device
/// being created through that proxy, and hands back a proxy for the device too. Each proxy
/// is a tiny object of its own (vptr + the real interface pointer + the real methods it
/// detours) whose vtable forwards every slot to the real object through an x86 thunk — except
/// <c>CreateDevice</c> on the one and <c>GetDeviceData</c>/<c>GetDeviceState</c> on the
/// other, which call the real method and then APPEND the plugin's own key events (DIK
/// scancode, 0x80 = down) to the buffer the game passed in, within the capacity it asked for.
///
/// Why a proxy and not a patched vptr: DirectInput validates the objects it is handed by
/// reading the header it keeps in front of its own vtables (<c>Common_hresPv</c>); an object
/// whose vptr points at a plain copy fails that read with an access violation inside
/// dinput8.dll (live 2026-09-03: the first CreateDevice after the swap crashed the game). The
/// Steam overlay proxies the same three interfaces this way in this process
/// (docs/re/frame-hook.md §"Wrapping the returned interface provably works here"), and the real
/// objects are never modified at all.
///
/// All of it runs on the game's main thread: the game creates its devices and reads them
/// there, and the frame hook that queues events runs there too — so the queue is a plain
/// ring with no locking.
///
/// <see cref="CreateDeviceDetour"/> also recognises the system MOUSE device (GUID_SysMouse) and
/// hands it to <see cref="DirectInputMouseHook"/> — a second, sibling proxy built with this
/// class's own <see cref="BuildProxyTable"/>/<see cref="NewProxy"/> (kept <c>internal</c> so it
/// can be reused rather than duplicated). See <see cref="IsolateRealInput"/> and
/// docs/research/isolated-rig.md's "Input isolation" section for why the mouse needed wrapping
/// too: the engine polls it once a frame for the camera look axes
/// (<c>CExoInputInternal::UpdateMouseDelta</c>), a path that has nothing to do with the cursor
/// or window messages and so was not covered by the keyboard wrap above.
/// </summary>
internal static unsafe class DirectInputKeyboardHook
{
    private const int DirectInput8Slots = 11;      // IUnknown(3) + IDirectInput8's 8
    private const int DeviceSlots = 32;            // IUnknown(3) + IDirectInputDevice8's 29
    private const int CreateDeviceSlot = 3;        // +0x0C
    private const int GetDeviceStateSlot = 9;      // +0x24
    private const int GetDeviceDataSlot = 10;      // +0x28
    private const uint ObjectDataSize = 0x14;      // sizeof(DIDEVICEOBJECTDATA) on x86
    private const uint PeekFlag = 0x1;             // DIGDD_PEEK
    private const uint KeyboardStateSize = 256;    // c_dfDIKeyboard: one byte per DIK
    private const int QueueCapacity = 64;
    private const int ThunkStride = 32;

    private const uint MemCommitReserve = NativeMethods.MemCommit | NativeMethods.MemReserve;
    private const uint PageExecuteReadWrite = 0x40;

    // Proxy object layout (all 32-bit slots):
    //   +0  vptr            → the proxy vtable for its interface
    //   +4  real interface  → what every forwarded call is re-targeted at
    //   +8  real method A   → CreateDevice (IDirectInput8) / GetDeviceState (device)
    //   +12 real method B   → GetDeviceData (device)
    private const int ProxyReal = 4;
    private const int ProxyMethodA = 8;
    private const int ProxyMethodB = 12;
    private const int ProxySize = 16;

    private static nint _directInputTable;   // shared by every IDirectInput8 proxy
    private static nint _deviceTable;        // shared by every keyboard-device proxy
    private static nint _directInputProxy;
    private static nint _keyboardProxy;

    // Pending events, oldest first. Written by Enqueue (frame hook), drained by the patched
    // GetDeviceData (the engine's per-frame read) — both on the main thread.
    private static readonly (byte Dik, bool Down)[] Pending = new (byte, bool)[QueueCapacity];
    private static int _queueHead;
    private static int _queueCount;
    private static uint _sequence = 0x40000000; // well clear of the real device's own numbers

    // For GetDeviceState: the keys the plugin currently holds, OR'd into the 256-byte state
    // if the engine ever reads it that way (it reads the keyboard buffered, but consistency
    // costs nothing).
    private static readonly byte[] Held = new byte[256];

    /// <summary>Isolated rig only: whether the <c>input key</c> verb currently holds
    /// <paramref name="dik"/> down. Always false for a real player.</summary>
    internal static bool IsHeldByRig(byte dik) => IsolateRealInput && dik != 0 && Held[dik] != 0;

    public static bool IsDirectInputWrapped => _directInputProxy != 0;
    public static bool IsKeyboardWrapped => _keyboardProxy != 0;
    public static long InjectedEvents { get; private set; }
    public static long DroppedEvents { get; private set; }
    public static long RealReads { get; private set; }
    /// <summary>What the engine's most recent buffered read looked like — the shape the
    /// append condition below judges, so `gamepad status` can say WHY nothing was injected.</summary>
    public static int LastReadHr { get; private set; }

    /// <summary>
    /// Rig only (the virtual pad): answer a failed read — the engine's keyboard is
    /// foreground-only and the rig's windows never are — with the queued events alone. Off
    /// for a real pad, whose events are only ever queued while the window IS foreground.
    /// </summary>
    public static bool DeliverWhileUnacquired { get; set; }

    /// <summary>
    /// coop.ini <c>isolate_real_input</c> (default 0, off for every real player): when true,
    /// every REAL keyboard press is dropped before the engine ever sees it —
    /// <see cref="GetDeviceStateDetour"/> answers with only the plugin's own held (synthetic)
    /// keys, <see cref="GetDeviceDataDetour"/> answers with only the plugin's own queued
    /// (synthetic) events. <see cref="DirectInputMouseHook"/> reads this SAME flag for the
    /// mouse device. Set once, from <c>PluginConfig.ReadBool("isolate_real_input", false)</c> —
    /// only <c>tools/demo.ps1</c>/<c>demo3.ps1 -Isolated</c> ever turn it on, and only for the
    /// run. See docs/research/isolated-rig.md's "Input isolation" section.
    /// </summary>
    public static bool IsolateRealInput { get; set; }

    private static readonly GameplayKeyboardLock GameplayLock = new();
    internal static bool GameplayLocked =>
        (MinigameLockController.IsLocked || SoloSegmentSpectateController.IsSpectating)
        && !Odyssey.IsConversationActive && !Odyssey.IsFullScreenGuiUp && !Odyssey.IsConsoleOpen;

    internal static void DiscardGameplayQueuedInput()
    {
        _queueCount = GameplayKeyboardLock.PurgeQueued(Pending, _queueHead, _queueCount);
        for (int i = 0; i < Held.Length; i++)
            if (!GameplayKeyboardLock.Allows((byte)i)) Held[i] = 0;
        DirectInputMouseHook.ClearQueuedDeltas();
    }

    public static (uint ObjectSize, uint Flags, uint Capacity, bool HasBuffer) LastReadShape { get; private set; }

    public static string? LastDiagnostic { get; private set; }

    /// <summary>
    /// Replaces the <c>IDirectInput8</c> the real <c>DirectInput8Create</c> just returned in
    /// <paramref name="output"/> (the caller's <c>void**</c>) with a proxy for it. Safe to
    /// call for every creation (the game makes one). MAIN THREAD (the export runs there).
    /// </summary>
    public static void WrapDirectInput(nint output)
    {
        try
        {
            if (!NativeMethods.IsWritable(output, (nuint)sizeof(nint)))
            {
                return;
            }

            nint real = *(nint*)output;
            if (real == 0 || !NativeMethods.IsReadable(real) || !NativeMethods.IsReadable(*(nint*)real, (nuint)(DirectInput8Slots * sizeof(nint))))
            {
                LastDiagnostic = "IDirectInput8 pointer not readable; not wrapped";
                return;
            }

            if (_directInputTable == 0)
            {
                _directInputTable = BuildProxyTable(DirectInput8Slots,
                    (CreateDeviceSlot, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, nint, int>)&CreateDeviceDetour));
                if (_directInputTable == 0)
                {
                    LastDiagnostic = "VirtualAlloc for the IDirectInput8 proxy table failed";
                    return;
                }
            }

            nint realTable = *(nint*)real;
            nint proxy = NewProxy(_directInputTable, real, ((nint*)realTable)[CreateDeviceSlot], 0);
            if (proxy == 0)
            {
                LastDiagnostic = "VirtualAlloc for the IDirectInput8 proxy failed";
                return;
            }

            *(nint*)output = proxy;
            _directInputProxy = proxy;
            LastDiagnostic = $"IDirectInput8 proxied (real 0x{real:X}, vtable 0x{realTable:X})";
        }
        catch (Exception ex)
        {
            LastDiagnostic = $"IDirectInput8 wrap failed: {ex.Message}";
        }
    }

    /// <summary>Queues one key transition for the engine's next keyboard read.</summary>
    public static bool Enqueue(byte dik, bool down)
    {
        if (down && GameplayLocked && !GameplayKeyboardLock.Allows(dik))
        {
            Held[dik] = 0;
            return true;
        }
        if (_queueCount >= QueueCapacity)
        {
            DroppedEvents++;
            return false;
        }

        Pending[(_queueHead + _queueCount) % QueueCapacity] = (dik, down);
        _queueCount++;
        Held[dik] = down ? (byte)0x80 : (byte)0;
        return true;
    }

    public static int PendingEvents => _queueCount;

    /// <summary>Clears the held state — for a disconnect, a focus loss or the pad being
    /// turned off, when the CALLER has already emitted releases (or no longer cares).</summary>
    public static void ClearHeld()
    {
        Array.Clear(Held);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateDeviceDetour(nint self, nint guid, nint* device, nint outer)
    {
        nint real = *(nint*)(self + ProxyReal);
        var createDevice = (delegate* unmanaged[Stdcall]<nint, nint, nint*, nint, int>)*(nint*)(self + ProxyMethodA);
        int hr = createDevice(real, guid, device, outer);
        try
        {
            if (hr >= 0 && device != null && *device != 0)
            {
                if (IsSystemKeyboard(guid))
                {
                    WrapKeyboard(device);
                }
                else if (DirectInputMouseHook.IsSystemMouse(guid))
                {
                    DirectInputMouseHook.Wrap(device);
                }
            }
        }
        catch (Exception ex)
        {
            LastDiagnostic = $"input device wrap failed: {ex.Message}";
        }

        return hr;
    }

    // GUID_SysKeyboard {6F1D2B61-D5A0-11CF-BFC7-444553540000}
    private static bool IsSystemKeyboard(nint guid)
    {
        if (guid == 0 || !NativeMethods.IsReadable(guid, 16))
        {
            return false;
        }

        uint* p = (uint*)guid;
        return p[0] == 0x6F1D2B61 && p[1] == 0x11CFD5A0 && p[2] == 0x4544C7BF && p[3] == 0x00005453;
    }

    private static void WrapKeyboard(nint* device)
    {
        if (!NativeMethods.IsWritable((nint)device, (nuint)sizeof(nint))
            || !NativeMethods.IsReadable(*device, (nuint)sizeof(nint)))
        {
            LastDiagnostic = "keyboard device pointer not accessible; not wrapped";
            return;
        }

        nint real = *device;
        nint realTable = *(nint*)real;
        if (!NativeMethods.IsReadable(realTable, (nuint)(DeviceSlots * sizeof(nint))))
        {
            LastDiagnostic = "keyboard device vtable not readable; not wrapped";
            return;
        }

        if (_deviceTable == 0)
        {
            _deviceTable = BuildProxyTable(DeviceSlots,
                (GetDeviceStateSlot, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int>)&GetDeviceStateDetour),
                (GetDeviceDataSlot, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, uint*, uint, int>)&GetDeviceDataDetour));
            if (_deviceTable == 0)
            {
                LastDiagnostic = "VirtualAlloc for the keyboard proxy table failed";
                return;
            }
        }

        nint proxy = NewProxy(_deviceTable, real, ((nint*)realTable)[GetDeviceStateSlot], ((nint*)realTable)[GetDeviceDataSlot]);
        if (proxy == 0)
        {
            LastDiagnostic = "VirtualAlloc for the keyboard proxy failed";
            return;
        }

        *device = proxy;
        _keyboardProxy = proxy;
        LastDiagnostic = $"keyboard device proxied (real 0x{real:X}, vtable 0x{realTable:X})";
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetDeviceStateDetour(nint self, uint size, nint data)
    {
        nint real = *(nint*)(self + ProxyReal);
        var getDeviceState = (delegate* unmanaged[Stdcall]<nint, uint, nint, int>)*(nint*)(self + ProxyMethodA);
        int hr = getDeviceState(real, size, data);
        if (hr >= 0 && size == KeyboardStateSize && data != 0)
        {
            byte* state = (byte*)data;
            bool locked = GameplayLocked;
            for (int i = 0; i < 256; i++)
            {
                if (locked && !GameplayKeyboardLock.Allows((byte)i)) Held[i] = 0;
                state[i] = locked && !GameplayKeyboardLock.Allows((byte)i) ? (byte)0
                    : InputIsolationRule.MaskKeyboardStateByte(IsolateRealInput, state[i], Held[i]);
            }
        }

        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetDeviceDataDetour(nint self, uint objectSize, nint data, uint* inOut, uint flags)
    {
        nint real = *(nint*)(self + ProxyReal);
        var getDeviceData = (delegate* unmanaged[Stdcall]<nint, uint, nint, uint*, uint, int>)*(nint*)(self + ProxyMethodB);

        // The caller's capacity is only knowable BEFORE the call: DirectInput writes the
        // returned count back into the same DWORD.
        uint capacity = inOut == null ? 0 : *inOut;
        int hr = getDeviceData(real, objectSize, data, inOut, flags);
        RealReads++;
        LastReadHr = hr;
        LastReadShape = (objectSize, flags, capacity, data != 0);
        bool locked = GameplayLocked;
        if (locked) DiscardGameplayQueuedInput();

        // Append only to a real, consuming read into a real buffer: a null buffer is a
        // "how many are waiting" / flush query, PEEK would hand our events out twice, and a
        // failed read (DIERR_NOTACQUIRED / INPUTLOST while the window is inactive) is about
        // to be followed by a re-acquire — the events stay queued for the next good read.
        // The one exception is the rig's virtual pad (DeliverWhileUnacquired): its window is
        // never the foreground one, so the engine's foreground-only keyboard NEVER acquires
        // (live 2026-09-03: hr 0x8007000C on every read), and the read is answered with our
        // events alone as if it had.
        if (data == 0 || inOut == null || objectSize != ObjectDataSize || (flags & PeekFlag) != 0)
        {
            return hr;
        }
        // Do not introduce managed exceptions at this unmanaged entry point for a
        // malformed count. Retail reads are small; an unsupported buffer passes through.
        if (capacity > 4096 || !NativeMethods.IsWritable(data, (nuint)capacity * ObjectDataSize)) return hr;

        uint count;
        if (hr < 0)
        {
            if (!locked && (!DeliverWhileUnacquired || _queueCount == 0))
            {
                return hr;
            }

            count = 0;
            hr = 0;
        }
        else
        {
            // Isolating: the real device may well have real key events waiting (a rig window is
            // never foreground, but a brief re-acquire during the automated load step, or a
            // stray background-eligible read, could still hand some back) - hide them by
            // reporting zero of them, exactly as the synthetic-append loop below expects, so it
            // overwrites buffer[0..) with the plugin's own queued events instead.
            count = Math.Min(capacity, InputIsolationRule.VisibleRealEventCount(IsolateRealInput, *inOut));
        }
        byte* buffer = (byte*)data;
        uint timestamp = (uint)Environment.TickCount;
        while (_queueCount > 0 && count < capacity)
        {
            (byte dik, bool down) = Pending[_queueHead];
            _queueHead = (_queueHead + 1) % QueueCapacity;
            _queueCount--;

            uint* entry = (uint*)(buffer + count * ObjectDataSize);
            entry[0] = dik;                       // dwOfs: the DIK scancode
            entry[1] = down ? 0x80u : 0u;         // dwData: bit 7 = key down
            entry[2] = timestamp;                 // dwTimeStamp
            entry[3] = _sequence++;               // dwSequence
            entry[4] = 0;                         // uAppData
            count++;
            InjectedEvents++;
        }

        count = (uint)GameplayLock.Apply(new Span<uint>((void*)data, (int)capacity * 5),
            (int)count, locked, timestamp);
        *inOut = count;
        return hr;
    }

    /// <summary>Internal (not private): <see cref="DirectInputMouseHook"/> reuses this rather
    /// than duplicating it — same technique, same reasons, see the class doc comment.</summary>
    internal static nint NewProxy(nint table, nint real, nint methodA, nint methodB)
    {
        nint proxy = NativeMethods.VirtualAlloc(0, ProxySize, MemCommitReserve, NativeMethods.PageReadWrite);
        if (proxy == 0)
        {
            return 0;
        }

        *(nint*)proxy = table;
        *(nint*)(proxy + ProxyReal) = real;
        *(nint*)(proxy + ProxyMethodA) = methodA;
        *(nint*)(proxy + ProxyMethodB) = methodB;
        return proxy;
    }

    /// <summary>
    /// One executable block holding the proxy vtable followed by one forwarding thunk per
    /// slot. A forwarding thunk re-targets the call at the real object and tail-jumps into
    /// the real object's own vtable, so every argument and the stdcall clean-up stay exactly
    /// as the caller made them:
    /// <code>
    ///   mov eax, [esp+4]        ; this = the proxy
    ///   mov eax, [eax+4]        ; the real interface it stands for
    ///   mov [esp+4], eax        ; becomes the callee's this
    ///   mov eax, [eax]          ; its vtable
    ///   jmp dword ptr [eax+N*4] ; the real method for this slot
    /// </code>
    /// The slots named in <paramref name="detours"/> point at the plugin's own functions
    /// instead, which do the same re-targeting themselves.
    /// </summary>
    /// <summary>Internal (not private): <see cref="DirectInputMouseHook"/> reuses this rather
    /// than duplicating it — same technique, same reasons, see the class doc comment.</summary>
    internal static nint BuildProxyTable(int slots, params (int Slot, nint Detour)[] detours)
    {
        int tableBytes = slots * sizeof(nint);
        nint block = NativeMethods.VirtualAlloc(0, (nuint)(tableBytes + slots * ThunkStride), MemCommitReserve, PageExecuteReadWrite);
        if (block == 0)
        {
            return 0;
        }

        nint* table = (nint*)block;
        for (int slot = 0; slot < slots; slot++)
        {
            byte* code = (byte*)(block + tableBytes + slot * ThunkStride);
            int i = 0;
            code[i++] = 0x8B; code[i++] = 0x44; code[i++] = 0x24; code[i++] = 0x04;   // mov eax,[esp+4]
            code[i++] = 0x8B; code[i++] = 0x40; code[i++] = (byte)ProxyReal;           // mov eax,[eax+4]
            code[i++] = 0x89; code[i++] = 0x44; code[i++] = 0x24; code[i++] = 0x04;   // mov [esp+4],eax
            code[i++] = 0x8B; code[i++] = 0x00;                                        // mov eax,[eax]
            code[i++] = 0xFF; code[i++] = 0xA0;                                        // jmp [eax+disp32]
            *(int*)(code + i) = slot * sizeof(nint);
            i += 4;
            code[i] = 0xCC;
            table[slot] = (nint)code;
        }

        foreach ((int slot, nint detour) in detours)
        {
            table[slot] = detour;
        }

        return block;
    }
}
