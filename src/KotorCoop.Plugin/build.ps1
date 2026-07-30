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
$vsRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community'
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
    ''
    'The game loads DirectInput from its own directory before the system one, so this'
    'is picked up on next launch. Nothing else was modified; remove it with -Uninstall.'
}
