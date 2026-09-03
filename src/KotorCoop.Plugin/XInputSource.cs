using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// The physical pad: XInput, loaded at runtime (xinput1_4 on Windows 8+, then the DirectX
/// redistributable's xinput1_3, then the in-box xinput9_1_0) so a machine without any of
/// them — or without a pad — costs nothing and breaks nothing. The first connected slot is
/// the player's pad; a disconnected slot is re-tried every couple of seconds rather than
/// every frame, because XInputGetState on an empty slot is one of the slower calls there is.
/// </summary>
internal static unsafe class XInputSource
{
    private const uint ErrorDeviceNotConnected = 1167;
    private const int Slots = 4;
    private const long RetryMs = 2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    private static delegate* unmanaged[Stdcall]<uint, XInputState*, uint> _getState;
    private static bool _loadAttempted;
    private static readonly long[] NextProbeMs = new long[Slots];

    /// <summary>Which library resolved XInputGetState, for the status line; null when none.</summary>
    public static string? Library { get; private set; }

    /// <summary>The slot (0..3) currently supplying the pad, or -1.</summary>
    public static int ActiveSlot { get; private set; } = -1;

    public static bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return _getState is not null;
        }
    }

    private static void EnsureLoaded()
    {
        if (_loadAttempted)
        {
            return;
        }

        _loadAttempted = true;
        foreach (string name in new[] { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" })
        {
            nint module = NativeMethods.LoadLibrary(name);
            if (module == 0)
            {
                continue;
            }

            nint fn = NativeMethods.GetProcAddress(module, "XInputGetState");
            if (fn == 0)
            {
                continue;
            }

            _getState = (delegate* unmanaged[Stdcall]<uint, XInputState*, uint>)fn;
            Library = name;
            return;
        }
    }

    /// <summary>
    /// Reads the active pad. False when no pad is connected (the active slot is dropped the
    /// moment its read fails, and every slot is probed again on its own retry timer).
    /// </summary>
    public static bool TryRead(long nowMs, out PadState state)
    {
        state = PadState.Idle;
        EnsureLoaded();
        if (_getState is null)
        {
            return false;
        }

        if (ActiveSlot >= 0 && Read(ActiveSlot, out state))
        {
            return true;
        }

        ActiveSlot = -1;
        for (int slot = 0; slot < Slots; slot++)
        {
            if (nowMs < NextProbeMs[slot])
            {
                continue;
            }

            if (Read(slot, out state))
            {
                ActiveSlot = slot;
                return true;
            }

            NextProbeMs[slot] = nowMs + RetryMs;
        }

        return false;
    }

    private static bool Read(int slot, out PadState state)
    {
        XInputState raw;
        uint result = _getState((uint)slot, &raw);
        if (result != 0)
        {
            // ERROR_DEVICE_NOT_CONNECTED is the expected answer for an empty slot; anything
            // else is treated the same way — no pad this frame — rather than trusted.
            state = PadState.Idle;
            return false;
        }

        state = new PadState(
            (PadButtons)raw.Buttons,
            Normalise(raw.ThumbLX),
            Normalise(raw.ThumbLY),
            Normalise(raw.ThumbRX),
            Normalise(raw.ThumbRY),
            raw.LeftTrigger / 255f,
            raw.RightTrigger / 255f);
        return true;
    }

    private static float Normalise(short axis) => Math.Clamp(axis / 32767f, -1f, 1f);
}
