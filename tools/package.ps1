# Builds the release packages. Both are the same GAME-DIRECTORY IMAGE: extracting one straight
# into the KOTOR folder is a complete install; the Windows zip can also be double-clicked
# (KotorCoop.Agent.exe finds the game and copies everything in), the Linux tarball ships
# install.sh for the same job on the Linux side of a Proton/Wine setup. Layout of the image:
#
#   dinput8.dll                 the NativeAOT plugin (the proxy the game loads itself)
#   KotorCoop.*                 the agent: one self-contained exe (runtime inside), no pdbs
#   coop.ini                    the config template
#   Override\k_coop_*           the override assets the plugin needs
#   chain-loader\dinput8.dll    the C wrapper for sharing the dinput8 slot with another mod
#   dinput8.chain.example       the annotated chain list
#   README.txt                  install / play / configure / troubleshoot, version-stamped
#   install.ps1                 [zip only] command-line wrapper over `KotorCoop.Agent.exe install`
#   KotorCoop.DEFAULTS.ini      incoming player defaults, kept separately from coop.ini
#   KotorCoop.CollectLogs.cmd   double-click local support collection
#   install.sh                  [tar.gz only] the native Linux installer (bash), mode 0755
#
# The tarball has one top-level directory (kotor-coop-<version>/) as tarballs do; the zip has
# none. Extract into a separate folder and use the installer for safe updates and chaining.
# The game binaries inside are identical:
# KOTOR is a Windows program on every platform, so there is no separate Linux build of the mod.
#
# The agent's own pdbs are NOT in either player package (dead weight for a player - they carry
# no runtime behaviour, only debug symbols) but ARE kept, separately, as
# dist\kotor-coop-<version>-symbols.zip: a maintainer needs them to turn a managed stack trace
# from a bug report back into file/line, and a publish only produces them alongside a fresh
# build, not on demand later. See docs/research/proton.md for why the exe is self-contained.
#
# Does NOT touch a game install and does NOT run the game. CI runs this on every push and
# attaches both files to the run; releases require explicit tested-tag promotion.
# Tested v* tags require manual numbered promotion (.github/workflows/release.yml). The symbols zip is CI-artifact-only -
# it is never attached to a GitHub release, only kept for maintainers to pull off a CI run.
#
#   pwsh tools\package.ps1                 # dist\kotor-coop-<git describe>-windows.zip + -linux.tar.gz
#   pwsh tools\package.ps1 -Version 1.2.3  # override the version
#   pwsh tools\package.ps1 -WarnAsError    # what CI runs: any build warning fails the package
param(
    [string]$Version = '',
    [string]$OutDir = '',
    [switch]$WarnAsError
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutDir) { $OutDir = Join-Path $repo 'dist' }

if (-not $Version) {
    # Only v* tags count: the rolling "latest" release tag would otherwise name every build
    # "latest-1-g<sha>".
    $Version = (git -C $repo describe --tags --match 'v*' --always 2>$null)
    if (-not $Version) { $Version = 'dev' }
    $Version = $Version.Trim()

    # The VERSION file is the release number. The build is named exactly VERSION on the commit
    # that last changed that file, and "<VERSION>-<commits since>-g<sha>" after it, so the first
    # build of a bumped VERSION is the numbered candidate and later ones are clearly newer
    # snapshots of it. Numbered publication requires explicit promotion of a tested tag.
    $versionFile = Join-Path $repo 'VERSION'
    if (Test-Path $versionFile) {
        $declared = (Get-Content $versionFile -Raw).Trim()
        $bumped = (git -C $repo log -1 --format=%H -- VERSION 2>$null)
        if ($declared -and $bumped) {
            $since = [int](git -C $repo rev-list --count "$($bumped.Trim())..HEAD" 2>$null)
            $sha = (git -C $repo rev-parse --short=7 HEAD 2>$null)
            $Version = if ($since -eq 0) { $declared } else { "$declared-$since-g$($sha.Trim())" }
        }
        elseif ($declared) { $Version = $declared }
    }
}
$Version = $Version -replace '^v', ''

"packaging kotor-coop $Version"

$staging = Join-Path $env:TEMP "kotorcoop-package-$Version"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

# build.ps1 imports the MSVC x86 environment (vcvarsall.bat) into ITS OWN process env, which
# includes a "Platform=x86" variable that MSBuild picks up from any dotnet invocation that
# follows in the same process — silently redirecting the agent's plain `dotnet publish` below
# into a bin\x86\... output path instead of bin\Release\net10.0-windows\win-x64\publish. Running the
# native builds as child pwsh processes keeps that environment from leaking into this script.
$warnArgs = if ($WarnAsError) { @('-WarnAsError') } else { @() }

# --- plugin: NativeAOT win-x86 dinput8.dll -------------------------------------------------
pwsh -NoProfile -File (Join-Path $repo 'src\KotorCoop.Plugin\build.ps1') @warnArgs
if ($LASTEXITCODE -ne 0) { throw "plugin build failed with exit code $LASTEXITCODE" }

$pluginDll = Get-ChildItem (Join-Path $repo 'src\KotorCoop.Plugin\bin\x86\Release\net10.0-windows\win-x86\publish\dinput8.dll') -ErrorAction SilentlyContinue
if (-not $pluginDll) {
    $pluginDll = Get-ChildItem (Join-Path $repo 'src\KotorCoop.Plugin\bin') -Recurse -Filter 'dinput8.dll' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if (-not $pluginDll) { throw 'plugin build succeeded but dinput8.dll was not found' }

Copy-Item $pluginDll.FullName (Join-Path $staging 'dinput8.dll')
"staged plugin: $($pluginDll.FullName)"

# --- loader: the generic dinput8 chain wrapper (plain C, a few KB) --------------------------
pwsh -NoProfile -File (Join-Path $repo 'src\KotorCoop.Loader\build.ps1') @warnArgs
if ($LASTEXITCODE -ne 0) { throw "loader build failed with exit code $LASTEXITCODE" }

$loaderDll = Join-Path $repo 'src\KotorCoop.Loader\bin\dinput8.dll'
if (-not (Test-Path $loaderDll)) { throw "loader build succeeded but $loaderDll was not found" }

New-Item -ItemType Directory -Path (Join-Path $staging 'chain-loader') | Out-Null
Copy-Item $loaderDll (Join-Path $staging 'chain-loader\dinput8.dll')
Copy-Item (Join-Path $repo 'src\KotorCoop.Loader\dinput8.chain.example') (Join-Path $staging 'dinput8.chain.example')
"staged loader: $loaderDll"

# --- agent: self-contained single-file win-x64 publish (the csproj's _IsPublishing block) ----
# One KotorCoop.Agent.exe with the runtime inside: no .NET install on the player's machine,
# and nothing to put into a Wine prefix for the Proton/Steam Deck case (docs/research/proton.md).
$agentProject = Join-Path $repo 'src\KotorCoop.Agent'
$agentPublishDir = Join-Path $agentProject 'bin\Release\net10.0-windows\win-x64\publish'
# Start from an empty publish directory: an incremental publish over a previous one skipped the
# referenced projects' pdbs (seen 2026-09-03: the second run in a row shipped 2 KotorCoop.*
# files instead of 6), and the package must not depend on what the last run left behind.
if (Test-Path $agentPublishDir) { Remove-Item $agentPublishDir -Recurse -Force }
Push-Location $agentProject
try {
    # Stamped into KotorCoop.Agent.exe's version resource (InformationalVersion / FileVersionInfo
    # .ProductVersion) so a later install/setup run can tell a player what's already there vs.
    # what this package would put in (PackageInstaller.ReadVersion, InstallCommand.ReportVersion).
    $agentArgs = @('publish', '-c', 'Release', "-p:InformationalVersion=$Version")
    if ($WarnAsError) { $agentArgs += '-warnaserror' }
    dotnet @agentArgs
    if ($LASTEXITCODE -ne 0) { throw "agent publish failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

if (-not (Test-Path (Join-Path $agentPublishDir 'KotorCoop.Agent.exe'))) {
    throw "agent publish succeeded but KotorCoop.Agent.exe not found in $agentPublishDir"
}

# Game root, not a subfolder: AgentLauncher searches the game directory first. Only
# KotorCoop.* files, so the uninstaller's "everything KotorCoop.* is ours" rule holds — the
# single-file publish is what keeps a bundled runtime inside that rule (a plain self-contained
# publish drops ~200 System.*.dll beside the game and trips the check below).
$agentFiles = Get-ChildItem $agentPublishDir -File -Filter 'KotorCoop.*'
$agentPdbs = $agentFiles | Where-Object { $_.Extension -eq '.pdb' }
$agentBinaries = $agentFiles | Where-Object { $_.Extension -ne '.pdb' }
foreach ($file in $agentBinaries) { Copy-Item $file.FullName (Join-Path $staging $file.Name) }
$stray = Get-ChildItem $agentPublishDir -File | Where-Object { $_.Name -notlike 'KotorCoop.*' }
if ($stray) { throw "agent publish contains non-KotorCoop.* files the installer cannot track: $($stray.Name -join ', ')" }
$agentExe = Get-Item (Join-Path $agentPublishDir 'KotorCoop.Agent.exe')
if ($agentExe.Length -lt 20MB) { throw "KotorCoop.Agent.exe is $([math]::Round($agentExe.Length / 1MB, 1)) MB: not the self-contained single-file publish (see the _IsPublishing block in KotorCoop.Agent.csproj)" }
$missingPdb = @('Agent', 'Memory', 'Protocol', 'Session', 'Steam') | Where-Object { -not (Test-Path (Join-Path $agentPublishDir "KotorCoop.$_.pdb")) }
if ($missingPdb) { throw "agent publish is missing the pdb(s) of: $($missingPdb -join ', ') (crash symbolication needs them - see the symbols zip below)" }
"staged agent: $agentPublishDir ($($agentBinaries.Count) files, $($agentPdbs.Count) pdbs held back for the symbols zip)"

# --- symbols: the pdbs a player never needs, kept for turning a bug report's stack trace ---
# back into file/line. Its own zip, never inside the player package or a GitHub release.
$symbolsDir = Join-Path $env:TEMP "kotorcoop-symbols-$Version"
if (Test-Path $symbolsDir) { Remove-Item $symbolsDir -Recurse -Force }
New-Item -ItemType Directory -Path $symbolsDir | Out-Null
foreach ($pdb in $agentPdbs) { Copy-Item $pdb.FullName (Join-Path $symbolsDir $pdb.Name) }

# --- coop.ini template, renamed from the .example so a player can use it as-is -------------
$iniSource = Join-Path $repo 'profiles\coop.ini.example'
if (-not (Test-Path $iniSource)) { throw "coop.ini.example not found at $iniSource" }
Copy-Item $iniSource (Join-Path $staging 'coop.ini')
Copy-Item $iniSource (Join-Path $staging 'KotorCoop.DEFAULTS.ini')
"staged coop.ini <- $iniSource"

# --- k_coop_ override assets the plugin needs (phantom blueprint, spawn waypoint) ----------
# Checked-in outputs of PhantomBlueprint/SpawnWaypointBlueprint (src/KotorCoop.Memory/GameFiles).
$overrideSource = Join-Path $repo 'profiles\override'
$overrideFiles = Get-ChildItem $overrideSource -File -Filter 'k_coop_*' -ErrorAction SilentlyContinue
if (-not $overrideFiles) { throw "no k_coop_* files found under $overrideSource" }
New-Item -ItemType Directory -Path (Join-Path $staging 'Override') | Out-Null
foreach ($asset in $overrideFiles) { Copy-Item $asset.FullName (Join-Path $staging 'Override' $asset.Name) }
"staged override assets: $($overrideFiles.Count) files"

# Per-mod compatibility rules are co-op assets, not the mods themselves.
New-Item -ItemType Directory -Path (Join-Path $staging 'coop-mods') | Out-Null
Get-ChildItem (Join-Path $repo 'profiles\mods') -File -Filter '*.json' |
    Copy-Item -Destination (Join-Path $staging 'coop-mods')

# --- installer wrapper + README ------------------------------------------------------------
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $staging 'KotorCoop.LICENSE.txt')
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') (Join-Path $staging 'KotorCoop.THIRD-PARTY-INVENTORY.md')
& (Join-Path $PSScriptRoot 'Stage-ThirdPartyNotices.ps1') `
    -AgentDepsPath (Join-Path $agentPublishDir '..\KotorCoop.Agent.deps.json') `
    -PluginAssetsPath (Join-Path $repo 'src\KotorCoop.Plugin\obj\project.assets.json') `
    -Destination (Join-Path $staging 'KotorCoop.THIRD-PARTY-NOTICES.txt')
Copy-Item (Join-Path $repo 'docs\LEGAL.md') (Join-Path $staging 'KotorCoop.LEGAL.txt')
Copy-Item (Join-Path $PSScriptRoot 'install.ps1') (Join-Path $staging 'install.ps1')
Copy-Item (Join-Path $PSScriptRoot 'KotorCoop.CollectLogs.cmd') (Join-Path $staging 'KotorCoop.CollectLogs.cmd')
# Settings migration runs in the agent; no separate PowerShell drift table is required.

$readme = (Get-Content (Join-Path $PSScriptRoot 'package-README.txt') -Raw).Replace('{{VERSION}}', $Version)
Set-Content -Path (Join-Path $staging 'README.txt') -Value $readme -Encoding UTF8 -NoNewline
"staged install.ps1 + log collector + README.txt"

# --- Windows: zip it up ----------------------------------------------------------------------
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$zipPath = Join-Path $OutDir "kotor-coop-$Version-windows.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath
"packaged -> $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1KB)) KB)"

# --- symbols zip: CI-artifact-only, never attached to a release -----------------------------
$symbolsZipPath = Join-Path $OutDir "kotor-coop-$Version-symbols.zip"
if (Test-Path $symbolsZipPath) { Remove-Item $symbolsZipPath -Force }
Compress-Archive -Path (Join-Path $symbolsDir '*') -DestinationPath $symbolsZipPath
"packaged -> $symbolsZipPath ($([math]::Round((Get-Item $symbolsZipPath).Length / 1KB)) KB)"

# --- Linux: tar.gz with install.sh executable ------------------------------------------------
# Written through System.Formats.Tar rather than tar.exe: Windows' bsdtar records every file
# as 0666, so a `tar xzf` on Linux would leave install.sh without its exec bit. install.sh is
# also normalised to LF here (the repo has *.sh eol=lf in .gitattributes; this is the belt to
# that brace, an autocrlf checkout must not ship a script bash cannot parse).
Remove-Item (Join-Path $staging 'install.ps1')
$installSh = (Get-Content (Join-Path $PSScriptRoot 'install.sh') -Raw) -replace "`r`n", "`n"
[System.IO.File]::WriteAllText((Join-Path $staging 'install.sh'), $installSh, [System.Text.UTF8Encoding]::new($false))

$tarPath = Join-Path $OutDir "kotor-coop-$Version-linux.tar.gz"
if (Test-Path $tarPath) { Remove-Item $tarPath -Force }
$tarRoot = "kotor-coop-$Version"
$fileStream = [System.IO.File]::Create($tarPath)
try {
    $gzip = [System.IO.Compression.GZipStream]::new($fileStream, [System.IO.Compression.CompressionLevel]::Optimal)
    try {
        $tar = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
        try {
            $rx = [System.IO.UnixFileMode]'UserRead, GroupRead, OtherRead'
            $rw = $rx -bor [System.IO.UnixFileMode]::UserWrite
            $rwx = $rw -bor [System.IO.UnixFileMode]'UserExecute, GroupExecute, OtherExecute'
            $dir = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::Directory, "$tarRoot/")
            $dir.Mode = $rwx
            $tar.WriteEntry($dir)
            # GetRelativePath, not a Substring on $staging: $env:TEMP is the 8.3 short form on
            # some machines (C:\Users\CHRIST~1\...) while FullName is always the long one.
            $stagingFull = (Get-Item $staging).FullName
            foreach ($item in Get-ChildItem $staging -Recurse | Sort-Object FullName) {
                $rel = [System.IO.Path]::GetRelativePath($stagingFull, $item.FullName).Replace('\', '/')
                if ($item.PSIsContainer) {
                    $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::Directory, "$tarRoot/$rel/")
                    $entry.Mode = $rwx
                    $tar.WriteEntry($entry)
                    continue
                }
                $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, "$tarRoot/$rel")
                $entry.Mode = if ($rel -eq 'install.sh') { $rwx } else { $rw }
                $entry.ModificationTime = $item.LastWriteTimeUtc
                $data = [System.IO.File]::OpenRead($item.FullName)
                try {
                    $entry.DataStream = $data
                    $tar.WriteEntry($entry)
                }
                finally { $data.Dispose() }
            }
        }
        finally { $tar.Dispose() }
    }
    finally { $gzip.Dispose() }
}
finally { $fileStream.Dispose() }
Remove-Item $staging -Recurse -Force
Remove-Item $symbolsDir -Recurse -Force

"packaged -> $tarPath ($([math]::Round((Get-Item $tarPath).Length / 1KB)) KB)"
