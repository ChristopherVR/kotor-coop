# Screenshots one game window's OWN CONTENT — regardless of what else is on top of it, and
# regardless of where on (or off) the desktop it sits. docs/research/isolated-rig.md.
#
# Primary path: PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT) asks DWM directly for that
# window's composed surface. Unlike a screen-rectangle grab (CopyFromScreen at the window's
# position, what this script used to do exclusively) it is correct under occlusion and at an
# off-screen position, because it never reads the desktop at all — only unlike a raw
# GetDC(hwnd)+BitBlt (what made older per-window captures in this repo "come back black" for a
# D3D/OpenGL window, see tools/siteclip.ps1's header) it asks DWM for the ALREADY-COMPOSITED
# frame instead of the window's own device context, which is what makes it work for KOTOR's
# swapchain at all. Needs DWM composition (on by default since Windows 8) and the window to be
# NOT MINIMIZED; an exclusive-fullscreen swapchain bypasses DWM entirely and this will not see
# it either — run KOTOR windowed (the rig already does).
#
# Fallback (used automatically if PrintWindow fails, or returns a suspiciously flat image — the
# old symptom): the original screen-rectangle CopyFromScreen, which needs the window on-screen,
# unoccluded and briefly brought to the foreground (it un-minimizes and raises the window, same
# as before). Same parameters as before; nothing about the caller-visible contract changed.
param(
    [string]$OutPath,
    [string]$ProcessName = 'swkotor',
    [int]$ProcId = 0,
    [switch]$FullScreen,
    # Skip straight to the screen-rectangle fallback (for comparison, or a machine with DWM
    # composition disabled).
    [switch]$ForceScreenCapture
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
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
'@

$proc = if ($ProcId -ne 0) { Get-Process -Id $ProcId -ErrorAction SilentlyContinue }
        else { Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $proc) { Write-Error "target process not running"; exit 1 }

$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { Write-Error 'no main window handle'; exit 1 }

$wr = New-Object WinApi+RECT
[WinApi]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
$winW = $wr.Right - $wr.Left
$winH = $wr.Bottom - $wr.Top

$cr = New-Object WinApi+RECT
[WinApi]::GetClientRect($hwnd, [ref]$cr) | Out-Null
$tl = New-Object WinApi+POINT
$tl.X = 0; $tl.Y = 0
[WinApi]::ClientToScreen($hwnd, [ref]$tl) | Out-Null
$clientOffsetX = $tl.X - $wr.Left
$clientOffsetY = $tl.Y - $wr.Top
$clientW = $cr.Right - $cr.Left
$clientH = $cr.Bottom - $cr.Top

if ($winW -le 0 -or $winH -le 0) { Write-Error "bad window size ${winW}x${winH}"; exit 1 }

function Get-PrintWindowCapture {
    param([IntPtr]$Hwnd, [int]$W, [int]$H)
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    try {
        # PW_RENDERFULLCONTENT = 2 (Windows 8.1+): pulls the DWM-composed frame, which is
        # what actually holds a D3D/OpenGL window's pixels. Flag 0 (plain PrintWindow) asks
        # the window to WM_PRINT itself, which a game's own wndproc never implements and which
        # comes back blank for exactly this kind of window.
        $ok = [WinApi]::PrintWindow($Hwnd, $hdc, 2)
    } finally {
        $g.ReleaseHdc($hdc)
    }
    $g.Dispose()
    if (-not $ok) { $bmp.Dispose(); return $null }
    return $bmp
}

# A PrintWindow call that returned true but produced one flat colour end to end is the known
# failure shape (an unminimised-but-not-actually-composited window, or a stale/black frame) —
# worth falling back from rather than shipping a useless screenshot.
function Test-LooksBlank {
    param([System.Drawing.Bitmap]$Bmp)
    if ($Bmp.Width -le 0 -or $Bmp.Height -le 0) { return $true }
    $w = $Bmp.Width; $h = $Bmp.Height
    $c0 = $Bmp.GetPixel(0, 0)
    # Each pair is wrapped with explicit parens around the arithmetic: PowerShell's comma
    # operator binds tighter than binary `-`, so an unparenthesised `$w - 1, $h - 1` parses as
    # `$w - (1, $h) - 1` (subtracting an array) and throws, not the two-element pair it looks
    # like.
    $points = @(
        @(([Math]::Floor($w / 2)), ([Math]::Floor($h / 2))),
        @(([Math]::Floor($w * 0.25)), ([Math]::Floor($h * 0.75))),
        @(([Math]::Floor($w * 0.75)), ([Math]::Floor($h * 0.25))),
        @(($w - 1), ($h - 1))
    )
    foreach ($p in $points) {
        $c = $Bmp.GetPixel([int]$p[0], [int]$p[1])
        if ($c.ToArgb() -ne $c0.ToArgb()) { return $false }
    }
    return $true
}

$method = $null
$bmp = $null
$x = 0; $y = 0; $w = 0; $h = 0

if (-not $ForceScreenCapture) {
    $full = Get-PrintWindowCapture -Hwnd $hwnd -W $winW -H $winH
    if ($null -ne $full -and -not (Test-LooksBlank $full)) {
        $method = 'PrintWindow (DWM composed; occlusion/position-independent)'
        if ($FullScreen) {
            $bmp = $full
            $x = $wr.Left; $y = $wr.Top; $w = $winW; $h = $winH
        }
        elseif ($clientW -gt 0 -and $clientH -gt 0 -and $clientOffsetX -ge 0 -and $clientOffsetY -ge 0 `
                -and ($clientOffsetX + $clientW) -le $winW -and ($clientOffsetY + $clientH) -le $winH) {
            $cropRect = New-Object System.Drawing.Rectangle($clientOffsetX, $clientOffsetY, $clientW, $clientH)
            $bmp = $full.Clone($cropRect, $full.PixelFormat)
            $full.Dispose()
            $x = $tl.X; $y = $tl.Y; $w = $clientW; $h = $clientH
        }
        else {
            # Client rect didn't make sense (unusual window styling) — hand back the whole
            # window rather than fail outright.
            $bmp = $full
            $x = $wr.Left; $y = $wr.Top; $w = $winW; $h = $winH
        }
    }
    elseif ($null -ne $full) {
        $full.Dispose()
    }
}

if ($null -eq $bmp) {
    $method = if ($ForceScreenCapture) { 'screen capture (forced)' } else { 'screen capture (PrintWindow unavailable/blank fallback)' }

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
        $x = $tl.X; $y = $tl.Y; $w = $clientW; $h = $clientH
    }

    if ($w -le 0 -or $h -le 0) { Write-Error "bad capture size ${w}x${h}"; exit 1 }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
}

$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

"captured ${w}x${h} at ($x,$y) via $method -> $OutPath"
