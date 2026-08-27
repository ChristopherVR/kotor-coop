# Installs an extracted kotor-coop package into a game directory. A thin wrapper: the real
# work is KotorCoop.Agent.exe's `install`/`uninstall` verbs (src/KotorCoop.Agent/
# PackageInstaller.cs), which the no-argument double-click setup uses too.
#
# You do not need this script. Either extract the zip straight into the game folder, or
# double-click KotorCoop.Agent.exe. It exists for scripted installs (build.ps1 -Install, the
# dev rig) and for people who prefer a command line.
#
#   pwsh install.ps1 -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'
#   pwsh install.ps1 -GameDir '...' -Chain                         # keep another mod's dinput8.dll too
#   pwsh install.ps1 -GameDir '...' -Chain -Load reshade.dll,x.asi # ...and load extra DLLs as well
#   pwsh install.ps1 -GameDir '...' -Force                         # overwrite a foreign dinput8.dll
#   pwsh install.ps1 -GameDir '...' -Uninstall
param(
    [Parameter(Mandatory)]
    [string]$GameDir,
    [switch]$Uninstall,
    [switch]$Chain,
    [string[]]$Load = @(),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$agent = Join-Path $PSScriptRoot 'KotorCoop.Agent.exe'
if (-not (Test-Path $agent)) { throw "KotorCoop.Agent.exe not found next to install.ps1 - run this from an extracted kotor-coop package" }

if ($Uninstall) {
    & $agent uninstall --game-path $GameDir
}
else {
    $args = @('install', '--from', $PSScriptRoot, '--game-path', $GameDir)
    if ($Chain) { $args += '--chain' }
    if ($Load.Count -gt 0) { $args += @('--load', ($Load -join ',')) }
    if ($Force) { $args += '--force' }
    & $agent @args
}

if ($LASTEXITCODE -ne 0) { throw "KotorCoop.Agent.exe exited with code $LASTEXITCODE" }
