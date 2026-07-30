param(
    [Parameter(Mandatory = $true)][string[]]$Candidates,
    [int]$PositionDelta = -0x174,
    [string]$ProcessName = 'swkotor'
)

Add-Type @'
using System; using System.Runtime.InteropServices;
public class M {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  public static IntPtr Open(int pid){ return OpenProcess(0x0410, false, (uint)pid); }
  public static float[] Vec(IntPtr h, IntPtr a){
    byte[] b = new byte[12]; int r;
    if(!ReadProcessMemory(h,a,b,12,out r) || r!=12) return null;
    return new float[]{ BitConverter.ToSingle(b,0), BitConverter.ToSingle(b,4), BitConverter.ToSingle(b,8) }; }
}
'@

$p = Get-Process -Name $ProcessName | Select-Object -First 1
$h = [M]::Open($p.Id)

foreach ($c in $Candidates) {
    $base = [Convert]::ToInt64($c.TrimStart('0', 'x', 'X'), 16)
    if ($c -match '^0x') { $base = [Convert]::ToInt64($c.Substring(2), 16) }
    $addr = [IntPtr]::new($base + $PositionDelta)
    $v = [M]::Vec($h, $addr)

    if ($null -eq $v) { continue }

    $finite = [float]::IsFinite($v[0]) -and [float]::IsFinite($v[1]) -and [float]::IsFinite($v[2])
    if (-not $finite) { continue }

    # Plausible world coordinates for a Taris interior module.
    $plausible = ([math]::Abs($v[0]) -gt 1 -and [math]::Abs($v[0]) -lt 1000) -and
                 ([math]::Abs($v[1]) -gt 1 -and [math]::Abs($v[1]) -lt 1000) -and
                 ([math]::Abs($v[2]) -lt 100)

    if ($plausible) {
        "{0}  position 0x{1:X}  ({2,8:F2}, {3,8:F2}, {4,7:F2})" -f $c, ($base + $PositionDelta), $v[0], $v[1], $v[2]
    }
}

[M]::CloseHandle($h) | Out-Null
