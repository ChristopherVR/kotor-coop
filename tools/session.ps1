# Brings up a full two-instance co-op session, unattended.
#
# The one thing that makes menu automation reliable, learned the hard way (traps 22/25):
# a freshly launched window has never been activated, so KOTOR's DirectInput ignores every
# injected click AND the game's own cursor hit-test returns nothing. A real MINIMISE ->
# RESTORE cycle before loading fixes it. Verified 2026-07-30: with the cycle, both
# instances loaded "on attempt 1"; without it, four attempts x two instances all timed out.
#
#   pwsh tools\session.ps1                 # launch both, load save 1, start both agents
#   pwsh tools\session.ps1 -Save 2         # a different save slot
#   pwsh tools\session.ps1 -Stop           # shut it all down
param(
    [int]$Save = 1,
    [switch]$Stop,
    [switch]$NoAgents,
    [string]$Game1 = 'C:\GOG Games\Star Wars KOTOR (GOG)\swkotor.exe',
    [string]$Game2 = 'D:\Games\swkotor-gog-p2\swkotor.exe',
    [int]$Port = 27140
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$agent = Join-Path $root 'src\KotorCoop.Agent\bin\Debug\net10.0-windows\KotorCoop.Agent.exe'
$movewin = Join-Path $PSScriptRoot 'movewin.ps1'
$inputalive = Join-Path $PSScriptRoot 'inputalive.ps1'

if ($Stop) {
    Get-Process KotorCoop.Agent -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false
    Get-Process swkotor -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false
    'session stopped'
    return
}

if (-not (Test-Path $agent)) { throw "agent not built: $agent" }

function Start-Instance {
    param([string]$Path, [int]$Instance, [int]$X)

    $before = @(Get-Process swkotor -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)

    if ($Instance -eq 1) { & $agent launch --windowed --game-path $Path | Out-Null }
    else                 { & $agent launch --instance $Instance --game-path $Path | Out-Null }

    # Wait for the new pid rather than sleeping a guessed interval.
    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 500
        $now = @(Get-Process swkotor -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
        $new = $now | Where-Object { $_ -notin $before }
    } while (-not $new -and (Get-Date) -lt $deadline)

    if (-not $new) { throw "instance $Instance never appeared" }
    $procId = @($new)[0]

    # The window appears several seconds after the process does; moving it before then
    # throws "no main window".
    $windowDeadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 500
        $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
        $proc.Refresh()
    } while ($proc -and $proc.MainWindowHandle -eq [IntPtr]::Zero -and (Get-Date) -lt $windowDeadline)

    & $movewin -ProcId $procId -X $X -Y 0 | Out-Null

    # Write-Host, not a bare string: anything written to the success stream becomes part
    # of this function's return value, and a pid plus a status line is not a pid.
    Write-Host "instance $Instance is pid $procId"

    # Wait for the menu (the window exists many seconds earlier, through the logo
    # movies), then activation-cycle and ASK THE GAME whether its menu input is alive
    # before clicking. Trap 25's dead-input state is undiagnosable from the outside but
    # perfectly predicted by the GUI hit-test — see tools\inputalive.ps1. Clicking a dead
    # instance never recovers; relaunching is what works, so that is what this does.
    Start-Sleep -Seconds 15

    & $movewin -ProcId $procId -Minimize | Out-Null
    Start-Sleep -Seconds 2
    & $movewin -ProcId $procId -Restore | Out-Null
    Start-Sleep -Seconds 3

    & $inputalive -ProcId $procId | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "instance $Instance has dead menu input; relaunching it"
        Stop-Process -Id $procId -Force -Confirm:$false
        Start-Sleep -Seconds 3
        return $null
    }

    & $agent load --pid $procId --load-save $Save --load-via-menu | Out-Null
    if ($LASTEXITCODE -eq 0) { return $procId }

    Write-Warning "instance $Instance reported live input but did not load; relaunching"
    Stop-Process -Id $procId -Force -Confirm:$false
    Start-Sleep -Seconds 3
    return $null
}

function Start-InstanceWithRetries {
    param([string]$Path, [int]$Instance, [int]$X)

    for ($try = 1; $try -le 5; $try++) {
        $result = Start-Instance -Path $Path -Instance $Instance -X $X
        if ($result) { return $result }
        Write-Warning "instance ${Instance}: relaunch $try"
    }

    throw "instance $Instance never came up with working menu input after 5 launches"
}

$pid1 = Start-InstanceWithRetries -Path $Game1 -Instance 1 -X 0
$pid2 = Start-InstanceWithRetries -Path $Game2 -Instance 2 -X 830

Write-Host "both instances in-world: $pid1 (host) and $pid2 (client)"

if ($NoAgents) { return }

Start-Process -FilePath $agent -ArgumentList @(
    'run', '--pid', $pid1, '--kotor', '--host', '--port', $Port, '--name', 'Lanik') | Out-Null
Start-Sleep -Seconds 3
Start-Process -FilePath $agent -ArgumentList @(
    'run', '--pid', $pid2, '--kotor', '--join', "127.0.0.1:$Port", '--name', 'Malak') | Out-Null

Start-Sleep -Seconds 8
'agents started; phantoms should be spawning. Plugin logs:'
"  $(Split-Path $Game1)\kotorcoop-plugin.log"
"  $(Split-Path $Game2)\kotorcoop-plugin.log"
