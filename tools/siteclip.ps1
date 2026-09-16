# Records the two side-by-side KOTOR windows of a running demo (tools/demo.ps1) into one GIF
# for the player site (site/media). Captures the desktop region spanning both game windows
# with ffmpeg's gdigrab (a windowed D3D game composited by DWM screenshots fine this way,
# unlike shot.ps1's per-window BitBlt which can come back black), then quantises to a
# two-pass palette GIF.
#
#   pwsh tools\siteclip.ps1 -Name together -Seconds 8            # site\media\together.gif
#   pwsh tools\siteclip.ps1 -Name fight -Seconds 10 -Width 1200  # wider output
#
# Needs ffmpeg on PATH or at -Ffmpeg. Runs while the demo is up; drive the action (walkto,
# spawncreature, say ...) through tools\blockpoke.ps1 console from another shell, or pass a
# -Action script block that is invoked right after the recording starts.
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [int]$Seconds = 8,
    [int]$Fps = 12,
    [int]$Width = 1200,
    [string]$Ffmpeg = 'ffmpeg',
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'site\media'),
    [string]$WorkDir = "$env:TEMP\kotorcoop-siteclip",
    [scriptblock]$Action = $null,
    # Trim this many pixels of window chrome (title bar) off the top of each window's rect.
    [int]$TrimTop = 0
)

$ErrorActionPreference = 'Stop'
Add-Type @'
using System; using System.Runtime.InteropServices;
public class SC {
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
'@

$games = @(Get-Process swkotor -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 })
if ($games.Count -lt 1) { throw 'no swkotor window found; start the demo first (tools\demo.ps1)' }

# Union of every game window's CLIENT area (no title bars), in screen coordinates.
$left = [int]::MaxValue; $top = [int]::MaxValue; $right = [int]::MinValue; $bottom = [int]::MinValue
foreach ($g in $games) {
    $r = New-Object SC+RECT
    [SC]::GetClientRect($g.MainWindowHandle, [ref]$r) | Out-Null
    $p = New-Object SC+POINT; $p.X = 0; $p.Y = 0
    [SC]::ClientToScreen($g.MainWindowHandle, [ref]$p) | Out-Null
    $left = [Math]::Min($left, $p.X); $top = [Math]::Min($top, $p.Y + $TrimTop)
    $right = [Math]::Max($right, $p.X + $r.Right); $bottom = [Math]::Max($bottom, $p.Y + $r.Bottom)
}
# gdigrab wants even dimensions.
$w = ($right - $left) -band -2; $h = ($bottom - $top) -band -2
Write-Host ("capturing {0}x{1} at ({2},{3}) from {4} window(s) for {5}s" -f $w, $h, $left, $top, $games.Count, $Seconds)

New-Item -ItemType Directory -Force $WorkDir, $OutDir | Out-Null
$mp4 = Join-Path $WorkDir "$Name.mp4"
$gif = Join-Path $OutDir "$Name.gif"
Remove-Item $mp4 -ErrorAction SilentlyContinue

$rec = Start-Process -FilePath $Ffmpeg -PassThru -NoNewWindow -RedirectStandardError (Join-Path $WorkDir "$Name.ffmpeg.log") -ArgumentList @(
    '-y', '-f', 'gdigrab', '-framerate', $Fps, '-offset_x', $left, '-offset_y', $top,
    '-video_size', "${w}x${h}", '-draw_mouse', '0', '-i', 'desktop', '-t', $Seconds,
    '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', $mp4)

if ($Action) { Start-Sleep -Milliseconds 700; & $Action }
$rec.WaitForExit()
if (-not (Test-Path $mp4)) { throw "ffmpeg produced no recording; see $WorkDir\$Name.ffmpeg.log" }

# Cut each window's client area out of the one recording and butt them together, so the
# desktop gap between the windows never shows. Then a two-pass palette: one palette for the
# whole clip keeps the UI colours stable frame to frame.
$crops = @(); $labels = @()
for ($i = 0; $i -lt $games.Count; $i++) {
    $g = $games[$i]
    $r = New-Object SC+RECT; [SC]::GetClientRect($g.MainWindowHandle, [ref]$r) | Out-Null
    $p = New-Object SC+POINT; $p.X = 0; $p.Y = 0; [SC]::ClientToScreen($g.MainWindowHandle, [ref]$p) | Out-Null
    $crops += ("[s{0}]crop={1}:{2}:{3}:{4}[c{0}]" -f $i, ($r.Right -band -2), (($r.Bottom - $TrimTop) -band -2), ($p.X - $left), ($p.Y + $TrimTop - $top))
    $labels += "[c$i]"
}
$splitOut = (0..($games.Count - 1) | ForEach-Object { "[s$_]" }) -join ''
$stack = if ($games.Count -gt 1) { ($labels -join '') + "hstack=inputs=$($games.Count)[st];[st]" } else { "$($labels[0])" }
$filters = "[0:v]split=$($games.Count)$splitOut;" + ($crops -join ';') + ";" +
    "${stack}fps=$Fps,scale=${Width}:-1:flags=lanczos,split[p0][p1];[p0]palettegen=max_colors=192:stats_mode=diff[pal];[p1][pal]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle"
& $Ffmpeg -y -loglevel error -i $mp4 -filter_complex $filters -loop 0 $gif
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg gif conversion failed' }
$size = (Get-Item $gif).Length
Write-Host ("wrote {0} ({1:N1} MB)" -f $gif, ($size / 1MB))
