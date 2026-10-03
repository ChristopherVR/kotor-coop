# Installs an extracted kotor-coop package into a game directory. A thin wrapper: the real
# work is KotorCoop.Agent.exe's `install`/`uninstall` verbs (src/KotorCoop.Agent/
# PackageInstaller.cs), which the no-argument double-click setup uses too.
#
# You do not need this script. Either extract the zip straight into the game folder, or
# double-click KotorCoop.Agent.exe. It exists for scripted installs (build.ps1 -Install, the
# dev rig) and for people who prefer a command line.
#
#   pwsh install.ps1 -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'
#   pwsh install.ps1 -GameDir '...' -Uninstall
#
# --- another mod already owns dinput8.dll (ReShade, an ASI loader, ...) --------------------
# Detected and installed as a chain automatically, no flag needed: the loader takes
# dinput8.dll, the other mod is kept as dinput8.prev.dll and loaded right after kotor-coop. A
# plain copy of Windows' own dinput8.dll is recognised as not-a-mod and simply replaced
# instead. -Chain/-Load/-Force below only matter when you want to override that automatic
# choice:
#
#   pwsh install.ps1 -GameDir '...' -Chain                         # force chain mode even if nothing foreign is there yet
#   pwsh install.ps1 -GameDir '...' -Chain -Load reshade.dll,x.asi # ...and load extra DLLs as well
#   pwsh install.ps1 -GameDir '...' -Force                         # overwrite a foreign dinput8.dll outright, no chain
#
# Re-running install (with or without these flags) over an existing install - plain or chain -
# upgrades the plugin in place and never duplicates a dinput8.chain entry, so it's always safe
# to just run install.ps1 again to pick up a new version.
#
# --- health check (-Check) ------------------------------------------------------------------
# Reports the install layout found (plain / chain / foreign-only / none) and verifies it -
# every dinput8.chain entry exists, the loader and KotorCoop.Plugin.dll carry their markers,
# coop.ini exists - without changing anything. One PASS/FAIL line per check; every ordinary
# install run prints the same report at the end.
#
#   pwsh install.ps1 -GameDir '...' -Check
#
# --- Steam installs ----------------------------------------------------------------------
# Nothing special. -GameDir is the folder holding swkotor.exe, normally
# 'C:\Program Files (x86)\Steam\steamapps\common\swkotor' (KotorCoop.Agent.exe finds Steam
# libraries on other drives by itself, through steamapps\libraryfolders.vdf):
#
#   pwsh install.ps1 -GameDir 'C:\Program Files (x86)\Steam\steamapps\common\swkotor'
#
# dinput8.dll goes in the game folder there exactly as it does on a DRM-free install: the game
# imports DINPUT8 statically and the loader searches its own directory first, and Valve's
# SteamStub wrapper does not change either fact - it decrypts .text in place before the game's
# entry point runs, long before our DirectInput8Create export is called. -Chain works there too;
# chain mode is a file-name convention among mods and has nothing to do with the DRM wrapper.
# The Steam overlay does NOT own the SwapBuffers import slot the frame hook wants (verified
# live), so no overlay change is needed; docs/TROUBLESHOOTING.md has the fallback if a future
# overlay build ever does. Do not unwrap or replace the Steam swkotor.exe.
# See docs/research/steam-support.md 18.
#
# --- the coop.ini "upgrade trap" -----------------------------------------------------------
# PackageInstaller.cs never overwrites an existing coop.ini (deliberately: a player's password,
# name and tuned flags must survive an upgrade). When the shipped DEFAULT for a key changes
# (menu_button/effect_sync/trigger_relay all flipped 0 -> 1 on 2026-08-27 - see docs/CONFIG.md's
# footnotes), an upgraded install silently keeps the OLD value forever with nothing telling the
# player. Every plain install now WARNS about this (a dry run - nothing is written); pass
# -UpdateDefaults to have it rewrite just those lines in place.
#
#   pwsh install.ps1 -GameDir '...'                  # warns only, changes nothing (the default)
#   pwsh install.ps1 -GameDir '...' -UpdateDefaults   # also rewrites the drifted lines in place
#
# Parsing and migration are shared with double-click setup in SettingsMigration.cs.
# Changes follow the incoming player template, are opt-in, and create a backup first.
# Historical developer switches are preserved rather than migrated by this wrapper.
#
# --- crash dumps (-EnableCrashDumps / -DisableCrashDumps) ----------------------------------
# A player-facing counterpart to the dev-only tools/crashlog.ps1 -EnableDumps: turns on Windows
# Error Reporting local minidumps for swkotor.exe so a crash report has more than an exception
# code and an offset. Needs an elevated (Administrator) PowerShell - checked up front with a
# clear message, never silently skipped.
#
#   pwsh install.ps1 -GameDir '...' -EnableCrashDumps                    # dumps under <GameDir>\crashdumps
#   pwsh install.ps1 -GameDir '...' -EnableCrashDumps -DumpFolder 'D:\dumps'
#   pwsh install.ps1 -GameDir '...' -DisableCrashDumps
param(
    [Parameter(Mandatory)]
    [string]$GameDir,
    [switch]$Uninstall,
    [switch]$Chain,
    [string[]]$Load = @(),
    [switch]$Force,
    [switch]$Check,
    [switch]$UpdateDefaults,
    [switch]$EnableCrashDumps,
    [switch]$DisableCrashDumps,
    [string]$DumpFolder = ''
)

$ErrorActionPreference = 'Stop'
$agent = Join-Path $PSScriptRoot 'KotorCoop.Agent.exe'
if (-not (Test-Path $agent)) { throw "KotorCoop.Agent.exe not found next to install.ps1 - run this from an extracted kotor-coop package" }

if ($EnableCrashDumps -and $DisableCrashDumps) {
    throw '-EnableCrashDumps and -DisableCrashDumps are mutually exclusive'
}

# --- crash dumps: an independent, machine-wide WER setting - handled before the install/
# uninstall branch below so it works standalone (install.ps1 -GameDir ... -EnableCrashDumps
# with no other action) or combined with a normal install in one call. ------------------------
if ($EnableCrashDumps -or $DisableCrashDumps) {
    $currentUser = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    $isAdmin = $currentUser.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if (-not $isAdmin) {
        $message = "crash dumps need an elevated (Run as Administrator) PowerShell - Windows " +
            "Error Reporting's LocalDumps key lives under HKEY_LOCAL_MACHINE, which a standard " +
            "user cannot write. Re-run this exact command from an Administrator PowerShell."
        throw $message
    }

    $wer = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\swkotor.exe'

    if ($EnableCrashDumps) {
        $folder = if ($DumpFolder) { $DumpFolder } else { Join-Path $GameDir 'crashdumps' }
        New-Item -Path $wer -Force | Out-Null
        New-Item -ItemType Directory -Force -Path $folder | Out-Null
        Set-ItemProperty -Path $wer -Name 'DumpFolder' -Value $folder -Type ExpandString
        Set-ItemProperty -Path $wer -Name 'DumpCount' -Value 10 -Type DWord
        Set-ItemProperty -Path $wer -Name 'DumpType' -Value 2 -Type DWord   # 2 = full dump
        "crash dumps enabled for swkotor.exe -> $folder"
        'full dumps of a 32-bit game are roughly 200 MB each, capped at the 10 most recent.'
        "disable again with: pwsh install.ps1 -GameDir '$GameDir' -DisableCrashDumps"
    }
    else {
        if (Test-Path $wer) {
            Remove-Item -Path $wer -Recurse -Force
            'crash dumps disabled for swkotor.exe (the LocalDumps registry key was removed).'
        }
        else {
            'crash dumps were already off for swkotor.exe - nothing to do.'
        }
    }
}

# --- health check (-Check): read-only, standalone - reports and exits before touching
# anything else below. --------------------------------------------------------------------
if ($Check) {
    & $agent install --game-path $GameDir --check
    if ($LASTEXITCODE -ne 0) { throw 'chain health check found a problem - see the FAIL line(s) above' }
    return
}

if ($Uninstall) {
    & $agent uninstall --game-path $GameDir
}
else {
    $installArgs = @('install', '--from', $PSScriptRoot, '--game-path', $GameDir)
    if ($Chain) { $installArgs += '--chain' }
    if ($Load.Count -gt 0) { $installArgs += @('--load', ($Load -join ',')) }
    if ($Force) { $installArgs += '--force' }
    if ($UpdateDefaults) { $installArgs += '--update-defaults' }
    & $agent @installArgs
}

if ($LASTEXITCODE -ne 0) { throw "KotorCoop.Agent.exe exited with code $LASTEXITCODE" }

# Defaults migration is handled by the agent for both command-line and double-click setup.
