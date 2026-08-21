# Builds the in-process plugin as a native 32-bit dinput8.dll and optionally installs it.
#
# NativeAOT shells out to link.exe and vswhere.exe, neither of which is reachable outside
# a Visual Studio developer environment, so this imports the MSVC x86 environment first.
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\swkotor',
    [switch]$Install,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot

# Locate VS2022 via vswhere rather than hardcoding the Community edition path: a dev box
# normally has Community, but CI runners (GitHub's windows-latest included) ship Enterprise,
# and the two editions live at different paths. Community is tried first only as a fallback
# for a machine where vswhere itself is not installed yet.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community'
if (Test-Path $vswhere) {
    $found = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($found) { $vsRoot = ($found | Select-Object -First 1).Trim() }
}
$vcvars = "$vsRoot\VC\Auxiliary\Build\vcvarsall.bat"

function Import-MsvcEnvironment {
    if (-not (Test-Path $vcvars)) { throw "vcvarsall.bat not found at $vcvars" }

    $output = cmd /c "`"$vcvars`" x86 >nul 2>&1 && set" 2>$null
    foreach ($line in $output) {
        if ($line -match '^([^=]+)=(.*)$' -and $matches[1] -notin @('PROMPT', 'TERM')) {
            Set-Item -Path "Env:$($matches[1])" -Value $matches[2] -ErrorAction SilentlyContinue
        }
    }

    $installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
    if ((Test-Path $installer) -and ($env:PATH -notlike "*$installer*")) {
        $env:PATH = "$installer;$env:PATH"
    }
}

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

    # The agent -Install deploys. Every file is KotorCoop.*, so this can never touch a
    # game file or a user's mod.
    foreach ($ours in Get-ChildItem $GameDir -File -Filter 'KotorCoop.*' -ErrorAction SilentlyContinue) {
        Remove-Item $ours.FullName -Force
        "removed agent file $($ours.FullName)"
    }

    # Remove only the override assets we installed — every one is a k_coop_ resref, so
    # this can never delete a user's mod file.
    $overrideTarget = Join-Path $GameDir 'Override'
    if (Test-Path $overrideTarget) {
        foreach ($ours in Get-ChildItem $overrideTarget -File -Filter 'k_coop_*') {
            Remove-Item $ours.FullName -Force
            "removed override $($ours.FullName)"
        }
    }

    return
}

Import-MsvcEnvironment
"MSVC x86 environment imported (cl: $((Get-Command cl.exe).Source))"

Push-Location $projectDir
try {
    dotnet publish -r win-x86 -c Release
    if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

$built = Get-ChildItem "$projectDir\bin\x86\Release\net10.0-windows\win-x86\publish\dinput8.dll" -ErrorAction SilentlyContinue
if (-not $built) {
    $built = Get-ChildItem "$projectDir\bin" -Recurse -Filter 'dinput8.dll' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

if (-not $built) { throw 'build succeeded but dinput8.dll was not found' }

"built: $($built.FullName)  ($([math]::Round($built.Length / 1KB)) KB)"

# Confirm it really is a 32-bit image. Loading an x64 DLL into swkotor.exe would fail
# silently from the game's point of view, and the symptom (game runs, plugin never
# appears) looks exactly like a code bug.
$bytes = [System.IO.File]::ReadAllBytes($built.FullName)
$pe = [BitConverter]::ToInt32($bytes, 0x3C)
$machine = [BitConverter]::ToUInt16($bytes, $pe + 4)
$arch = switch ($machine) { 0x14C { 'x86 (correct)' } 0x8664 { 'x64 (WRONG)' } default { "0x$('{0:X}' -f $machine)" } }
"architecture: $arch"
if ($machine -ne 0x14C) { throw 'plugin must be x86 to load into swkotor.exe' }

if ($Install) {
    if (-not (Test-Path $GameDir)) { throw "game directory not found: $GameDir" }

    $target = Join-Path $GameDir 'dinput8.dll'
    Copy-Item $built.FullName $target -Force
    "installed -> $target"

    # Deploy the co-op override assets (the phantom blueprint, etc.). Every file we ship
    # uses a k_coop_ resref, so it can never collide with a user's mods; we only ever ADD
    # to the override folder, never touch a file we did not put there.
    $overrideSource = Join-Path (Split-Path (Split-Path $projectDir -Parent) -Parent) 'profiles\override'
    if (Test-Path $overrideSource) {
        $overrideTarget = Join-Path $GameDir 'Override'
        if (-not (Test-Path $overrideTarget)) { New-Item -ItemType Directory -Path $overrideTarget | Out-Null }

        foreach ($asset in Get-ChildItem $overrideSource -File) {
            Copy-Item $asset.FullName (Join-Path $overrideTarget $asset.Name) -Force
            "installed override -> $(Join-Path $overrideTarget $asset.Name)"
        }
    }

    # Deploy the AGENT beside the game too. The plugin's AgentLauncher auto-starts
    # KotorCoop.Agent.exe from the game directory (or kotorcoop\/coop\ under it) — that is
    # what makes "launch the game and play" the whole experience. Until now the agent was
    # hand-copied once and silently went stale on every rebuild, which is the worst kind of
    # version skew: the plugin and agent meet over a signature-checked shared block, so a
    # stale agent either refuses the block or, worse, drives it with last week's semantics.
    # Installing the plugin without the agent it launches is half an install.
    $agentProject = Join-Path (Split-Path $projectDir -Parent) 'KotorCoop.Agent'
    Push-Location $agentProject
    try {
        dotnet build -v q --nologo
        if ($LASTEXITCODE -ne 0) { throw "agent build failed with exit code $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }

    $agentBin = Join-Path $agentProject 'bin\Debug\net10.0-windows'
    if (-not (Test-Path (Join-Path $agentBin 'KotorCoop.Agent.exe'))) {
        throw "agent build succeeded but KotorCoop.Agent.exe not found in $agentBin"
    }

    # Game root, not a subfolder: AgentLauncher searches the game dir FIRST, so a loose
    # copy there (which every existing install has) would shadow anything we put deeper.
    $agentInstalled = 0
    foreach ($file in Get-ChildItem $agentBin -File) {
        try {
            Copy-Item $file.FullName (Join-Path $GameDir $file.Name) -Force
            $agentInstalled++
        }
        catch [System.IO.IOException] {
            # A running agent holds its own image; the plugin and overrides above still
            # installed. Say so rather than half-failing silently.
            throw "agent file locked ($($file.Name)) - stop KotorCoop.Agent.exe and re-run -Install"
        }
    }

    "installed agent -> $GameDir ($agentInstalled files)"

    ''
    'The game loads DirectInput from its own directory before the system one, so this'
    'is picked up on next launch. Nothing else was modified; remove it with -Uninstall.'
}
