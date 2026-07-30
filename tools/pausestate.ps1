# Reads the server's pause bitmask out of a running KOTOR.
#
# CServerExoAppInternal + 0x10078 is a byte of pause REASONS, not a boolean:
# SetPauseState(mask, value) ors/clears the bit, so several things can hold the game
# paused at once. Bit 0x02 is the one the window-deactivation path sets, which is what
# makes an unfocused instance stop simulating.
#
#   [0x7A39FC] APP_MANAGER -> +0x08 CServerExoApp -> +0x04 CServerExoAppInternal -> +0x10078
param(
    [string]$ProcessName = 'swkotor',
    [int]$ProcId = 0,
    [int]$Samples = 1,
    [int]$IntervalMs = 1000
)

Add-Type @'
using System; using System.Runtime.InteropServices;
public class PS {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  public static IntPtr Open(int pid){ return OpenProcess(0x0410, false, (uint)pid); }
  public static byte[] Read(IntPtr h, long a, int n){
    byte[] b = new byte[n]; int r;
    if(!ReadProcessMemory(h, new IntPtr(a), b, n, out r) || r != n) return null;
    return b; }
  public static uint U32(IntPtr h, long a){
    byte[] b = Read(h, a, 4); return b == null ? 0 : BitConverter.ToUInt32(b, 0); }
}
'@

$proc = if ($ProcId -ne 0) { Get-Process -Id $ProcId -ErrorAction SilentlyContinue }
        else { Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $proc) { Write-Error 'target process not running'; exit 1 }

$h = [PS]::Open($proc.Id)

$appManager = [PS]::U32($h, 0x007A39FC)
if ($appManager -eq 0) { Write-Error 'app manager not initialised'; exit 1 }
$server = [PS]::U32($h, $appManager + 0x08)
if ($server -eq 0) { Write-Error 'server not running (still at the main menu?)'; exit 1 }
$internal = [PS]::U32($h, $server + 0x04)
$addr = $internal + 0x10078

"pid $($proc.Id)  pause byte at 0x{0:X}" -f $addr

# Only two bits are ever written by the engine. Bit 0x01 is Force Stasis / Time Stop (it
# carries an object exclusion list); bit 0x02 is the general gameplay pause used by the
# spacebar, every auto-pause, the GUI screens AND window focus loss. Bits 0x04 and above
# are never set and would be unsafe to set: the unpause path has no handler for them, so
# the world timer would stay frozen with no way back.
$reasons = @{ 1 = 'time-stop'; 2 = 'gameplay-pause (spacebar/autopause/GUI/focus-loss)' }

for ($i = 0; $i -lt $Samples; $i++) {
    # Compare against $null explicitly: a one-element byte array holding 0 is falsy in
    # PowerShell, so `if (-not $b)` reports a perfectly good read of an unpaused game as
    # a failure.
    $b = [PS]::Read($h, $addr, 1)
    if ($null -eq $b) { 'read failed'; break }
    $v = $b[0]
    $set = @()
    foreach ($bit in 1, 2, 4, 8, 16, 32, 64, 128) {
        if ($v -band $bit) { $set += ($reasons[$bit] ?? ("bit 0x{0:X}" -f $bit)) }
    }
    $desc = if ($set.Count -eq 0) { 'RUNNING' } else { 'PAUSED: ' + ($set -join ', ') }
    "  {0}  mask 0x{1:X2}  {2}" -f (Get-Date -Format 'HH:mm:ss'), $v, $desc
    if ($i -lt $Samples - 1) { Start-Sleep -Milliseconds $IntervalMs }
}

[PS]::CloseHandle($h) | Out-Null
