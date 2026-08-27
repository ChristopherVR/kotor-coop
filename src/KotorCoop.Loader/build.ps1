# Builds the generic dinput8 chain loader (see dinput8_loader.c) as a tiny native x86 DLL
# with the MSVC toolset — no CRT, no runtime, a few KB.
#
#   pwsh src\KotorCoop.Loader\build.ps1              # -> bin\dinput8.dll
#   pwsh src\KotorCoop.Loader\build.ps1 -WarnAsError # what CI runs
param(
    [switch]$WarnAsError
)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$repo = Split-Path (Split-Path $projectDir -Parent) -Parent

# Same MSVC x86 environment the plugin build uses (cl.exe, link.exe on PATH, INCLUDE/LIB set).
. (Join-Path $repo 'tools\devenv.ps1') -Arch x86 -Quiet

$outDir = Join-Path $projectDir 'bin'
$objDir = Join-Path $projectDir 'obj'
foreach ($dir in @($outDir, $objDir)) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
}

$source = Join-Path $projectDir 'dinput8_loader.c'
$def = Join-Path $projectDir 'dinput8.def'
$obj = Join-Path $objDir 'dinput8_loader.obj'
$dll = Join-Path $outDir 'dinput8.dll'

$clArgs = @(
    '/nologo', '/W4', '/O1', '/GS-', '/Gy', '/Zl',       # /Zl: no default CRT library reference
    '/D', 'UNICODE', '/D', '_UNICODE',
    '/c', $source, "/Fo$obj"
)
if ($WarnAsError) { $clArgs += '/WX' }

& cl.exe @clArgs
if ($LASTEXITCODE -ne 0) { throw "cl.exe failed with exit code $LASTEXITCODE" }

$linkArgs = @(
    '/nologo', '/DLL', '/NODEFAULTLIB', '/ENTRY:DllMain', '/SUBSYSTEM:WINDOWS,5.01',
    '/MACHINE:X86', '/SAFESEH:NO', '/OPT:REF', '/OPT:ICF',
    "/DEF:$def", "/OUT:$dll", $obj, 'kernel32.lib'
)
& link.exe @linkArgs
if ($LASTEXITCODE -ne 0) { throw "link.exe failed with exit code $LASTEXITCODE" }

# Same sanity check as the plugin build: an x64 image would be loaded by nothing and fail silently.
$bytes = [System.IO.File]::ReadAllBytes($dll)
$pe = [BitConverter]::ToInt32($bytes, 0x3C)
$machine = [BitConverter]::ToUInt16($bytes, $pe + 4)
if ($machine -ne 0x14C) { throw "loader must be x86 (machine 0x$('{0:X}' -f $machine))" }

"built: $dll  ($([math]::Round((Get-Item $dll).Length / 1KB)) KB, x86)"
