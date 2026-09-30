# Brings up a THREE-instance co-op session: one host and two joiners, unattended.
#
# Everything in this project had only ever been run with two players, and the interesting
# bugs are the ones that need a third: a peer list with two entries in it, a slot table that
# can be reordered, two attackers on one phantom. This is the harness that gets there.
#
#   pwsh tools\demo3.ps1                 # bring all three up and connect them
#   pwsh tools\demo3.ps1 -Save 2         # a different save slot (1 = top of the list)
#   pwsh tools\demo3.ps1 -CreateGame3    # make the third game directory first (~6 GB copy)
#   pwsh tools\demo3.ps1 -Stop           # shut everything down
#   pwsh tools\demo3.ps1 -NoAgents       # three games in-world, no session (for hand tests)
#
# WHAT A THIRD INSTANCE NEEDS, beyond what demo.ps1 already does:
#
#  1. A RENAMED SINGLE-INSTANCE MUTEX. Retail creates "swkotor" and exits -1 if it already
#     exists. `agent launch --instance N` starts the process suspended and rewrites that
#     string in .rdata to "kcoopN" before resuming (InstanceLauncher.cs). N is clamped 1..9
#     and "kcoop9" still fits in the 7 characters the stock name reserves, so a third, fourth
#     and fifth instance need no new code at all - only a distinct N.
#
#  2. A THIRD GAME DIRECTORY. Not for the mutex - for everything else that is per-directory:
#     saves, swkotor.ini, coop.ini, the plugin log and the agent log. Two instances sharing
#     one directory would fight over the save folder and interleave their logs.
#     -CreateGame3 copies it from instance 2's, which already has the plugin and the override
#     assets installed.
#
#  3. EXACTLY ONE AGENT PER GAME. See the auto_start_agent block below - this is the trap
#     that produced a phantom third peer in a two-player session.
param(
    [string]$Game1 = 'C:\GOG Games\Star Wars KOTOR (GOG)',
    [string]$Game2 = 'D:\Games\swkotor-gog-p2',
    [string]$Game3 = 'D:\Games\swkotor-gog-p3',
    [int]$Port = 27140,
    [string]$Name1 = 'Lanik',
    [string]$Name2 = 'Malak',
    [string]$Name3 = 'Bastila',
    [string]$WorkDir = "$env:TEMP\kotorcoop-demo3",
    [int]$Save = 1,
    [string]$AgentPath = '',
    [switch]$Stop,
    [switch]$CreateGame3,
    [switch]$NoAgents,
    [switch]$SkipLaunch,
    # Same isolated-rig switch as demo.ps1 (docs/research/isolated-rig.md): keeps all three
    # windows off the user's visible desktop and never activates them for the FINAL layout
    # (the per-instance launch positioning above stays cosmetic either way).
    [switch]$Isolated,
    [int]$MonitorX = 0,
    [int]$MonitorY = 0
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$agent = $AgentPath ? $AgentPath : (Join-Path $repo 'src\KotorCoop.Agent\bin\Debug\net10.0-windows\KotorCoop.Agent.exe')
$movewin = Join-Path $PSScriptRoot 'movewin.ps1'
$inputalive = Join-Path $PSScriptRoot 'inputalive.ps1'

# Defined ABOVE the -Stop block (same reasoning as demo.ps1's own copy): -Stop restores
# isolate_real_input on the way out, and a PowerShell function does not exist until its
# definition line has executed.
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
    Get-Process KotorCoop.Agent -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false
    Get-Process swkotor -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false

    # Undo -Isolated's real-input drop (docs/research/isolated-rig.md's "Input isolation"
    # section) in all three installs - the rig must not leave a real player's install silently
    # eating their own keyboard/mouse after the run ends.
    foreach ($g in $Game1, $Game2, $Game3) {
        if (Test-Path (Join-Path $g 'coop.ini')) {
            Set-CoopIniKey -Directory $g -Key 'isolate_real_input' -Value '0'
        }
    }

    'stopped agents and game instances; restored isolate_real_input = 0 in every install'
    return
}

if (-not (Test-Path $agent)) { throw "agent not built: $agent   (run: dotnet build KotorCoop.slnx)" }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

# ---------------------------------------------------------------- game dir 3 ----
#
# Copying a KOTOR install is ~6 GB, so it is never done implicitly: without -CreateGame3 the
# script says exactly what to run and stops. Instance 2's directory is the source because it
# already has dinput8.dll, coop.ini and the Override assets in place.
if (-not (Test-Path (Join-Path $Game3 'swkotor.exe'))) {
    if (-not $CreateGame3) {
        Write-Host ''
        Write-Host "The third game directory does not exist: $Game3"
        Write-Host 'Re-run with -CreateGame3, or make it by hand:'
        Write-Host ""
        Write-Host "    robocopy `"$Game2`" `"$Game3`" /MIR /NFL /NDL /NJH /NJS /NP"
        Write-Host ''
        throw "missing $Game3"
    }

    if (-not (Test-Path (Join-Path $Game2 'swkotor.exe'))) {
        throw "cannot create $Game3 - the source install $Game2 is not there either"
    }

    $sourceGb = [math]::Round(
        ((Get-ChildItem $Game2 -Recurse -File -ErrorAction SilentlyContinue |
            Measure-Object Length -Sum).Sum / 1GB), 1)
    $targetDrive = (Split-Path $Game3 -Qualifier)
    $freeGb = [math]::Round(
        ((Get-PSDrive -Name $targetDrive.TrimEnd(':')).Free / 1GB), 1)

    Write-Host "copying $Game2 -> $Game3  ($sourceGb GB; $freeGb GB free on $targetDrive)"
    if ($freeGb -lt ($sourceGb + 2)) {
        throw "not enough free space on ${targetDrive}: need ~$($sourceGb + 2) GB, have $freeGb GB"
    }

    robocopy $Game2 $Game3 /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    # robocopy's exit codes are a bit field; anything under 8 is success.
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with code $LASTEXITCODE" }
    "created $Game3"
}

$games = @($Game1, $Game2, $Game3)
$names = @($Name1, $Name2, $Name3)

foreach ($g in $games) {
    if (-not (Test-Path (Join-Path $g 'swkotor.exe'))) { throw "not a KOTOR install: $g" }
    if (-not (Test-Path (Join-Path $g 'dinput8.dll'))) {
        Write-Warning "$g has no dinput8.dll - the plugin is not installed there. Run: pwsh src\KotorCoop.Plugin\build.ps1 -Install -GameDir '$g'"
    }
}

# ------------------------------------------------------- one agent per game ----
#
# ⚠ TWO THINGS START AN AGENT. The plugin's AgentLauncher starts one per game the moment
# dinput8.dll loads (coop.ini auto_start_agent, ON by default), and this script starts one
# per game as well. Both attach to the same swkotor.exe, both find the same shared block,
# and both join the session - which is how a two-instance run ended up reporting a third
# peer and then killing the host.
#
# Two defences, and both are wanted. The agent now takes a per-pid mutex and a second one
# refuses to attach (exit 3), so a stray agent is inert rather than a ghost player. And this
# script turns auto-start OFF in each coop.ini, so the agents that DO run are the ones it
# started, with the host/join roles it chose - the plugin's own agent uses --menu and
# coop.ini's mode, which for these directories says something different.
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

function Set-CoopName {
    param([string]$Directory, [string]$PlayerName)

    $path = Join-Path $Directory 'coop.ini'
    $lines = if (Test-Path $path) { @(Get-Content $path) } else { @('[coop]') }

    $found = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*name\s*=') { $lines[$i] = "name = $PlayerName"; $found = $true }
    }

    if (-not $found) { $lines += "name = $PlayerName" }
    Set-Content -Path $path -Value $lines -Encoding ASCII
}

for ($i = 0; $i -lt 3; $i++) {
    Set-AutoStartAgent -Directory $games[$i] -Value '0'
    Set-CoopName -Directory $games[$i] -PlayerName $names[$i]
    if ($Isolated) { Set-CoopIniKey -Directory $games[$i] -Key 'isolate_real_input' -Value '1' }
}
'auto_start_agent = 0 written to all three coop.ini files (this script owns the agents)'
if ($Isolated) {
    # Same "Input isolation" switch as demo.ps1 -Isolated (docs/research/isolated-rig.md):
    # drops real keyboard/mouse input to every instance while it runs invisibly. Restored to 0
    # on -Stop above.
    'isolate_real_input = 1 written to all three coop.ini files for this run'
}

# ------------------------------------------------------------------ launching ----
#
# The recipe is session.ps1's, which is the one that survives repetition: wait for the pid,
# wait for the window, activation-cycle it (traps 22/25 - a never-activated window ignores
# every injected click), ASK the game whether its menu input is alive, then load. A dead
# instance is relaunched rather than clicked at, because clicking never recovers it.
function Start-Instance {
    param([string]$Directory, [int]$Instance, [int]$X, [int]$Y)

    $exe = Join-Path $Directory 'swkotor.exe'
    $before = @(Get-Process swkotor -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)

    # ⚠ INSTANCE 1 LOADS ITS SAVE AS PART OF THE LAUNCH. demo.ps1 has always done it that way
    # for the first instance and it is the only combination with a day of evidence behind it;
    # splitting the load into a separate step (as this script did) failed every relaunch.
    # Instances 2 and 3 keep the separate load — for THEM, folding it in is what fails, which
    # is the opposite asymmetry and is recorded as such in demo.ps1.
    if ($Instance -eq 1) { & $agent launch --windowed --load-save $Save --game-path $exe | Out-Null }
    else                 { & $agent launch --instance $Instance --game-path $exe | Out-Null }

    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 500
        $now = @(Get-Process swkotor -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
        $new = $now | Where-Object { $_ -notin $before }
    } while (-not $new -and (Get-Date) -lt $deadline)

    if (-not $new) { throw "instance $Instance never appeared" }
    $procId = @($new)[0]

    $windowDeadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 500
        $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if ($proc) { $proc.Refresh() }
    } while ($proc -and $proc.MainWindowHandle -eq [IntPtr]::Zero -and (Get-Date) -lt $windowDeadline)

    Write-Host "instance $Instance is pid $procId  ($Directory)"

    # ⚠ DO NOT TOUCH THE WINDOW BEFORE LOADING. This used to position the window and then
    # minimise/restore it before the load, and all four activation cycles then failed for
    # instance 2 — on a machine where demo.ps1's instance 2 loads within a retry or two.
    # demo.ps1 launches, waits, loads, and positions the window only afterwards; moving a
    # window the game is still bringing up is the difference. Positioning happens at the end
    # of this function instead, once the save is in.
    Start-Sleep -Seconds 14

    # ⚠ THE PLUGIN LOAD ROUTE, not the menu one, and no input probe gating it.
    #
    # This used to demand `--load-via-menu` behind an inputalive check, and on a machine where
    # the two-instance harness loads first time every time it burned all five relaunches of
    # instance 1 reporting "dead menu input". The probe hovers a hardcoded menu pixel, and
    # three windows being shuffled for position do not present the same menu the two-window
    # layout does — so it was measuring its own assumptions, not the game.
    #
    # demo.ps1 has always used `agent load --load-save`, which tries the in-process plugin
    # route (no clicking at all) and falls back to the menu by itself. That is the path with a
    # day of evidence behind it, so the three-instance harness now takes it too. The retry
    # below is now driven by whether the load ACTUALLY SUCCEEDED, which is the honest signal.
    if ($Instance -eq 1) {
        # Already loaded by the launch above; place it, then park it.
        & $movewin -ProcId $procId -X $X -Y $Y | Out-Null
        & $movewin -ProcId $procId -Minimize | Out-Null
        return $procId
    }

    & $agent load --pid $procId --load-save $Save | Out-Null

    # ⚠ ACTIVATION-CYCLE BEFORE RELAUNCHING. Menu input injection fails intermittently
    # (Handoff traps 22 and 25) and a minimise/restore cycle is the one thing known to revive
    # it — demo.ps1 has done this for its second instance all along and reaches a session
    # nearly every time. This script skipped straight to killing and relaunching the process,
    # which is both slower and, measured here, does not work: instance 2 burned relaunch after
    # relaunch while the cheap fix was never tried.
    for ($cycle = 1; $cycle -le 4 -and $LASTEXITCODE -ne 0; $cycle++) {
        Write-Warning "instance $Instance auto-load failed; activation-cycling the window (retry $cycle/4)"
        & $movewin -ProcId $procId -Minimize | Out-Null
        Start-Sleep -Seconds 2
        & $movewin -ProcId $procId -Restore | Out-Null
        Start-Sleep -Seconds 3
        & $agent load --pid $procId --load-save $Save | Out-Null
    }

    if ($LASTEXITCODE -eq 0) {
        & $movewin -ProcId $procId -X $X -Y $Y | Out-Null
        # Park it. An in-world KOTOR keeps reclaiming the foreground, and the NEXT instance's
        # menu automation cannot reach a window that keeps losing focus. A minimised instance
        # still simulates at full frame rate (measured), so nothing is lost by doing this -
        # and with three instances there are two of them to keep out of the way, not one.
        & $movewin -ProcId $procId -Minimize | Out-Null
        return $procId
    }

    Write-Warning "instance $Instance did not load; relaunching"
    Stop-Process -Id $procId -Force -Confirm:$false
    Start-Sleep -Seconds 3
    return $null
}

function Start-InstanceWithRetries {
    param([string]$Directory, [int]$Instance, [int]$X, [int]$Y)

    for ($try = 1; $try -le 5; $try++) {
        $result = Start-Instance -Directory $Directory -Instance $Instance -X $X -Y $Y
        if ($result) { return $result }
        Write-Warning "instance ${Instance}: relaunch $try"
    }

    throw "instance $Instance never came up with working menu input after 5 launches"
}

if ($SkipLaunch) {
    $procs = @(Get-Process swkotor -ErrorAction SilentlyContinue | Sort-Object StartTime)
    if ($procs.Count -lt 3) { throw "expected 3 swkotor processes, found $($procs.Count)" }
    $pid1 = $procs[0].Id; $pid2 = $procs[1].Id; $pid3 = $procs[2].Id
}
else {
    if (Get-Process swkotor -ErrorAction SilentlyContinue) {
        throw 'swkotor is already running; use -Stop first, or -SkipLaunch to attach to what is up'
    }

    # Three 800-wide windows do not fit across one 1920 screen, so the third goes underneath.
    # Position is cosmetic - the checks read memory, not pixels.
    $pid1 = Start-InstanceWithRetries -Directory $Game1 -Instance 1 -X 0   -Y 0
    $pid2 = Start-InstanceWithRetries -Directory $Game2 -Instance 2 -X 830 -Y 0
    $pid3 = Start-InstanceWithRetries -Directory $Game3 -Instance 3 -X 415 -Y 340
}

# Restore all three now that nothing else needs to steal the foreground. -Isolated:
# un-minimise WITHOUT activating, and land all three off the user's visible desktop instead
# of at the screen origin.
foreach ($p in $pid1, $pid2, $pid3) { & $movewin -ProcId $p -Restore -NoActivate:$Isolated | Out-Null }
Start-Sleep -Milliseconds 500
$offX = $Isolated ? $MonitorX : 0
$offY = $Isolated ? $MonitorY : 0
& $movewin -ProcId $pid1 -X ($offX + 0)   -Y ($offY + 0)   | Out-Null
& $movewin -ProcId $pid2 -X ($offX + 830) -Y ($offY + 0)   | Out-Null
& $movewin -ProcId $pid3 -X ($offX + 415) -Y ($offY + 340) | Out-Null

if ($Isolated) {
    foreach ($p in $pid1, $pid2, $pid3) { & $movewin -ProcId $p -Ghost }
    "isolated: all three windows placed at x=$offX.. (never activated; tools\shot.ps1 and " +
        "tools\click.ps1/blockpoke's 'input' verb both work regardless of position or occlusion)"
}

''
"instance 1 (host)  : pid $pid1   $Game1"
"instance 2 (client): pid $pid2   $Game2"
"instance 3 (client): pid $pid3   $Game3"
''

# The firewall prompt steals foreground and blocks injected input; it has an empty window
# title, so it can only be dismissed by killing its host process.
Get-Process PickerHost -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false

if ($NoAgents) {
    'three instances in-world; no agents started (-NoAgents).'
    return
}

$drop1 = Join-Path $WorkDir 'drop1.txt'
$drop2 = Join-Path $WorkDir 'drop2.txt'
$drop3 = Join-Path $WorkDir 'drop3.txt'
Remove-Item $drop1, $drop2, $drop3 -ErrorAction SilentlyContinue

# -Isolated: the redirected agents have no readout to watch, so keep their windows hidden.
$agentWindowStyle = $Isolated ? 'Hidden' : 'Normal'

Start-Process -FilePath $agent -ArgumentList `
    'run', '--pid', $pid1, '--kotor', '--host', '--port', $Port, '--name', $Name1, '--drop-file', $drop1 `
    -RedirectStandardOutput (Join-Path $WorkDir 'agent1.log') `
    -RedirectStandardError  (Join-Path $WorkDir 'agent1.err') `
    -WindowStyle $agentWindowStyle

Start-Sleep -Seconds 4

Start-Process -FilePath $agent -ArgumentList `
    'run', '--pid', $pid2, '--kotor', '--join', "127.0.0.1:$Port", '--name', $Name2, '--drop-file', $drop2 `
    -RedirectStandardOutput (Join-Path $WorkDir 'agent2.log') `
    -RedirectStandardError  (Join-Path $WorkDir 'agent2.err') `
    -WindowStyle $agentWindowStyle

# Stagger the two joins rather than racing them. The host assigns peer ids under a lock so a
# simultaneous join is safe, but a staggered one makes the logs readable - and the whole
# point of this harness is reading what happened.
Start-Sleep -Seconds 6

Start-Process -FilePath $agent -ArgumentList `
    'run', '--pid', $pid3, '--kotor', '--join', "127.0.0.1:$Port", '--name', $Name3, '--drop-file', $drop3 `
    -RedirectStandardOutput (Join-Path $WorkDir 'agent3.log') `
    -RedirectStandardError  (Join-Path $WorkDir 'agent3.err') `
    -WindowStyle $agentWindowStyle

. (Join-Path $PSScriptRoot 'Wait-CoopRigReady.ps1')
"waiting for all three native worlds and peer phantoms..."
Wait-CoopRigReady -ProcessIds @($pid1, $pid2, $pid3) | Write-Output

''
'three-player co-op is live. Each game should hold TWO phantoms.'
''
"  agent readouts : $WorkDir\agent1.log, agent2.log, agent3.log"
'  plugin logs    :'
foreach ($g in $games) { "                   $g\kotorcoop-plugin.log" }
''
'prove it:'
"  pwsh tools\verify3.ps1"
''
'if a game vanished:'
'  pwsh tools\crashlog.ps1'
''
'shut down with: pwsh tools\demo3.ps1 -Stop'
