<#
.SYNOPSIS
  Relaunch only the joiner (instance 2) of a running tools\demo.ps1 rig and reconnect it, as a
  player would after their game crashed: start the game again, start the helper, join.

  pwsh tools\relaunch_joiner.ps1                # same defaults as demo.ps1
  pwsh tools\relaunch_joiner.ps1 -Isolated      # ghost the window like demo.ps1 -Isolated

  The host and its agent are left alone. Prints "instance 2: pid N" when the native world is up.
  Used by the crash/recovery checks (RELEASE-READINESS item "Save, inventory and crash recovery").
#>
param(
    [string]$Game2 = 'D:\Games\swkotor-gog-p2',
    [int]$Port = 27140,
    [string]$Name2 = 'Malak',
    [string]$WorkDir = "$env:TEMP\kotorcoop-demo",
    [string]$AgentPath = '',
    [switch]$Isolated
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$agent = $AgentPath ? $AgentPath : (Join-Path $repo 'src\KotorCoop.Agent\bin\Debug\net10.0-windows\KotorCoop.Agent.exe')
$exe = Join-Path $Game2 'swkotor.exe'

# Anything left of the old joiner (a crashed game can leave its helper behind).
Get-CimInstance Win32_Process -Filter "Name='KotorCoop.Agent.exe'" |
    Where-Object { $_.CommandLine -match '--join' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Get-Process swkotor -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

'launching instance 2 (renamed mutex) ...'
& $agent launch --instance 2 --game-path $exe
if ($LASTEXITCODE -ne 0) { throw 'instance 2 failed to launch' }
$second = Get-Process swkotor | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
if (-not $second) { throw 'instance 2 is not running' }
$pid2 = $second.Id

if ($Isolated) { & (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $pid2 -Ghost }

$log = Join-Path $WorkDir 'agent2.log'
if (Test-Path $log) { Move-Item -Path $log -Destination "$log.prev" -Force }
$drop2 = Join-Path $WorkDir 'drop2.txt'
$args2 = @('run', '--pid', $pid2, '--kotor', '--join', "127.0.0.1:$Port", '--name', $Name2, '--drop-file', $drop2, '--log', $log)
$quoted = ($args2 | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ' '
$style = $Isolated ? 'Hidden' : 'Normal'
$console = Start-Process -FilePath 'pwsh' -ArgumentList '-NoExit', '-Command', "& '$agent' $quoted" -WindowStyle $style -PassThru
Add-Content -Path (Join-Path $WorkDir 'consoles.txt') -Value $console.Id

"instance 2: pid $pid2"
