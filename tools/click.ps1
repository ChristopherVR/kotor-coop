param(
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [string]$ProcessName = 'swkotor',
    [int]$ProcId = 0,
    [int]$SettleMs = 250,
    # Move the cursor and stop, without pressing. KOTOR only draws an object's name while
    # the cursor rests on it, so hovering is how you find out what is under a pixel.
    [switch]$HoverOnly
)

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Mouse {
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {
        public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    public static void Focus(IntPtr hwnd) {
        uint cur = GetCurrentThreadId();
        uint pid;
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        bool att = (fg != 0 && fg != cur) && AttachThreadInput(cur, fg, true);
        // Same Alt-tap fix as sendkey.ps1's Focus(): AttachThreadInput alone was confirmed
        // live (2026-08-23) NOT to satisfy Windows' foreground-lock guard by itself; the
        // synthetic Alt keydown/keyup right before SetForegroundWindow does.
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        ShowWindow(hwnd, 9); BringWindowToTop(hwnd); SetForegroundWindow(hwnd);
        if (att) AttachThreadInput(cur, fg, false);
    }

    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);

    // Move through SendInput, not SetCursorPos. KOTOR reads the mouse through DirectInput,
    // which observes the injected-input stream; SetCursorPos writes the cursor position
    // without generating that input, so the game never notices the pointer moved and no
    // hover state (and therefore no object name) is ever produced.
    public static void Move(int x, int y) {
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        INPUT[] i = new INPUT[1];
        i[0].type = 0;
        i[0].u.mi.dx = (x * 65535) / (w - 1);
        i[0].u.mi.dy = (y * 65535) / (h - 1);
        i[0].u.mi.dwFlags = 0x0001 | 0x8000; // MOVE | ABSOLUTE
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }

    // Move first so the game's own hover logic runs, then press.
    public static void Click(int x, int y) {
        Move(x, y);
        System.Threading.Thread.Sleep(150);
        Button(0x0002); // LEFTDOWN
        System.Threading.Thread.Sleep(60);
        Button(0x0004); // LEFTUP
    }
    static void Button(uint flag) {
        INPUT[] i = new INPUT[1];
        i[0].type = 0;
        i[0].u.mi.dwFlags = flag;
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }
}
'@

$proc = if ($ProcId -ne 0) { Get-Process -Id $ProcId -ErrorAction SilentlyContinue }
        else { Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $proc) { Write-Error "target process not running"; exit 1 }

[Mouse]::Focus($proc.MainWindowHandle)
Start-Sleep -Milliseconds 400

# X/Y are client-relative (i.e. screenshot pixels). Translate to screen coordinates,
# because in windowed mode the client area is offset by the border and title bar.
$pt = New-Object Mouse+POINT
$pt.X = $X; $pt.Y = $Y
[Mouse]::ClientToScreen($proc.MainWindowHandle, [ref]$pt) | Out-Null

if ($HoverOnly) {
    [Mouse]::Move($pt.X, $pt.Y)
    Start-Sleep -Milliseconds $SettleMs
    "hovered client ($X,$Y) -> screen ($($pt.X),$($pt.Y))"
}
else {
    [Mouse]::Click($pt.X, $pt.Y)
    Start-Sleep -Milliseconds $SettleMs
    "clicked client ($X,$Y) -> screen ($($pt.X),$($pt.Y))"
}
