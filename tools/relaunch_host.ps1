<#
.SYNOPSIS
  Relaunch only the host (instance 1) of a tools\demo.ps1 rig, as a host would after their game
  crashed: start the game on its newest save, start the helper, host again. The joiner is left
  alone, so whatever it does about a lost and returning host is what a player would see.

  pwsh tools\relaunch_host.ps1 [-Save 1] [-Isolated]
#>
param(
    [string]$Game1 = 'C:\GOG Games\Star Wars KOTOR (GOG)',
    [int]$Port = 27140,
    [string]$Name1 = 'Lanik',
    [int]$Save = 1,
    [string]$WorkDir = "$env:TEMP\kotorcoop-demo",
    [string]$AgentPath = '',
    [switch]$Isolated
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$agent = $AgentPath ? $AgentPath : (Join-Path $repo 'src\KotorCoop.Agent\bin\Debug\net10.0-windows\KotorCoop.Agent.exe')
$exe = Join-Path $Game1 'swkotor.exe'

Get-CimInstance Win32_Process -Filter "Name='KotorCoop.Agent.exe'" |
    Where-Object { $_.CommandLine -match '--host' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Get-Process swkotor -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

"launching instance 1 ..."
& $agent launch --game-path $exe
if ($LASTEXITCODE -ne 0) { throw 'instance 1 failed to launch' }
$first = $null
for ($i = 0; $i -lt 30 -and -not $first; $i++) {
    Start-Sleep -Seconds 1
    $first = Get-Process swkotor -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
}
if (-not $first) { throw 'instance 1 is not running' }
$pid1 = $first.Id

# The launch's own --load-save left a relaunched host at the main menu (2026-10-09); the
# explicit load is what the demo falls back to for instance 2, so do it directly, with retries.
"loading save $Save in instance 1 ..."
$loaded = $false
for ($attempt = 1; $attempt -le 3 -and -not $loaded; $attempt++) {
    & $agent load --pid $pid1 --load-save $Save
    $loaded = $LASTEXITCODE -eq 0
    if (-not $loaded) { Write-Warning "instance 1 load attempt $attempt failed; retrying"; Start-Sleep -Seconds 5 }
}
if (-not $loaded) { throw 'instance 1 did not load its save' }
if ($Isolated) { & (Join-Path $PSScriptRoot 'movewin.ps1') -ProcId $pid1 -Ghost }

$log = Join-Path $WorkDir 'agent1.log'
if (Test-Path $log) { Move-Item -Path $log -Destination "$log.prev" -Force }
$drop1 = Join-Path $WorkDir 'drop1.txt'
$args1 = @('run', '--pid', $pid1, '--kotor', '--host', '--port', $Port, '--name', $Name1, '--drop-file', $drop1, '--log', $log)
$quoted = ($args1 | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ' '
$style = $Isolated ? 'Hidden' : 'Normal'
$console = Start-Process -FilePath 'pwsh' -ArgumentList '-NoExit', '-Command', "& '$agent' $quoted" -WindowStyle $style -PassThru
Add-Content -Path (Join-Path $WorkDir 'consoles.txt') -Value $console.Id

"instance 1: pid $pid1"
