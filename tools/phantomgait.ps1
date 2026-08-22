# Live-test for docs/research/phantom-locomotion.md: polls a driven body's ACTUAL engine
# animation state every 250 ms while its peer walks and runs, so "moves in sync but slides,
# no run cycle" (or the opposite: a healthy walk-then-run loop) shows up as data instead of a
# subjective screenshot.
#
# Reads the same fields Odyssey.DriveBody writes (docs/research/realtime-creature-movement.md
# §9/§10): the SERVER creature's animation id (+0xD4), move-state (+0xA8C) and run flag
# (+0xA98), and the CLIENT TWIN's animation id (+0x48) and playback-rate scale (+0x1F8) — the
# twin is what actually renders, so a healthy run shows server==twin==0x2714 with a nonzero
# rate; server-only or a rate stuck at 0.0 is exactly the "legs frozen, body slides" defect
# docs/research/realtime-creature-movement.md §8 describes.
#
# Usage (run on the WATCHING instance, i.e. the one whose screen shows the OTHER player's
# phantom):
#
#   pwsh tools\phantomgait.ps1                      # auto: every live phantom slot, 20s
#   pwsh tools\phantomgait.ps1 -Seconds 40
#   pwsh tools\phantomgait.ps1 -ObjectId 0x1A2B      # a specific object id instead (a
#                                                     # mirrored enemy or companion — read
#                                                     # its id from `creatures.ps1` or the
#                                                     # in-game target console command)
#   pwsh tools\phantomgait.ps1 -Csv gait.csv
#
# Procedure this backs (docs/research/phantom-locomotion.md's live-test section):
#   1. Two instances connected, both in the same module, standing apart.
#   2. On the HOST: walk in a straight line for ~3s, then run in a straight line for ~5s,
#      then stop.
#   3. On the CLIENT, during that whole window: run this script with no -ObjectId (auto-picks
#      slot 0's phantom, which is the host's body as the client sees it).
#   4. Swap roles (client walks/runs, host watches) and repeat.
#   5. Repeat once more against a mirrored enemy's id and a mirrored companion's id, if the
#      module has either, using -ObjectId.
#
# Expected healthy output: `anim` transitions idle(0x2710) -> walk(0x2712) -> run(0x2714) and
# back, `twin` tracks `anim` within one sample, `rate` is nonzero whenever `moving`=1, and
# `moveState` is -1 whenever `moving`=1 (1 when idle) — see the field table in
# docs/research/realtime-creature-movement.md §9. A regression reads as `anim`/`twin` parked
# at idle while `pos` keeps changing between samples (the exact "moves in sync but slides"
# report), or `rate`=0.00 while `moving`=1 (frozen legs).
param(
    [string]$ProcessName = 'swkotor',
    [int]$ProcId = 0,
    [string]$ObjectId = '',
    [int]$Seconds = 20,
    [int]$IntervalMs = 250,
    [string]$Csv = ''
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System; using System.Runtime.InteropServices;
public class PG {
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
  public static long FindSig(IntPtr h, byte[] sig, int wildStart, int wildLen){
    long addr = 0; int sz = Marshal.SizeOf(typeof(MBI));
    while(addr < 0x7FFFFFFF){
      MBI m; if(VirtualQueryEx(h, new IntPtr(addr), out m, sz) == 0) break;
      long size = (long)m.RegionSize; if(size <= 0) break;
      bool ok = m.State == 0x1000 && m.Type == 0x20000 && (m.Protect & 0x101) == 0
             && (m.Protect & (0x02|0x04|0x08|0x20|0x40|0x80)) != 0;
      if(ok && size <= 64L*1024*1024){
        byte[] buf = Read(h, (long)m.BaseAddress, (int)size);
        if(buf != null)
          for(int i = 0; i + sig.Length <= buf.Length; i += 4){
            bool hit = true;
            for(int j = 0; j < sig.Length; j++){
              if(j >= wildStart && j < wildStart + wildLen) continue;
              if(buf[i+j] != sig[j]) { hit = false; break; }
            }
            if(hit) return (long)m.BaseAddress + i;
          }
      }
      long next = (long)m.BaseAddress + size; if(next <= addr) break; addr = next;
    }
    return 0; }
  // Sweep for the creature vtable pointer, same idiom as tools\creatures.ps1.
  public static System.Collections.Generic.List<long> ScanVtable(IntPtr h, uint needle){
    var hits = new System.Collections.Generic.List<long>();
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

$h = [PG]::Open($proc.Id)

function U32Bytes($b) { if ($null -eq $b) { [uint32]0xFFFFFFFF } else { [BitConverter]::ToUInt32($b, 0) } }
function I32Bytes($b) { if ($null -eq $b) { 0 } else { [BitConverter]::ToInt32($b, 0) } }
function F32Bytes($b) { if ($null -eq $b) { 0.0 } else { [BitConverter]::ToSingle($b, 0) } }

# ---- resolve which object id(s) to watch --------------------------------------------------
$ids = @()

if ($ObjectId -ne '') {
    $ids = @([Convert]::ToUInt32($ObjectId, 16))
} else {
    # Locate the plugin's shared block (same signature blockpoke.ps1 uses) and read the
    # relocated phantom-id table (SharedBlock.OffsetPhantomIdTable = 0x1F80, 8 x uint32).
    $sig = [byte[]](0x4B,0x4F,0x43,0x4F,0x4F,0x50,0x30,0x31, 0,0,0,0, 0x01,0xEE,0xFF,0xC0)
    $block = [PG]::FindSig($h, $sig, 8, 4)
    if ($block -eq 0) { Write-Error 'plugin shared block not found (is the co-op plugin loaded?)'; exit 1 }

    for ($i = 0; $i -lt 8; $i++) {
        $id = U32Bytes ([PG]::Read($h, $block + 0x1F80 + ($i * 4), 4))
        if ($id -ne 0 -and $id -ne 0xFFFFFFFF) { $ids += $id }
    }

    if ($ids.Count -eq 0) { Write-Error 'no live phantom found in the shared block; pass -ObjectId explicitly for an enemy or companion'; exit 1 }
    "auto-selected phantom id(s): $(($ids | ForEach-Object { '0x{0:X}' -f $_ }) -join ', ')"
}

# ---- resolve each id to a creature address, once (CSWSCreature vtable, per creatures.ps1) -
$CreatureVtable = 0x007470C8
$OFF_ID = 0x04; $OFF_POS = 0x90; $OFF_ANIM = 0xD4; $OFF_MOVESTATE = 0xA8C; $OFF_RUNFLAG = 0xA98
$OFF_CLIENT_OBJ = 0x224
$TWIN_ANIM = 0x48; $TWIN_RATE = 0x1F8

"scanning for CSWSCreature objects (this can take a few seconds)..."
$candidates = [PG]::ScanVtable($h, [uint32]$CreatureVtable)

$targets = @{}
foreach ($c in $candidates) {
    $id = U32Bytes ([PG]::Read($h, $c + $OFF_ID, 4))
    if ($ids -contains $id) { $targets[$id] = $c }
}

foreach ($id in $ids) {
    if (-not $targets.ContainsKey($id)) { "WARNING: id 0x$($id.ToString('X')) not resolved to a live creature (yet)" }
}
if ($targets.Count -eq 0) { Write-Error 'none of the requested ids resolved to a live creature'; exit 1 }

"watching $($targets.Count) bod$(if ($targets.Count -eq 1) { 'y' } else { 'ies' }) every ${IntervalMs}ms for ${Seconds}s"
""

$rows = @()
$header = "{0,-12} {1,-10} {2,-24} {3,6} {4,6} {5,9} {6,10} {7,6} {8,6} {9,6}" -f `
    'time', 'id', 'pos', 'anim', 'twin', 'rate', 'moveState', 'run', 'speed', 'moving'
$header
('-' * $header.Length)

$lastPos = @{}
$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) {
    $stamp = (Get-Date).ToString('HH:mm:ss.fff')
    foreach ($id in $targets.Keys) {
        $c = $targets[$id]
        $pos = [PG]::Read($h, $c + $OFF_POS, 12)
        $x = F32Bytes ([PG]::Read($h, $c + $OFF_POS, 4))
        $y = F32Bytes ([PG]::Read($h, $c + $OFF_POS + 4, 4))
        $anim = U32Bytes ([PG]::Read($h, $c + $OFF_ANIM, 4))
        $moveState = I32Bytes ([PG]::Read($h, $c + $OFF_MOVESTATE, 4))
        $runFlag = I32Bytes ([PG]::Read($h, $c + $OFF_RUNFLAG, 4))

        $clientObj = U32Bytes ([PG]::Read($h, $c + $OFF_CLIENT_OBJ, 4))
        $twinAnim = 0; $rate = 0.0
        if ($clientObj -ne 0 -and $clientObj -ne 0xFFFFFFFF) {
            $twinAnimBytes = [PG]::Read($h, $clientObj + $TWIN_ANIM, 2)
            if ($null -ne $twinAnimBytes) { $twinAnim = [BitConverter]::ToUInt16($twinAnimBytes, 0) }
            $rate = F32Bytes ([PG]::Read($h, $clientObj + $TWIN_RATE, 4))
        }

        $speed = 0.0
        if ($lastPos.ContainsKey($id)) {
            $dx = $x - $lastPos[$id].X; $dy = $y - $lastPos[$id].Y
            $dt = $IntervalMs / 1000.0
            $speed = [Math]::Sqrt($dx * $dx + $dy * $dy) / $dt
        }
        $lastPos[$id] = [PSCustomObject]@{ X = $x; Y = $y }

        $moving = if ($moveState -eq -1) { 1 } else { 0 }

        $line = "{0,-12} 0x{1,-8:X} ({2,8:F2},{3,7:F2}) 0x{4,-4:X} 0x{5,-4:X} {6,9:F2} {7,10} {8,6} {9,6:F2} {10,6}" -f `
            $stamp, $id, $x, $y, $anim, $twinAnim, $rate, $moveState, $runFlag, $speed, $moving
        $line

        $rows += [PSCustomObject]@{
            Time = $stamp; ObjectId = ('0x{0:X}' -f $id); X = $x; Y = $y
            Anim = ('0x{0:X}' -f $anim); Twin = ('0x{0:X}' -f $twinAnim); Rate = $rate
            MoveState = $moveState; RunFlag = $runFlag; Speed = $speed; Moving = $moving
        }
    }
    Start-Sleep -Milliseconds $IntervalMs
}

[PG]::CloseHandle($h) | Out-Null

if ($Csv -ne '') {
    $rows | Export-Csv -Path $Csv -NoTypeInformation
    "wrote $($rows.Count) rows to $Csv"
}

# Quick verdict: any sample where the body was genuinely covering ground (speed above the
# walk floor) but rendered idle is exactly the reported defect.
$sliding = $rows | Where-Object { $_.Speed -gt 0.3 -and $_.Twin -eq '0x2710' }
if ($sliding.Count -gt 0) {
    ""
    "SLIDE DETECTED: $($sliding.Count) sample(s) moving at >0.3 u/s while the twin's animation read idle (0x2710)."
    $sliding | Select-Object -First 5 | Format-Table
} else {
    ""
    "no idle-while-moving samples observed."
}
