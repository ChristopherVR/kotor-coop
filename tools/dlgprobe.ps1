# Measures a SHARED conversation end to end, against a live two-instance session.
#
#   pwsh tools\dlgprobe.ps1 [-Tag end_trask] [-Seconds 45]
#
# The question this exists to answer is not "did a conversation open" — the plugin log has
# always said that — but "did ONE conversation run, and how long did the follower's copy take
# to deliver a line". Both were guessed at before: the follower's content watchdog was set to
# a number nobody had measured, and a conversation that opened and closed repeatedly looked
# identical, from the outside, to one that stayed open.
#
# So it samples the two dialogue serials over time. A serial that climbs steadily is churn —
# each open/close pair bumps it twice. A serial that settles is one conversation holding.
param(
    [string]$Tag = 'end_trask',
    [int]$Seconds = 45,
    [string]$Log1 = 'C:\GOG Games\Star Wars KOTOR (GOG)\kotorcoop-plugin.log',
    [string]$Log2 = 'D:\Games\swkotor-gog-p2\kotorcoop-plugin.log'
)

$ErrorActionPreference = 'Stop'

$found = @(Get-Process swkotor -ErrorAction SilentlyContinue | Sort-Object StartTime)
if ($found.Count -lt 2) { throw "expected 2 swkotor processes, found $($found.Count)" }

Add-Type @'
using System; using System.Runtime.InteropServices;
public class DP {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int w);
  [DllImport("kernel32.dll")] public static extern int VirtualQueryEx(IntPtr h, IntPtr a, out MBI m, int l);
  [StructLayout(LayoutKind.Sequential)] public struct MBI { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect,__a1; public UIntPtr RegionSize; public uint State,Protect,Type,__a2; }
  public static byte[] R(IntPtr h, long a, int n){ byte[] b=new byte[n]; int r; if(!ReadProcessMemory(h,new IntPtr(a),b,n,out r)||r!=n) return null; return b; }
  public static uint U32(IntPtr h, long a){ var b=R(h,a,4); return b==null?0:BitConverter.ToUInt32(b,0); }
  public static bool W32(IntPtr h, long a, int v){ int w; return WriteProcessMemory(h,new IntPtr(a),BitConverter.GetBytes(v),4,out w); }
  public static string Str(IntPtr h, long a, int n){ var b=R(h,a,n); if(b==null) return ""; int e=Array.IndexOf(b,(byte)0); if(e<0)e=n; return System.Text.Encoding.ASCII.GetString(b,0,e); }
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

$h1 = [DP]::OpenProcess(0x1F0FFF, $false, $found[0].Id)
$h2 = [DP]::OpenProcess(0x1F0FFF, $false, $found[1].Id)
$b1 = [DP]::FindBlock($h1)
$b2 = [DP]::FindBlock($h2)
if ($b1 -eq 0 -or $b2 -eq 0) { throw "plugin shared block not found (host $b1, client $b2)" }

# Block fields (SharedBlock.cs).
$OffLocalSerial   = 0xCE8
$OffLocalPartner  = 0x1200
$OffRemoteActive  = 0x1228
$OffRemotePartner = 0x1240
$OffTestTag       = 0x1D70   # test hook: tag to open a conversation with
$OffTestSerial    = 0x1D90   # ... bumped to fire it

function LineCount($p) { if (Test-Path $p) { @(Get-Content $p).Count } else { 0 } }
function NewLines($p, $since) {
    if (-not (Test-Path $p)) { return @() }
    $all = @(Get-Content $p)
    if ($all.Count -le $since) { return @() }
    return $all[$since..($all.Count - 1)]
}

$mark1 = LineCount $Log1
$mark2 = LineCount $Log2

"host pid $($found[0].Id) block 0x{0:X}   client pid $($found[1].Id) block 0x{1:X}" -f $b1, $b2
"opening a conversation with '$Tag' on the HOST ..."

$tagBytes = [byte[]]::new(32)
[Array]::Copy([Text.Encoding]::ASCII.GetBytes($Tag), $tagBytes, $Tag.Length)
for ($i = 0; $i -lt 32; $i += 4) {
    [DP]::W32($h1, $b1 + $OffTestTag + $i, [BitConverter]::ToInt32($tagBytes, $i)) | Out-Null
}
[DP]::W32($h1, $b1 + $OffTestSerial, [int]([DP]::U32($h1, $b1 + $OffTestSerial) + 1)) | Out-Null

''
'  t     host#  client#   remoteActive  hostPartner   clientPartner'
$samples = @()
for ($t = 0; $t -lt $Seconds; $t++) {
    Start-Sleep -Seconds 1
    $s1 = [DP]::U32($h1, $b1 + $OffLocalSerial)
    $s2 = [DP]::U32($h2, $b2 + $OffLocalSerial)
    $ra = [DP]::U32($h2, $b2 + $OffRemoteActive)
    $p1 = [DP]::Str($h1, $b1 + $OffLocalPartner, 32)
    $p2 = [DP]::Str($h2, $b2 + $OffLocalPartner, 32)
    $samples += [pscustomobject]@{ T = $t; H = $s1; C = $s2 }
    '{0,3}   {1,6}  {2,7}   {3,12}  {4,-12}  {5}' -f $t, $s1, $s2, $ra, $p1, $p2
}

''
'--- new plugin log lines, HOST ---'
NewLines $Log1 $mark1 | Where-Object { $_ -match 'dialogue|conversation' }
''
'--- new plugin log lines, CLIENT ---'
NewLines $Log2 $mark2 | Where-Object { $_ -match 'dialogue|conversation' }

''
# The verdict. Over a held conversation the serials should move a handful of times (real
# line changes) and then settle — NOT climb once every second or two, which is the signature
# of a conversation being reopened.
$hostDelta   = $samples[-1].H - $samples[0].H
$clientDelta = $samples[-1].C - $samples[0].C
"serial movement over $Seconds s: host +$hostDelta, client +$clientDelta"
if ($hostDelta -gt ($Seconds / 3) -or $clientDelta -gt ($Seconds / 3)) {
    'VERDICT: still churning — the serials climb faster than lines could plausibly change.'
} else {
    'VERDICT: serials settled — consistent with ONE conversation holding open.'
}

[DP]::CloseHandle($h1) | Out-Null
[DP]::CloseHandle($h2) | Out-Null
