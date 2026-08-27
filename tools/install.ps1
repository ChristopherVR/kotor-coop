# Installs a packaged kotor-coop release (see package.ps1) into a game directory: the plugin
# as dinput8.dll, the agent beside it, the k_coop_ override assets into Override, and a
# coop.ini template if the player does not already have one. src/KotorCoop.Plugin/build.ps1
# -Install runs this same script against a freshly built staging tree.
#
# Run this from inside an EXTRACTED kotor-coop-<version>.zip — it expects plugin\, agent\,
# override\, loader\ and coop.ini next to itself, which is exactly what the zip contains.
#
#   pwsh install.ps1 -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'
#   pwsh install.ps1 -GameDir '...' -Chain                         # keep another mod's dinput8.dll too
#   pwsh install.ps1 -GameDir '...' -Chain -Load reshade.dll,x.asi # ...and load extra DLLs as well
#   pwsh install.ps1 -GameDir '...' -Uninstall
#
# CHAIN MODE (-Chain). Only one file can be called dinput8.dll. If another mod already owns
# that name (ReShade, an ASI loader, ...) the plain install would overwrite it, so it refuses
# and tells you about -Chain. With -Chain the tiny generic loader (src/KotorCoop.Loader)
# becomes dinput8.dll, the plugin is installed as KotorCoop.Plugin.dll, the mod that was
# there is renamed dinput8.prev.dll, and a dinput8.chain file lists what to load, in order:
#
#     KotorCoop.Plugin.dll
#     dinput8.prev.dll
#     <anything you passed to -Load>
#
# The loader loads every entry and forwards the game's DirectInput calls to the plugin; the
# plugin forwards to the entry after itself (or the system dinput8.dll). -Uninstall reverses
# all of it, including putting dinput8.prev.dll back under its original name.
param(
    [Parameter(Mandatory)]
    [string]$GameDir,
    [switch]$Uninstall,
    # Chain mode: the loader becomes dinput8.dll and an existing foreign one is kept.
    [switch]$Chain,
    # Extra DLLs (paths relative to the game directory) appended to dinput8.chain. Implies -Chain.
    [string[]]$Load = @(),
    # Overwrite a foreign dinput8.dll instead of refusing (you know it is expendable).
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$packageDir = $PSScriptRoot
$chainExtra = @($Load | Where-Object { $_ })
$chainMode = $Chain -or $chainExtra.Count -gt 0

$PluginFileName = 'KotorCoop.Plugin.dll'   # the plugin's name when the loader owns dinput8.dll
$PreviousFileName = 'dinput8.prev.dll'     # where a foreign dinput8.dll goes in chain mode
$ChainFileName = 'dinput8.chain'

# Both our DLLs carry a searchable marker: the plugin's startup log banner (a UTF-16 .NET
# string literal, PluginHost.EnsureStarted) and the loader's exported marker symbol (ASCII).
# Anything without either is somebody else's dinput8.dll and is never overwritten or deleted
# without being asked. Latin1 maps bytes 1:1 to chars, so one ordinal IndexOf finds a
# pattern at any byte alignment.
function Get-DinputKind([string]$Path) {
    if (-not (Test-Path $Path)) { return 'none' }
    $image = [System.Text.Encoding]::Latin1.GetString([System.IO.File]::ReadAllBytes($Path))
    $loaderMarker = 'KotorCoop.Loader chain dinput8'
    $pluginMarker = [System.Text.Encoding]::Latin1.GetString([System.Text.Encoding]::Unicode.GetBytes('=== KotorCoop plugin loaded'))
    if ($image.IndexOf($loaderMarker, [System.StringComparison]::Ordinal) -ge 0) { return 'loader' }
    if ($image.IndexOf($pluginMarker, [System.StringComparison]::Ordinal) -ge 0) { return 'plugin' }
    return 'foreign'
}

if ($Uninstall) {
    $proxy = Join-Path $GameDir 'dinput8.dll'
    $kind = Get-DinputKind $proxy
    switch ($kind) {
        'none'    { "nothing installed at $proxy" }
        'foreign' { "left $proxy alone: it is not ours (kind: $kind)" }
        default   { Remove-Item $proxy -Force; "removed $proxy ($kind)" }
    }

    $chainFile = Join-Path $GameDir $ChainFileName
    if (Test-Path $chainFile) { Remove-Item $chainFile -Force; "removed $chainFile" }

    $previous = Join-Path $GameDir $PreviousFileName
    if ((Test-Path $previous) -and -not (Test-Path $proxy)) {
        Rename-Item $previous $proxy
        "restored the other mod's dinput8.dll from $PreviousFileName"
    }

    foreach ($name in @('kotorcoop-plugin.log', 'dinput8-chain.log')) {
        $log = Join-Path $GameDir $name
        if (Test-Path $log) { Remove-Item $log -Force; "removed $log" }
    }

    # Every agent file (and the chain-mode plugin) is KotorCoop.*, so this can never touch a
    # game file or a user's mod.
    foreach ($ours in Get-ChildItem $GameDir -File -Filter 'KotorCoop.*' -ErrorAction SilentlyContinue) {
        Remove-Item $ours.FullName -Force
        "removed $($ours.FullName)"
    }

    # Every override file we ship is a k_coop_ resref, so this can never delete a user's mod.
    $overrideTarget = Join-Path $GameDir 'Override'
    if (Test-Path $overrideTarget) {
        foreach ($ours in Get-ChildItem $overrideTarget -File -Filter 'k_coop_*') {
            Remove-Item $ours.FullName -Force
            "removed override $($ours.FullName)"
        }
    }

    # coop.ini is a player's own config (host, password, name...); install never overwrites
    # one that already existed, so uninstall never removes it either.
    return
}

if (-not (Test-Path $GameDir)) { throw "game directory not found: $GameDir" }
if (-not (Test-Path (Join-Path $GameDir 'swkotor.exe'))) {
    Write-Warning "no swkotor.exe in $GameDir - is this really the game directory?"
}

$pluginSource = Join-Path $packageDir 'plugin\dinput8.dll'
if (-not (Test-Path $pluginSource)) { throw "plugin\dinput8.dll not found next to install.ps1 - run this from an extracted kotor-coop package" }
$loaderSource = Join-Path $packageDir 'loader\dinput8.dll'

$proxyTarget = Join-Path $GameDir 'dinput8.dll'
$existingKind = Get-DinputKind $proxyTarget

# A foreign dinput8.dll is another mod. Never silently overwrite it.
if ($existingKind -eq 'foreign' -and -not $chainMode -and -not $Force) {
    throw @"
$proxyTarget already exists and is not kotor-coop's - another mod is using the dinput8 slot.
  -Chain   keeps it: the loader becomes dinput8.dll and loads both (recommended)
  -Force   overwrites it (that mod stops working)
"@
}

if ($chainMode) {
    if (-not (Test-Path $loaderSource)) { throw "loader\dinput8.dll not found next to install.ps1 - this package has no chain loader" }

    if ($existingKind -eq 'foreign') {
        $previous = Join-Path $GameDir $PreviousFileName
        if (Test-Path $previous) { throw "$previous already exists - resolve that first (an earlier chain install?)" }
        Rename-Item $proxyTarget $previous
        "kept the existing dinput8.dll as $PreviousFileName"
    }

    Copy-Item $loaderSource $proxyTarget -Force
    "installed loader -> $proxyTarget"

    $pluginTarget = Join-Path $GameDir $PluginFileName
    Copy-Item $pluginSource $pluginTarget -Force
    "installed plugin -> $pluginTarget"

    # Build the chain: the plugin first (it is the proxy the game's calls are forwarded to),
    # the previous mod next (so the plugin forwards into it), then whatever was asked for.
    $entries = @($PluginFileName)
    if (Test-Path (Join-Path $GameDir $PreviousFileName)) { $entries += $PreviousFileName }
    foreach ($extra in $chainExtra) {
        if ($extra -notin $entries) { $entries += $extra }
    }

    $chainFile = Join-Path $GameDir $ChainFileName
    $lines = @(
        '# dinput8.chain - DLLs the kotor-coop loader (dinput8.dll) loads, in order.',
        '# One path per line, relative to this directory. # and ; start a comment.',
        '# The first entry that exports DirectInput8Create receives the game''s DirectInput',
        '# calls; the kotor-coop plugin forwards them to the entry after itself, then to',
        '# the system dinput8.dll. See README.md in the package.'
    ) + $entries
    Set-Content -Path $chainFile -Value $lines -Encoding UTF8
    "wrote $chainFile ($($entries -join ', '))"
}
else {
    # Plain mode: the plugin IS dinput8.dll. Clean up a previous chain-mode install so two
    # copies of the plugin cannot both load.
    foreach ($stale in @((Join-Path $GameDir $PluginFileName), (Join-Path $GameDir $ChainFileName))) {
        if (Test-Path $stale) { Remove-Item $stale -Force; "removed stale chain-mode file $stale" }
    }

    Copy-Item $pluginSource $proxyTarget -Force
    "installed -> $proxyTarget"
}

# Agent goes in the game root, not a subfolder: AgentLauncher searches the game dir first, so
# a loose copy there (what every install needs) would otherwise be shadowed by anything deeper.
$agentSource = Join-Path $packageDir 'agent'
if (-not (Test-Path $agentSource)) { throw "agent\ not found next to install.ps1 - run this from an extracted kotor-coop package" }

$agentInstalled = 0
foreach ($file in Get-ChildItem $agentSource -File) {
    try {
        Copy-Item $file.FullName (Join-Path $GameDir $file.Name) -Force
        $agentInstalled++
    }
    catch [System.IO.IOException] {
        # A running agent holds its own image; the plugin above still installed. Say so
        # rather than half-failing silently.
        throw "agent file locked ($($file.Name)) - stop KotorCoop.Agent.exe and re-run install.ps1"
    }
}
"installed agent -> $GameDir ($agentInstalled files)"

# Override assets: every file we ship uses a k_coop_ resref, so it can never collide with a
# mod, and this only ever adds to the folder, never touches a file it did not put there.
$overrideSource = Join-Path $packageDir 'override'
if (-not (Test-Path $overrideSource)) { throw "override\ not found next to install.ps1 - run this from an extracted kotor-coop package" }

$overrideTarget = Join-Path $GameDir 'Override'
if (-not (Test-Path $overrideTarget)) { New-Item -ItemType Directory -Path $overrideTarget | Out-Null }

$overrideInstalled = 0
foreach ($asset in Get-ChildItem $overrideSource -File) {
    Copy-Item $asset.FullName (Join-Path $overrideTarget $asset.Name) -Force
    $overrideInstalled++
}
"installed override assets -> $overrideTarget ($overrideInstalled files)"

# coop.ini is the player's own config once it exists (host/password/name/overlay settings);
# never clobber one a previous install or the player already wrote.
$iniSource = Join-Path $packageDir 'coop.ini'
$iniTarget = Join-Path $GameDir 'coop.ini'
if (Test-Path $iniTarget) {
    "coop.ini already exists at $iniTarget - left untouched"
}
elseif (Test-Path $iniSource) {
    Copy-Item $iniSource $iniTarget
    "installed -> $iniTarget"
}

''
'The game loads DirectInput from its own directory before the system one, so this is'
'picked up on next launch. Nothing else was modified; remove it with install.ps1 -Uninstall.'
