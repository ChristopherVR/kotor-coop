param(
    [long]$Address = 587622916,
    [float]$DeltaX = 6.0,
    [float]$DeltaY = 0.0,
    [int]$HoldSeconds = 3,
    [string]$ProcessName = 'swkotor'
)

Add-Type @'
using System; using System.Runtime.InteropServices;
public class W {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int w);
  // PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION
  public static IntPtr Open(int pid){ return OpenProcess(0x0438, false, (uint)pid); }
  public static float[] Read(IntPtr h, IntPtr a){
    byte[] b = new byte[12]; int r;
    if(!ReadProcessMemory(h,a,b,12,out r) || r!=12) return null;
    return new float[]{ BitConverter.ToSingle(b,0), BitConverter.ToSingle(b,4), BitConverter.ToSingle(b,8) }; }
  public static bool Write(IntPtr h, IntPtr a, float x, float y, float z){
    byte[] b = new byte[12];
    Buffer.BlockCopy(BitConverter.GetBytes(x),0,b,0,4);
    Buffer.BlockCopy(BitConverter.GetBytes(y),0,b,4,4);
    Buffer.BlockCopy(BitConverter.GetBytes(z),0,b,8,4);
    int w; return WriteProcessMemory(h,a,b,12,out w) && w==12; }
}
'@

$p = Get-Process -Name $ProcessName | Select-Object -First 1
$h = [W]::Open($p.Id)
$addr = [IntPtr]::new($Address)

$orig = [W]::Read($h, $addr)
if ($null -eq $orig) { "READ FAILED"; exit 1 }
"original : ({0:F2}, {1:F2}, {2:F2})" -f $orig[0], $orig[1], $orig[2]

$tx = $orig[0] + $DeltaX
$ty = $orig[1] + $DeltaY
$ok = [W]::Write($h, $addr, $tx, $ty, $orig[2])
"write    : ({0:F2}, {1:F2}, {2:F2})  ok={3}" -f $tx, $ty, $orig[2], $ok

# Hold the value against the engine, which rewrites position every frame from its own
# simulation. A single write is usually reverted before the next frame is drawn.
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$reverts = 0
while ($sw.Elapsed.TotalSeconds -lt $HoldSeconds) {
    $now = [W]::Read($h, $addr)
    if ($null -ne $now -and [math]::Abs($now[0] - $tx) -gt 0.01) { $reverts++ }
    [W]::Write($h, $addr, $tx, $ty, $orig[2]) | Out-Null
    Start-Sleep -Milliseconds 8
}

$after = [W]::Read($h, $addr)
"after    : ({0:F2}, {1:F2}, {2:F2})" -f $after[0], $after[1], $after[2]
"engine overwrote our value $reverts time(s) during $HoldSeconds s of holding"

[W]::CloseHandle($h) | Out-Null
