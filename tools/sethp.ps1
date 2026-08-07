# Reads or writes a creature's current hit points by engine object id.
#
# For testing the co-op enemy replication without having to stage a real fight: damage an
# enemy on the authoritative instance and watch the mirrored copy follow. Current HP is
# +0xDC (u16). +0xE0 is the STORED MAX, which is 0 for the PC because the character
# sheet's max is computed from class levels rather than stored.
param(
    [Parameter(Mandatory = $true)][int]$ProcId,
    [Parameter(Mandatory = $true)][uint32]$ObjectId,
    [int]$Set = -1
)

Add-Type @'
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public class H {
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

$h = [H]::Open($ProcId)
if ($h -eq [IntPtr]::Zero) { Write-Error "cannot open pid $ProcId"; exit 1 }

$found = 0
foreach ($c in [H]::Scan($h, 0x007470C8)) {
    $id = [BitConverter]::ToUInt32([H]::Read($h, $c + 0x04, 4), 0)
    if ($id -eq $ObjectId) { $found = $c; break }
}

if ($found -eq 0) { Write-Error ("no creature with id 0x{0:X} in pid {1}" -f $ObjectId, $ProcId); exit 1 }

# SIGNED. The engine reads current HP as int16 and sign-extends it (movsx after the
# vtable +0x9C getter), and OnApplyDeath parks a dead body at -11 -- read unsigned, a
# downed creature reports 65525 and looks healthier than it has ever been.
$cur = [BitConverter]::ToInt16([H]::Read($h, $found + 0xDC, 2), 0)
$max = [BitConverter]::ToUInt16([H]::Read($h, $found + 0xE0, 2), 0)

# The two flags that stop a body dying, so "why is this thing stuck at 1 hp" is one command
# instead of an evening. min1hp (+0x200) floors damage at 1 and turns an applied Death
# effect into "set hp 1"; plot (+0xF8) zeroes the damage outright. KOTOR's own modules set
# min1hp on the PC -- the Endar Spire command deck holds it for the whole deck.
# See docs/research/min-one-hitpoint.md.
$min1 = [BitConverter]::ToInt32([H]::Read($h, $found + 0x200, 4), 0)
$plot = [BitConverter]::ToInt32([H]::Read($h, $found + 0xF8, 4), 0)
$flags = @()
if ($min1 -ne 0) { $flags += "min1hp" }
if ($plot -ne 0) { $flags += "plot" }
$flagText = if ($flags.Count -gt 0) { " [" + ($flags -join ",") + "]" } else { "" }

if ($Set -ge 0) {
    [H]::Write($h, $found + 0xDC, [BitConverter]::GetBytes([uint16]$Set)) | Out-Null
    $now = [BitConverter]::ToInt16([H]::Read($h, $found + 0xDC, 2), 0)
    "pid {0} creature 0x{1:X} @0x{2:X}: hp {3} -> {4} (stored max {5}){6}" -f $ProcId, $ObjectId, $found, $cur, $now, $max, $flagText
}
else {
    "pid {0} creature 0x{1:X} @0x{2:X}: hp {3} (stored max {4}){5}" -f $ProcId, $ObjectId, $found, $cur, $max, $flagText
}

[H]::CloseHandle($h) | Out-Null
