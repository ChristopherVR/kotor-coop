# Reads the in-process plugin's shared block out of a running KOTOR.
#
# The frame counter is the useful number: it only advances when the game's main loop
# reaches SwapBuffers, so comparing it across a couple of seconds answers "is this
# instance still running" for an unfocused or minimised window, which the pause bitmask
# alone does not.
param(
    [string]$ProcessName = 'swkotor',
    [int]$ProcId = 0,
    [int]$Samples = 2,
    [int]$IntervalMs = 2000
)

Add-Type @'
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public class PB {
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
  // "KOCOOP01" + version + guard 0xC0FFEE01 is the plugin's 16-byte signature.
  public static long FindBlock(IntPtr h, byte[] sig){
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
            // bytes 8-11 are the block version, which bumps as the layout grows; this
            // diagnostic reads fields that have never moved, so any version matches.
            for(int j = 0; j < sig.Length; j++){
              if(j >= 8 && j < 12) continue;
              if(buf[i+j] != sig[j]) { hit = false; break; }
            }
            if(hit) return (long)m.BaseAddress + i;
          }
      }
      long next = (long)m.BaseAddress + size; if(next <= addr) break; addr = next;
    }
    return 0; }
}
'@

$proc = if ($ProcId -ne 0) { Get-Process -Id $ProcId -ErrorAction SilentlyContinue }
        else { Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $proc) { Write-Error 'target process not running'; exit 1 }

$h = [PB]::Open($proc.Id)

# magic "KOCOOP01", any version (bytes 8-11 are ignored by FindBlock), guard 0xC0FFEE01
$sig = [byte[]](0x4B,0x4F,0x43,0x4F,0x4F,0x50,0x30,0x31, 0,0,0,0, 0x01,0xEE,0xFF,0xC0)
$block = [PB]::FindBlock($h, $sig)
if ($block -eq 0) { Write-Error 'plugin shared block not found (is dinput8.dll installed?)'; exit 1 }

"pid $($proc.Id)  shared block at 0x{0:X}" -f $block

function U32($off) {
    $b = [PB]::Read($h, $block + $off, 4)
    if ($null -eq $b) { return -1 }
    [BitConverter]::ToUInt32($b, 0)
}

$prevFrames = -1
for ($i = 0; $i -lt $Samples; $i++) {
    $frames    = U32 0x250
    $heartbeat = U32 0x4C
    $phantoms  = U32 0x25C
    $cleared   = U32 0x26C

    $rate = if ($prevFrames -ge 0) {
        "{0,8:N0} fps" -f (($frames - $prevFrames) / ($IntervalMs / 1000.0))
    } else { '         -' }

    "  {0}  frames {1,12:N0} {2}  heartbeat {3,8}  phantoms {4}  pauses-cleared {5}" -f `
        (Get-Date -Format 'HH:mm:ss'), $frames, $rate, $heartbeat, $phantoms, $cleared

    $prevFrames = $frames
    if ($i -lt $Samples - 1) { Start-Sleep -Milliseconds $IntervalMs }
}

[PB]::CloseHandle($h) | Out-Null
