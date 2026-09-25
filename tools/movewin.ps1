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
    [switch]$Restore,
    # Isolated-rig companion to -Restore (docs/research/isolated-rig.md): un-minimises WITHOUT
    # activating (SW_SHOWNOACTIVATE instead of SW_RESTORE), so an off-screen/background
    # instance never steals the OS foreground or the user's keyboard focus. No effect without
    # -Restore.
    [switch]$NoActivate,
    # Isolated rig (docs/research/isolated-rig.md): make the window invisible, click-through
    # and absent from the taskbar/Alt-Tab while it stays ON a monitor. Windows only composes
    # a D3D window that overlaps a monitor, so an off-screen window captures black; a fully
    # transparent layered window is still composed, and PrintWindow (tools\shot.ps1) reads it.
    [switch]$Ghost,
    # Undo -Ghost (make the window visible and clickable again).
    [switch]$Unghost
)

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$proc = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
if (-not $proc) { Write-Error "process $ProcId not running"; exit 1 }

$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { Write-Error "process $ProcId has no main window"; exit 1 }

# WS_EX_LAYERED 0x80000 | WS_EX_TRANSPARENT 0x20 | WS_EX_TOOLWINDOW 0x80, minus WS_EX_APPWINDOW 0x40000.
if ($Ghost -or $Unghost) {
    $ex = [Win]::GetWindowLong($hwnd, -20)
    if ($Ghost) {
        $ex = ($ex -bor 0x80000 -bor 0x20 -bor 0x80) -band (-bnot 0x40000)
        [Win]::SetWindowLong($hwnd, -20, $ex) | Out-Null
        [Win]::SetLayeredWindowAttributes($hwnd, 0, 0, 2) | Out-Null   # LWA_ALPHA, alpha 0
        # HWND_BOTTOM; SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_FRAMECHANGED
        [Win]::SetWindowPos($hwnd, [IntPtr]1, 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0010 -bor 0x0020) | Out-Null
        "pid $ProcId ghosted (invisible, click-through, off the taskbar; still captured by shot.ps1)"
    }
    else {
        $ex = ($ex -band (-bnot (0x80000 -bor 0x20 -bor 0x80))) -bor 0x40000
        [Win]::SetWindowLong($hwnd, -20, $ex) | Out-Null
        [Win]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0004 -bor 0x0010 -bor 0x0020) | Out-Null
        "pid $ProcId visible again"
    }
    return
}

if ($Minimize) {
    [Win]::ShowWindow($hwnd, 6) | Out-Null   # SW_MINIMIZE
    "pid $ProcId minimised"
    return
}

if ($Restore) {
    $show = if ($NoActivate) { 4 } else { 9 }   # SW_SHOWNOACTIVATE : SW_RESTORE
    [Win]::ShowWindow($hwnd, $show) | Out-Null
    "pid $ProcId restored$(if ($NoActivate) { ' (no-activate)' } else { '' })"
    return
}

# SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
[Win]::SetWindowPos($hwnd, [IntPtr]::Zero, $X, $Y, 0, 0, 0x0001 -bor 0x0004 -bor 0x0010) | Out-Null

$r = New-Object Win+RECT
[Win]::GetWindowRect($hwnd, [ref]$r) | Out-Null
"pid $ProcId window at ($($r.Left),$($r.Top)) size $($r.Right - $r.Left)x$($r.Bottom - $r.Top)"
