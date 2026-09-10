# Live-test for the twin-orientation-snap fix (Odyssey.Phantom.cs DriveBody, twin's
# CSWCObject::SetOrientation vtable+0x88 call): polls a mirrored creature's SERVER-side
# orientation (the phantom CSWSCreature this instance drives from the network, +0x9C) against
# its CLIENT TWIN's rendered orientation (+0x30) every ~100ms, so "twin still turning toward a
# facing the server already snapped to several samples ago" (the easing defect,
# ClientServerSyncHandler_Orientation / RotateCreatureToOrientation, vtable+0x130) shows up as
# numbers instead of a screenshot judgement call.
#
# Offsets: docs/re/rendering.md §9.2 struct table.
#   CSWSCreature +0x9C orientation (Vec3), +0x224 client_object
#   CSWCCreature +0x30 orientation (Vec3)
#
# Usage (run on the instance that is WATCHING the mirrored creature, i.e. the peer, not the
# instance whose player/AI is actually driving it):
#
#   pwsh tools\facingtrace.ps1 -Tag trask -Seconds 15
#   pwsh tools\facingtrace.ps1 -ObjectId 0x1A2B -Seconds 15
#
# Expected healthy output: `srvDeg` and `twinDeg` match within ~1 sample (one ~100ms tick)
# whenever srvDeg changes abruptly. A regression reads as twinDeg trailing srvDeg by several
# consecutive samples after a jump (a visible turn-in-place) instead of snapping immediately.
param(
    [string]$ProcessName = 'swkotor',
    [int]$ProcId = 0,
    [string]$Tag = '',
    [string]$ObjectId = '',
    [int]$Seconds = 15,
    [int]$IntervalMs = 100,
    [string]$Csv = ''
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public class FT {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  [DllImport("kernel32.dll")] public static extern int VirtualQueryEx(IntPtr h, IntPtr a, out MBI m, int l);
  [StructLayout(LayoutKind.Sequential)] public struct MBI {
    public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect, __a1;
    public UIntPtr RegionSize; public uint State, Protect, Type, __a2; }
  public static IntPtr Open(int pid){ return OpenProcess(0x0410, false, (uint)pid); }
  public static byte[] Read(IntPtr h, long a, int n){
    byte[] b = new byte[n]; int r;
    if(!ReadProcessMemory(h, new IntPtr(a), b, n, out r) || r != n) return null;
    return b; }
  public static List<long> Scan(IntPtr h, uint needle){
    var hits = new List<long>();
    long addr = 0; int mbiSize = Marshal.SizeOf(typeof(MBI));
    while(addr < 0x7FFFFFFF){
      MBI m;
      if(VirtualQueryEx(h, new IntPtr(addr), out m, mbiSize) == 0) break;
      long size = (long)m.RegionSize; if(size <= 0) break;
      bool ok = m.State == 0x1000 && m.Type == 0x20000 && (m.Protect & 0x101) == 0
             && (m.Protect & (0x02|0x04|0x08|0x20|0x40|0x80)) != 0;
      if(ok && size <= 64L*1024*1024){
        byte[] buf = Read(h, (long)m.BaseAddress, (int)size);
        if(buf != null)
          for(int i = 0; i + 4 <= buf.Length; i += 4)
            if(BitConverter.ToUInt32(buf, i) == needle) hits.Add((long)m.BaseAddress + i);
      }
      long next = (long)m.BaseAddress + size; if(next <= addr) break; addr = next;
    }
    return hits; }
}
'@

$proc = if ($ProcId -ne 0) { Get-Process -Id $ProcId -ErrorAction SilentlyContinue }
        else { Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $proc) { Write-Error 'target process not running'; exit 1 }

$h = [FT]::Open($proc.Id)

function F32($b) { if ($null -eq $b) { 0.0 } else { [BitConverter]::ToSingle($b, 0) } }
function U32($b) { if ($null -eq $b) { [uint32]0xFFFFFFFF } else { [BitConverter]::ToUInt32($b, 0) } }

$CreatureVtable = 0x007470C8
$OFF_ID = 0x04; $OFF_TAG = 0x18; $OFF_ORI = 0x9C; $OFF_CLIENT_OBJ = 0x224
$TWIN_ORI = 0x30

function ReadExoString($base, $off) {
    $b = [FT]::Read($h, $base + $off, 8); if(-not $b){ return '' }
    $ptr = [BitConverter]::ToUInt32($b, 0); $len = [BitConverter]::ToUInt32($b, 4)
    if($ptr -eq 0 -or $len -eq 0 -or $len -gt 64){ return '' }
    $s = [FT]::Read($h, $ptr, [int]$len); if(-not $s){ return '' }
    return [System.Text.Encoding]::ASCII.GetString($s)
}

"scanning for CSWSCreature objects..."
$hits = [FT]::Scan($h, [uint32]$CreatureVtable)

$target = 0
if ($ObjectId -ne '') {
    $wantId = [Convert]::ToUInt32($ObjectId, 16)
    foreach ($c in $hits) { if ((U32 ([FT]::Read($h, $c + $OFF_ID, 4))) -eq $wantId) { $target = $c; break } }
} elseif ($Tag -ne '') {
    foreach ($c in $hits) {
        $t = (ReadExoString $c $OFF_TAG).TrimEnd([char]0)
        if ($t -like "*$Tag*") { $target = $c; break }
    }
} else {
    Write-Error 'pass -Tag or -ObjectId'; exit 1
}

if ($target -eq 0) { Write-Error 'no matching creature found'; exit 1 }
$id = U32 ([FT]::Read($h, $target + $OFF_ID, 4))
"watching creature at 0x$($target.ToString('X')) id 0x$($id.ToString('X'))"
""

$header = "{0,-12} {1,9} {2,9} {3,8} {4,8} {5,8}" -f 'time', 'srvX', 'srvY', 'srvDeg', 'twinDeg', 'delta'
$header
('-' * $header.Length)

$rows = @()
$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) {
    $stamp = (Get-Date).ToString('HH:mm:ss.fff')
    $sx = F32 ([FT]::Read($h, $target + $OFF_ORI, 4))
    $sy = F32 ([FT]::Read($h, $target + $OFF_ORI + 4, 4))
    $srvDeg = [Math]::Atan2($sy, $sx) * 180.0 / [Math]::PI

    $clientObj = U32 ([FT]::Read($h, $target + $OFF_CLIENT_OBJ, 4))
    $twinDeg = 0.0
    if ($clientObj -ne 0 -and $clientObj -ne 0xFFFFFFFF) {
        $tx = F32 ([FT]::Read($h, $clientObj + $TWIN_ORI, 4))
        $ty = F32 ([FT]::Read($h, $clientObj + $TWIN_ORI + 4, 4))
        $twinDeg = [Math]::Atan2($ty, $tx) * 180.0 / [Math]::PI
    }

    $delta = $srvDeg - $twinDeg
    while ($delta -gt 180) { $delta -= 360 }
    while ($delta -lt -180) { $delta += 360 }

    $line = "{0,-12} {1,9:F3} {2,9:F3} {3,8:F2} {4,8:F2} {5,8:F2}" -f $stamp, $sx, $sy, $srvDeg, $twinDeg, $delta
    $line

    $rows += [PSCustomObject]@{ Time = $stamp; SrvDeg = $srvDeg; TwinDeg = $twinDeg; Delta = $delta }
    Start-Sleep -Milliseconds $IntervalMs
}

[FT]::CloseHandle($h) | Out-Null

if ($Csv -ne '') { $rows | Export-Csv -Path $Csv -NoTypeInformation; "wrote $($rows.Count) rows to $Csv" }

# Verdict: after any sample where srvDeg jumps >20deg from the previous sample, how many
# SUBSEQUENT samples show |delta| > 5deg before it settles. 0-1 = snap (healthy with the fix).
# Several in a row = visible easing (the defect).
""
$jumps = @()
for ($i = 1; $i -lt $rows.Count; $i++) {
    $prev = $rows[$i-1].SrvDeg; $cur = $rows[$i].SrvDeg
    $d = $cur - $prev; while ($d -gt 180) { $d -= 360 }; while ($d -lt -180) { $d += 360 }
    if ([Math]::Abs($d) -gt 20) { $jumps += $i }
}
if ($jumps.Count -eq 0) {
    "no server-orientation jump >20deg observed in this window; re-run during the actual repro step."
} else {
    foreach ($j in $jumps) {
        $trail = 0
        for ($k = $j; $k -lt $rows.Count -and [Math]::Abs($rows[$k].Delta) -gt 5; $k++) { $trail++ }
        "jump at sample $j (srvDeg $($rows[$j-1].SrvDeg.ToString('F1')) -> $($rows[$j].SrvDeg.ToString('F1'))): twin took $trail sample(s) (~$($trail * $IntervalMs)ms) to settle within 5deg"
    }
}
