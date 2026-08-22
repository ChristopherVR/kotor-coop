# Live-verifies the 2026-08-22 control-return regression fix (docs/research/control-return.md):
# a CLIENT who was already controlling a party-roster companion (Trask, by default) at the
# moment it (re)connects must end up back on its own PC, camera included, not locked onto the
# companion.
#
# Requires an already-running `tools/demo.ps1` pair (both instances loaded, connected, Trask
# recruited and in the party). Injected keystrokes never reach this game
# (docs/live-verification.md), so the Tab/portrait click that used to be the one manual step
# here is now the dev-only `leader <slot|pc>` console verb (docs/research/control-return.md) —
# it writes the SAME CClientExoAppInternal+0x20 field a real Tab press does, so the harness can
# put either instance onto Trask without a human. Trask is LIVE-CONFIRMED party-roster slot 0
# in end_m01aa (partystate.ps1, phantom-locomotion.md §14.3), not 1.
#
#   pwsh tools\control-return-live.ps1
#   pwsh tools\control-return-live.ps1 -Game1 'C:\GOG Games\Star Wars KOTOR (GOG)' -Game2 'D:\Games\swkotor-gog-p2'
#
# Four scenarios, one run:
#   1. CLIENT controls Trask, then reconnects -> the regression itself. PASS means the
#      client's plugin log shows, in order: "control: you are driving companion 0x..."
#      (ControlReturnController saw the claim), no "party together: ... clamping ..." line
#      BETWEEN that claim and the hand-back (PartyTogetherClampRule deferred), then
#      "control: putting the camera back on your own character (0x...)" (control-return landed).
#   2. HOST controls Trask, then the CLIENT reconnects -> the host is untouched by construction
#      (ControlReturnController's mirroring gate is client-only, and CoopAgent.PumpOnce only
#      calls LockControlToPlayer when `!session.IsEnemyAuthority`, which a host never is) — PASS
#      means `leader 0` answers applied and the host's plugin log logs no "control:" line at all
#      across the window.
#   3. CLIENT stays on its own PC and disconnects -> nothing changes: no "control:" claim line
#      at all (ControlReturnController only ever logs once it sees a session-owned companion
#      controlled).
#   4. `control_return=0` opt-out: documented as a static/code-inspection check here, not a
#      live restart — `ControlReturnController._enabled` is `readonly`, read once from coop.ini
#      at construction, and `OnFrame` returns immediately while it is false, so setting it
#      leaves whatever `leader` last switched to exactly where it was — see
#      docs/research/control-return.md for the citation.
param(
    [string]$Game1 = 'C:\GOG Games\Star Wars KOTOR (GOG)',
    [string]$Game2 = 'D:\Games\swkotor-gog-p2',
    [int]$TraskSlot = 0,
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
$hostPluginLog = Join-Path $Game1 'kotorcoop-plugin.log'
$clientPid = Get-KotorPid -InstallDir $Game2
$hostPid = Get-KotorPid -InstallDir $Game1
"client pid $clientPid ($Game2)"
"host pid $hostPid ($Game1)"

if (-not (Test-Path $clientPluginLog)) {
    throw "no plugin log at '$clientPluginLog' yet - is the client instance actually running with the plugin loaded?"
}
if (-not (Test-Path $hostPluginLog)) {
    throw "no plugin log at '$hostPluginLog' yet - is the host instance actually running with the plugin loaded?"
}

$allPass = $true

# ---- Scenario 1: CLIENT controls Trask, then reconnects -> THE regression itself ---------

"=== scenario 1: client controls Trask (slot $TraskSlot), then reconnects ==="
"disconnecting the client..."
Invoke-Console -ProcId $clientPid -Command 'disconnect' | Out-Host

"switching the client onto Trask (leader $TraskSlot)..."
$leaderReply = Invoke-Console -ProcId $clientPid -Command "leader $TraskSlot"
$leaderReply | Out-Host
if (($leaderReply -join "`n") -notmatch 'control switched to party slot') {
    "FAIL  'leader $TraskSlot' did not confirm the switch - is Trask actually recruited and spawned?"
    $allPass = $false
}

# Mark the log position now: everything relevant to this run happened after it, and an older
# claim/hand-back pair (from a previous connect in the same session) must not count.
$sincePosition = if (Test-Path $clientPluginLog) { (Get-Item $clientPluginLog).Length } else { 0 }

"reconnecting the client..."
Invoke-Console -ProcId $clientPid -Command 'reconnect' | Out-Host

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

# ---- Scenario 2: HOST controls Trask, then the CLIENT reconnects -> host untouched -------

Write-Host ''
"=== scenario 2: host controls Trask (slot $TraskSlot), client reconnects -> host keeps control ==="
$hostSince = if (Test-Path $hostPluginLog) { (Get-Item $hostPluginLog).Length } else { 0 }

$hostLeaderReply = Invoke-Console -ProcId $hostPid -Command "leader $TraskSlot"
$hostLeaderReply | Out-Host
if (($hostLeaderReply -join "`n") -notmatch 'control switched to party slot') {
    "FAIL  host 'leader $TraskSlot' did not confirm the switch"
    $allPass = $false
}

"disconnecting and reconnecting the client (a real connect event) while the host drives Trask..."
Invoke-Console -ProcId $clientPid -Command 'disconnect' | Out-Host
Start-Sleep -Milliseconds 500
Invoke-Console -ProcId $clientPid -Command 'reconnect' | Out-Host
Start-Sleep -Seconds 3

$hostControlLine = Find-LineAfter -Path $hostPluginLog -Pattern 'control: (you are driving|putting the camera)' -SincePosition $hostSince
if ($hostControlLine) {
    "FAIL  the host's plugin logged a control-return line at all - ControlReturnController should never run on a host"
    "      $($hostControlLine.Trim())"
    $allPass = $false
} else {
    "PASS  no control-return activity on the host - it owns its companions unconditionally, by construction"
    "      (ControlReturnController's mirroring gate is client-only; CoopAgent.PumpOnce only calls"
    "       LockControlToPlayer when '!session.IsEnemyAuthority', which a host never is)"
}

# Put the host back on its own PC for whatever runs next (lag testing, etc).
Invoke-Console -ProcId $hostPid -Command 'leader pc' | Out-Host

# ---- Scenario 3: CLIENT stays on its own PC, disconnects -> nothing changes --------------

Write-Host ''
"=== scenario 3: client on its own PC, disconnects -> no control-return activity ==="
Invoke-Console -ProcId $clientPid -Command 'leader pc' | Out-Host
Start-Sleep -Milliseconds 500

$reverseSince = if (Test-Path $clientPluginLog) { (Get-Item $clientPluginLog).Length } else { 0 }
Invoke-Console -ProcId $clientPid -Command 'disconnect' | Out-Host
Start-Sleep -Seconds 2

$reverseClaim = Find-LineAfter -Path $clientPluginLog -Pattern 'control: you are driving companion' -SincePosition $reverseSince
if ($reverseClaim) {
    "FAIL  control-return logged a claim even though the client was on its own PC"
    "      $($reverseClaim.Trim())"
    $allPass = $false
} else {
    "PASS  no claim logged - disconnecting while on your own PC is a no-op, as documented"
}

"reconnecting the client to leave the rig in a connected state..."
Invoke-Console -ProcId $clientPid -Command 'reconnect' | Out-Host

# ---- Scenario 4: control_return=0 opt-out -------------------------------------------------

Write-Host ''
"=== scenario 4: control_return=0 opt-out ==="
"NOT live-restarted this run (ControlReturnController._enabled is readonly, read once from"
"coop.ini at construction - a live toggle needs a fresh process). Verified by source instead:"
"OnFrame's first line is 'if (!_enabled) { return; }' (ControlReturnController.cs), so with"
"control_return=0 nothing here ever claims or hands back - whatever 'leader' last switched to"
"(or a real Tab press) simply stays, exactly as docs/research/control-return.md documents."

Write-Host ''
if ($allPass) {
    Write-Host 'ALL PASS' -ForegroundColor Green
    exit 0
} else {
    Write-Host 'AT LEAST ONE FAILURE - see above' -ForegroundColor Red
    exit 1
}
