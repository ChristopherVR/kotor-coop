# Tests an extracted Windows release using its real, self-contained installer.
# Fixtures have placeholder game/third-party DLL files: this verifies packaging and
# preservation, not executable compatibility or actual gameplay. Never launches a game.
# Leaves evidence in a new work directory; uninstalls fixtures in finally to remove any
# per-executable firewall rules created by an elevated run.
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [string]$WorkDirectory = ''
)

$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $WorkDirectory) {
    $WorkDirectory = Join-Path $repoRoot ('artifacts\package-check-' + [Guid]::NewGuid().ToString('N'))
}
$fixtureRoot = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $fixtureRoot) { throw 'WorkDirectory must be new; refusing to modify existing data.' }
$installerExe = Join-Path $packageRoot 'KotorCoop.Agent.exe'
foreach ($required in @('KotorCoop.Agent.exe', 'dinput8.dll', 'chain-loader\dinput8.dll',
                       'coop.ini', 'install.ps1', 'README.txt',
                       'Override\k_coop_phantom.utc', 'KotorCoop.LICENSE.txt',
                       'KotorCoop.LEGAL.txt', 'KotorCoop.THIRD-PARTY-NOTICES.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $required))) {
        throw "Missing package file: $required"
    }
}
foreach ($required in @('KotorCoop.CollectLogs.cmd', 'KotorCoop.DEFAULTS.ini')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $required))) {
        throw "Missing package file: $required"
    }
}
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
$fixtureGames = @()
$checks = [Collections.Generic.List[string]]::new()

function Assert-Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "FAIL: $Description" }
    $checks.Add($Description)
    "PASS: $Description"
}
function Invoke-Installer([string]$Step, [string[]]$Arguments, [int]$ExpectedExit = 0) {
    $transcript = @(& $installerExe @Arguments 2>&1)
    $exit = $LASTEXITCODE
    $transcript | Set-Content -LiteralPath (Join-Path $fixtureRoot "$Step.txt") -Encoding utf8
    Assert-Check ($exit -eq $ExpectedExit) "$Step exit code $exit (expected $ExpectedExit)"
}
try {
    foreach ($layout in @('plain', 'chain')) {
        $game = Join-Path $fixtureRoot "$layout game with spaces"
        New-Item -ItemType Directory -Path $game | Out-Null
        $fixtureGames += $game
        [IO.File]::WriteAllText((Join-Path $game 'swkotor.exe'), 'installer fixture only; not a runnable game')
        $customIni = "[coop]`r`nname = Preserve Me`r`nport = 27140`r`nmenu_button = 1`r`n"
        [IO.File]::WriteAllText((Join-Path $game 'coop.ini'), $customIni)
        $foreign = 'third-party loader fixture; preservation test only'
        if ($layout -eq 'chain') {
            [IO.File]::WriteAllText((Join-Path $game 'dinput8.dll'), $foreign)
        }
        Invoke-Installer "$layout-install" @('install', '--game-path', $game)
        foreach ($notice in @('KotorCoop.LICENSE.txt', 'KotorCoop.LEGAL.txt', 'KotorCoop.THIRD-PARTY-NOTICES.txt')) {
            Assert-Check ((Get-FileHash -LiteralPath (Join-Path $game $notice)).Hash -eq
                (Get-FileHash -LiteralPath (Join-Path $packageRoot $notice)).Hash) "$layout installs $notice unchanged"
        }
        Assert-Check ([IO.File]::ReadAllText((Join-Path $game 'coop.ini')) -ceq $customIni) "$layout custom settings preserved"
        Invoke-Installer "$layout-check" @('install', '--check', '--game-path', $game)
        if ($layout -eq 'chain') {
            Assert-Check ([IO.File]::ReadAllText((Join-Path $game 'dinput8.prev.dll')) -ceq $foreign) 'foreign DLL preserved'
            Copy-Item -LiteralPath (Join-Path $game 'dinput8.prev.dll') -Destination (Join-Path $game 'extra.dll')
            Add-Content -LiteralPath (Join-Path $game 'dinput8.chain') -Value 'extra.dll'
        }
        Invoke-Installer "$layout-upgrade" @('install', '--game-path', $game)
        Assert-Check ([IO.File]::ReadAllText((Join-Path $game 'coop.ini')) -ceq $customIni) "$layout upgrade preserves settings"
        Assert-Check (Test-Path -LiteralPath (Join-Path $game 'KotorCoop.CollectLogs.cmd')) "$layout installs double-click log collector"
        Assert-Check ((Get-FileHash -LiteralPath (Join-Path $game 'KotorCoop.DEFAULTS.ini')).Hash -eq
            (Get-FileHash -LiteralPath (Join-Path $packageRoot 'coop.ini')).Hash) "$layout ships exact incoming defaults"
        $oldIni = $customIni.Replace('menu_button = 1', 'menu_button = 0')
        [IO.File]::WriteAllText((Join-Path $game 'coop.ini'), $oldIni)
        Invoke-Installer "$layout-preview-defaults" @('install', '--game-path', $game)
        Assert-Check ([IO.File]::ReadAllText((Join-Path $game 'coop.ini')) -ceq $oldIni) "$layout migration is opt-in"
        Invoke-Installer "$layout-migrate-defaults" @('install', '--game-path', $game, '--update-defaults')
        Assert-Check ([IO.File]::ReadAllText((Join-Path $game 'coop.ini')) -ceq $customIni) "$layout migration preserves custom settings"
        $backups = @(Get-ChildItem -LiteralPath $game -Filter 'coop.ini.before-defaults-*.bak')
        Assert-Check ($backups.Count -eq 1 -and [IO.File]::ReadAllText($backups[0].FullName) -ceq $oldIni) "$layout migration backs up original settings"
        if ($layout -eq 'chain') {
            $entries = @(Get-Content -LiteralPath (Join-Path $game 'dinput8.chain') | Where-Object { $_.Trim() -eq 'extra.dll' })
            Assert-Check ($entries.Count -eq 1) 'custom chain entry preserved exactly once'
            $plugin = Join-Path $game 'KotorCoop.Plugin.dll'
            Move-Item -LiteralPath $plugin -Destination "$plugin.held"
            Invoke-Installer 'broken-chain-check' @('install', '--check', '--game-path', $game) 1
            Move-Item -LiteralPath "$plugin.held" -Destination $plugin
            [IO.File]::WriteAllText((Join-Path $game 'dinput8-chain.log'), 'chain diagnostic fixture')
            [IO.File]::WriteAllText((Join-Path $game 'coop.ini'), $customIni + "password = fixture-secret`r`nhost = fixture-private-address`r`n")
            Invoke-Installer 'support-bundle' @('support', '--game-path', $game)
            $bundleLine = Get-Content -LiteralPath (Join-Path $fixtureRoot 'support-bundle.txt') |
                Where-Object { $_ -match '^wrote (.+) \(\d+ file\(s\)\)\.$' } | Select-Object -First 1
            if (-not $bundleLine -or $bundleLine -notmatch '^wrote (.+) \(\d+ file\(s\)\)\.$') {
                throw 'No support bundle path was reported'
            }
            $bundlePath = [IO.Path]::GetFullPath($Matches[1])
            $desktopPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
            $bundleParent = [IO.Path]::GetDirectoryName($bundlePath)
            if ($bundleParent -ne $desktopPath -and $bundleParent -ne $game) {
                throw 'Unexpected support bundle destination'
            }
            $bundleEvidence = Join-Path $fixtureRoot 'support-bundle.zip'
            Move-Item -LiteralPath $bundlePath -Destination $bundleEvidence
            $zip = [IO.Compression.ZipFile]::OpenRead($bundleEvidence)
            try {
                Assert-Check ($null -ne $zip.GetEntry('dinput8-chain.log')) 'chain diagnostics bundled'
                foreach ($entryName in @('coop.ini', 'system.txt')) {
                    $reader = [IO.StreamReader]::new($zip.GetEntry($entryName).Open())
                    try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
                    if ($entryName -eq 'coop.ini') {
                        Assert-Check (-not $text.Contains('fixture-secret') -and -not $text.Contains('fixture-private-address')) 'settings secrets redacted'
                    }
                    else {
                        $version = (Get-Item -LiteralPath $installerExe).VersionInfo.ProductVersion
                        Assert-Check ($text.Contains("agent       $version") -and $text.Contains("installed   $version")) 'collecting and installed release versions bundled'
                    }
                }
            }
            finally { $zip.Dispose() }
            [IO.File]::WriteAllText((Join-Path $game 'coop.ini'), $customIni)
        }
        Invoke-Installer "$layout-uninstall" @('uninstall', '--game-path', $game)
        Assert-Check ([IO.File]::ReadAllText((Join-Path $game 'coop.ini')) -ceq $customIni) "$layout uninstall preserves settings"
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $game 'KotorCoop.Agent.exe'))) "$layout agent removed"
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $game 'KotorCoop.CollectLogs.cmd'))) "$layout log shortcut removed"
        if ($layout -eq 'chain') {
            Assert-Check ([IO.File]::ReadAllText((Join-Path $game 'dinput8.dll')) -ceq $foreign) 'foreign DLL restored'
            Assert-Check (Test-Path -LiteralPath (Join-Path $game 'extra.dll')) 'custom chain DLL retained'
        }
        else {
            Assert-Check (-not (Test-Path -LiteralPath (Join-Path $game 'dinput8.dll'))) 'plain plugin removed'
        }
    }
    "Package verification: $($checks.Count) checks passed. Evidence: $fixtureRoot"
}
finally {
    foreach ($game in $fixtureGames) {
        # The only targets are directories created above; never the package or a real install.
        & $installerExe uninstall --game-path $game *> (Join-Path $game 'cleanup.txt')
    }
}
