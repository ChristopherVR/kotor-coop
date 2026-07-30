# Enumerates CSWSCreature objects in the live game using the offsets from the public
# Ghidra address database (LaneDibello), verified against this exact binary by sha256.
param(
    [string]$ProcessName = 'swkotor',
    [int]$ProcId = 0,
    [uint32]$CreatureVtable = 0x007470C8
)

Add-Type @'
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public class M {
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
  // Sweep private committed memory for a 4-byte value (the vtable pointer).
  public static List<long> Scan(IntPtr h, uint needle){
    var hits = new List<long>();
    long addr = 0; int mbiSize = Marshal.SizeOf(typeof(MBI));
    while(addr < 0x7FFFFFFF){
      MBI m;
      if(VirtualQueryEx(h, new IntPtr(addr), out m, mbiSize) == 0) break;
      long size = (long)m.RegionSize; if(size <= 0) break;
      // Exclude PAGE_NOACCESS (0x01) and PAGE_GUARD (0x100) only; require some readable bit.
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

$p = if ($ProcId -ne 0) { Get-Process -Id $ProcId } else { Get-Process -Name $ProcessName | Select-Object -First 1 }
$h = [M]::Open($p.Id)

# Offsets straight from the address database (class CSWSObject, embedded at CSWSCreature+0).
$OFF_TAG = 0x18; $OFF_AREA = 0x8C; $OFF_POS = 0x90; $OFF_ORI = 0x9C
$OFF_ANIM = 0xD4; $OFF_HP = 0xE0; $OFF_ID = 0x04; $OFF_TYPE = 0x08
$OFF_STATS = 0xA74   # CSWSCreature.creature_stats -> CSWSCreatureStats*
$STATS_STR = 0xE9; $STATS_XP = 0x68; $STATS_ISPC = 0x6C; $STATS_FACTION = 0x78

function ReadExoString($base, $off) {
    $b = [M]::Read($h, $base + $off, 8); if(-not $b){ return '' }
    $ptr = [BitConverter]::ToUInt32($b, 0); $len = [BitConverter]::ToUInt32($b, 4)
    if($ptr -eq 0 -or $len -eq 0 -or $len -gt 64){ return '' }
    $s = [M]::Read($h, $ptr, [int]$len); if(-not $s){ return '' }
    return [System.Text.Encoding]::ASCII.GetString($s)
}

$hits = [M]::Scan($h, $CreatureVtable)
"found $($hits.Count) CSWSCreature object(s)"
""
"{0,-12} {1,-10} {2,-16} {3,-28} {4,6} {5,5} {6,7} {7,4}" -f 'object','id','tag','position','hp','str','xp','pc'
"-" * 104

foreach($c in $hits){
    $id   = [BitConverter]::ToUInt32([M]::Read($h,$c+$OFF_ID,4),0)
    $tag  = ReadExoString $c $OFF_TAG
    $pos  = [M]::Read($h, $c+$OFF_POS, 12)
    $hp   = [BitConverter]::ToInt32([M]::Read($h,$c+$OFF_HP,4),0)
    $x=[BitConverter]::ToSingle($pos,0); $y=[BitConverter]::ToSingle($pos,4); $z=[BitConverter]::ToSingle($pos,8)

    $statsPtr = [BitConverter]::ToUInt32([M]::Read($h,$c+$OFF_STATS,4),0)
    $str=0; $xp=0; $isPc=0
    if($statsPtr -gt 0x10000){
        $sb = [M]::Read($h, $statsPtr+$STATS_STR, 1); if($sb){ $str = $sb[0] }
        $xb = [M]::Read($h, $statsPtr+$STATS_XP, 4);  if($xb){ $xp = [BitConverter]::ToUInt32($xb,0) }
        $pb = [M]::Read($h, $statsPtr+$STATS_ISPC,4); if($pb){ $isPc = [BitConverter]::ToInt32($pb,0) }
    }

    "{0,-12} {1,-10} {2,-16} ({3,7:F2},{4,7:F2},{5,6:F2}) {6,6} {7,5} {8,7} {9,4}" -f `
        ("0x" + $c.ToString("X")), ("0x" + $id.ToString("X")), $tag, $x, $y, $z, $hp, $str, $xp, $isPc
}

[M]::CloseHandle($h) | Out-Null
