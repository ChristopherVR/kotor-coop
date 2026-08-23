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
    [switch]$SkipLaunch,
    # Extra raw args appended to instance 1/2's own `agent run` invocation — e.g.
    # -Agent1ExtraArgs @('--enemy-census-cap','5') to shrink the host's combat-census
    # truncation for a liveness-reconciliation live test (docs/research/creature-liveness.md).
    # Empty by default: no effect on the ordinary demo.
    [string[]]$Agent1ExtraArgs = @(),
    [string[]]$Agent2ExtraArgs = @()
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$agent = Join-Path $repo 'src\KotorCoop.Agent\bin\Debug\net10.0-windows\KotorCoop.Agent.exe'

# Defined ABOVE the -Stop block because -Stop restores the flag on the way out. (A
# PowerShell function does not exist until its definition line has executed.)
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

if ($Stop) {
    Get-Process KotorCoop.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process swkotor -ErrorAction SilentlyContinue | Stop-Process -Force

    # Undo the rig's auto_start_agent=0 (written below so the rig's own visible agents are
    # the only ones attached). Leaving it off is what made a rig-touched install lose the
    # invisible-agent behaviour for REAL play afterwards: the player launches the game, no
    # agent starts, the Co-op flow silently does nothing, and nothing on screen says why.
    # The rig is a test harness; it must not permanently reconfigure the product.
    Set-AutoStartAgent -Directory $Game1 -Value '1'
    Set-AutoStartAgent -Directory $Game2 -Value '1'
    "restored auto_start_agent = 1 in both installs (the rig turns it off while running)"

    # The agent windows are launched with -NoExit, so killing the agent leaves an empty
    # console sitting there. Left alone they pile up one pair per run until the desktop is
    # full of dead terminals, and the next run's windows are indistinguishable from the
    # last one's. Close them here: the run records its console pids, and the title match is
    # the fallback for windows left over from a run whose record is gone.
    $record = Join-Path $WorkDir 'consoles.txt'
    $closed = 0

    if (Test-Path $record) {
        foreach ($line in Get-Content $record) {
            $consolePid = 0
            if ([int]::TryParse($line.Trim(), [ref]$consolePid) -and $consolePid -gt 0) {
                $console = Get-Process -Id $consolePid -ErrorAction SilentlyContinue
                # Check the name: pids are recycled, and killing a stranger would be worse
                # than leaving a window open.
                if ($console -and $console.ProcessName -eq 'pwsh') {
                    $console | Stop-Process -Force
                    $closed++
                }
            }
        }

        Remove-Item $record -Force -ErrorAction SilentlyContinue
    }

    foreach ($console in Get-Process pwsh -ErrorAction SilentlyContinue) {
        if ($console.Id -ne $PID -and $console.MainWindowTitle -like 'coop agent *') {
            $console | Stop-Process -Force
            $closed++
        }
    }

    "stopped agents and game instances; closed $closed agent console(s)"
    return
}

if (-not (Test-Path $agent)) { throw "agent not built: $agent   (run: dotnet build KotorCoop.slnx)" }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

# Start the console record clean: a leftover list from an earlier run names pids that have
# since been recycled onto unrelated processes.
Remove-Item (Join-Path $WorkDir 'consoles.txt') -Force -ErrorAction SilentlyContinue

# ⚠ TWO THINGS START AN AGENT. The plugin's AgentLauncher starts one per game the moment
# dinput8.dll loads (coop.ini auto_start_agent, ON by default), and this script starts one per
# game as well. Both attach to the same swkotor.exe, both write the same shared block, and both
# join the session — which is how a two-instance run came to report a THIRD peer and then kill
# the host. The agent now refuses to be the second one attached to a pid, so a stray one is
# inert rather than a ghost player; turning auto-start off as well means the agents that do run
# are the ones with the host/join roles this script chose, rather than coop.ini's.
# (-Stop restores the flag to 1: the rig must not permanently reconfigure the product.)
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

# ⚠ Each agent gets its own VISIBLE console, and writes its own log file.
#
# They used to be started with -RedirectStandardOutput straight to a log, which is why the
# two agent windows sat there blank: redirecting the stream is what stops it reaching a
# console. Reported from use as "we also want to see logs in those running consoles".
#
# The window is NOT piped, deliberately. The readout redraws in place, which needs a real
# console — put a pipe in the way and every redraw appends instead of overwriting, which is
# the log "spamming" that piping through Tee-Object produced. The file comes from the
# agent's own --log instead: one compact timestamped line per change, in the same place
# every tool and handoff note already expects it.
function Start-AgentConsole {
    param([string]$Title, [string[]]$AgentArgs, [string]$LogPath)

    # ⚠ NOT piped. The agent redraws its readout IN PLACE, which needs a real console: put a
    # pipe (Tee-Object) in the way and Console.IsOutputRedirected goes true, every redraw
    # appends instead of overwriting, and the window fills with thousands of copies of the
    # same screen. That was tried and reported straight back as the log "spamming".
    #
    # So the window gets the live colour readout, and a SECOND agent writes nothing — the
    # log file is produced by the agent itself, which emits one compact timestamped line per
    # change when its output is redirected. Here that redirection is the tee'd copy started
    # separately below, so both forms exist without either spoiling the other.
    $quoted = ($AgentArgs | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ' '
    $command = "`$Host.UI.RawUI.WindowTitle = '$Title'; & '$agent' $quoted"

    # -NoExit so a crashed or exited agent leaves its last words on screen instead of the
    # window vanishing with the evidence in it. The pid is recorded so -Stop can close the
    # window too — otherwise every run leaves another pair of dead terminals behind.
    $console = Start-Process -FilePath 'pwsh' -ArgumentList '-NoExit', '-Command', $command -PassThru
    Add-Content -Path (Join-Path $WorkDir 'consoles.txt') -Value $console.Id
}

Start-AgentConsole -Title "coop agent 1 - $Name1 (host)" -LogPath (Join-Path $WorkDir 'agent1.log') `
    -AgentArgs (@('run', '--pid', $pid1, '--kotor', '--host', '--port', $Port, '--name', $Name1, '--drop-file', $drop1,
                 '--log', (Join-Path $WorkDir 'agent1.log')) + $Agent1ExtraArgs)

Start-Sleep -Seconds 4

Start-AgentConsole -Title "coop agent 2 - $Name2 (join)" -LogPath (Join-Path $WorkDir 'agent2.log') `
    -AgentArgs (@('run', '--pid', $pid2, '--kotor', '--join', "127.0.0.1:$Port", '--name', $Name2, '--drop-file', $drop2,
                 '--log', (Join-Path $WorkDir 'agent2.log')) + $Agent2ExtraArgs)

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
