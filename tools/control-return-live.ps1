# Live-verifies the 2026-08-22 control-return regression fix (docs/research/control-return.md):
# a CLIENT who was already controlling a party-roster companion (Trask, by default) at the
# moment it (re)connects must end up back on its own PC, camera included, not locked onto the
# companion.
#
# Requires an already-running `tools/demo.ps1` pair (both instances loaded, connected, Trask
# available in the party). Because injected keystrokes never reach this game
# (docs/live-verification.md), the one step this script cannot automate is the Tab/portrait
# click that puts Trask in control — a human does that one step, everything else (disconnect,
# the prompt, reconnect, and reading the plugin log for the actual verdict) is automatic.
#
#   pwsh tools\control-return-live.ps1
#   pwsh tools\control-return-live.ps1 -Game1 'C:\GOG Games\Star Wars KOTOR (GOG)' -Game2 'D:\Games\swkotor-gog-p2'
#
# What PASS means: the client's plugin log shows, in order —
#   1. "control: you are driving companion 0x..."   (ControlReturnController saw the claim)
#   2. no "party together: ... clamping ..." line BETWEEN that claim and the hand-back below
#      (PartyTogetherClampRule deferred, per docs/research/control-return.md)
#   3. "control: putting the camera back on your own character (0x...)"  (control-return landed)
param(
    [string]$Game1 = 'C:\GOG Games\Star Wars KOTOR (GOG)',
    [string]$Game2 = 'D:\Games\swkotor-gog-p2',
    [int]$TimeoutSec = 30
)

$ErrorActionPreference = 'Stop'
$blockpoke = Join-Path $PSScriptRoot 'blockpoke.ps1'

function Get-KotorPid {
    param([string]$InstallDir)

    $proc = Get-Process swkotor -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1

    if (-not $proc) {
        throw "no running swkotor.exe found under '$InstallDir' - is tools\demo.ps1 up?"
    }

    return $proc.Id
}

function Invoke-Console {
    param([int]$ProcId, [string]$Command)

    & $blockpoke -ProcId $ProcId console $Command | Out-Null
    Start-Sleep -Milliseconds 300
    & $blockpoke -ProcId $ProcId console
}

# Returns the FIRST matching line at or after $SincePosition (byte offset into the file), or
# $null. Used to bound the search to "since the disconnect/reconnect this run triggered" so a
# line left over from an earlier session in the same log cannot produce a false PASS.
function Find-LineAfter {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Pattern,
        [long]$SincePosition = 0
    )

    if (-not (Test-Path $Path)) {
        return $null
    }

    $content = Get-Content -Path $Path -Raw -ErrorAction SilentlyContinue
    if (-not $content -or $content.Length -le $SincePosition) {
        return $null
    }

    $tail = $content.Substring([int]$SincePosition)
    $hit = $tail -split "`n" | Where-Object { $_ -match $Pattern } | Select-Object -First 1
    return $hit
}

function Wait-ForLine {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Pattern,
        [long]$SincePosition = 0,
        [int]$TimeoutSeconds = $TimeoutSec
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $hit = Find-LineAfter -Path $Path -Pattern $Pattern -SincePosition $SincePosition
        if ($hit) {
            return $hit
        }

        Start-Sleep -Milliseconds 500
    }

    return $null
}

$clientPluginLog = Join-Path $Game2 'kotorcoop-plugin.log'
$clientPid = Get-KotorPid -InstallDir $Game2
"client pid $clientPid ($Game2)"

if (-not (Test-Path $clientPluginLog)) {
    throw "no plugin log at '$clientPluginLog' yet - is the client instance actually running with the plugin loaded?"
}

"disconnecting the client..."
Invoke-Console -ProcId $clientPid -Command 'disconnect' | Out-Host

Write-Host ''
Write-Host 'MANUAL STEP: on the CLIENT window (Game2), Tab (or click a party portrait) until' -ForegroundColor Yellow
Write-Host 'TRASK is your controlled character - confirm the camera and WASD are on Trask, not' -ForegroundColor Yellow
Write-Host 'your own PC. Everything after this is automatic.' -ForegroundColor Yellow
Read-Host 'Press Enter once Trask is in control'

# Mark the log position now: everything relevant to this run happened after it, and an older
# claim/hand-back pair (from a previous connect in the same session) must not count.
$sincePosition = if (Test-Path $clientPluginLog) { (Get-Item $clientPluginLog).Length } else { 0 }

"reconnecting the client..."
Invoke-Console -ProcId $clientPid -Command 'reconnect' | Out-Host

$allPass = $true

$claim = Wait-ForLine -Path $clientPluginLog -Pattern 'control: you are driving companion' -SincePosition $sincePosition
if ($claim) {
    "PASS  control-return saw the claim"
    "      $($claim.Trim())"
} else {
    "FAIL  control-return never logged the claim - either Trask was not actually in control at reconnect, or ControlReturnController is not running (control_return=0?)"
    $allPass = $false
}

$hitLine = Wait-ForLine -Path $clientPluginLog -Pattern 'control: putting the camera back on your own character' -SincePosition $sincePosition
if ($hitLine) {
    "PASS  control-return handed the camera back"
    "      $($hitLine.Trim())"
} else {
    "FAIL  no hand-back line within ${TimeoutSec}s - THE REGRESSION: control/camera stayed on the companion"
    $allPass = $false
}

if ($claim -and $hitLine) {
    # THE REGRESSION'S OWN SIGNATURE: a "party together: ... clamping" line landing strictly
    # between the claim and the hand-back is exactly PartyTogetherClampRule failing to defer -
    # trimming the PC's own slot out of CSWParty while the player is still driving the
    # companion. None should appear there; the clamp resumes only once the hand-back line
    # itself has been logged.
    $content = Get-Content -Path $clientPluginLog -Raw
    $claimIndex = $content.IndexOf($claim.Trim())
    $handBackIndex = $content.IndexOf($hitLine.Trim())

    if ($claimIndex -ge 0 -and $handBackIndex -gt $claimIndex) {
        $between = $content.Substring($claimIndex, $handBackIndex - $claimIndex)
        if ($between -match 'party together:.*clamping') {
            "FAIL  the party-together clamp fired WHILE control was still on the companion - PartyTogetherClampRule did not defer"
            $allPass = $false
        } else {
            "PASS  the party-together clamp stayed deferred for the whole window"
        }
    }
}

Write-Host ''
if ($allPass) {
    Write-Host 'ALL PASS' -ForegroundColor Green
    exit 0
} else {
    Write-Host 'AT LEAST ONE FAILURE - see above' -ForegroundColor Red
    exit 1
}
