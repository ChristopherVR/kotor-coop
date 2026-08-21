# Builds a release archive: the NativeAOT plugin, the agent, the coop.ini template, and the
# k_coop_ override assets the plugin installs — everything build.ps1 -Install copies into a
# game directory by hand, zipped up so a player never needs the .NET SDK or MSVC toolset.
#
# Does NOT touch a game install and does NOT run the game. Pair with tools/install.ps1 to
# actually deploy the result.
#
#   pwsh tools\package.ps1                # dist\kotor-coop-<version>.zip
#   pwsh tools\package.ps1 -Version 1.2.3  # override the git-describe version
param(
    [string]$Version = '',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutDir) { $OutDir = Join-Path $repo 'dist' }

if (-not $Version) {
    $Version = (git -C $repo describe --tags --always 2>$null)
    if (-not $Version) { $Version = 'dev' }
    $Version = $Version.Trim()
}

"packaging kotor-coop $Version"

$staging = Join-Path $env:TEMP "kotorcoop-package-$Version"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

# --- plugin: NativeAOT win-x86 dinput8.dll -------------------------------------------------
# build.ps1 imports the MSVC x86 environment (vcvarsall.bat) into ITS OWN process env, which
# includes a "Platform=x86" variable that MSBuild picks up from any dotnet invocation that
# follows in the same process — silently redirecting the agent's plain `dotnet publish` below
# into a bin\x86\... output path instead of bin\Release\net10.0-windows\publish. Running it as
# a child pwsh process (rather than dot-sourcing/calling it in-process) keeps that environment
# from leaking into the rest of this script. No -Install, so it never touches a game.
pwsh -NoProfile -File (Join-Path $repo 'src\KotorCoop.Plugin\build.ps1')
if ($LASTEXITCODE -ne 0) { throw "plugin build failed with exit code $LASTEXITCODE" }

$pluginDll = Get-ChildItem (Join-Path $repo 'src\KotorCoop.Plugin\bin\x86\Release\net10.0-windows\win-x86\publish\dinput8.dll') -ErrorAction SilentlyContinue
if (-not $pluginDll) {
    $pluginDll = Get-ChildItem (Join-Path $repo 'src\KotorCoop.Plugin\bin') -Recurse -Filter 'dinput8.dll' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if (-not $pluginDll) { throw 'plugin build succeeded but dinput8.dll was not found' }

$pluginOut = Join-Path $staging 'plugin'
New-Item -ItemType Directory -Path $pluginOut | Out-Null
Copy-Item $pluginDll.FullName (Join-Path $pluginOut 'dinput8.dll')
"staged plugin: $($pluginDll.FullName)"

# --- agent: framework-dependent publish, same net10.0-windows TFM as the plugin/game -------
$agentProject = Join-Path $repo 'src\KotorCoop.Agent'
Push-Location $agentProject
try {
    dotnet publish -c Release
    if ($LASTEXITCODE -ne 0) { throw "agent publish failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

$agentPublishDir = Join-Path $agentProject 'bin\Release\net10.0-windows\publish'
if (-not (Test-Path $agentPublishDir)) { throw "agent publish succeeded but $agentPublishDir was not found" }
if (-not (Test-Path (Join-Path $agentPublishDir 'KotorCoop.Agent.exe'))) {
    throw "agent publish succeeded but KotorCoop.Agent.exe not found in $agentPublishDir"
}

$agentOut = Join-Path $staging 'agent'
New-Item -ItemType Directory -Path $agentOut | Out-Null
Copy-Item (Join-Path $agentPublishDir '*') $agentOut -Recurse -Force
"staged agent: $agentPublishDir ($((Get-ChildItem $agentOut -File).Count) files)"

# --- coop.ini template, renamed from the .example so a player can use it as-is -------------
$iniSource = Join-Path $repo 'profiles\coop.ini.example'
if (-not (Test-Path $iniSource)) { throw "coop.ini.example not found at $iniSource" }
Copy-Item $iniSource (Join-Path $staging 'coop.ini')
"staged coop.ini <- $iniSource"

# --- k_coop_ override assets the plugin installs (phantom blueprint, spawn waypoint) -------
# These are checked-in outputs of PhantomBlueprint/SpawnWaypointBlueprint (see
# src/KotorCoop.Memory/GameFiles), not generated at package time — build.ps1 -Install copies
# them the same way, straight out of profiles/override.
$overrideSource = Join-Path $repo 'profiles\override'
if (-not (Test-Path $overrideSource)) { throw "override assets not found at $overrideSource" }

$overrideOut = Join-Path $staging 'override'
New-Item -ItemType Directory -Path $overrideOut | Out-Null
$overrideFiles = Get-ChildItem $overrideSource -File -Filter 'k_coop_*'
if ($overrideFiles.Count -eq 0) { throw "no k_coop_* files found under $overrideSource" }
foreach ($asset in $overrideFiles) {
    Copy-Item $asset.FullName (Join-Path $overrideOut $asset.Name)
}
"staged override assets: $($overrideFiles.Count) files"

# --- installer script, so the extracted zip is self-contained ------------------------------
Copy-Item (Join-Path $PSScriptRoot 'install.ps1') (Join-Path $staging 'install.ps1')

# --- zip it up -------------------------------------------------------------------------------
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$zipPath = Join-Path $OutDir "kotor-coop-$Version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath
Remove-Item $staging -Recurse -Force

$zipSize = (Get-Item $zipPath).Length
"packaged -> $zipPath ($([math]::Round($zipSize / 1KB)) KB)"
