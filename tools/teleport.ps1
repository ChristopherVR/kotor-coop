# Moves a KOTOR instance's player to a world position by writing CSWSObject.position.
#
# For testing only. A write to +0x90 sticks because nothing else is driving the player
# (measured: 0 engine overwrites in 5s for an idle player, versus ~64/s for a party member
# running follow-AI), and it saves several minutes of steering a character across a module
# with injected keystrokes every time an encounter needs reaching.
param(
    [Parameter(Mandatory = $true)][int]$ProcId,
    [Parameter(Mandatory = $true)][float]$X,
    [Parameter(Mandatory = $true)][float]$Y,
    [float]$Z = [float]::NaN
)

Add-Type @'
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public class T2 {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int w);
  [DllImport("kernel32.dll")] public static extern int VirtualQueryEx(IntPtr h, IntPtr a, out MBI m, int l);
  [StructLayout(LayoutKind.Sequential)] public struct MBI {
    public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect, __a1;
    public UIntPtr RegionSize; public uint State, Protect, Type, __a2; }
  public static IntPtr Open(int pid){ return OpenProcess(0x0438, false, (uint)pid); }
  public static byte[] Read(IntPtr h, long a, int n){
    byte[] b = new byte[n]; int r;
    if(!ReadProcessMemory(h, new IntPtr(a), b, n, out r) || r != n) return null;
    return b; }
  public static bool Write(IntPtr h, long a, byte[] b){ int w;
    return WriteProcessMemory(h, new IntPtr(a), b, b.Length, out w) && w == b.Length; }
  public static List<long> Scan(IntPtr h, uint needle){
    var hits = new List<long>(); long addr = 0; int sz = Marshal.SizeOf(typeof(MBI));
    while(addr < 0x7FFFFFFF){
      MBI m; if(VirtualQueryEx(h, new IntPtr(addr), out m, sz) == 0) break;
      long size = (long)m.RegionSize; if(size <= 0) break;
      bool ok = m.State == 0x1000 && m.Type == 0x20000 && (m.Protect & 0x101) == 0
             && (m.Protect & (0x02|0x04|0x08|0x20|0x40|0x80)) != 0;
      if(ok && size <= 64L*1024*1024){
        byte[] buf = Read(h, (long)m.BaseAddress, (int)size);
        if(buf != null) for(int i = 0; i + 4 <= buf.Length; i += 4)
          if(BitConverter.ToUInt32(buf, i) == needle) hits.Add((long)m.BaseAddress + i);
      }
      long next = (long)m.BaseAddress + size; if(next <= addr) break; addr = next;
    }
    return hits; }
}
'@

$h = [T2]::Open($ProcId)
if ($h -eq [IntPtr]::Zero) { Write-Error "could not open pid $ProcId for writing"; exit 1 }

$player = 0
foreach ($c in [T2]::Scan($h, 0x007470C8)) {
    $sp = [BitConverter]::ToUInt32([T2]::Read($h, $c + 0xA74, 4), 0)
    if ($sp -lt 0x10000) { continue }
    $isPc = [BitConverter]::ToInt32([T2]::Read($h, $sp + 0x6C, 4), 0)
    if ($isPc -ne 0) { $player = $c; break }
}

if ($player -eq 0) { Write-Error 'no player creature found'; exit 1 }

$cur = [T2]::Read($h, $player + 0x90, 12)
$oz = [BitConverter]::ToSingle($cur, 8)
if ([float]::IsNaN($Z)) { $Z = $oz }

$buf = [byte[]]::new(12)
[BitConverter]::GetBytes([float]$X).CopyTo($buf, 0)
[BitConverter]::GetBytes([float]$Y).CopyTo($buf, 4)
[BitConverter]::GetBytes([float]$Z).CopyTo($buf, 8)

if ([T2]::Write($h, $player + 0x90, $buf)) {
    "pid {0} player 0x{1:X}: ({2:F2},{3:F2},{4:F2}) -> ({5:F2},{6:F2},{7:F2})" -f `
        $ProcId, $player, [BitConverter]::ToSingle($cur,0), [BitConverter]::ToSingle($cur,4), $oz, $X, $Y, $Z
} else {
    Write-Error 'position write failed'
}

[T2]::CloseHandle($h) | Out-Null
