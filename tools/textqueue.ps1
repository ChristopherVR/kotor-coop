# Dumps the ENGINE's own on-screen text queue (Global::AurPostString's PostedString list,
# 0x007B90FC/0x007B9100 - see docs/research/text-render-queued.md) out of a running KOTOR: the
# column/row/text every plugin controller has actually asked the game to draw, right now.
#
# Built for the "is this overlay landing where the code says it should" class of question -
# console-overlay.md, chat-input-scroll.md and text-render-queued.md all carry NEEDS-LIVE notes
# on exact column/row placement, and tools\shot.ps1 is not always trustworthy for it: a
# minimise/restore leaves it showing a stale frame (docs/live-verification.md), and on a machine
# where screen capture itself is unreliable (e.g. GDI returning a black frame for a window that
# is provably rendering, per the plugin log's own frame counter) a screenshot answers nothing at
# all. This reads the STRUCTURE the engine is about to draw directly, no pixels needed: it
# proves (or disproves) that a controller posted the right text at the right cell, independent
# of whether this session can see the result.
#
# Absolute addresses, not a shared-block signature scan: the list lives in the game's own
# .data/.bss, not the plugin's block, and this build has no ASLR (disk == memory, base
# 0x400000 - see docs/research/console-overlay.md), so the same addresses apply on every run.
#
#   pwsh tools\textqueue.ps1 -ProcId 1234
#   pwsh tools\textqueue.ps1                    # first swkotor.exe found
#
# Each row printed is one live PostedString node: column (x), row (line, 0 = top, larger =
# lower), and up to 200 bytes of its text. A line that looks cut short with a trailing "..." at
# the printed length is this session's own controllers truncating on purpose (ChatOverlayController
# ,see console-overlay.md's 2026-09-04 addendum) - anything else worth flagging as a real
# placement bug.
param(
    [string]$ProcessName = 'swkotor',
    [int]$ProcId = 0
)

Add-Type @'
using System; using System.Runtime.InteropServices;
public class TQ {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  public static IntPtr Open(int pid){ return OpenProcess(0x0410, false, (uint)pid); }
}
'@

$proc = if ($ProcId -ne 0) { Get-Process -Id $ProcId -ErrorAction SilentlyContinue }
        else { Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $proc) { Write-Error 'target process not running'; exit 1 }

$h = [TQ]::Open($proc.Id)

function Read32([long]$addr) {
    $b = New-Object byte[] 4; $r = 0
    if (-not [TQ]::ReadProcessMemory($h, [IntPtr]$addr, $b, 4, [ref]$r)) { return $null }
    [BitConverter]::ToInt32($b, 0)
}
function ReadCStr([long]$addr, [int]$cap) {
    $b = New-Object byte[] $cap; $r = 0
    if (-not [TQ]::ReadProcessMemory($h, [IntPtr]$addr, $b, $cap, [ref]$r)) { return '<unreadable>' }
    $n = [Array]::IndexOf($b, [byte]0); if ($n -lt 0) { $n = $cap }
    [Text.Encoding]::ASCII.GetString($b, 0, $n)
}

# Global::ScreenWidth/Height - the ACTUAL current viewport, so a placement question can be read
# against the real resolution rather than an assumption (docs/research/console-overlay.md's
# 2026-09-04 addendum: this rig's own default is 800x600, no Width/Height set in swkotor.ini).
$width  = Read32 0x0078D1D4
$height = Read32 0x0078D1D8
"pid $($proc.Id)  screen ${width}x${height}"

$listPtr = Read32 0x007B90FC
$count   = Read32 0x007B9100
if ($null -eq $listPtr -or $null -eq $count) { Write-Error 'could not read the text queue (wrong build/offsets?)'; exit 1 }
"text queue: dataPtr=0x{0:X}  count={1}" -f $listPtr, $count

for ($i = 0; $i -lt $count; $i++) {
    $nodePtr = Read32 ($listPtr + ($i * 4))
    if ($null -eq $nodePtr -or $nodePtr -eq 0) { "  [$i] <unreadable slot>"; continue }
    $text = ReadCStr $nodePtr 200
    $x    = Read32 ($nodePtr + 0x42C)
    $line = Read32 ($nodePtr + 0x430)
    "  [{0}] col={1,3}  row={2,3}  '{3}'" -f $i, $x, $line, $text
}

[TQ]::CloseHandle($h) | Out-Null
