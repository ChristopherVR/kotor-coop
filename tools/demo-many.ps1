# Isolated 2..8-player rig. Only this run's PID/start-time pairs are stopped.
# Example: & tools/demo-many.ps1 -GameDirectories @($g1,$g2,$g3) -AgentPath $publishedAgent
# Stop with the printed WorkDir: & tools/demo-many.ps1 -Stop -WorkDir $runDirectory
param(
    [ValidateCount(2,8)][string[]]$GameDirectories,
    [string]$AgentPath = '', [string]$WorkDir = '',
    [ValidateRange(1024,65535)][int]$Port = 28140,
    [ValidateRange(1,999)][int]$Save = 1,
    # A fresh host avoids retaining a stock opening dialogue from a loaded fixture.
    [switch]$HostNewGame,
    [ValidateRange(30,900)][int]$TimeoutSeconds = 300,
    [switch]$Stop
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($Stop -and !$WorkDir) { throw 'Stop requires the original WorkDir.' }
if (!$WorkDir) { $WorkDir = Join-Path $repo ('artifacts/demo-many/' + [guid]::NewGuid().ToString('N')) }
$WorkDir = [IO.Path]::GetFullPath($WorkDir)
$record = Join-Path $WorkDir 'rig.json'
function Save-State { $script:rig | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $record -Encoding utf8 }
function Stop-OwnedRig {
    for ($index = $script:rig.Processes.Count - 1; $index -ge 0; $index--) {
        $owned = $script:rig.Processes[$index]
        $process = Get-Process -Id $owned.Id -ErrorAction SilentlyContinue
        if ($process -and $process.StartTime.ToUniversalTime().Ticks -eq $owned.StartTicks -and $process.Path -eq $owned.Path) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit(10000) > $null
        }
    }
    foreach ($backup in $script:rig.Backups) {
        $path = [IO.Path]::GetFullPath($backup.Path)
        if ($script:rig.Games -notcontains [IO.Path]::GetDirectoryName($path)) { throw "Invalid restore path: $path" }
        if ($backup.Exists) {
            [IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($backup.Bytes))
            [IO.File]::SetLastWriteTimeUtc($path, [DateTime]::new([long]$backup.ModifiedTicks, [DateTimeKind]::Utc))
        } elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    $script:rig.Status = 'Stopped'; Save-State
}
if ($Stop) {
    $script:rig = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json -AsHashtable
    if ($script:rig.Status -eq 'Stopped') { "Rig is already stopped: $WorkDir"; return }
    Stop-OwnedRig; "Stopped owned rig and restored configuration: $WorkDir"; return
}
if (!$GameDirectories -or $GameDirectories.Count -lt 2) { throw 'Provide 2..8 explicit game directories.' }
$agent = (Resolve-Path -LiteralPath $AgentPath).Path
if (![IO.File]::Exists($agent) -or [IO.Path]::GetFileName($agent) -ne 'KotorCoop.Agent.exe') { throw 'AgentPath must be a published KotorCoop.Agent.exe.' }
# Published releases are self-contained single-file executables. Framework-dependent
# builds carry the managed DLL and must also carry its runtime manifests.
if (Test-Path -LiteralPath (Join-Path (Split-Path $agent) 'KotorCoop.Agent.dll')) {
    foreach ($required in @('KotorCoop.Agent.deps.json','KotorCoop.Agent.runtimeconfig.json')) {
        if (!(Test-Path -LiteralPath (Join-Path (Split-Path $agent) $required))) { throw "Missing agent dependency: $required" }
    }
}
$games = @($GameDirectories | ForEach-Object { (Resolve-Path -LiteralPath $_).Path.TrimEnd('\') })
if (@($games | Sort-Object -Unique).Count -ne $games.Count) { throw 'Game directories must be distinct.' }
foreach ($game in $games) {
    if ((Get-Item -LiteralPath $game).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked install root is not supported: $game" }
    foreach ($required in @('swkotor.exe','dinput8.dll','chitin.key','saves')) {
        if (!(Test-Path -LiteralPath (Join-Path $game $required))) { throw "Missing $required in $game" }
    }
    foreach ($existing in Get-Process -Name swkotor -ErrorAction SilentlyContinue) {
        if ([IO.Path]::GetDirectoryName($existing.Path) -eq $game) { throw "Install is already running: $game (pid $($existing.Id))" }
    }
}
if (Test-Path -LiteralPath $record) {
    $previous = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
    if ($previous.Status -ne 'Stopped') { throw "Stop the existing owned rig first: $WorkDir" }
}
New-Item -ItemType Directory -Path $WorkDir -Force > $null
$script:rig = @{ Status='Starting'; Games=$games; Agent=$agent; Processes=@(); Backups=@() }
foreach ($game in $games) {
    foreach ($name in @('coop.ini','swkotor.ini')) {
        $path = Join-Path $game $name; $exists = Test-Path -LiteralPath $path
        $script:rig.Backups += @{ Path=$path; Exists=$exists; Bytes=($exists ? [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) : ''); ModifiedTicks=($exists ? [IO.File]::GetLastWriteTimeUtc($path).Ticks : 0) }
    }
}
Save-State
function Set-Key([string]$directory,[string]$key,[string]$value) {
    $path = Join-Path $directory 'coop.ini'
    $text = (Test-Path -LiteralPath $path) ? [IO.File]::ReadAllText($path) : "[coop]`n"
    $pattern = '(?m)^\s*' + [regex]::Escape($key) + '\s*=.*$'
    $text = [regex]::IsMatch($text,$pattern) ? [regex]::Replace($text,$pattern,"$key = $value") : ($text.TrimEnd() + "`n$key = $value`n")
    [IO.File]::WriteAllText($path,$text)
}
function Own-Process($process, [string]$expectedPath = '') {
    # The shell's freshly started Process can transiently report a null Path.
    # Retain the exact executable we launched so scoped cleanup still validates it.
    $ownedPath = $expectedPath ? [IO.Path]::GetFullPath($expectedPath) : $process.Path
    if (!$ownedPath) { throw 'Cannot record an owned process without its executable path.' }
    $script:rig.Processes += @{ Id=$process.Id; StartTicks=$process.StartTime.ToUniversalTime().Ticks; Path=$ownedPath }
    Save-State
}
function Start-Agent([string[]]$arguments,[string]$label,[switch]$Wait) {
    # Windows argv quoting; no intermediate shell or command evaluation.
    $quoted = @($arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' })
    $out = Join-Path $WorkDir ($label + '.stdout.log'); $err = Join-Path $WorkDir ($label + '.stderr.log')
    $worker = Start-Process -FilePath $agent -ArgumentList $quoted -WorkingDirectory $repo -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err -PassThru
    Own-Process $worker $agent
    if ($Wait) {
        if (!$worker.WaitForExit(210000)) { throw "$label timed out; see $out" }
        if ($worker.ExitCode -ne 0) { throw "$label failed: $([IO.File]::ReadAllText($err))" }
        return [IO.File]::ReadAllText($out)
    }
    return $worker
}
try {
    foreach ($game in $games) {
        Set-Key $game 'auto_start_agent' '0'; Set-Key $game 'isolate_real_input' '1'; Set-Key $game 'join_mode' 'save'
    }
    $gamePids = @()
    for ($i=0; $i -lt $games.Count; $i++) {
        # Serialize selection through the game's own mutex acquisition, not through
        # its intro/save load. Two concurrently started rigs must not select the
        # same apparently free name while neither game has created it yet.
        $launchGate = [Threading.Mutex]::new($false,'Local\KotorCoopLiveRigLaunch')
        $gateOwned = $false
        try {
            try { $gateOwned = $launchGate.WaitOne(60000) }
            catch [Threading.AbandonedMutexException] { $gateOwned = $true }
            if (!$gateOwned) { throw 'Another rig is still launching a game; launch gate timed out.' }
            $instanceNumber = 0
            foreach ($candidate in 2..99) {
                $existingMutex = $null
                if ([Threading.Mutex]::TryOpenExisting("kcoop$candidate", [ref]$existingMutex)) {
                    $existingMutex.Dispose(); continue
                }
                $instanceNumber=$candidate; break
            }
            if (!$instanceNumber) { throw 'No free game-instance mutex names remain.' }
            $output = Start-Agent @('launch','--instance',"$instanceNumber",'--windowed','--game-path',(Join-Path $games[$i] 'swkotor.exe')) "launch$($i+1)" -Wait
            if ($output -notmatch 'running as pid (\d+)') { throw "No owned game PID in launch result: $output" }
            $gameProcess = Get-Process -Id ([int]$Matches[1])
            if ($gameProcess.Path -ne (Join-Path $games[$i] 'swkotor.exe')) { throw 'Launcher returned an unexpected game executable.' }
            Own-Process $gameProcess; $gamePids += $gameProcess.Id
            $mutexDeadline = [DateTime]::UtcNow.AddSeconds(10)
            $claimedMutex = $null
            do {
                if ([Threading.Mutex]::TryOpenExisting("kcoop$instanceNumber",[ref]$claimedMutex)) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $mutexDeadline)
            if (!$claimedMutex) { throw "Launched game did not acquire kcoop${instanceNumber}: $output" }
            $claimedMutex.Dispose()
        } finally {
            if ($gateOwned) { $launchGate.ReleaseMutex() }
            $launchGate.Dispose()
        }
        $windowDeadline = [DateTime]::UtcNow.AddSeconds(20)
        do { $gameProcess.Refresh(); if ($gameProcess.MainWindowHandle) { break }; Start-Sleep -Milliseconds 250 } while ([DateTime]::UtcNow -lt $windowDeadline)
        & (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $gameProcess.Id -Restore -NoActivate -Ghost
        . (Join-Path $PSScriptRoot 'Wait-CoopPluginReady.ps1')
        Wait-CoopPluginReady -ProcessId $gameProcess.Id -TimeoutSeconds 60 | Write-Output
        if ($i -ne 0 -or !$HostNewGame) {
            Start-Agent @('load','--pid',"$($gameProcess.Id)",'--load-save',"$Save") "load$($i+1)" -Wait > $null
        }
    }
    for ($i=0; $i -lt $games.Count; $i++) {
        $runArguments = @('run','--pid',"$($gamePids[$i])",'--kotor','--name',"Rig$($i+1)",'--log',(Join-Path $WorkDir "agent$($i+1).log"),'--drop-file',(Join-Path $WorkDir "drop$($i+1).txt"))
        $runArguments += ($i -eq 0) ? @('--host','--port',"$Port") : @('--join',"127.0.0.1:$Port")
        if ($i -eq 0 -and $HostNewGame) { $runArguments += '--menu' }
        Start-Agent $runArguments "agent$($i+1)" > $null
        if ($i -eq 0) { Start-Sleep -Seconds 2 }
    }
    if ($HostNewGame) {
        foreach ($drive in @(@('newgame'),@('quick','0'),@('accept'))) {
            $driveArgs=@{ProcId=$gamePids[0];Command='chargen';Arg1=$drive[0]}
            if ($drive.Count -gt 1) { $driveArgs.Arg2=[int]$drive[1] }
            $driveResult = & (Join-Path $PSScriptRoot 'blockpoke.ps1') @driveArgs
            if (!$? -or ($driveResult -join "`n") -notmatch 'chargen drive: request (\d+)\s+applied \1\s+status 0') {
                throw "Fresh host character drive failed: $($drive[0]): $($driveResult -join ' ')"
            }
            $driveResult | Write-Output
        }
    }
    . (Join-Path $PSScriptRoot 'Wait-CoopRigReady.ps1')
    Wait-CoopRigReady -ProcessIds $gamePids -TimeoutSeconds $TimeoutSeconds | Write-Output
    $script:rig.Status='Ready'; Save-State
    "Ready $($games.Count)-player rig; game PIDs $($gamePids -join ', '); owned state: $record"
    "Cleanup: & '$PSScriptRoot/demo-many.ps1' -Stop -WorkDir '$WorkDir'"
} catch {
    Stop-OwnedRig
    throw
}
