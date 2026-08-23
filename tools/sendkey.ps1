param(
    [Parameter(Mandatory = $true)][string[]]$Keys,
    [string]$ProcessName = 'swkotor',
    [int]$ProcId = 0,
    [int]$HoldMs = 60,
    [int]$GapMs = 400
)

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Keys {
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {
        public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // Scan codes, not virtual keys: KOTOR reads the keyboard through DirectInput.
    public static void Tap(ushort scan, int holdMs) {
        Send(scan, false);
        System.Threading.Thread.Sleep(holdMs);
        Send(scan, true);
    }
    static void Send(ushort scan, bool up) {
        INPUT[] i = new INPUT[1];
        i[0].type = 1;
        i[0].u.ki.wVk = 0;
        // High-byte 0xE0 marks an extended key (the arrow cluster, as opposed to the
        // numpad): without KEYEVENTF_EXTENDEDKEY an injected 0x48 is NUMPAD-8, which
        // types '8' instead of registering VK_UP.
        bool ext = (scan & 0xFF00) == 0xE000;
        i[0].u.ki.wScan = (ushort)(scan & 0xFF);
        i[0].u.ki.dwFlags = (uint)(0x0008 | (ext ? 0x0001 : 0) | (up ? 0x0002 : 0));
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }
    public static void Focus(IntPtr hwnd) {
        uint cur = GetCurrentThreadId();
        uint pid;
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        bool att = (fg != 0 && fg != cur) && AttachThreadInput(cur, fg, true);
        // The Alt tap is load-bearing, not decoration: Windows' foreground-lock guard
        // refuses SetForegroundWindow from a background process unless it looks like the
        // CALLING process just received input itself. A synthetic Alt keydown/keyup right
        // before the call satisfies that check even when AttachThreadInput alone does not
        // (confirmed live 2026-08-23 — AttachThreadInput succeeded but the foreground window
        // stayed on the other app until this tap was added).
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        ShowWindow(hwnd, 9); BringWindowToTop(hwnd); SetForegroundWindow(hwnd);
        if (att) AttachThreadInput(cur, fg, false);
    }
}
'@

$scanCodes = @{
    'esc' = 0x01; '1' = 0x02; '2' = 0x03; '3' = 0x04; '4' = 0x05; '5' = 0x06
    'q' = 0x10; 'w' = 0x11; 'e' = 0x12; 'r' = 0x13; 't' = 0x14; 'y' = 0x15
    'u' = 0x16; 'i' = 0x17; 'o' = 0x18; 'p' = 0x19
    'a' = 0x1E; 's' = 0x1F; 'd' = 0x20; 'f' = 0x21; 'g' = 0x22; 'h' = 0x23
    'j' = 0x24; 'k' = 0x25; 'l' = 0x26
    'z' = 0x2C; 'x' = 0x2D; 'c' = 0x2E; 'v' = 0x2F; 'b' = 0x30; 'n' = 0x31; 'm' = 0x32
    'tab' = 0x0F; 'enter' = 0x1C; 'space' = 0x39
    # Extended (0xE0-prefixed) arrow-cluster keys; the plain 0x48/0x50 are the NUMPAD.
    'up' = 0xE048; 'down' = 0xE050; 'left' = 0xE04B; 'right' = 0xE04D
    'grave' = 0x29; 'backspace' = 0x0E
}

$proc = if ($ProcId -ne 0) { Get-Process -Id $ProcId -ErrorAction SilentlyContinue }
        else { Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $proc) { Write-Error "target process not running"; exit 1 }

[Keys]::Focus($proc.MainWindowHandle)
Start-Sleep -Milliseconds 500

foreach ($k in $Keys) {
    $key = $k.ToLower()
    if (-not $scanCodes.ContainsKey($key)) { Write-Error "unknown key '$k'"; exit 1 }
    [Keys]::Tap([uint16]$scanCodes[$key], $HoldMs)
    "sent '$key' (scan 0x{0:X2})" -f $scanCodes[$key]
    Start-Sleep -Milliseconds $GapMs
}
