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
    # The game executable's file name in both installs. A copy under another name (for example
    # swkotor_np.exe) runs without a per-image debug setting such as the rig's full page heap.
    [string]$ExeName = 'swkotor.exe',
    [int]$Port = 27140,
    [string]$Name1 = 'Lanik',
    [string]$Name2 = 'Malak',
    [string]$WorkDir = "$env:TEMP\kotorcoop-demo",
    [int]$Save = 1,
    # A published agent lets source builds continue while this live rig is running.
    [string]$AgentPath = '',
    [switch]$Stop,
    [switch]$SkipLaunch,
    [switch]$ClientNewCharacter,
    # Like -ClientNewCharacter (instance 2 left at the main menu, no save) but with the shipped
    # default join_mode = auto: the join presses New Game itself the first time, and carries
    # saves\kotorcoop-character.* on every join after. Delete that sidecar in $Game2 first to
    # test the first-join leg; leave it to test the later-join leg.
    [switch]$ClientAutoCharacter,
    # Extra raw args appended to instance 1/2's own `agent run` invocation — e.g.
    # -Agent1ExtraArgs @('--enemy-census-cap','5') to shrink the host's combat-census
    # truncation for a liveness-reconciliation live test (docs/research/creature-liveness.md).
    # Empty by default: no effect on the ordinary demo.
    [string[]]$Agent1ExtraArgs = @(),
    [string[]]$Agent2ExtraArgs = @(),
    # Keeps both windows off the user's visible desktop and never activates them (no
    # SetForegroundWindow, no SW_RESTORE) for the "play while the rig runs" workflow
    # (docs/research/isolated-rig.md). Positions the pair at ($MonitorX,$MonitorY) and
    # ($MonitorX+830,$MonitorY) instead of (0,0)/(830,0), and un-minimises with
    # SW_SHOWNOACTIVATE instead of SW_RESTORE. tools\shot.ps1's PrintWindow path and
    # tools\click.ps1/blockpoke's `input` verb both work regardless of position or
    # occlusion, so screenshots and control still work; see the doc for the one remaining
    # gap (the automated menu-load step still focuses the window briefly).
    [switch]$Isolated,
    # Where the windows sit. Default 0,0: on the primary monitor but ghosted (invisible,
    # click-through, see movewin.ps1 -Ghost), because an off-monitor D3D window captures black.
    # Older note: 6000px right of the origin was the first choice, well past
    # any normal multi-monitor span (GetSystemMetrics(SM_CXVIRTUALSCREEN) on this rig's own
    # desk is 4480px). Point this at a real monitor's origin instead (e.g. a second GPU
    # output nobody looks at, or a virtual/indirect display driver's origin once one is
    # installed) to have DWM compose the windows somewhere concretely off-screen rather than
    # merely past the edge of the virtual desktop.
    [int]$MonitorX = 0,
    [int]$MonitorY = 0
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$procName = [IO.Path]::GetFileNameWithoutExtension($ExeName)
$agent = $AgentPath ? $AgentPath : (Join-Path $repo 'src\KotorCoop.Agent\bin\Debug\net10.0-windows\KotorCoop.Agent.exe')

# The two placements below (main-menu client parked at instance-2's slot, and the final
# side-by-side layout) share this pair so -Isolated only has to be decided once.
$pos1X = $Isolated ? $MonitorX : 0
$pos1Y = $Isolated ? $MonitorY : 0
$pos2X = $Isolated ? ($MonitorX + 830) : 830
$pos2Y = $Isolated ? $MonitorY : 0

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

# The same single-key rewrite, for any key. Set-AutoStartAgent stays as it is rather than
# becoming a call to this: it is referenced from the -Stop path, which must keep working even
# if this one is edited, and it is the only key the rig writes on EVERY run.
function Set-CoopIniKey {
    param([string]$Directory, [string]$Key, [string]$Value)

    $path = Join-Path $Directory 'coop.ini'
    $lines = if (Test-Path $path) { @(Get-Content $path) } else { @('[coop]') }

    $found = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "^\s*$([regex]::Escape($Key))\s*=") {
            $lines[$i] = "$Key = $Value"
            $found = $true
        }
    }

    if (-not $found) { $lines += "$Key = $Value" }
    Set-Content -Path $path -Value $lines -Encoding ASCII
}

if ($Stop) {
    Get-Process KotorCoop.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process $procName -ErrorAction SilentlyContinue | Stop-Process -Force

    # Undo the rig's auto_start_agent=0 (written below so the rig's own visible agents are
    # the only ones attached). Leaving it off is what made a rig-touched install lose the
    # invisible-agent behaviour for REAL play afterwards: the player launches the game, no
    # agent starts, the Co-op flow silently does nothing, and nothing on screen says why.
    # The rig is a test harness; it must not permanently reconfigure the product.
    Set-AutoStartAgent -Directory $Game1 -Value '1'
    Set-AutoStartAgent -Directory $Game2 -Value '1'
    "restored auto_start_agent = 1 in both installs (the rig turns it off while running)"

    # Same rule for -ClientNewCharacter's key: the rig must not leave the second install
    # configured to send a real player through character creation on their next join.
    Set-CoopIniKey -Directory $Game2 -Key 'join_mode' -Value 'save'
    "restored join_mode = save in the client install"

    # Undo -Isolated's real-input drop (docs/research/isolated-rig.md's "Input isolation"
    # section) - the rig must not leave a real player's install silently eating their own
    # keyboard/mouse after the run ends.
    Set-CoopIniKey -Directory $Game1 -Key 'isolate_real_input' -Value '0'
    Set-CoopIniKey -Directory $Game2 -Key 'isolate_real_input' -Value '0'
    "restored isolate_real_input = 0 in both installs"

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

# -Isolated: also stop REAL keyboard/mouse input from reaching either game while it runs
# invisibly on the user's own monitor (docs/research/isolated-rig.md's "Input isolation"
# section). The DirectInput mouse device is polled once a frame for the camera look axes
# regardless of the window's real OS focus, so a ghosted window can otherwise let the user's
# real mouse spin the camera (or a stray keypress act) while they play something else.
# Restored to 0 on -Stop, same as auto_start_agent above.
if ($Isolated) {
    Set-CoopIniKey -Directory $Game1 -Key 'isolate_real_input' -Value '1'
    Set-CoopIniKey -Directory $Game2 -Key 'isolate_real_input' -Value '1'
    "isolate_real_input = 1 written to both installs for this run (real keyboard/mouse input " +
        "to either game is dropped; the plugin's own input/gamepad console verbs still work)"
}

# -ClientNewCharacter: the first-time-joiner rig. The client is left AT THE MAIN MENU with no
# save loaded — that is the whole scenario, a player who has never played this game — and its
# coop.ini asks for the chargen join. The agent then arms the plugin's capture and waits for a
# character to exist; the run's own steps (or a human) drive New Game and character creation.
# -Stop puts join_mode back to `save`.
$clientJoinMode = $ClientNewCharacter ? 'newcharacter' : ($ClientAutoCharacter ? 'auto' : 'save')
Set-CoopIniKey -Directory $Game2 -Key 'join_mode' -Value $clientJoinMode
# Either at-the-menu rig: instance 2 gets no save, and the join itself brings the character.
$clientAtMenu = $ClientNewCharacter -or $ClientAutoCharacter
if ($clientAtMenu) {
    "client install set to join_mode = $clientJoinMode; it will NOT load a save"
}

if (-not $SkipLaunch) {
    if (Get-Process $procName -ErrorAction SilentlyContinue) {
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
    & $agent launch --load-save $Save --game-path (Join-Path $Game1 $ExeName)
    if ($LASTEXITCODE -ne 0) { throw 'instance 1 failed to launch or load' }

    $first = @(Get-Process $procName)[0]
    & (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $first.Id -Minimize

    'launching instance 2 (renamed mutex) ...'
    & $agent launch --instance 2 --game-path (Join-Path $Game2 $ExeName)
    if ($LASTEXITCODE -ne 0) { throw 'instance 2 failed to launch' }

    # Load as a separate step, after the menu has had time to settle. Folding it into the
    # launch above is what used to fail; 14s is what has been observed to work.
    Start-Sleep -Seconds 14
    $second = @(Get-Process $procName | Sort-Object StartTime)[1]

    if ($clientAtMenu) {
        # ⚠ THE POINT OF THE SWITCH: instance 2 gets NO save. Loading one here would give it
        # a character to carry, which is precisely the case join_mode = newcharacter does not
        # cover. It sits at the main menu until New Game and character creation are driven.
        "instance 2 left at the main menu (join_mode = $clientJoinMode): no save loaded"
        & (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $second.Id -X $pos2X -Y $pos2Y
        $global:LASTEXITCODE = 0
    }
    else {

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
            & $movewin -ProcId $second.Id -Restore -NoActivate:$Isolated
            Start-Sleep -Seconds 3
            & $agent load --pid $second.Id --load-save $Save
        }
    }

    if ($LASTEXITCODE -ne 0) {
        & (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $second.Id -X $pos2X -Y $pos2Y
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

    } # end of the load block (skipped by -ClientNewCharacter / -ClientAutoCharacter)
}

$procs = @(Get-Process $procName | Sort-Object StartTime)
if ($procs.Count -lt 2) { throw "expected 2 swkotor processes, found $($procs.Count)" }
$pid1 = $procs[0].Id
$pid2 = $procs[1].Id

# Side by side, so one screenshot shows both players. Safe now that both are loaded.
# -Isolated: un-minimise WITHOUT activating, and land both off the user's visible desktop
# instead of at the screen origin.
& (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $pid1 -Restore -NoActivate:$Isolated
Start-Sleep -Milliseconds 500
& (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $pid1 -X $pos1X -Y $pos1Y
& (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $pid2 -X $pos2X -Y $pos2Y

if ($Isolated) {
    & (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $pid1 -Ghost
    & (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $pid2 -Ghost
    "isolated: both windows placed at x=$pos1X.. (never activated; tools\shot.ps1 and " +
        "tools\click.ps1/blockpoke's 'input' verb both work regardless of position or occlusion)"
}

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
    # A fresh log per launch (the previous one is kept as .prev): the agent appends, and
    # verify.ps1's whole-log checks (per-frame spam, crash scan) otherwise read lines from earlier days.
    if ($LogPath -and (Test-Path $LogPath)) { Move-Item -Path $LogPath -Destination "$LogPath.prev" -Force }
    $quoted = ($AgentArgs | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ' '
    $command = "`$Host.UI.RawUI.WindowTitle = '$Title'; & '$agent' $quoted"

    # -NoExit so a crashed or exited agent leaves its last words on screen instead of the
    # window vanishing with the evidence in it. The pid is recorded so -Stop can close the
    # window too — otherwise every run leaves another pair of dead terminals behind.
    # -Isolated: start minimised so the rig never puts a window in front of the user.
    $style = $Isolated ? 'Hidden' : 'Normal'
    $console = Start-Process -FilePath 'pwsh' -ArgumentList '-NoExit', '-Command', $command -WindowStyle $style -PassThru
    Add-Content -Path (Join-Path $WorkDir 'consoles.txt') -Value $console.Id
}

Start-AgentConsole -Title "coop agent 1 - $Name1 (host)" -LogPath (Join-Path $WorkDir 'agent1.log') `
    -AgentArgs (@('run', '--pid', $pid1, '--kotor', '--host', '--port', $Port, '--name', $Name1, '--drop-file', $drop1,
                 '--log', (Join-Path $WorkDir 'agent1.log')) + $Agent1ExtraArgs)

Start-Sleep -Seconds 4

Start-AgentConsole -Title "coop agent 2 - $Name2 (join)" -LogPath (Join-Path $WorkDir 'agent2.log') `
    -AgentArgs (@('run', '--pid', $pid2, '--kotor', '--join', "127.0.0.1:$Port", '--name', $Name2, '--drop-file', $drop2,
                 '--log', (Join-Path $WorkDir 'agent2.log')) + $Agent2ExtraArgs)

. (Join-Path $PSScriptRoot 'Wait-CoopRigReady.ps1')
"waiting for both native worlds and peer phantoms..."
Wait-CoopRigReady -ProcessIds @($pid1, $pid2) | Write-Output

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
