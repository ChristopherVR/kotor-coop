# Runs the co-op verification battery against a live two-instance session and prints a
# PASS/FAIL line per feature. Assumes tools\session.ps1 has both instances in-world with
# both agents connected.
#
#   pwsh tools\verify.ps1 -Host1 <pid> -Client2 <pid>
#
# Every check reads or drives real engine state; nothing is inferred from the agent's own
# bookkeeping, because the agent believing a thing is not the thing being true.
param(
    [Parameter(Mandatory)] [int]$Host1,
    [Parameter(Mandatory)] [int]$Client2,
    [string]$Log1 = 'C:\GOG Games\Star Wars KOTOR (GOG)\kotorcoop-plugin.log',
    [string]$Log2 = 'D:\Games\swkotor-gog-p2\kotorcoop-plugin.log'
)

Add-Type @'
using System; using System.Runtime.InteropServices;
public class VF {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int w);
  [DllImport("kernel32.dll")] public static extern int VirtualQueryEx(IntPtr h, IntPtr a, out MBI m, int l);
  [StructLayout(LayoutKind.Sequential)] public struct MBI { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect,__a1; public UIntPtr RegionSize; public uint State,Protect,Type,__a2; }
  public static byte[] R(IntPtr h, long a, int n){ byte[] b=new byte[n]; int r; if(!ReadProcessMemory(h,new IntPtr(a),b,n,out r)||r!=n) return null; return b; }
  public static uint U32(IntPtr h, long a){ var b=R(h,a,4); return b==null?0:BitConverter.ToUInt32(b,0); }
  public static int I16(IntPtr h, long a){ var b=R(h,a,2); return b==null?0:BitConverter.ToInt16(b,0); }
  public static byte U8(IntPtr h, long a){ var b=R(h,a,1); return b==null?(byte)0:b[0]; }
  public static bool W32(IntPtr h, long a, int v){ int w; return WriteProcessMemory(h,new IntPtr(a),BitConverter.GetBytes(v),4,out w); }
  public static bool W16(IntPtr h, long a, short v){ int w; return WriteProcessMemory(h,new IntPtr(a),BitConverter.GetBytes(v),2,out w); }
  public static string Str(IntPtr h, long a, int n){ var b=R(h,a,n); if(b==null) return ""; int e=Array.IndexOf(b,(byte)0); if(e<0)e=n; return System.Text.Encoding.ASCII.GetString(b,0,e); }
  // Every creature is a 4-byte CSWSCreature vtable; the object id follows it.
  public static long FindCreature(IntPtr h, uint objId){
    long addr=0; int sz=Marshal.SizeOf(typeof(MBI));
    while(addr<0x7FFFFFFF){ MBI m; if(VirtualQueryEx(h,new IntPtr(addr),out m,sz)==0) break;
      long size=(long)m.RegionSize; if(size<=0) break;
      if(m.State==0x1000&&m.Type==0x20000&&(m.Protect&0x101)==0&&size<=64L*1024*1024){
        var buf=R(h,(long)m.BaseAddress,(int)size);
        if(buf!=null) for(int i=0;i+8<=buf.Length;i+=4)
          if(BitConverter.ToUInt32(buf,i)==0x007470C8 && BitConverter.ToUInt32(buf,i+4)==objId)
            return (long)m.BaseAddress+i; }
      long next=(long)m.BaseAddress+size; if(next<=addr) break; addr=next; }
    return 0; }
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

$results = [ordered]@{}
function Check($name, $ok, $detail) {
    $results[$name] = [pscustomobject]@{ Pass = $ok; Detail = $detail }
    "{0,-34} {1}  {2}" -f $name, $(if ($ok) { 'PASS' } else { 'FAIL' }), $detail
}
function Tail($path, $n = 40) { if (Test-Path $path) { Get-Content $path -Tail $n } else { @() } }

$h1 = [VF]::OpenProcess(0x0438, $false, $Host1)
$h2 = [VF]::OpenProcess(0x0438, $false, $Client2)
$b1 = [VF]::FindBlock($h1)
$b2 = [VF]::FindBlock($h2)

"host  pid $Host1  block 0x{0:X}  version {1}" -f $b1, [VF]::U32($h1, $b1 + 8)
"client pid $Client2  block 0x{0:X}  version {1}" -f $b2, [VF]::U32($h2, $b2 + 8)
''

# --- 1. phantoms exist both ways ---------------------------------------------------
$ph1 = [VF]::U32($h1, $b1 + 0x264)   # first phantom object id, host side
$ph2 = [VF]::U32($h2, $b2 + 0x264)
Check 'phantoms spawned both ways' (($ph1 -ne 0) -and ($ph2 -ne 0)) ("host sees 0x{0:X}, client sees 0x{1:X}" -f $ph1, $ph2)

# --- 2. phantom ownership shape (7.4 redesign) -------------------------------------
$c1 = if ($ph1) { [VF]::FindCreature($h1, $ph1) } else { 0 }
if ($c1) {
    $plot = [VF]::U32($h1, $c1 + 0xF8); $min1 = [VF]::U32($h1, $c1 + 0x200)
    $move = [VF]::U32($h1, $c1 + 0x9F0); $appr = [VF]::U32($h1, $c1 + 0xA60)
    Check 'phantom damageable, not a sink' (($plot -eq 0) -and ($min1 -eq 1) -and (($move -band 4) -eq 0)) `
        ("plot=$plot min1hp=$min1 movebits=0x{0:X} appearance=$appr" -f $move)
} else { Check 'phantom damageable, not a sink' $false 'phantom creature not found' }

# --- 3. appearance replication ------------------------------------------------------
if ($c1) { Check 'phantom wears the peer appearance' ([VF]::U32($h1, $c1 + 0xA60) -gt 20) ("row $([VF]::U32($h1,$c1+0xA60))") }

# --- 4. shared pause (client + server halves) ---------------------------------------
function PauseMask($h) {
    $am = [VF]::U32($h, 0x7A39FC)
    $srv = [VF]::U32($h, $am + 8); $srvInt = if ($srv) { [VF]::U32($h, $srv + 4) } else { 0 }
    $cli = [VF]::U32($h, $am + 4); $cliInt = if ($cli) { [VF]::U32($h, $cli + 4) } else { 0 }
    $s = if ($srvInt) { [VF]::U8($h, $srvInt + 0x10078) } else { 0 }
    $c = if ($cliInt) { [VF]::U8($h, $cliInt + 0x1CC) } else { 0 }
    return @{ Server = $s; Client = $c; Union = ($s -bor $c) }
}
[VF]::W32($h2, $b2 + 0x96C, 1) | Out-Null     # tell the client instance a peer is paused
Start-Sleep -Seconds 3
$pm = PauseMask $h2
Check 'shared pause holds both halves' (($pm.Union -band 1) -ne 0) `
    ("client mask=0x{0:X} server mask=0x{1:X}" -f $pm.Client, $pm.Server)
[VF]::W32($h2, $b2 + 0x96C, 0) | Out-Null
Start-Sleep -Seconds 3
$pm2 = PauseMask $h2
Check 'pause releases when the peer does' (($pm2.Union -band 1) -eq 0) `
    ("client mask=0x{0:X} server mask=0x{1:X}" -f $pm2.Client, $pm2.Server)

# --- 5. journal apply ----------------------------------------------------------------
$before = ([VF]::U32($h2, $b2 + 0x99C))
$tag = 'k_swg_bastila'
$bytes = [byte[]]::new(32)
[Array]::Copy([Text.Encoding]::ASCII.GetBytes($tag), $bytes, $tag.Length)
[VF]::W32($h2, $b2 + 0x998, 20) | Out-Null

# The tag goes in a dword at a time; no unmanaged buffer needed for 32 bytes.
for ($i = 0; $i -lt 32; $i += 4) { [VF]::W32($h2, $b2 + 0x978 + $i, [BitConverter]::ToInt32($bytes, $i)) | Out-Null }
[VF]::W32($h2, $b2 + 0x974, [int]([VF]::U32($h2, $b2 + 0x974) + 1)) | Out-Null
Start-Sleep -Seconds 3
$after = [VF]::U32($h2, $b2 + 0x99C)
$status = [VF]::U32($h2, $b2 + 0x9A0)
Check 'journal request serviced' (($after -ne $before) -and ($status -eq 0)) `
    "applied serial $before -> $after, status $status"
Check 'journal logged by the plugin' ((Tail $Log2 | Where-Object { $_ -match 'journal:' }).Count -gt 0) `
    (((Tail $Log2 | Where-Object { $_ -match 'journal:' }) | Select-Object -Last 1) ?? 'no journal line')

''
'summary: {0} passed, {1} failed' -f ($results.Values | Where-Object Pass).Count, ($results.Values | Where-Object { -not $_.Pass }).Count
[VF]::CloseHandle($h1) | Out-Null
[VF]::CloseHandle($h2) | Out-Null
