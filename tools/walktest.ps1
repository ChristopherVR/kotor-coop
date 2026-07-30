param([long]$Address = 587622916, [string]$ProcessName = 'swkotor')

Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public class WT {
  // --- window helpers -------------------------------------------------------
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

  public static int CloseDialogs() {
    List<IntPtr> hits = new List<IntPtr>();
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      if (!IsWindowVisible(h)) return true;
      StringBuilder t = new StringBuilder(256); GetWindowText(h, t, 256);
      string s = t.ToString();
      if (s.Contains("Security") || s.Contains("Firewall") || s.Contains("Defender")) hits.Add(h);
      return true;
    }, IntPtr.Zero);
    foreach (IntPtr h in hits) SendMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
    return hits.Count;
  }

  public static void Focus(IntPtr hwnd) {
    uint cur = GetCurrentThreadId(); uint pid;
    uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
    bool a = (fg != 0 && fg != cur) && AttachThreadInput(cur, fg, true);
    ShowWindow(hwnd, 9); BringWindowToTop(hwnd); SetForegroundWindow(hwnd);
    if (a) AttachThreadInput(cur, fg, false);
  }
  public static uint FgPid() { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return p; }

  // --- input ----------------------------------------------------------------
  [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk, scan; public uint flags, time; public IntPtr ex; }
  [StructLayout(LayoutKind.Sequential)] public struct MI { public int dx, dy; public uint data, flags, time; public IntPtr ex; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public MI mi; [FieldOffset(0)] public KI ki; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public U u; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] p, int cb);

  static void Key(ushort scan, bool up) {
    INPUT[] i = new INPUT[1]; i[0].type = 1; i[0].u.ki.scan = scan;
    i[0].u.ki.flags = (uint)(0x0008 | (up ? 0x0002 : 0));
    SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
  }
  public static void Hold(ushort scan, int ms) { Key(scan, false); System.Threading.Thread.Sleep(ms); Key(scan, true); }

  // --- memory ---------------------------------------------------------------
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool inh, uint pid);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int n, out int read);

  public static IntPtr Open(int pid) { return OpenProcess(0x0410, false, (uint)pid); }
  public static float[] ReadVec(IntPtr h, IntPtr addr) {
    byte[] b = new byte[12]; int r;
    if (!ReadProcessMemory(h, addr, b, 12, out r) || r != 12) return null;
    return new float[] { BitConverter.ToSingle(b,0), BitConverter.ToSingle(b,4), BitConverter.ToSingle(b,8) };
  }
}
'@

$closed = [WT]::CloseDialogs()
if ($closed -gt 0) { "dismissed $closed security dialog(s)"; Start-Sleep -Milliseconds 800 }

$p = Get-Process -Name $ProcessName | Select-Object -First 1
$h = [WT]::Open($p.Id)
[WT]::Focus($p.MainWindowHandle)
Start-Sleep -Milliseconds 700
"foreground pid: $([WT]::FgPid())  (game is $($p.Id))"

$addr = [IntPtr]::new($Address)
function Show($label) {
    $v = [WT]::ReadVec($h, $addr)
    if ($null -eq $v) { "$label : READ FAILED" } else { "{0,-16}: ({1,8:F2}, {2,8:F2}, {3,7:F2})" -f $label, $v[0], $v[1], $v[2] }
}

$scan = @{ w = 0x11; a = 0x1E; s = 0x1F; d = 0x20 }

Show 'start'
foreach ($step in @(@('a',900), @('w',1500), @('w',1500), @('d',900), @('w',1500), @('s',1200))) {
    [WT]::Hold([uint16]$scan[$step[0]], [int]$step[1])
    Start-Sleep -Milliseconds 350
    Show ("after " + $step[0] + " " + $step[1] + "ms")
}

[WT]::CloseHandle($h) | Out-Null
