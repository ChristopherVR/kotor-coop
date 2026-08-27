# Imports the MSVC developer environment into the CURRENT PowerShell session.
#
# NativeAOT shells out to link.exe (needs INCLUDE/LIB/PATH) and vswhere.exe (only on PATH in a
# developer prompt); the loader build calls cl.exe/link.exe directly. Both build scripts
# dot-source this file, and it works standalone too:
#
#   . tools\devenv.ps1          # x86 (what swkotor.exe needs)
#   . tools\devenv.ps1 -Arch x64
#
# Visual Studio is located through vswhere, not a hardcoded edition path: a dev box usually
# has Community, GitHub's windows-latest runners ship Enterprise, and the two live in
# different directories. The Community path is only the fallback for a machine where vswhere
# itself is missing.
param(
    [string]$Arch = 'x86',
    [switch]$Quiet
)

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community'
if (Test-Path $vswhere) {
    $found = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($found) { $vsRoot = ($found | Select-Object -First 1).Trim() }
}

$vcvars = Join-Path $vsRoot 'VC\Auxiliary\Build\vcvarsall.bat'
if (-not (Test-Path $vcvars)) {
    throw "vcvarsall.bat not found at $vcvars - install the 'Desktop development with C++' workload (MSVC x86/x64 build tools)"
}

# `set` after vcvarsall prints the fully-configured environment; import it wholesale.
$output = cmd /c "`"$vcvars`" $Arch >nul 2>&1 && set" 2>$null
foreach ($line in $output) {
    if ($line -match '^([^=]+)=(.*)$' -and $matches[1] -notin @('PROMPT', 'TERM')) {
        Set-Item -Path "Env:$($matches[1])" -Value $matches[2] -ErrorAction SilentlyContinue
    }
}

$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path $installer) -and ($env:PATH -notlike "*$installer*")) {
    $env:PATH = "$installer;$env:PATH"
}

if (-not $Quiet) {
    "MSVC $Arch environment imported from $vsRoot (cl: $((Get-Command cl.exe -ErrorAction SilentlyContinue).Source))"
}
