# Scrolls the mouse wheel over a window. KOTOR maps the wheel to camera zoom, which is
# the difference between seeing the floor at your feet and staring at your own back.
param(
    [Parameter(Mandatory = $true)][int]$Clicks,
    [string]$ProcessName = 'swkotor',
    [int]$ProcId = 0,
    [int]$X = 400,
    [int]$Y = 300
)

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Wheel {
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {
        public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; public int pad1, pad2; }
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
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    public static void Focus(IntPtr hwnd) {
        uint cur = GetCurrentThreadId(); uint pid;
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        bool att = (fg != 0 && fg != cur) && AttachThreadInput(cur, fg, true);
        ShowWindow(hwnd, 9); BringWindowToTop(hwnd); SetForegroundWindow(hwnd);
        if (att) AttachThreadInput(cur, fg, false);
    }

    // One wheel notch is WHEEL_DELTA (120); negative scrolls back (zoom out in KOTOR).
    public static void Scroll(int notches) {
        INPUT[] i = new INPUT[1];
        i[0].type = 0;
        i[0].mi.dwFlags = 0x0800; // MOUSEEVENTF_WHEEL
        i[0].mi.mouseData = unchecked((uint)(notches * 120));
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }
}
'@

$proc = if ($ProcId -ne 0) { Get-Process -Id $ProcId -ErrorAction SilentlyContinue }
        else { Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $proc) { Write-Error 'target process not running'; exit 1 }

[Wheel]::Focus($proc.MainWindowHandle)
Start-Sleep -Milliseconds 400

$pt = New-Object Wheel+POINT
$pt.X = $X; $pt.Y = $Y
[Wheel]::ClientToScreen($proc.MainWindowHandle, [ref]$pt) | Out-Null
[Wheel]::SetCursorPos($pt.X, $pt.Y) | Out-Null
Start-Sleep -Milliseconds 200

$step = if ($Clicks -lt 0) { -1 } else { 1 }
for ($i = 0; $i -ne $Clicks; $i += $step) {
    [Wheel]::Scroll($step)
    Start-Sleep -Milliseconds 120
}

"scrolled $Clicks notch(es) at client ($X,$Y)"
