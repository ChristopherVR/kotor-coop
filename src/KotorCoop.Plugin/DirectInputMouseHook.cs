using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Wraps the game's DirectInput MOUSE device (GUID_SysMouse) the same way
/// <see cref="DirectInputKeyboardHook"/> already wraps the keyboard: a forwarding proxy around
/// the real <c>IDirectInputDevice8</c> whose <c>GetDeviceState</c> is detoured. Reuses that
/// class's own <see cref="DirectInputKeyboardHook.BuildProxyTable"/>/
/// <see cref="DirectInputKeyboardHook.NewProxy"/> rather than duplicating them — same technique
/// (DirectInput validates the vptr it is handed, so a forwarding proxy is required, not a
/// patched vptr; a plain vptr swap crashed the game live 2026-09-03 — see that file's own
/// remarks), same reasons.
///
/// <b>Why this exists (2026-09-25, docs/research/isolated-rig.md).</b> Live on the isolated rig
/// (two invisible, click-through KOTOR windows sitting on the user's own monitor while they play
/// something else): the host's in-game camera kept swinging between screenshots taken seconds
/// apart while nobody drove it. The engine polls THIS device once a frame, immediate mode,
/// <c>cbData 0x10</c> (<c>DIMOUSESTATE</c>) — <c>CExoInputInternal::UpdateMouseDelta
/// 0x005E0110</c> — and turns <c>lX</c>/<c>lY</c> straight into the camera look axes
/// (mouse-cursor-input.md's answer sheet). This is a COMPLETELY SEPARATE path from the cursor:
/// the cursor position is read only from window messages (<c>WM_MOUSEMOVE</c>/<c>WM_*BUTTON*</c>
/// in <c>Global::WinMessageHandler</c>), which a <c>WS_EX_TRANSPARENT</c> ghost window does not
/// receive for the point under the real cursor (Windows routes hit-testing straight through it
/// to whatever is underneath) — so ghosting alone protects the cursor/click path, but does
/// nothing for this one. Before this class the mouse device was never wrapped at all — only the
/// keyboard was — so every real relative mouse delta reached the camera unfiltered whenever the
/// device was acquired.
///
/// A backgrounded, un-focused window is not normally able to keep this device acquired at all —
/// <c>SetCooperativeLevel(hwnd, 0x0A)</c> (docs/re/frame-hook.md), and the keyboard's own
/// empirical behaviour under the SAME flag value is to fail every read with
/// <c>DIERR_NOTACQUIRED</c> while unfocused (<see cref="DirectInputKeyboardHook"/>'s own remarks,
/// live 2026-09-03). But <c>CClientExoApp::SetInputActive 0x005EDA10</c> — which
/// <c>Odyssey.EnsureInputActive()</c> calls DIRECTLY, in-process, every time the rig's own
/// synthetic input needs to get past <c>WinMessageHandler</c>'s activity gate — "writes the
/// flag, re-Acquires the keyboard and mouse devices" (Odyssey.Input.cs's own comment on that
/// thunk). That call is not gated by the window's real OS focus the way a genuine
/// <c>WM_ACTIVATE</c> would be, so it is a concrete, in-repo way the real mouse device's
/// <c>Acquire()</c> could succeed again even while the ghost window is never the foreground
/// window — a plausible, but NOT live-confirmed, root cause for the swinging camera (this pass
/// could not launch the game — see the doc's NEEDS-LIVE recipe). Whichever exact path let a real
/// poll through, the fix here does not depend on diagnosing it further: once wrapped, isolation
/// answers every <c>GetDeviceState</c> with an all-neutral state regardless of what the real
/// hardware or the real acquisition state actually is.
///
/// Off (coop.ini <c>isolate_real_input</c> = 0, the default): a byte-for-byte passthrough for
/// every real player — this proxy exists at all only because DirectInput must be handed a valid
/// object of ITS OWN choosing (see above), not a copy; nothing about wrapping changes behaviour
/// until <see cref="DirectInputKeyboardHook.IsolateRealInput"/> is turned on.
/// </summary>
internal static unsafe class DirectInputMouseHook
{
    private const int DeviceSlots = 32;        // IUnknown(3) + IDirectInputDevice8's 29
    private const int GetDeviceStateSlot = 9;  // +0x24

    // Proxy object layout, matching DirectInputKeyboardHook's (methodB unused here - the mouse
    // is polled immediate-mode only, see the class doc comment; no GetDeviceData detour needed).
    private const int ProxyReal = 4;
    private const int ProxyMethodA = 8;

    private static nint _deviceTable;
    private static nint _mouseProxy;

    public static bool IsWrapped => _mouseProxy != 0;
    public static long RealReads { get; private set; }
    public static string? LastDiagnostic { get; private set; }

    /// <summary>GUID_SysMouse {6F1D2B60-D5A0-11CF-BFC7-444553540000} — one byte off
    /// GUID_SysKeyboard's Data1 (…61 for the keyboard, …60 for the mouse); see
    /// <see cref="DirectInputKeyboardHook"/>'s own IsSystemKeyboard for the field layout this
    /// mirrors.</summary>
    public static bool IsSystemMouse(nint guid)
    {
        if (guid == 0 || !NativeMethods.IsReadable(guid, 16))
        {
            return false;
        }

        uint* p = (uint*)guid;
        return p[0] == 0x6F1D2B60 && p[1] == 0x11CFD5A0 && p[2] == 0x4544C7BF && p[3] == 0x00005453;
    }

    /// <summary>
    /// Replaces the freshly-created mouse device at <paramref name="device"/> (the caller's
    /// <c>void**</c>) with a proxy for it. Called from
    /// <see cref="DirectInputKeyboardHook.CreateDeviceDetour"/> once <see cref="IsSystemMouse"/>
    /// has matched the GUID — MAIN THREAD (that detour runs there, same as the keyboard wrap).
    /// </summary>
    public static void Wrap(nint* device)
    {
        if (!NativeMethods.IsWritable((nint)device, (nuint)sizeof(nint))
            || !NativeMethods.IsReadable(*device, (nuint)sizeof(nint)))
        {
            LastDiagnostic = "mouse device pointer not accessible; not wrapped";
            return;
        }

        nint real = *device;
        nint realTable = *(nint*)real;
        if (!NativeMethods.IsReadable(realTable, (nuint)(DeviceSlots * sizeof(nint))))
        {
            LastDiagnostic = "mouse device vtable not readable; not wrapped";
            return;
        }

        if (_deviceTable == 0)
        {
            _deviceTable = DirectInputKeyboardHook.BuildProxyTable(DeviceSlots,
                (GetDeviceStateSlot, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int>)&GetDeviceStateDetour));
            if (_deviceTable == 0)
            {
                LastDiagnostic = "VirtualAlloc for the mouse proxy table failed";
                return;
            }
        }

        nint proxy = DirectInputKeyboardHook.NewProxy(_deviceTable, real, ((nint*)realTable)[GetDeviceStateSlot], 0);
        if (proxy == 0)
        {
            LastDiagnostic = "VirtualAlloc for the mouse proxy failed";
            return;
        }

        *device = proxy;
        _mouseProxy = proxy;
        LastDiagnostic = $"mouse device proxied (real 0x{real:X}, vtable 0x{realTable:X})";
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetDeviceStateDetour(nint self, uint size, nint data)
    {
        nint real = *(nint*)(self + ProxyReal);
        var getDeviceState = (delegate* unmanaged[Stdcall]<nint, uint, nint, int>)*(nint*)(self + ProxyMethodA);
        int hr = getDeviceState(real, size, data);
        RealReads++;

        if (hr >= 0 && data != 0
            && InputIsolationRule.ShouldNeutralizeMouseState(DirectInputKeyboardHook.IsolateRealInput, size))
        {
            // DIMOUSESTATE: LONG lX, lY, lZ; BYTE rgbButtons[4] - zero every field. This device
            // is polled for nothing else (see the class doc comment), so an all-neutral state
            // means exactly "the real mouse did not move and no button is down".
            byte* p = (byte*)data;
            for (uint i = 0; i < size; i++)
            {
                p[i] = 0;
            }
        }

        return hr;
    }
}
