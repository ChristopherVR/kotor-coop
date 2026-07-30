# Moves a process's main window. Used to place two KOTOR instances side by side so a
# single screenshot can show both players at once.
param(
    [Parameter(Mandatory = $true)][int]$ProcId,
    [int]$X = 0,
    [int]$Y = 0,
    # An in-world KOTOR instance keeps taking the foreground back, which stops menu
    # automation from reaching a second instance. Minimising it parks it without stopping
    # it: a minimised instance still simulates at full speed (measured).
    [switch]$Minimize,
    [switch]$Restore
)

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$proc = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
if (-not $proc) { Write-Error "process $ProcId not running"; exit 1 }

$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { Write-Error "process $ProcId has no main window"; exit 1 }

if ($Minimize) {
    [Win]::ShowWindow($hwnd, 6) | Out-Null   # SW_MINIMIZE
    "pid $ProcId minimised"
    return
}

if ($Restore) {
    [Win]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
    "pid $ProcId restored"
    return
}

# SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
[Win]::SetWindowPos($hwnd, [IntPtr]::Zero, $X, $Y, 0, 0, 0x0001 -bor 0x0004 -bor 0x0010) | Out-Null

$r = New-Object Win+RECT
[Win]::GetWindowRect($hwnd, [ref]$r) | Out-Null
"pid $ProcId window at ($($r.Left),$($r.Top)) size $($r.Right - $r.Left)x$($r.Bottom - $r.Top)"
