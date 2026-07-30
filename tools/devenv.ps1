# Imports the MSVC x86 developer environment into the current PowerShell session.
# NativeAOT shells out to link.exe, which needs INCLUDE/LIB/PATH set up, and it also
# calls vswhere.exe, which only lives on PATH inside a developer prompt.
param([string]$Arch = 'x86')

$vsRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community'
$vcvars = "$vsRoot\VC\Auxiliary\Build\vcvarsall.bat"

if (-not (Test-Path $vcvars)) { throw "vcvarsall.bat not found at $vcvars" }

$installerDir = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"

# `set` after vcvarsall gives the fully-configured environment; import it wholesale.
$output = cmd /c "`"$vcvars`" $Arch >nul 2>&1 && set" 2>$null

foreach ($line in $output) {
    if ($line -match '^([^=]+)=(.*)$') {
        $name = $matches[1]
        $value = $matches[2]
        if ($name -notin @('PROMPT', 'TERM')) {
            Set-Item -Path "Env:$name" -Value $value -ErrorAction SilentlyContinue
        }
    }
}

# vswhere lives in the Installer directory, which vcvarsall does not add.
if ((Test-Path $installerDir) -and ($env:PATH -notlike "*$installerDir*")) {
    $env:PATH = "$installerDir;$env:PATH"
}

"MSVC $Arch environment imported"
"  cl.exe    : $((Get-Command cl.exe -ErrorAction SilentlyContinue).Source)"
"  link.exe  : $((Get-Command link.exe -ErrorAction SilentlyContinue).Source)"
"  vswhere   : $((Get-Command vswhere.exe -ErrorAction SilentlyContinue).Source)"
