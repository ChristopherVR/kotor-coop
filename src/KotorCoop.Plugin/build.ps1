# Builds the in-process plugin as a native 32-bit dinput8.dll and optionally installs it,
# together with the agent and the k_coop_ override assets, into a game directory.
#
# NativeAOT shells out to link.exe and vswhere.exe, neither of which is reachable outside
# a Visual Studio developer environment, so this imports the MSVC x86 environment first
# (tools/devenv.ps1). No -Install: just builds. -Uninstall removes everything -Install placed.
#
#   pwsh src\KotorCoop.Plugin\build.ps1                                   # build only
#   pwsh src\KotorCoop.Plugin\build.ps1 -Install                          # build + deploy to the GOG default
#   pwsh src\KotorCoop.Plugin\build.ps1 -Install -GameDir 'D:\Games\swkotor-gog-p2'
#   pwsh src\KotorCoop.Plugin\build.ps1 -WarnAsError                      # what CI runs
param(
    # The DRM-free GOG build is the only supported target (README: Requirements).
    [string]$GameDir = 'C:\GOG Games\Star Wars KOTOR (GOG)',
    [switch]$Install,
    [switch]$Uninstall,
    # Fail the build on any compiler/analyzer warning (CA2014 & co.), so a warning that
    # only shows up in a log never ships. CI passes this.
    [switch]$WarnAsError
)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$repo = Split-Path (Split-Path $projectDir -Parent) -Parent

if ($Uninstall) {
    # Same removal rules as the packaged installer, so a dev install and a player install
    # come out the same way: only files we put there, identified by marker/prefix.
    & (Join-Path $repo 'tools\install.ps1') -GameDir $GameDir -Uninstall
    return
}

. (Join-Path $repo 'tools\devenv.ps1') -Arch x86

$publishDir = Join-Path $projectDir 'bin\x86\Release\net10.0-windows\win-x86\publish'

Push-Location $projectDir
try {
    # vcvarsall sets Platform=x86. Depending on how dotnet/MSBuild was launched, that
    # environment variable can either add an `x86` directory to the default output path or
    # be ignored. Pin the publish directory so deployment never has to guess which artifact
    # this invocation just produced (and cannot silently select an older DLL from another
    # output tree).
    $publishArgs = @('publish', '-r', 'win-x86', '-c', 'Release', '-o', $publishDir)
    if ($WarnAsError) { $publishArgs += '-warnaserror' }
    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

$builtPath = Join-Path $publishDir 'dinput8.dll'
if (-not (Test-Path -LiteralPath $builtPath -PathType Leaf)) {
    throw "publish succeeded but dinput8.dll was not found at the configured output path: $builtPath"
}
$built = Get-Item -LiteralPath $builtPath

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

if (-not $Install) { return }

if (-not (Test-Path $GameDir)) { throw "game directory not found: $GameDir" }

# Deploy the AGENT beside the game too. The plugin's AgentLauncher auto-starts
# KotorCoop.Agent.exe from the game directory — that is what makes "launch the game and
# play" the whole experience. A hand-copied agent silently goes stale on every rebuild,
# which is the worst kind of version skew: the plugin and agent meet over a
# signature-checked shared block, so a stale agent either refuses the block or, worse,
# drives it with last week's semantics. Installing the plugin without the agent it
# launches is half an install. Debug configuration on purpose: it is the same build
# tools/demo.ps1 launches by hand, so the rig and the auto-started agent never differ.
$agentProject = Join-Path (Split-Path $projectDir -Parent) 'KotorCoop.Agent'
Push-Location $agentProject
# devenv.ps1 (imported above for link.exe/vswhere.exe) sets $env:Platform = 'x86' as part of
# vcvarsall's environment dump. dotnet/MSBuild treats a `Platform` env var as an implicit
# `-p:Platform=x86` override, which redirects THIS build's output to `bin\x86\Debug\...`
# instead of the plain `bin\Debug\...` the check below (and demo.ps1/tools/*) expect — a
# silent path mismatch, not a build failure. Clear it only around this one dotnet call.
$savedPlatform = $env:Platform
Remove-Item Env:\Platform -ErrorAction SilentlyContinue
try {
    dotnet build -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "agent build failed with exit code $LASTEXITCODE" }
}
finally {
    if ($null -ne $savedPlatform) { $env:Platform = $savedPlatform }
    Pop-Location
}

$agentBin = Join-Path $agentProject 'bin\Debug\net10.0-windows'
if (-not (Test-Path (Join-Path $agentBin 'KotorCoop.Agent.exe'))) {
    throw "agent build succeeded but KotorCoop.Agent.exe not found in $agentBin"
}

# One code path for "put these files into a game directory": the agent's own installer
# (PackageInstaller.cs), run from a staging tree laid out exactly like the release zip.
$staging = Join-Path $env:TEMP 'kotorcoop-dev-install'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $staging 'Override') | Out-Null
Copy-Item $built.FullName (Join-Path $staging 'dinput8.dll')
Get-ChildItem $agentBin -File -Filter 'KotorCoop.*' | Copy-Item -Destination $staging
Get-ChildItem (Join-Path $repo 'profiles\override') -File -Filter 'k_coop_*' | Copy-Item -Destination (Join-Path $staging 'Override')
Copy-Item (Join-Path $repo 'profiles\coop.ini.example') (Join-Path $staging 'coop.ini')

$loaderDll = Join-Path $repo 'src\KotorCoop.Loader\bin\dinput8.dll'
if (Test-Path $loaderDll) {
    New-Item -ItemType Directory -Path (Join-Path $staging 'chain-loader') | Out-Null
    Copy-Item $loaderDll (Join-Path $staging 'chain-loader\dinput8.dll')
}

& (Join-Path $staging 'KotorCoop.Agent.exe') install --from $staging --game-path $GameDir
if ($LASTEXITCODE -ne 0) { throw "install failed with exit code $LASTEXITCODE" }
Remove-Item $staging -Recurse -Force
