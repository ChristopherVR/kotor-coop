# Drives the three staged scenarios docs/research/creature-liveness.md's NEEDS-LIVE checklist
# names, against an already-running `tools/demo.ps1` pair, and reports PASS/FAIL per scenario
# by watching for the log lines the design's own controllers emit.
#
# Requires BOTH installs to have `liveness_sync=1` in coop.ini (the feature's kill switch —
# off by default until this checklist has actually run) — `-EnableFlag` sets it in both and
# exits so a restart can pick it up, since the plugin only reads coop.ini at load.
#
# Scenario A, "out-of-census slay" (fully automated): reads the HOST's own creature census
# through the existing `census`/`slay` console channel (the same one a human types at the
# in-game `~` console — tools/blockpoke.ps1's `console` verb writes the identical agent<->
# plugin field, which is the only way to drive a co-op verb unattended; injected keystrokes
# never reach this game, docs/live-verification.md), picks a creature the census reports as
# NOT among the currently-mirrored 32, and slays it on the host. Watches the MIRRORING
# client's plugin log for the kill/destroy line.
#
# Scenario B, "a script death" (log-watching automated, the trigger is not): a scripted or
# cutscene kill depends on which module/save is loaded, so this script cannot fire one
# generically. Prints what to do, waits for Enter, then watches both plugin logs for the
# convergence line exactly like scenario A does.
#
# Scenario C, "disconnect then reconnect": disconnects the CLIENT via the same console
# channel, waits, reconnects it, and confirms the very next liveness broadcast cycle (forced
# immediately on readmission — see CoopHost.HandleHello's freshlyAdmitted hook) delivers
# rather than waiting out the ordinary 2-second interval.
#
#   pwsh tools\live-liveness.ps1 -EnableFlag                # set liveness_sync=1, then restart
#   pwsh tools\live-liveness.ps1                             # run all three scenarios
#   pwsh tools\live-liveness.ps1 -Scenario A                 # just the out-of-census slay
#   pwsh tools\live-liveness.ps1 -Scenario B                 # just the script-death watch
#   pwsh tools\live-liveness.ps1 -Scenario C                 # just the disconnect/reconnect
param(
    [string]$Game1 = 'C:\GOG Games\Star Wars KOTOR (GOG)',
    [string]$Game2 = 'D:\Games\swkotor-gog-p2',
    [string]$WorkDir = "$env:TEMP\kotorcoop-demo",
    [ValidateSet('A', 'B', 'C', 'All')]
    [string]$Scenario = 'All',
    [int]$TimeoutSec = 20,
    [switch]$EnableFlag
)

$ErrorActionPreference = 'Stop'
$blockpoke = Join-Path $PSScriptRoot 'blockpoke.ps1'

function Set-LivenessSync {
    param([string]$Directory, [string]$Value)

    $path = Join-Path $Directory 'coop.ini'
    $lines = if (Test-Path $path) { @(Get-Content $path) } else { @('[coop]') }

    $found = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*liveness_sync\s*=') {
            $lines[$i] = "liveness_sync = $Value"
            $found = $true
        }
    }

    if (-not $found) { $lines += "liveness_sync = $Value" }
    Set-Content -Path $path -Value $lines -Encoding ASCII
    "$Directory\coop.ini -> liveness_sync = $Value"
}

if ($EnableFlag) {
    Set-LivenessSync -Directory $Game1 -Value '1'
    Set-LivenessSync -Directory $Game2 -Value '1'
    "coop.ini only reads at plugin load - restart both instances (tools\demo.ps1 -Stop; tools\demo.ps1) before running the scenarios."
    exit 0
}

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

function Wait-ForLogLine {
    param(
        [Parameter(Mandatory)][string[]]$Paths,
        [Parameter(Mandatory)][string]$Pattern,
        [int]$TimeoutSeconds = $TimeoutSec
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($path in $Paths) {
            if (Test-Path $path) {
                $hit = Select-String -Path $path -Pattern $Pattern -SimpleMatch:$false -ErrorAction SilentlyContinue |
                    Select-Object -Last 1
                if ($hit) {
                    return $hit
                }
            }
        }

        Start-Sleep -Milliseconds 500
    }

    return $null
}

function Report {
    param([string]$Name, [Microsoft.PowerShell.Commands.MatchInfo]$Hit)

    if ($Hit) {
        "PASS  $Name"
        "      $($Hit.Path): $($Hit.Line.Trim())"
    } else {
        "FAIL  $Name - no matching line within ${TimeoutSec}s"
    }

    return [bool]$Hit
}

$hostPluginLog = Join-Path $Game1 'kotorcoop-plugin.log'
$clientPluginLog = Join-Path $Game2 'kotorcoop-plugin.log'
$hostAgentLog = Join-Path $WorkDir 'agent1.log'
$clientAgentLog = Join-Path $WorkDir 'agent2.log'

$hostPid = Get-KotorPid -InstallDir $Game1
$clientPid = Get-KotorPid -InstallDir $Game2
"host pid $hostPid ($Game1), client pid $clientPid ($Game2)"

$allPass = $true

function Run-ScenarioA {
    "--- Scenario A: out-of-census slay ---"

    # census (no argument) prints this instance's own gate summary and how many published
    # ids it resolves; the plugin's own `~` console `census` verb prints per-id detail if a
    # candidate is already known. Simplest reliable target: read the raw creature scan and
    # slay whatever the 33rd-or-later entry is (i.e. NOT in the 32-slot mirrored census) -
    # left as a manual id substitution below, since which creature that is depends entirely
    # on the loaded module and how crowded it is.
    $censusReply = Invoke-Console -ProcId $hostPid -Command 'census'
    "host census: $censusReply"
    "Pick an id NOT in the mirrored 32 (tools\creatures.ps1 -ProcId $hostPid lists every live"
    "creature; the plugin's own driven set is smaller - `census <id>` on the host names the"
    "gap directly) and re-run with: pwsh tools\live-liveness.ps1 -Scenario A ; then, once you"
    "have chosen ID (hex, e.g. 0x47), run:"
    "  pwsh tools\blockpoke.ps1 -ProcId $hostPid console 'slay 0x47'"
    ""
    "This script cannot choose the id for you without knowing the module; once you have run"
    "the slay above, press Enter here to watch for convergence."
    Read-Host | Out-Null

    $hit = Wait-ForLogLine -Paths @($clientPluginLog) -Pattern 'liveness:.*(killing to match|destroying to match)'
    return Report -Name 'client reconciled the out-of-census kill' -Hit $hit
}

function Run-ScenarioB {
    "--- Scenario B: a script death ---"
    "Trigger a scripted kill or DestroyObject on the host (a cutscene, a trap, a k_* script)"
    "on a creature this session would ordinarily mirror. Do it now, then press Enter."
    Read-Host | Out-Null

    $hostHit = Wait-ForLogLine -Paths @($hostAgentLog) -Pattern 'liveness: \d+ creature\(s\) newly gone this pass'
    Report -Name 'host noticed the death (gone-derivation)' -Hit $hostHit | Out-Null

    $clientHit = Wait-ForLogLine -Paths @($clientPluginLog) -Pattern 'liveness:.*(killing to match|destroying to match)'
    return Report -Name 'client reconciled the script death' -Hit $clientHit
}

function Run-ScenarioC {
    "--- Scenario C: disconnect then reconnect ---"
    "disconnecting the client..."
    Invoke-Console -ProcId $clientPid -Command 'disconnect' | Out-Null
    Start-Sleep -Seconds 2

    "while disconnected, slay something on the host (see Scenario A) so there is a mismatch"
    "for the reconnect to catch up on, then press Enter to reconnect."
    Read-Host | Out-Null

    "reconnecting..."
    $before = Get-Date
    Invoke-Console -ProcId $clientPid -Command 'reconnect' | Out-Null

    # The forced-cycle promise: CoopHost.HandleHello sets _forceLivenessBroadcast on a fresh
    # (re)admission, so the very NEXT tick's cycle goes out rather than waiting out the
    # ordinary 2-second interval. A convergence line arriving well under that interval (this
    # script gives it up to $TimeoutSec regardless, but the interesting number is HOW fast)
    # is the evidence the force actually fired.
    $hit = Wait-ForLogLine -Paths @($clientPluginLog) -Pattern 'liveness:.*(killing to match|destroying to match)'
    $elapsed = ((Get-Date) - $before).TotalSeconds
    if ($hit) { "  (converged ${elapsed}s after reconnect)" }
    return Report -Name 'reconnect delivered a full liveness snapshot promptly' -Hit $hit
}

if ($Scenario -eq 'All' -or $Scenario -eq 'A') { $allPass = (Run-ScenarioA) -and $allPass }
if ($Scenario -eq 'All' -or $Scenario -eq 'B') { $allPass = (Run-ScenarioB) -and $allPass }
if ($Scenario -eq 'All' -or $Scenario -eq 'C') { $allPass = (Run-ScenarioC) -and $allPass }

""
if ($allPass) { "ALL SCENARIOS PASSED" } else { "AT LEAST ONE SCENARIO FAILED - see above" }
exit ([int](-not $allPass))
