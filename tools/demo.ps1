# Brings up the whole two-player co-op demo: two KOTOR instances side by side, each with
# the in-process plugin, and an agent per instance bridging them over UDP.
#
# The second instance is started through `agent launch --instance 2`, which gives it its
# own single-instance mutex name. Without that the retail guard makes it exit immediately
# with code -1. See src/KotorCoop.Agent/InstanceLauncher.cs.
#
# Fully unattended: both instances launch, load a save and connect on their own.
#
#   pwsh tools\demo.ps1              # bring the whole thing up
#   pwsh tools\demo.ps1 -Save 2      # load a different save slot (1 = top of the list)
#   pwsh tools\demo.ps1 -Stop        # shut everything down
param(
    [string]$Game1 = 'C:\GOG Games\Star Wars KOTOR (GOG)',
    [string]$Game2 = 'D:\Games\swkotor-gog-p2',
    [int]$Port = 27140,
    [string]$Name1 = 'Lanik',
    [string]$Name2 = 'Malak',
    [string]$WorkDir = "$env:TEMP\kotorcoop-demo",
    [int]$Save = 1,
    [switch]$Stop,
    [switch]$SkipLaunch
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$agent = Join-Path $repo 'src\KotorCoop.Agent\bin\Debug\net10.0-windows\KotorCoop.Agent.exe'

if ($Stop) {
    Get-Process KotorCoop.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process swkotor -ErrorAction SilentlyContinue | Stop-Process -Force
    'stopped agents and game instances'
    return
}

if (-not (Test-Path $agent)) { throw "agent not built: $agent   (run: dotnet build KotorCoop.slnx)" }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

# ⚠ TWO THINGS START AN AGENT. The plugin's AgentLauncher starts one per game the moment
# dinput8.dll loads (coop.ini auto_start_agent, ON by default), and this script starts one per
# game as well. Both attach to the same swkotor.exe, both write the same shared block, and both
# join the session — which is how a two-instance run came to report a THIRD peer and then kill
# the host. The agent now refuses to be the second one attached to a pid, so a stray one is
# inert rather than a ghost player; turning auto-start off as well means the agents that do run
# are the ones with the host/join roles this script chose, rather than coop.ini's.
function Set-AutoStartAgent {
    param([string]$Directory, [string]$Value)

    $path = Join-Path $Directory 'coop.ini'
    $lines = if (Test-Path $path) { @(Get-Content $path) } else { @('[coop]') }

    $found = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*auto_start_agent\s*=') {
            $lines[$i] = "auto_start_agent = $Value"
            $found = $true
        }
    }

    if (-not $found) { $lines += "auto_start_agent = $Value" }
    Set-Content -Path $path -Value $lines -Encoding ASCII
}

Set-AutoStartAgent -Directory $Game1 -Value '0'
Set-AutoStartAgent -Directory $Game2 -Value '0'

if (-not $SkipLaunch) {
    if (Get-Process swkotor -ErrorAction SilentlyContinue) {
        throw 'swkotor is already running; use -Stop first, or -SkipLaunch to attach to what is up'
    }

    # The recipe here is load-bearing and was arrived at the hard way. Each instance is
    # driven into its save while it is the ONLY visible game window, at the position it
    # launched at, with the other instance minimised. Three separate things break
    # otherwise: a click on a background window is eaten by the activation, an in-world
    # KOTOR keeps reclaiming the foreground, and the game tracks its own cursor from
    # relative DirectInput deltas rather than the OS pointer, so clicks stop landing once
    # a window has been moved. Minimising is safe - a minimised instance still runs at
    # full frame rate (measured).
    "launching instance 1 and loading save $Save ..."
    & $agent launch --load-save $Save --game-path (Join-Path $Game1 'swkotor.exe')
    if ($LASTEXITCODE -ne 0) { throw 'instance 1 failed to launch or load' }

    $first = @(Get-Process swkotor)[0]
    & (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $first.Id -Minimize

    'launching instance 2 (renamed mutex) ...'
    & $agent launch --instance 2 --game-path (Join-Path $Game2 'swkotor.exe')
    if ($LASTEXITCODE -ne 0) { throw 'instance 2 failed to launch' }

    # Load as a separate step, after the menu has had time to settle. Folding it into the
    # launch above is what used to fail; 14s is what has been observed to work.
    Start-Sleep -Seconds 14
    $second = @(Get-Process swkotor | Sort-Object StartTime)[1]

    "loading save $Save in instance 2 ..."
    & $agent load --pid $second.Id --load-save $Save

    if ($LASTEXITCODE -ne 0) {
        # Menu input injection fails intermittently (traps 22 and 25); a real
        # activation cycle — minimise then restore — is the one thing known to revive
        # it. Retry a few times before falling back to a human.
        $movewin = Join-Path $PSScriptRoot 'movewin.ps1'
        for ($attempt = 1; $attempt -le 4 -and $LASTEXITCODE -ne 0; $attempt++) {
            Write-Warning "instance 2 auto-load failed; activation-cycling the window (retry $attempt/4)"
            & $movewin -ProcId $second.Id -Minimize
            Start-Sleep -Seconds 2
            & $movewin -ProcId $second.Id -Restore
            Start-Sleep -Seconds 3
            & $agent load --pid $second.Id --load-save $Save
        }
    }

    if ($LASTEXITCODE -ne 0) {
        & (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $second.Id -X 830 -Y 0
        if ([Console]::IsInputRedirected) {
            # Non-interactive run (trap 24: Read-Host would die, not wait). Leave the
            # menu up and keep going; the agent attaches once a save is loaded by
            # whatever drives this script.
            Write-Warning 'instance 2 did not auto-load and no human is attached; continuing without it.'
        }
        else {
            Write-Warning 'instance 2 did not auto-load. Load a save in that window by hand, then press Enter.'
            Read-Host | Out-Null
        }
    }
}

$procs = @(Get-Process swkotor | Sort-Object StartTime)
if ($procs.Count -lt 2) { throw "expected 2 swkotor processes, found $($procs.Count)" }
$pid1 = $procs[0].Id
$pid2 = $procs[1].Id

# Side by side, so one screenshot shows both players. Safe now that both are loaded.
& (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $pid1 -Restore
Start-Sleep -Milliseconds 500
& (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $pid1 -X 0 -Y 0
& (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $pid2 -X 830 -Y 0

''
"instance 1: pid $pid1   instance 2: pid $pid2"
''

# The firewall prompt steals foreground and blocks injected input; it has an empty window
# title, so it can only be dismissed by killing its host process.
Get-Process PickerHost -ErrorAction SilentlyContinue | Stop-Process -Force

$drop1 = Join-Path $WorkDir 'drop1.txt'
$drop2 = Join-Path $WorkDir 'drop2.txt'
Remove-Item $drop1, $drop2 -ErrorAction SilentlyContinue

Start-Process -FilePath $agent -ArgumentList `
    'run', '--pid', $pid1, '--kotor', '--host', '--port', $Port, '--name', $Name1, '--drop-file', $drop1 `
    -RedirectStandardOutput (Join-Path $WorkDir 'agent1.log') `
    -RedirectStandardError  (Join-Path $WorkDir 'agent1.err')

Start-Sleep -Seconds 4

Start-Process -FilePath $agent -ArgumentList `
    'run', '--pid', $pid2, '--kotor', '--join', "127.0.0.1:$Port", '--name', $Name2, '--drop-file', $drop2 `
    -RedirectStandardOutput (Join-Path $WorkDir 'agent2.log') `
    -RedirectStandardError  (Join-Path $WorkDir 'agent2.err')

Start-Sleep -Seconds 8

''
'co-op is live. Each game should now hold a phantom of the other player.'
''
"  agent readouts : $WorkDir\agent1.log, agent2.log"
"  plugin logs    : $Game1\kotorcoop-plugin.log"
"                   $Game2\kotorcoop-plugin.log"
''
'drop an item from either side:'
"  Set-Content '$drop1' g_w_lghtsbr01"
"  Set-Content '$drop2' g_i_medeqpmnt01"
''
'shut down with: pwsh tools\demo.ps1 -Stop'
