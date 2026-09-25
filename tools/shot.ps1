# Screenshots one game window's OWN CONTENT — regardless of what else is on top of it,
# regardless of where on (or off) the desktop it sits, and regardless of whether it is even
# composited at all. docs/research/isolated-rig.md.
#
# Primary path (default, since 2026-09-25): the plugin's own `screenshot` console verb
# (ScreenshotController, via blockpoke.ps1's `console` verb — the same mechanism click.ps1's
# default `input` path already uses). The plugin reads pixels straight out of the GL context
# from inside the hooked GDI32!SwapBuffers frame callback and writes a BMP to
# <gamedir>\kotorcoop-shots\<name>.bmp; this script polls for that file and converts it to
# -OutPath. Needs the out-of-process agent attached to the game instance (same requirement
# every other `blockpoke console ...` verb has) — same precondition -PrintWindow/
# -ForceScreenCapture do NOT have, which is why this script still falls back to them
# automatically if the plugin path fails for any reason (agent not attached, no reply, no file).
#
# Why not PrintWindow anymore: measured live 2026-09-25 that PrintWindow(hwnd, hdc,
# PW_RENDERFULLCONTENT) is UNRELIABLE for this game. A KOTOR window fully visible on screen and
# unoccluded (a plain CopyFromScreen of it had 294/300 lit sample points — genuinely on
# screen and rendering) captured solid BLACK via PrintWindow on the very same pass; it had
# worked in an earlier pass against the same kind of window. DWM evidently does not always have
# a composed surface to hand back for this game's OpenGL swapchain, even when the window is
# demonstrably visible — so PrintWindow is no longer trustworthy enough to be the default,
# though it is kept available via -PrintWindow (see below) since it needs no session/agent at
# all and is still occasionally the more convenient check.
#
# -PrintWindow: the old default chain — PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT) first
# (asks DWM directly for the window's composed surface; correct under occlusion and at an
# off-screen position when it works at all, wrong roughly as often as right per the finding
# above), falling back automatically to the screen-rectangle grab below if it fails or returns a
# suspiciously flat image. Needs DWM composition (on by default since Windows 8) and the window
# to be NOT MINIMIZED; an exclusive-fullscreen swapchain bypasses DWM entirely and this will not
# see it either.
#
# -ForceScreenCapture: the original screen-rectangle CopyFromScreen, which needs the window
# on-screen, unoccluded and briefly brought to the foreground (it un-minimizes and raises the
# window). The last-resort fallback every other path already falls back to.
#
# Same -OutPath/-ProcessName/-ProcId/-FullScreen contract as before regardless of method.
param(
    [string]$OutPath,
    [string]$ProcessName = 'swkotor',
    [int]$ProcId = 0,
    [switch]$FullScreen,
    # Force the old PrintWindow-first chain (skips the plugin path entirely).
    [switch]$PrintWindow,
    # Skip straight to the screen-rectangle fallback (for comparison, or a machine with DWM
    # composition disabled). Also skips the plugin path.
    [switch]$ForceScreenCapture,
    # Plugin path only: read GL_FRONT (whatever the last swap already presented) instead of the
    # default GL_BACK (this frame's just-rendered, not-yet-presented image). Try this if the
    # default comes back black — see docs/research/isolated-rig.md.
    [switch]$Front
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

# Plugin path: drives ScreenshotController through blockpoke.ps1's `console` verb (the same
# mechanism click.ps1's default `input` path already uses), then polls for the BMP it writes.
# Returns $null (nothing usable at all) or an object with .Path ($null on failure) and .Detail
# (the console reply / the reason it gave up), so the caller can log exactly why it is falling
# back rather than just silently trying the next method.
function Get-PluginScreenshot {
    param([System.Diagnostics.Process]$Proc, [switch]$UseFront)

    $gameDir = $null
    try { $gameDir = Split-Path -Parent $Proc.Path } catch {}
    if (-not $gameDir) {
        try { $gameDir = Split-Path -Parent $Proc.MainModule.FileName } catch {}
    }
    if (-not $gameDir) {
        try {
            $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$($Proc.Id)" -ErrorAction Stop
            if ($cim -and $cim.ExecutablePath) { $gameDir = Split-Path -Parent $cim.ExecutablePath }
        } catch {}
    }
    if (-not $gameDir) {
        return [pscustomobject]@{ Path = $null; Detail = 'could not determine the game directory from the process' }
    }

    # Alnum only, well under ScreenshotController's 24-character cap - a plain timestamp is
    # unique enough for one script invocation and never collides across processes.
    $name = 'shot' + (Get-Date -Format 'HHmmssfff')
    $verb = if ($UseFront) { "screenshot $name front" } else { "screenshot $name" }

    $blockpoke = Join-Path $PSScriptRoot 'blockpoke.ps1'
    $reply = (& $blockpoke -ProcId $Proc.Id console $verb 2>&1 | Out-String).Trim()

    if ($reply -match 'NO REPLY') {
        return [pscustomobject]@{ Path = $null; Detail = "console verb got no reply - is the out-of-process agent attached to this instance? ($reply)" }
    }

    $bmpPath = Join-Path (Join-Path $gameDir 'kotorcoop-shots') "$name.bmp"
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $bmpPath)) {
        Start-Sleep -Milliseconds 150
    }

    if (-not (Test-Path -LiteralPath $bmpPath)) {
        return [pscustomobject]@{ Path = $null; Detail = "'$bmpPath' never appeared within 8s ($reply)" }
    }

    # The plugin's FileStream has already closed by the time the file is visible at all (it
    # writes then disposes synchronously on the render thread before answering the cheat), but
    # give the filesystem a moment before this process opens it too.
    Start-Sleep -Milliseconds 100
    return [pscustomobject]@{ Path = $bmpPath; Detail = $reply }
}

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
$pluginBmpPath = $null

if (-not $PrintWindow -and -not $ForceScreenCapture) {
    $plugin = Get-PluginScreenshot -Proc $proc -UseFront:$Front
    if ($null -ne $plugin -and $plugin.Path) {
        try {
            $bmp = New-Object System.Drawing.Bitmap $plugin.Path
            $pluginBmpPath = $plugin.Path
            $method = "plugin GL readback ($(if ($Front) { 'front' } else { 'back' }) buffer, kotorcoop-shots\$([System.IO.Path]::GetFileName($plugin.Path)))"
            $w = $bmp.Width; $h = $bmp.Height; $x = 0; $y = 0
        }
        catch {
            Write-Warning "plugin screenshot file '$($plugin.Path)' could not be opened: $_"
            $bmp = $null
        }
    }
    else {
        $detail = if ($null -ne $plugin) { $plugin.Detail } else { 'unknown failure' }
        Write-Warning "plugin screenshot unavailable ($detail) - falling back to PrintWindow"
    }
}

if ($null -eq $bmp -and -not $ForceScreenCapture) {
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

if ($pluginBmpPath) {
    "captured ${w}x${h} via $method -> $OutPath (raw BMP left at $pluginBmpPath)"
} else {
    "captured ${w}x${h} at ($x,$y) via $method -> $OutPath"
}
