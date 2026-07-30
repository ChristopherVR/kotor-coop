# Answers "will menu automation work on this instance?" BEFORE anything tries to click.
#
# KOTOR's menu input dies intermittently and undiagnosably (Handoff trap 25): the window
# is foreground, DirectInput is forwarding, clicks land on the right pixel, and nothing
# happens. Measured 2026-07-30, the game's own GUI hit-test is a perfect oracle for it:
#
#   write the game cursor (CClientExoAppInternal +0x3C8/+0x3CC), send a net-zero relative
#   mouse wiggle so the game re-hit-tests, then read the control under the cursor at
#   CSWGuiManager +0x08 ([0x7A39F4]).
#
#   hover != 0  ->  every subsequent load attempt succeeded on the first try
#   hover == 0  ->  every subsequent load attempt failed, activation cycles included
#
# So: probe first, and RELAUNCH a dead instance rather than clicking into the void.
# Exit code 0 = input alive, 1 = dead.
param(
    [Parameter(Mandatory)] [int]$ProcId,
    [int]$X = 484,
    [int]$Y = 323
)

Add-Type @'
using System; using System.Runtime.InteropServices;
public class IA {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int w);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; public int p1, p2; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] i, int size);
  public static uint U32(IntPtr h, long a){ byte[] b = new byte[4]; int r;
    if(!ReadProcessMemory(h, new IntPtr(a), b, 4, out r)) return 0; return BitConverter.ToUInt32(b, 0); }
  public static void W32(IntPtr h, long a, int v){ int w;
    WriteProcessMemory(h, new IntPtr(a), BitConverter.GetBytes(v), 4, out w); }
  // Net-zero relative move: generates the real input event the hit-test needs without
  // actually moving the game's cursor off the position just written.
  public static void Wiggle(){
    INPUT[] m = new INPUT[1]; m[0].type = 0; m[0].mi.dwFlags = 0x0001;
    int s = Marshal.SizeOf(typeof(INPUT));
    m[0].mi.dx = 3; SendInput(1, m, s); System.Threading.Thread.Sleep(40);
    m[0].mi.dx = -3; SendInput(1, m, s); System.Threading.Thread.Sleep(40); }
}
'@

$proc = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
if (-not $proc) { Write-Error "process $ProcId not running"; exit 1 }
if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
    [IA]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 400
}

$h = [IA]::OpenProcess(0x0438, $false, $ProcId)
if ($h -eq [IntPtr]::Zero) { Write-Error "cannot open pid $ProcId"; exit 1 }

# WAIT FOR THE MENU FIRST. The window exists many seconds before the main menu does (the
# logo movies play in between), and probing during them reports hover 0 for the entirely
# innocent reason that there are no controls yet — which reads exactly like dead input
# and gets a healthy instance relaunched forever. CSWGuiMainMenu at
# CClientExoAppInternal + 0x280 is null until the menu is really up.
$deadline = (Get-Date).AddSeconds(120)
do {
    $appMgr = [IA]::U32($h, 0x7A39FC)
    $client = if ($appMgr) { [IA]::U32($h, $appMgr + 4) } else { 0 }
    $internal = if ($client) { [IA]::U32($h, $client + 4) } else { 0 }
    $mainMenu = if ($internal) { [IA]::U32($h, $internal + 0x280) } else { 0 }
    $gui = [IA]::U32($h, 0x7A39F4)
    if ($mainMenu -and $gui) { break }
    Start-Sleep -Milliseconds 750
} while ((Get-Date) -lt $deadline)

if (-not $mainMenu -or -not $gui) {
    [IA]::CloseHandle($h) | Out-Null
    'input probe: the main menu never came up'
    exit 1
}

[IA]::W32($h, $internal + 0x3C8, $X)
[IA]::W32($h, $internal + 0x3CC, $Y)
[IA]::W32($h, $gui + 0, $X)
[IA]::W32($h, $gui + 4, $Y)
[IA]::Wiggle()
Start-Sleep -Milliseconds 150

$hover = [IA]::U32($h, $gui + 8)
[IA]::CloseHandle($h) | Out-Null

if ($hover -ne 0) { "input alive (hover 0x{0:X})" -f $hover; exit 0 }
'input DEAD (hover 0x0) - relaunch this instance rather than clicking'
exit 1
