param(
    [string]$OutPath,
    [string]$ProcessName = 'swkotor',
    [int]$ProcId = 0,
    [switch]$FullScreen
)

Add-Type -AssemblyName System.Windows.Forms, System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class WinApi {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
'@

$proc = if ($ProcId -ne 0) { Get-Process -Id $ProcId -ErrorAction SilentlyContinue }
        else { Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $proc) { Write-Error "target process not running"; exit 1 }

$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { Write-Error 'no main window handle'; exit 1 }

# Foreground restrictions: attach to the current foreground thread's input queue first.
$cur = [WinApi]::GetCurrentThreadId()
$fg = [WinApi]::GetWindowThreadProcessId([WinApi]::GetForegroundWindow(), [ref]$null)
$attached = $false
if ($fg -ne 0 -and $fg -ne $cur) { $attached = [WinApi]::AttachThreadInput($cur, $fg, $true) }
[WinApi]::ShowWindow($hwnd, 9) | Out-Null
[WinApi]::BringWindowToTop($hwnd) | Out-Null
[WinApi]::SetForegroundWindow($hwnd) | Out-Null
if ($attached) { [WinApi]::AttachThreadInput($cur, $fg, $false) | Out-Null }

Start-Sleep -Milliseconds 700

if ($FullScreen) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $x = $b.X; $y = $b.Y; $w = $b.Width; $h = $b.Height
} else {
    $cr = New-Object WinApi+RECT
    [WinApi]::GetClientRect($hwnd, [ref]$cr) | Out-Null
    $tl = New-Object WinApi+POINT
    $tl.X = 0; $tl.Y = 0
    [WinApi]::ClientToScreen($hwnd, [ref]$tl) | Out-Null
    $x = $tl.X; $y = $tl.Y; $w = $cr.Right - $cr.Left; $h = $cr.Bottom - $cr.Top
}

if ($w -le 0 -or $h -le 0) { Write-Error "bad capture size ${w}x${h}"; exit 1 }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

"captured ${w}x${h} at ($x,$y) -> $OutPath"
