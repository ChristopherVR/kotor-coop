# Shows how the game last died, from the two places the answer can hide.
#
# The plugin log covers access violations and managed exceptions (CrashProbe). What it
# CANNOT cover is a .NET fail-fast: NativeAOT raises those with RaiseFailFastException /
# __fastfail, which bypasses every user-mode handler, so the process is gone with the log
# ending mid-sentence. The only record is the Windows Application log, and a crash that
# leaves no trace in the file you are already reading is a crash nobody looks for.
#
#   pwsh tools\crashlog.ps1              # the last few swkotor crashes
#   pwsh tools\crashlog.ps1 -Hours 2     # only recent ones
#   pwsh tools\crashlog.ps1 -EnableDumps # (admin) turn on local minidumps for next time
param(
    [int]$Hours = 24,
    [int]$Count = 5,
    [switch]$EnableDumps,
    [string]$DumpFolder = 'D:\Development\kotor-coop\.crashdumps'
)

$ErrorActionPreference = 'Stop'

if ($EnableDumps) {
    # WER local dumps are the difference between "0xc0000602 at +0x7d2c7" and a stack. This
    # needs elevation and is a machine-wide setting, so it is opt-in and never automatic.
    $key = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\swkotor.exe'
    try {
        New-Item -Path $key -Force | Out-Null
        New-Item -ItemType Directory -Force -Path $DumpFolder | Out-Null
        Set-ItemProperty -Path $key -Name 'DumpFolder' -Value $DumpFolder -Type ExpandString
        Set-ItemProperty -Path $key -Name 'DumpCount' -Value 10 -Type DWord
        Set-ItemProperty -Path $key -Name 'DumpType' -Value 2 -Type DWord   # 2 = full dump
        "local dumps enabled for swkotor.exe -> $DumpFolder"
        ''
        'Full dumps of a 32-bit game are ~200 MB each, capped at 10.'
        'Disable again with: Remove-Item -Recurse ' + "'$key'"
    }
    catch {
        Write-Warning "could not enable local dumps (run pwsh as administrator): $($_.Exception.Message)"
    }

    ''
}

$since = (Get-Date).AddHours(-$Hours)
$events = @(
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000; StartTime = $since } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -like '*swkotor*' } |
    Select-Object -First $Count)

if ($events.Count -eq 0) {
    "no swkotor.exe crashes in the Windows Application log in the last $Hours h."
}

foreach ($e in $events) {
    $m = $e.Message -split "`r?`n" | Where-Object { $_.Trim() }
    $module = ($m | Select-String 'Faulting module name: (.+?),').Matches.Groups[1].Value
    $code = ($m | Select-String 'Exception code: (\S+)').Matches.Groups[1].Value
    $offset = ($m | Select-String 'Fault offset: (\S+)').Matches.Groups[1].Value

    $meaning = switch ($code) {
        '0xc0000005' { 'access violation - a bad pointer' }
        '0xc0000602' { 'STATUS_FAIL_FAST_EXCEPTION - a .NET fail-fast inside our plugin' }
        '0xc00000fd' { 'stack overflow' }
        '0xc0000409' { 'security check / fast-fail' }
        default      { '' }
    }

    ''
    "$($e.TimeCreated)  module=$module  code=$code  offset=$offset"
    if ($meaning) { "    $meaning" }

    if ($code -eq '0xc0000602') {
        '    -> the plugin log will NOT have this. Look for a "FATAL (managed...)" line'
        '       written by CrashProbe just before the end; if there is none, the runtime'
        '       itself aborted and only a minidump will say why (-EnableDumps).'
    }
}

''
'plugin logs:'
foreach ($dir in 'C:\GOG Games\Star Wars KOTOR (GOG)', 'D:\Games\swkotor-gog-p2', 'D:\Games\swkotor-gog-p3') {
    $log = Join-Path $dir 'kotorcoop-plugin.log'
    if (Test-Path $log) {
        $fatal = @(Select-String -Path $log -Pattern 'FATAL|CRASH' -SimpleMatch:$false)
        "  $log  ($((Get-Item $log).LastWriteTime))  fatal/crash lines: $($fatal.Count)"
        $fatal | Select-Object -Last 3 | ForEach-Object { "      $($_.Line)" }
    }
}
