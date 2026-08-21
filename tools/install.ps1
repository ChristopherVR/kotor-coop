# Installs a packaged kotor-coop release (see package.ps1) into a game directory, the same
# way src/KotorCoop.Plugin/build.ps1 -Install does for a source build: the plugin as
# dinput8.dll, the agent beside it, the k_coop_ override assets into Override, and a coop.ini
# template if the player does not already have one.
#
# Run this from inside an EXTRACTED kotor-coop-<version>.zip — it expects plugin\, agent\,
# override\ and coop.ini next to itself, which is exactly what the zip contains.
#
#   pwsh install.ps1 -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'
#   pwsh install.ps1 -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)' -Uninstall
param(
    [Parameter(Mandatory)]
    [string]$GameDir,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$packageDir = $PSScriptRoot

if ($Uninstall) {
    $proxy = Join-Path $GameDir 'dinput8.dll'
    if (Test-Path $proxy) {
        Remove-Item $proxy -Force
        "removed $proxy"
    }
    else {
        "nothing installed at $proxy"
    }

    $log = Join-Path $GameDir 'kotorcoop-plugin.log'
    if (Test-Path $log) { Remove-Item $log -Force; "removed $log" }

    # Every agent file is KotorCoop.*, so this can never touch a game file or a user's mod.
    foreach ($ours in Get-ChildItem $GameDir -File -Filter 'KotorCoop.*' -ErrorAction SilentlyContinue) {
        Remove-Item $ours.FullName -Force
        "removed agent file $($ours.FullName)"
    }

    # Every override file we ship is a k_coop_ resref, so this can never delete a user's mod.
    $overrideTarget = Join-Path $GameDir 'Override'
    if (Test-Path $overrideTarget) {
        foreach ($ours in Get-ChildItem $overrideTarget -File -Filter 'k_coop_*') {
            Remove-Item $ours.FullName -Force
            "removed override $($ours.FullName)"
        }
    }

    # coop.ini is a player's own config (host, password, name...); -Install never overwrites
    # one that already existed, so -Uninstall never removes it either.
    return
}

if (-not (Test-Path $GameDir)) { throw "game directory not found: $GameDir" }

$pluginSource = Join-Path $packageDir 'plugin\dinput8.dll'
if (-not (Test-Path $pluginSource)) { throw "plugin\dinput8.dll not found next to install.ps1 - run this from an extracted kotor-coop package" }

$pluginTarget = Join-Path $GameDir 'dinput8.dll'
Copy-Item $pluginSource $pluginTarget -Force
"installed -> $pluginTarget"

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

foreach ($asset in Get-ChildItem $overrideSource -File) {
    Copy-Item $asset.FullName (Join-Path $overrideTarget $asset.Name) -Force
    "installed override -> $(Join-Path $overrideTarget $asset.Name)"
}

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
