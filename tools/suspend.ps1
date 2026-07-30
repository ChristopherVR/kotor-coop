# Suspends or resumes a process.
#
# Used while driving one KOTOR instance's menu with injected input: a second instance that
# is merely minimised still has DirectInput acquired and competes for that input, which
# makes menu automation intermittent. A suspended process cannot consume anything.
param(
    [Parameter(Mandatory = $true)][int]$ProcId,
    [switch]$Resume
)

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Susp {
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    // PROCESS_SUSPEND_RESUME | PROCESS_QUERY_INFORMATION
    public static IntPtr Open(int pid){ return OpenProcess(0x0800 | 0x0400, false, (uint)pid); }
}
'@

$h = [Susp]::Open($ProcId)
if ($h -eq [IntPtr]::Zero) { Write-Error "cannot open pid $ProcId"; exit 1 }

if ($Resume) {
    $rc = [Susp]::NtResumeProcess($h)
    "pid $ProcId resumed (status $rc)"
}
else {
    $rc = [Susp]::NtSuspendProcess($h)
    "pid $ProcId suspended (status $rc)"
}

[Susp]::CloseHandle($h) | Out-Null
