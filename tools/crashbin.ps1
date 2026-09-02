# Decodes kotorcoop-crash.bin: the last first-chance fault CrashProbe's machine-code stub
# handed straight to kernel32!WriteFile, on the faulting thread, in the same breath as
# filling the record — so it survives a process TERMINATED before the plugin's WORKER
# thread ever gets its next poll to turn that same record into a "FIRST-CHANCE ..." line in
# kotorcoop-plugin.log (see CrashProbe.cs's top-of-file doc comment, 2026-09-02: this is
# what a client vanishing with WER off and no Application-log entry looks like).
#
# Record layout (CrashProbe's RecXxx constants / src\KotorCoop.Plugin\CrashRecord.cs, the
# tested C# twin of this script), little-endian, fixed size:
#   0x00 serial (u32)   0x04 code (u32)     0x08 address (u32)
#   0x0C eip (u32)      0x10 esp (u32)      0x14 stackWords (u32)
#   0x18 .. 64 raw stack dwords (u32 each)
#
# The stub resets to offset 0 before every write (see CrashProbe.BuildHandler's comment on
# why), so today this file is always exactly one record — the LATEST fault this process saw
# before it either kept running or died. The decoder does not assume that stays true: it
# walks every whole record-sized chunk the file holds.
#
#   pwsh tools\crashbin.ps1                                    # default GOG dev install
#   pwsh tools\crashbin.ps1 -GameDir 'D:\Games\swkotor-gog-p2'  # a second instance
param(
    [string]$GameDir = 'C:\GOG Games\Star Wars KOTOR (GOG)'
)

$ErrorActionPreference = 'Stop'

$RecStack = 0x18
$StackWords = 64
$RecordSize = $RecStack + ($StackWords * 4)
$ImageLow = 0x400000
$ImageHigh = 0x8C3000

$binPath = Join-Path $GameDir 'kotorcoop-crash.bin'
if (-not (Test-Path $binPath)) {
    "no $binPath — the plugin never opened it (an old build?), or this process never faulted"
    exit 0
}

# CrashProbe logs its own resolved image range at install time ("crash probe: this
# plugin's image is 0x..0x.."); ExitProbe.OwnImage and DrainReports both read it the same
# way, live. A dead process cannot be asked, so this script parses it back out of the log
# instead — the same "OURS: rva ..." tag the FIRST-CHANCE log line would have printed.
$ownBase = $null
$ownHigh = $null
$logPath = Join-Path $GameDir 'kotorcoop-plugin.log'
if (Test-Path $logPath) {
    $match = Select-String -Path $logPath -Pattern "crash probe: this plugin's image is 0x([0-9A-Fa-f]+)\.\.0x([0-9A-Fa-f]+)" |
        Select-Object -Last 1
    if ($match) {
        $ownBase = [Convert]::ToUInt32($match.Matches[0].Groups[1].Value, 16)
        $ownHigh = [Convert]::ToUInt32($match.Matches[0].Groups[2].Value, 16)
    }
}

$bytes = [System.IO.File]::ReadAllBytes($binPath)
$recordCount = [Math]::Floor($bytes.Length / $RecordSize)

"$binPath : $($bytes.Length) bytes, $recordCount record(s) (a torn/short file below one $RecordSize-byte record shows 0)"

function U32([long]$offset) { [BitConverter]::ToUInt32($bytes, $offset) }

for ($r = 0; $r -lt $recordCount; $r++) {
    $base = $r * $RecordSize
    $serial  = U32 ($base + 0x00)
    $code    = U32 ($base + 0x04)
    $address = U32 ($base + 0x08)
    $eip     = U32 ($base + 0x0C)
    $esp     = U32 ($base + 0x10)
    $words   = [Math]::Min((U32 ($base + 0x14)), $StackWords)

    # Decimal, not hex: an unsuffixed PowerShell hex literal above 0x7FFFFFFF (all three of
    # these) parses as a NEGATIVE Int32, and comparing that against the UInt32 $code this
    # script reads from the file silently never matches — caught by testing this script
    # against a synthetic record before trusting it against a real crash.
    $name = switch ($code) {
        3221225477 { 'access violation' }        # 0xC0000005
        3221227010 { 'FAIL-FAST (0xC0000602)' }   # 0xC0000602
        3221225725 { 'stack overflow' }           # 0xC00000FD
        default    { 'exception' }
    }

    $ours = ''
    if ($null -ne $ownBase -and $eip -ge $ownBase -and $eip -lt $ownHigh) {
        $ours = " (OURS: rva 0x{0:X})" -f ($eip - $ownBase)
    }

    $trace = @()
    for ($i = 0; $i -lt $words; $i++) {
        $value = U32 ($base + $RecStack + ($i * 4))
        if ($value -ge $ImageLow -and $value -lt $ImageHigh) {
            $trace += ('0x{0:X}' -f $value)
        }
    }
    $traceText = if ($trace.Count -gt 0) { ' ' + ($trace -join ' ') } else { ' (none)' }

    ''
    "record $r : serial $serial"
    ("  {0} (0x{1:X8}) at 0x{2:X}, eip 0x{3:X}{4}, esp 0x{5:X}" -f $name, $code, $address, $eip, $ours, $esp)
    "  image-range stack values:$traceText"
}

if (-not $ownBase) {
    ''
    "no plugin image range found in $logPath — an eip inside dinput8.dll cannot be tagged"
    "OURS here. Run this against the .bin/log pair from the SAME session for that tag."
}
