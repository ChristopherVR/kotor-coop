# Runs a bark end to end across a live two-instance session and says whether it arrived.
#
#   pwsh tools\barkprobe.ps1 [-Tag end_weld] [-Seconds 8]
#
# A bark is the one-line bubble an NPC throws up — the utility droid's beep — and it never
# touches the dialogue system, so none of the dialogue checks can see it. It also cannot be
# provoked from outside the game: barks come from scripts and AI actions the starting area
# fires on triggers automation cannot reach, and clicking a droid opens a conversation
# instead. So the plugin carries a test hook that barks through the engine's own BarkString
# WITHOUT the replay guard, and this fires it and reads both logs.
#
# The question it answers is the one that went unanswered while the channel was broken: not
# "was a bark captured" but "did the OTHER instance say the line".
param(
    [string]$Tag = 'end_weld',
    [int]$Seconds = 8,
    [string]$Log1 = 'C:\GOG Games\Star Wars KOTOR (GOG)\kotorcoop-bark.log',
    [string]$Log2 = 'D:\Games\swkotor-gog-p2\kotorcoop-bark.log',
    # The test hook's own line is a 'test:' one, so the log splitter files it under general
    # rather than bark. Read both, or the host looks silent while it is doing its job.
    [string]$Gen1 = 'C:\GOG Games\Star Wars KOTOR (GOG)\kotorcoop-general.log'
)

$ErrorActionPreference = 'Stop'

$found = @(Get-Process swkotor -ErrorAction SilentlyContinue | Sort-Object StartTime)
if ($found.Count -lt 2) { throw "expected 2 swkotor processes, found $($found.Count)" }

Add-Type @'
using System; using System.Runtime.InteropServices;
public class BP {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int w);
  [DllImport("kernel32.dll")] public static extern int VirtualQueryEx(IntPtr h, IntPtr a, out MBI m, int l);
  [StructLayout(LayoutKind.Sequential)] public struct MBI { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect,__a1; public UIntPtr RegionSize; public uint State,Protect,Type,__a2; }
  public static byte[] R(IntPtr h, long a, int n){ byte[] b=new byte[n]; int r; if(!ReadProcessMemory(h,new IntPtr(a),b,n,out r)||r!=n) return null; return b; }
  public static uint U32(IntPtr h, long a){ var b=R(h,a,4); return b==null?0:BitConverter.ToUInt32(b,0); }
  public static bool W32(IntPtr h, long a, int v){ int w; return WriteProcessMemory(h,new IntPtr(a),BitConverter.GetBytes(v),4,out w); }
  public static long FindBlock(IntPtr h){
    byte[] sig = { 0x4B,0x4F,0x43,0x4F,0x4F,0x50,0x30,0x31, 0,0,0,0, 0x01,0xEE,0xFF,0xC0 };
    long addr=0; int sz=Marshal.SizeOf(typeof(MBI));
    while(addr<0x7FFFFFFF){ MBI m; if(VirtualQueryEx(h,new IntPtr(addr),out m,sz)==0) break;
      long size=(long)m.RegionSize; if(size<=0) break;
      if(m.State==0x1000&&m.Type==0x20000&&(m.Protect&0x101)==0&&size<=64L*1024*1024){
        var buf=R(h,(long)m.BaseAddress,(int)size);
        if(buf!=null) for(int i=0;i+16<=buf.Length;i+=4){ bool hit=true;
          for(int j=0;j<16;j++){ if(j>=8&&j<12) continue; if(buf[i+j]!=sig[j]){hit=false;break;} }
          if(hit) return (long)m.BaseAddress+i; } }
      long next=(long)m.BaseAddress+size; if(next<=addr) break; addr=next; }
    return 0; }
}
'@

$h1 = [BP]::OpenProcess(0x1F0FFF, $false, $found[0].Id)
$h2 = [BP]::OpenProcess(0x1F0FFF, $false, $found[1].Id)
$b1 = [BP]::FindBlock($h1)
$b2 = [BP]::FindBlock($h2)
if ($b1 -eq 0 -or $b2 -eq 0) { throw "plugin shared block not found (host $b1, client $b2)" }

# SharedBlock.cs, version 32.
$OffTestBarkTag    = 0x1DB0
$OffTestBarkSerial = 0x1DD0

function LineCount($p) { if (Test-Path $p) { @(Get-Content $p).Count } else { 0 } }
function NewLines($p, $since) {
    if (-not (Test-Path $p)) { return @() }
    $all = @(Get-Content $p)
    if ($all.Count -le $since) { return @() }
    return $all[$since..($all.Count - 1)]
}

$mark1 = LineCount $Log1
$mark2 = LineCount $Log2
$markG = LineCount $Gen1

"host pid $($found[0].Id) block 0x{0:X}   client pid $($found[1].Id) block 0x{1:X}" -f $b1, $b2
"barking from '$Tag' on the HOST ..."

$tagBytes = [byte[]]::new(32)
[Array]::Copy([Text.Encoding]::ASCII.GetBytes($Tag), $tagBytes, $Tag.Length)
for ($i = 0; $i -lt 32; $i += 4) {
    [BP]::W32($h1, $b1 + $OffTestBarkTag + $i, [BitConverter]::ToInt32($tagBytes, $i)) | Out-Null
}
[BP]::W32($h1, $b1 + $OffTestBarkSerial, [int]([BP]::U32($h1, $b1 + $OffTestBarkSerial) + 1)) | Out-Null

Start-Sleep -Seconds $Seconds

$new1 = @(NewLines $Log1 $mark1) + @(NewLines $Gen1 $markG | Where-Object { $_ -match 'bark' })
$new2 = NewLines $Log2 $mark2

''
'--- HOST ---'
$new1
''
'--- CLIENT ---'
$new2
''

# The verdict rests on the CLIENT, which is the half that was silent. "Captured" was never
# the problem — the host always captured something; it captured the wrong thing.
$spoke   = @($new1 | Where-Object { $_ -match 'barked a test line' }).Count -gt 0
$replayed = @($new2 | Where-Object { $_ -match 'replayed' }).Count -gt 0
$failed   = @($new2 | Where-Object { $_ -match 'could not replay' })

if (-not $spoke) {
    "VERDICT: the host never barked — nothing to carry (is '$Tag' in this module?)."
} elseif ($replayed) {
    'VERDICT: the bark reached the other instance and was spoken there.'
} elseif ($failed.Count -gt 0) {
    'VERDICT: it crossed the wire but the far side refused it:'
    $failed | ForEach-Object { "  $_" }
} else {
    'VERDICT: the host barked but the client logged nothing — it never crossed.'
}

[BP]::CloseHandle($h1) | Out-Null
[BP]::CloseHandle($h2) | Out-Null
