# Records the two side-by-side KOTOR windows of a running demo (tools/demo.ps1) into one clip
# for the player site (site/media). Captures the desktop region spanning both game windows
# with ffmpeg's gdigrab (a windowed D3D game composited by DWM screenshots fine this way,
# unlike shot.ps1's per-window BitBlt which can come back black), cuts each window's client
# area out and butts them together, and writes an H.264 MP4 the pages play with <video>.
# A GIF of the same clip came out at 8-13 MB for twenty seconds of a fight (the camera swings
# and every frame changes), where the MP4 is under 3 MB, so the GIF is opt-in (-Gif).
#
#   pwsh tools\siteclip.ps1 -Name together -Seconds 16              # site\media\together.mp4
#   pwsh tools\siteclip.ps1 -Name fight -Seconds 20 -Gif            # ...and fight.gif beside it
#   pwsh tools\siteclip.ps1 -Name fight -Seconds 20 -TrimStart 1.5  # drop the first 1.5 s
#
# Needs ffmpeg on PATH or at -Ffmpeg. Runs while the demo is up; drive the action (walkto,
# spawncreature, say ...) through tools\blockpoke.ps1 console from another shell, or pass a
# -Action script block that is invoked right after the recording starts. The -Action block
# only works when this script is called in-process (& .\tools\siteclip.ps1 -Action {..});
# a child `pwsh tools\siteclip.ps1 -Action {..}` serialises the block to a string and fails.
# The raw desktop recording is kept in -WorkDir, so a clip can be re-cut without re-recording.
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
    [int]$TrimTop = 0,
    # Also write a palette GIF next to the MP4 (for a README that cannot play video).
    [switch]$Gif,
    # Seconds to drop from the start of the recording (the pwsh start-up before the first action).
    [double]$TrimStart = 0
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

# Left-to-right by screen position, not by process id: the GIF's left pane is whichever window
# sits on the left of the desk, so a relaunch that hands the client the lower pid cannot swap
# the host and the joiner around (it did, once).
$games = @($games | Sort-Object {
    $p = New-Object SC+POINT; $p.X = 0; $p.Y = 0
    [SC]::ClientToScreen($_.MainWindowHandle, [ref]$p) | Out-Null
    $p.X
})

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
$mp4 = Join-Path $WorkDir "$Name.raw.mp4"
$out = Join-Path $OutDir "$Name.mp4"
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
# desktop gap between the windows never shows.
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
$layout = "[0:v]split=$($games.Count)$splitOut;" + ($crops -join ';') + ";" + $stack
$trim = if ($TrimStart -gt 0) { @('-ss', $TrimStart) } else { @() }

# The MP4 the site plays: even dimensions (-2) and yuv420p are what every browser decodes.
& $Ffmpeg -y -loglevel error @trim -i $mp4 -filter_complex "${layout}fps=$Fps,scale=${Width}:-2:flags=lanczos,format=yuv420p" `
    -c:v libx264 -preset slow -crf 23 -movflags +faststart -an $out
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg mp4 encode failed' }
Write-Host ("wrote {0} ({1:N1} MB); raw recording kept at {2}" -f $out, ((Get-Item $out).Length / 1MB), $mp4)

if ($Gif) {
    # Two-pass palette: one palette for the whole clip keeps the UI colours stable frame to frame.
    $palette = "${layout}fps=$Fps,scale=${Width}:-1:flags=lanczos,split[p0][p1];[p0]palettegen=max_colors=192:stats_mode=diff[pal];[p1][pal]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle"
    & $Ffmpeg -y -loglevel error @trim -i $mp4 -filter_complex $palette -loop 0 $gif
    if ($LASTEXITCODE -ne 0) { throw 'ffmpeg gif conversion failed' }
    Write-Host ("wrote {0} ({1:N1} MB)" -f $gif, ((Get-Item $gif).Length / 1MB))
}
