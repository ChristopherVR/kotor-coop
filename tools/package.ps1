# Builds the release zip: a GAME-DIRECTORY IMAGE. Extracting it straight into the KOTOR folder
# is a complete install; double-clicking KotorCoop.Agent.exe from anywhere else finds the
# game and copies everything in. Layout of the zip root:
#
#   dinput8.dll                 the NativeAOT plugin (the proxy the game loads itself)
#   KotorCoop.*                 the agent (exe, dlls, runtimeconfig)
#   coop.ini                    the config template
#   Override\k_coop_*           the override assets the plugin needs
#   chain-loader\dinput8.dll    the C wrapper for sharing the dinput8 slot with another mod
#   dinput8.chain.example       the annotated chain list
#   README.md                   install / play / configure / troubleshoot, version-stamped
#   install.ps1                 optional command-line wrapper over `KotorCoop.Agent.exe install`
#
# Does NOT touch a game install and does NOT run the game. CI runs this on every push and
# attaches the zip to the run; a green run on main or a v* tag becomes a GitHub release
# (.github/workflows/release.yml).
#
#   pwsh tools\package.ps1                 # dist\kotor-coop-<git describe>.zip
#   pwsh tools\package.ps1 -Version 1.2.3  # override the version
#   pwsh tools\package.ps1 -WarnAsError    # what CI runs: any build warning fails the package
param(
    [string]$Version = '',
    [string]$OutDir = '',
    [switch]$WarnAsError
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutDir) { $OutDir = Join-Path $repo 'dist' }

if (-not $Version) {
    # Only v* tags count: the rolling "latest" release tag would otherwise name every build
    # "latest-1-g<sha>".
    $Version = (git -C $repo describe --tags --match 'v*' --always 2>$null)
    if (-not $Version) { $Version = 'dev' }
    $Version = $Version.Trim()
}
$Version = $Version -replace '^v', ''

"packaging kotor-coop $Version"

$staging = Join-Path $env:TEMP "kotorcoop-package-$Version"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

# build.ps1 imports the MSVC x86 environment (vcvarsall.bat) into ITS OWN process env, which
# includes a "Platform=x86" variable that MSBuild picks up from any dotnet invocation that
# follows in the same process — silently redirecting the agent's plain `dotnet publish` below
# into a bin\x86\... output path instead of bin\Release\net10.0-windows\publish. Running the
# native builds as child pwsh processes keeps that environment from leaking into this script.
$warnArgs = if ($WarnAsError) { @('-WarnAsError') } else { @() }

# --- plugin: NativeAOT win-x86 dinput8.dll -------------------------------------------------
pwsh -NoProfile -File (Join-Path $repo 'src\KotorCoop.Plugin\build.ps1') @warnArgs
if ($LASTEXITCODE -ne 0) { throw "plugin build failed with exit code $LASTEXITCODE" }

$pluginDll = Get-ChildItem (Join-Path $repo 'src\KotorCoop.Plugin\bin\x86\Release\net10.0-windows\win-x86\publish\dinput8.dll') -ErrorAction SilentlyContinue
if (-not $pluginDll) {
    $pluginDll = Get-ChildItem (Join-Path $repo 'src\KotorCoop.Plugin\bin') -Recurse -Filter 'dinput8.dll' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if (-not $pluginDll) { throw 'plugin build succeeded but dinput8.dll was not found' }

Copy-Item $pluginDll.FullName (Join-Path $staging 'dinput8.dll')
"staged plugin: $($pluginDll.FullName)"

# --- loader: the generic dinput8 chain wrapper (plain C, a few KB) --------------------------
pwsh -NoProfile -File (Join-Path $repo 'src\KotorCoop.Loader\build.ps1') @warnArgs
if ($LASTEXITCODE -ne 0) { throw "loader build failed with exit code $LASTEXITCODE" }

$loaderDll = Join-Path $repo 'src\KotorCoop.Loader\bin\dinput8.dll'
if (-not (Test-Path $loaderDll)) { throw "loader build succeeded but $loaderDll was not found" }

New-Item -ItemType Directory -Path (Join-Path $staging 'chain-loader') | Out-Null
Copy-Item $loaderDll (Join-Path $staging 'chain-loader\dinput8.dll')
Copy-Item (Join-Path $repo 'src\KotorCoop.Loader\dinput8.chain.example') (Join-Path $staging 'dinput8.chain.example')
"staged loader: $loaderDll"

# --- agent: framework-dependent publish, same net10.0-windows TFM as the plugin/game -------
$agentProject = Join-Path $repo 'src\KotorCoop.Agent'
Push-Location $agentProject
try {
    $agentArgs = @('publish', '-c', 'Release')
    if ($WarnAsError) { $agentArgs += '-warnaserror' }
    dotnet @agentArgs
    if ($LASTEXITCODE -ne 0) { throw "agent publish failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

$agentPublishDir = Join-Path $agentProject 'bin\Release\net10.0-windows\publish'
if (-not (Test-Path (Join-Path $agentPublishDir 'KotorCoop.Agent.exe'))) {
    throw "agent publish succeeded but KotorCoop.Agent.exe not found in $agentPublishDir"
}

# Game root, not a subfolder: AgentLauncher searches the game directory first. Only
# KotorCoop.* files, so the uninstaller's "everything KotorCoop.* is ours" rule holds.
$agentFiles = Get-ChildItem $agentPublishDir -File -Filter 'KotorCoop.*'
foreach ($file in $agentFiles) { Copy-Item $file.FullName (Join-Path $staging $file.Name) }
$stray = Get-ChildItem $agentPublishDir -File | Where-Object { $_.Name -notlike 'KotorCoop.*' }
if ($stray) { throw "agent publish contains non-KotorCoop.* files the installer cannot track: $($stray.Name -join ', ')" }
"staged agent: $agentPublishDir ($($agentFiles.Count) files)"

# --- coop.ini template, renamed from the .example so a player can use it as-is -------------
$iniSource = Join-Path $repo 'profiles\coop.ini.example'
if (-not (Test-Path $iniSource)) { throw "coop.ini.example not found at $iniSource" }
Copy-Item $iniSource (Join-Path $staging 'coop.ini')
"staged coop.ini <- $iniSource"

# --- k_coop_ override assets the plugin needs (phantom blueprint, spawn waypoint) ----------
# Checked-in outputs of PhantomBlueprint/SpawnWaypointBlueprint (src/KotorCoop.Memory/GameFiles).
$overrideSource = Join-Path $repo 'profiles\override'
$overrideFiles = Get-ChildItem $overrideSource -File -Filter 'k_coop_*' -ErrorAction SilentlyContinue
if (-not $overrideFiles) { throw "no k_coop_* files found under $overrideSource" }
New-Item -ItemType Directory -Path (Join-Path $staging 'Override') | Out-Null
foreach ($asset in $overrideFiles) { Copy-Item $asset.FullName (Join-Path $staging 'Override' $asset.Name) }
"staged override assets: $($overrideFiles.Count) files"

# --- installer wrapper + README ------------------------------------------------------------
Copy-Item (Join-Path $PSScriptRoot 'install.ps1') (Join-Path $staging 'install.ps1')

$readme = (Get-Content (Join-Path $PSScriptRoot 'package-README.md') -Raw).Replace('{{VERSION}}', $Version)
Set-Content -Path (Join-Path $staging 'README.md') -Value $readme -Encoding UTF8 -NoNewline
"staged install.ps1 + README.md"

# --- zip it up -------------------------------------------------------------------------------
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$zipPath = Join-Path $OutDir "kotor-coop-$Version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath
Remove-Item $staging -Recurse -Force

"packaged -> $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1KB)) KB)"
