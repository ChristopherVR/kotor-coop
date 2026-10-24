# Read-only KOTOR 1 v1.03 main-menu geometry probe. A requested INI mode is not evidence
# of the actual viewport: use -ExpectedWidth/-ExpectedHeight to catch silent fallback.
# Geometry does not prove rendering, mouse activation, or controller navigation.
param(
    [Parameter(Mandatory)][int]$ProcId,
    [int]$ExpectedWidth = 0,
    [int]$ExpectedHeight = 0,
    [string]$OutPath
)
$ErrorActionPreference = 'Stop'
if (-not ('CoopMenuProbe' -as [type])) {
    Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class CoopMenuProbe {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr process);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] data, UIntPtr size, out UIntPtr read);
    public static byte[] Read(IntPtr process, long address, int size) {
        var data = new byte[size];
        if (!ReadProcessMemory(process, new IntPtr(address), data, (UIntPtr)size, out var read) || read.ToUInt64() != (ulong)size)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read menu memory at 0x" + address.ToString("X"));
        return data;
    }
    public static uint U32(IntPtr process, long address) => BitConverter.ToUInt32(Read(process, address, 4), 0);
}
'@
}
$process = Get-Process -Id $ProcId
$handle = [CoopMenuProbe]::OpenProcess(0x410, $false, $ProcId)
if ($handle -eq [IntPtr]::Zero) { throw 'Cannot open target for read-only inspection.' }
try {
    $engineWidth = [CoopMenuProbe]::U32($handle, 0x78D1D4)
    $engineHeight = [CoopMenuProbe]::U32($handle, 0x78D1D8)
    $window = [IntPtr]([long][CoopMenuProbe]::U32($handle, 0x7A39D8))
    $rect = [CoopMenuProbe+Rect]::new()
    $previousDpi = [CoopMenuProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))
    try { $hasClient = [CoopMenuProbe]::GetClientRect($window, [ref]$rect) -and $rect.Right -gt 0 -and $rect.Bottom -gt 0 }
    finally { [void][CoopMenuProbe]::SetThreadDpiAwarenessContext($previousDpi) }
    $width = if ($hasClient) { $rect.Right } else { $engineWidth }
    $height = if ($hasClient) { $rect.Bottom } else { $engineHeight }
    $manager = [CoopMenuProbe]::U32($handle, 0x7A39FC)
    if (!$manager) { throw 'Game client is not ready.' }
    $client = [CoopMenuProbe]::U32($handle, $manager + 4)
    if (!$client) { throw 'Game client is not ready.' }
    $internalClient = [CoopMenuProbe]::U32($handle, $client + 4)
    if (!$internalClient) { throw 'Game client is not ready.' }
    $menu = [CoopMenuProbe]::U32($handle, $internalClient + 0x280)
    if (!$menu) { throw 'Main menu is not available; close the browser and return to the main menu.' }
    # Through BTN_WARP's flags only; reading 0x1448 crosses the allocation under page heap.
    $bytes = [CoopMenuProbe]::Read($handle, $menu, 0x1290)
    $buttons = @(for ($offset = 0; $offset + 0x48 -le $bytes.Length; $offset += 4) {
        if ([BitConverter]::ToUInt32($bytes, $offset) -ne 0x73E658) { continue }
        $left = [BitConverter]::ToInt32($bytes, $offset + 4)
        $top = [BitConverter]::ToInt32($bytes, $offset + 8)
        $w = [BitConverter]::ToInt32($bytes, $offset + 12)
        $h = [BitConverter]::ToInt32($bytes, $offset + 16)
        $flags = [BitConverter]::ToUInt32($bytes, $offset + 0x44)
        [pscustomobject]@{
            Offset = ('0x{0:X}' -f $offset); IsCoop = ($offset -eq 0x1248)
            Left = $left; Top = $top; Width = $w; Height = $h
            Visible = (($flags -band 2) -ne 0); Locked = (($flags -band 0x20) -ne 0)
            InBounds = ($left -ge 0 -and $top -ge 0 -and $w -gt 0 -and $h -gt 0 -and
                ([long]$left + $w) -le $width -and ([long]$top + $h) -le $height)
        }
    })
    $coop = $buttons | Where-Object IsCoop | Select-Object -First 1
    $overlaps = @($buttons | Where-Object {
        $coop -and !$_.IsCoop -and $_.Visible -and $_.Width -gt 0 -and $_.Height -gt 0 -and
        $coop.Left -lt ([long]$_.Left + $_.Width) -and ([long]$coop.Left + $coop.Width) -gt $_.Left -and
        $coop.Top -lt ([long]$_.Top + $_.Height) -and ([long]$coop.Top + $coop.Height) -gt $_.Top
    })
    $modeMatches = (!$ExpectedWidth -or $width -eq $ExpectedWidth) -and (!$ExpectedHeight -or $height -eq $ExpectedHeight)
    $result = [ordered]@{
        CapturedUtc = [DateTime]::UtcNow.ToString('o'); ProcessId = $ProcId; Executable = $process.Path
        Width = $width; Height = $height; EngineWidth = $engineWidth; EngineHeight = $engineHeight
        SizeSource = $(if ($hasClient) { 'client' } else { 'engine' })
        ExpectedWidth = $ExpectedWidth; ExpectedHeight = $ExpectedHeight
        ModeMatches = $modeMatches; MainMenu = ('0x{0:X}' -f $menu)
        CoopGeometryPass = [bool]($coop -and $coop.Visible -and !$coop.Locked -and $coop.InBounds -and !$overlaps.Count)
        Buttons = $buttons; OverlappingOffsets = @($overlaps | Select-Object -ExpandProperty Offset)
        LoadedDlls = @($process.Modules | Where-Object { $_.ModuleName -like '*.dll' } | Select-Object -ExpandProperty ModuleName)
    }
    $json = $result | ConvertTo-Json -Depth 5
    if ($OutPath) { $json | Set-Content -LiteralPath $OutPath -Encoding utf8 }
    $json
    if (!$modeMatches -or !$result.CoopGeometryPass) { exit 1 }
}
finally { [void][CoopMenuProbe]::CloseHandle($handle) }
