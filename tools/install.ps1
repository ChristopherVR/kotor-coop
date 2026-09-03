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
# See tools/CoopIniDefaults.ps1 for the actual parsing/decision logic (dot-sourceable and
# side-effect-free on its own - exercise it directly per tools/README.md's manual-verification
# note; this repo has no Pester harness to automate it yet) and tools/README.md for both.
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

if ($Uninstall) {
    & $agent uninstall --game-path $GameDir
}
else {
    # Captured BEFORE the install call: PackageInstaller writes a fresh coop.ini only when none
    # exists, and a freshly-written file is by definition current - the drift check below only
    # means anything against a coop.ini that predates this run.
    $iniPath = Join-Path $GameDir 'coop.ini'
    $iniExistedBefore = Test-Path -LiteralPath $iniPath

    $installArgs = @('install', '--from', $PSScriptRoot, '--game-path', $GameDir)
    if ($Chain) { $installArgs += '--chain' }
    if ($Load.Count -gt 0) { $installArgs += @('--load', ($Load -join ',')) }
    if ($Force) { $installArgs += '--force' }
    & $agent @installArgs
}

if ($LASTEXITCODE -ne 0) { throw "KotorCoop.Agent.exe exited with code $LASTEXITCODE" }

# --- coop.ini upgrade-drift check: only for a plain (non-uninstall) run, and only when the
# player's coop.ini predates this install ------------------------------------------------------
if (-not $Uninstall -and $iniExistedBefore) {
    . (Join-Path $PSScriptRoot 'CoopIniDefaults.ps1')

    $playerIni = ConvertFrom-CoopIni -Path $iniPath
    # The reference is whatever coop.ini SHIPPED beside this install.ps1 in the current
    # package - since package.ps1 stages profiles/coop.ini.example as coop.ini in the same zip
    # root, this is the current, correct set of keys/defaults, no separate .example file needed.
    $referencePath = Join-Path $PSScriptRoot 'coop.ini'
    $referenceIni = if (Test-Path -LiteralPath $referencePath) { ConvertFrom-CoopIni -Path $referencePath } else { $null }

    $drift = Get-CoopIniDrift -PlayerIni $playerIni -ReferenceIni $referenceIni

    if ($drift.Drifted.Count -gt 0) {
        ''
        "your coop.ini predates this version and still has $($drift.Drifted.Count) default(s) that changed:"
        foreach ($entry in $drift.Drifted) {
            Write-Warning "  $($entry.Key) = $($entry.OldValue)  (new shipped default since $($entry.Date): $($entry.NewValue) - $($entry.Meaning))"
        }

        if ($UpdateDefaults) {
            $changed = Update-CoopIniDefaults -Path $iniPath -Drifted $drift.Drifted
            "updated $changed line(s) in $iniPath"
        }
        else {
            "run with -UpdateDefaults to rewrite just those lines in place (everything else in your coop.ini is left untouched)."
        }
    }

    if ($drift.MissingKeys.Count -gt 0) {
        ''
        $missingHeader = "informational: your coop.ini is also missing $($drift.MissingKeys.Count) key(s) " +
            "present in the shipped template (these simply use the code's own built-in default - " +
            "see docs/CONFIG.md):"
        $missingHeader
        "  $($drift.MissingKeys -join ', ')"
    }

    if ($drift.RemovableDeveloperKeys.Count -gt 0) {
        ''
        $removableKeys = $drift.RemovableDeveloperKeys | ForEach-Object { $_.Key }
        "your coop.ini also still sets $($drift.RemovableDeveloperKeys.Count) developer switch(es) " +
            "explicitly, each already at its own current default - noise, since the shipped " +
            "coop.ini no longer lists developer switches at all (see docs/CONFIG.md's " +
            "`"Developer switches`" table):"
        foreach ($entry in $drift.RemovableDeveloperKeys) {
            "  $($entry.Key) = $($entry.Value)"
        }

        if ($UpdateDefaults) {
            $removedCount = Remove-CoopIniKeys -Path $iniPath -Keys $removableKeys
            "removed $removedCount noise line(s) from $iniPath"
        }
        else {
            "run with -UpdateDefaults to remove just those lines (a developer switch set to " +
                "anything OTHER than its current default is left untouched - that is a " +
                "deliberate choice, not noise)."
        }
    }
}
