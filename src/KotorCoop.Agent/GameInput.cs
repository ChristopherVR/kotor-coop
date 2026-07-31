using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KotorCoop.Agent;

/// <summary>
/// Drives the game's keyboard input so discovery can run unattended.
///
/// Input is injected with SendInput using scan codes rather than virtual key codes.
/// KOTOR reads the keyboard through DirectInput (dinput8.dll is in its module list), and
/// DirectInput reads device state rather than the window message queue, so PostMessage
/// and friends are ignored. SendInput enters at the raw input layer, which DirectInput
/// does observe.
/// </summary>
internal static partial class GameInput
{
    // Scan codes (set 1). DirectInput keys off these, not off virtual key codes.
    public const ushort ScanW = 0x11;
    public const ushort ScanA = 0x1E;
    public const ushort ScanS = 0x1F;
    public const ushort ScanD = 0x20;
    public const ushort ScanEscape = 0x01;
    public const ushort ScanEnter = 0x1C;

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyEventScanCode = 0x0008;
    private const uint KeyEventKeyUp = 0x0002;

    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventAbsolute = 0x8000;

    private const int SwRestore = 9;

    private const int SmScreenX = 0;
    private const int SmScreenY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    // The union must be laid out against the largest member so sizeof(Input) matches
    // what SendInput expects (40 bytes on x64); a short struct is rejected outright.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageA", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const nint MkLeftButton = 0x0001;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attachTo, uint attachFrom, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref Point point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>The game window's client area in pixels, or (0,0) if it has no window.</summary>
    public static (int Width, int Height) GetClientSize(Process game)
    {
        game.Refresh();
        nint window = game.MainWindowHandle;

        if (window == 0 || !GetClientRect(window, out Rect rect))
        {
            return (0, 0);
        }

        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static void SendMouse(uint flags, int x = 0, int y = 0)
    {
        Input[] inputs =
        [
            new Input
            {
                Type = InputMouse,
                Union = new InputUnion
                {
                    Mouse = new MouseInput { X = x, Y = y, Data = 0, Flags = flags, Time = 0, ExtraInfo = 0 },
                },
            },
        ];

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    /// <summary>
    /// Moves the pointer through SendInput rather than SetCursorPos.
    ///
    /// This is not interchangeable. KOTOR reads the mouse through DirectInput, which
    /// observes the injected-input stream; SetCursorPos relocates the cursor without
    /// producing that input, so the game never registers that the pointer moved.
    /// </summary>
    public static void MoveMouseToScreen(int screenX, int screenY)
    {
        int width = GetSystemMetrics(SmScreenX);
        int height = GetSystemMetrics(SmScreenY);

        if (width <= 1 || height <= 1)
        {
            return;
        }

        SendMouse(
            MouseEventMove | MouseEventAbsolute,
            (screenX * 65535) / (width - 1),
            (screenY * 65535) / (height - 1));
    }

    /// <summary>
    /// Clicks at a point given in the game window's client coordinates.
    ///
    /// CAVEAT, and it is a real one: KOTOR's menus do not read the OS pointer. The game
    /// accumulates DirectInput relative deltas into a cursor of its own, clamped to the
    /// client area, so where the OS pointer sits and where the game thinks its cursor is
    /// can disagree. In practice this works when the window is at its launch position and
    /// nothing has moved the pointer far since, which covers driving a freshly started
    /// instance, and it is unreliable for a window that has been relocated. Pinning the
    /// cursor to a corner with a large relative move and stepping out by the wanted offset
    /// was tried and does move the game's cursor, but not one unit per pixel - the game
    /// applies a sensitivity multiplier that would have to be calibrated first. See
    /// Handoff.md 7.2.
    /// </summary>
    public static bool ClickClient(Process game, int clientX, int clientY)
    {
        game.Refresh();
        nint window = game.MainWindowHandle;

        if (window == 0)
        {
            return false;
        }

        // Posted, not synthesised. KOTOR reads the cursor position straight out of lParam in
        // its own window proc — it does NOT accumulate DirectInput deltas into an internal
        // cursor (docs/research/mouse-cursor-input.md) — so a posted message with the point
        // in lParam lands exactly where we aim, in CLIENT coordinates, and the physical
        // pointer is irrelevant.
        //
        // The SendInput version this replaces failed in two ways at once: it aimed at a
        // virtual-desktop point rather than the client area, and it depended on the game
        // being the foreground window, which it frequently is not while an agent is driving
        // it. That combination is the intermittently-dead menu clicking recorded as trap 25.
        //
        // The move is posted first and separately: the window proc runs its hover hit-test on
        // each input event, so the control under OUR coordinate is current before the button
        // event arrives.
        nint point = (clientY << 16) | (clientX & 0xFFFF);

        if (!PostMessage(window, WmMouseMove, 0, point))
        {
            return false;
        }

        Thread.Sleep(60);
        PostMessage(window, WmLeftButtonDown, MkLeftButton, point);
        Thread.Sleep(60);
        PostMessage(window, WmLeftButtonUp, 0, point);
        return true;
    }

    private static void Send(ushort scanCode, bool keyUp)
    {
        Input[] inputs =
        [
            new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = 0,
                        ScanCode = scanCode,
                        Flags = KeyEventScanCode | (keyUp ? KeyEventKeyUp : 0),
                        Time = 0,
                        ExtraInfo = 0,
                    },
                },
            },
        ];

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    public static void KeyDown(ushort scanCode) => Send(scanCode, keyUp: false);

    public static void KeyUp(ushort scanCode) => Send(scanCode, keyUp: true);

    /// <summary>Presses and releases a key.</summary>
    public static void TapKey(ushort scanCode) => HoldKey(scanCode, 60);

    /// <summary>Holds a key for a duration, then releases it. Always releases.</summary>
    public static void HoldKey(ushort scanCode, int milliseconds)
    {
        KeyDown(scanCode);

        try
        {
            Thread.Sleep(milliseconds);
        }
        finally
        {
            KeyUp(scanCode);
        }
    }

    /// <summary>Releases every key this class knows how to press.</summary>
    public static void ReleaseAll()
    {
        foreach (ushort scanCode in new[] { ScanW, ScanA, ScanS, ScanD })
        {
            KeyUp(scanCode);
        }
    }

    /// <summary>
    /// Brings the game window to the foreground. Windows normally refuses
    /// SetForegroundWindow from a background process, so the caller's input queue is
    /// briefly attached to the foreground window's thread, which lifts the restriction.
    /// </summary>
    public static bool FocusGameWindow(Process game, out string diagnostic)
    {
        game.Refresh();
        nint window = game.MainWindowHandle;

        if (window == 0)
        {
            diagnostic = "the game has no main window handle yet";
            return false;
        }

        uint currentThread = GetCurrentThreadId();
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = foregroundThread != 0
            && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            ShowWindow(window, SwRestore);
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        Thread.Sleep(250);

        GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundPid);
        if (foregroundPid == (uint)game.Id)
        {
            diagnostic = "game window focused";
            return true;
        }

        diagnostic = $"could not focus the game window (foreground pid is {foregroundPid})";
        return false;
    }

    public static bool IsGameForeground(Process game)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundPid);
        return foregroundPid == (uint)game.Id;
    }
}
