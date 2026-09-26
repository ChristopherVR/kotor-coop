# Prepare a writable, isolated KOTOR install for the BoS:SR bundled base + 1.1 patch.
# HoloPatcher applies the distributed changes.ini; it never receives the original game path.
param(
    [string]$GameDir = 'C:\GOG Games\Star Wars KOTOR (GOG)',
    [string]$RigRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\solomon-isolation'),
    [string]$PatcherPython = '',
    [string]$BaseMod = '',
    [string]$PatchMod = ''
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$isolatedRoot = [IO.Path]::GetFullPath($RigRoot)
if (-not $isolatedRoot.StartsWith($workspace + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The isolation root must be inside the workspace.'
}
if (-not $PatcherPython) { $PatcherPython = Join-Path $isolatedRoot 'python\Scripts\python.exe' }
if (-not $BaseMod) { $BaseMod = Join-Path $isolatedRoot 'mod-base' }
if (-not $PatchMod) { $PatchMod = Join-Path $isolatedRoot 'mod-patch' }
$isolatedGame = Join-Path $isolatedRoot 'host'
if (Test-Path -LiteralPath $isolatedGame) { throw 'host already exists; use a fresh RigRoot to avoid patching twice.' }
$isolatedClient = Join-Path $isolatedRoot 'client'
if (Test-Path -LiteralPath $isolatedClient) { throw 'client already exists; use a fresh RigRoot.' }
foreach ($required in @((Join-Path $GameDir 'swkotor.exe'), $PatcherPython,
        (Join-Path $BaseMod 'tslpatchdata\changes.ini'), (Join-Path $PatchMod 'tslpatchdata\changes.ini'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing $required" }
}
New-Item -ItemType Directory -Path $isolatedGame | Out-Null
# A real copy throughout: patchers can touch any game resource. No junctions or hard links.
& robocopy $GameDir $isolatedGame /E /XD saves logs crashdumps /XF 'KotorCoop.*' 'coop-*.log' /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
if ($LASTEXITCODE -ge 8) { throw "Game copy failed: $LASTEXITCODE" }
New-Item -ItemType Directory -Path (Join-Path $isolatedGame 'saves') -Force | Out-Null
foreach ($mod in @(@{Root=$BaseMod; Log='base-install.log'}, @{Root=$PatchMod; Log='patch-install.log'})) {
    & $PatcherPython -m holopatcher --game-dir $isolatedGame --tslpatchdata (Join-Path $mod.Root 'tslpatchdata') --install --console *> (Join-Path $isolatedRoot $mod.Log)
    if ($LASTEXITCODE -ne 0) { throw "Patcher failed; inspect $($mod.Log)" }
}
$ini = Join-Path $isolatedGame 'coop.ini'
if (-not (Test-Path -LiteralPath $ini)) {
    Copy-Item (Join-Path $workspace 'profiles\coop.ini.example') $ini
}
$settings = Get-Content -LiteralPath $ini -Raw
$settings = [regex]::Replace($settings, '(?m)^\s*auto_start_agent\s*=.*$', 'auto_start_agent=0')
$settings = [regex]::Replace($settings, '(?m)^\s*isolate_real_input\s*=.*$', 'isolate_real_input=1')
$settings = [regex]::Replace($settings, '(?m)^\s*join_mode\s*=.*$', 'join_mode=save')
Set-Content -LiteralPath $ini -Value $settings -Encoding ascii
& robocopy $isolatedGame $isolatedClient /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
if ($LASTEXITCODE -ge 8) { throw "Client copy failed: $LASTEXITCODE" }
'Prepared independent host/client game copies. Original install and saves were not patched.'
