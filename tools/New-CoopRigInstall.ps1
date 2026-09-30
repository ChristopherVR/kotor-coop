# Disposable stock rig install. Hardlinked resource roots must never be mod-patched.
# Runtime, saves, Override, modules, executable libraries and configuration stay private.
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$DestinationDirectory,
    [string]$BaselineSave = '000900 - live_baseline_20260930'
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path.TrimEnd('\')
$destination = [IO.Path]::GetFullPath($DestinationDirectory).TrimEnd('\')
$allowedRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot) 'artifacts/live-installs')).TrimEnd('\')
if (!$destination.StartsWith($allowedRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw "Destination must be within $allowedRoot" }
if (Test-Path -LiteralPath $destination) { throw "Destination already exists: $destination" }
if ([IO.Path]::GetPathRoot($source) -ne [IO.Path]::GetPathRoot($destination)) { throw 'Source and destination must share the same NTFS volume.' }
if ([IO.Path]::GetFileName($BaselineSave) -ne $BaselineSave) { throw 'BaselineSave must be a single save-folder name.' }
$sharedRoots = @('data','movies','streammusic','streamsounds','streamwaves','streamvoice','lips','TexturePacks','rims')
$privateRoots = @('Override','modules','miles','coop-mods')
$baseline = Join-Path $source ('saves/' + $BaselineSave)
if (!(Test-Path -LiteralPath (Join-Path $baseline 'SAVEGAME.sav'))) { throw "Missing stable baseline save: $baseline" }
# Resolve and validate every selected tree before creating any destination files.
$trees = @($sharedRoots + $privateRoots | ForEach-Object { Join-Path $source $_ }) + @($baseline)
$selected = @()
foreach ($tree in $trees) {
    if (!(Test-Path -LiteralPath $tree)) { continue }
    $selected += Get-Item -LiteralPath $tree
    $selected += Get-ChildItem -LiteralPath $tree -Recurse -Force
}
$rootFiles = @(Get-ChildItem -LiteralPath $source -File | Where-Object {
    $_.Extension -in @('.exe','.dll','.tlk','.key','.erf','.rexa') -or
    $_.Name -in @('coop.ini','swkotor.ini','KotorCoop.Agent.deps.json','KotorCoop.Agent.runtimeconfig.json')
})
foreach ($item in @((Get-Item -LiteralPath $source)) + $selected + $rootFiles) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked source path is not supported: $($item.FullName)" }
}
# Existing ancestors must not redirect the checked workspace destination elsewhere.
$ancestor = Split-Path $destination
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked destination ancestor: $ancestor" }
    $next = Split-Path $ancestor
    if ($next -eq $ancestor) { break }; $ancestor = $next
}
New-Item -ItemType Directory -Path $destination > $null
$linkedCount = 0; $copiedCount = 0; $samples = @()
foreach ($item in $selected) {
    $relative = [IO.Path]::GetRelativePath($source,$item.FullName)
    $target = [IO.Path]::GetFullPath((Join-Path $destination $relative))
    if (!$target.StartsWith($destination + '\',[StringComparison]::OrdinalIgnoreCase)) { throw "Escaping destination: $target" }
    if ($item.PSIsContainer) { New-Item -ItemType Directory -Path $target -Force > $null; continue }
    $root = $relative.Split([IO.Path]::DirectorySeparatorChar)[0]
    if ($root -in $sharedRoots) {
        New-Item -ItemType HardLink -Path $target -Target $item.FullName > $null; $linkedCount++
    } else { Copy-Item -LiteralPath $item.FullName -Destination $target; $copiedCount++ }
    if ($samples.Count -lt 3 -or ($root -in $sharedRoots -and $samples.Root -notcontains $root)) {
        $samples += @{ Root=$root; Source=$item.FullName; Destination=$target }
    }
}
foreach ($item in $rootFiles) { Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $destination $item.Name); $copiedCount++ }
foreach ($name in @('Temp','currentgame','gameinprogress','logs','kotorcoop-shots','saves')) {
    New-Item -ItemType Directory -Path (Join-Path $destination $name) -Force > $null
}
(Get-Item -LiteralPath (Join-Path $destination ('saves/' + $BaselineSave))).LastWriteTimeUtc = [DateTime]::UtcNow
# Bounded byte comparison: one sample from each hardlinked resource tree plus all
# private baseline files and the executable/plugin. Full compatibility audit remains available.
$samples += @(Get-ChildItem -LiteralPath $baseline -File | ForEach-Object {
    @{ Source=$_.FullName; Destination=(Join-Path $destination ([IO.Path]::GetRelativePath($source,$_.FullName))) }
})
foreach ($name in @('swkotor.exe','dinput8.dll','chitin.key')) { $samples += @{ Source=(Join-Path $source $name); Destination=(Join-Path $destination $name) } }
foreach ($sample in $samples) {
    if ((Get-FileHash -LiteralPath $sample.Source).Hash -ne (Get-FileHash -LiteralPath $sample.Destination).Hash) { throw "Clone verification mismatch: $($sample.Destination)" }
}
$manifest = @{ Source=$source; Baseline=$BaselineSave; HardlinkedRoots=$sharedRoots; LinkedFiles=$linkedCount; PrivateFiles=$copiedCount; ComparedFiles=$samples.Count; CreatedUtc=[DateTime]::UtcNow.ToString('o') }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'rig-install.json') -Encoding utf8
[pscustomobject]@{ Destination=$destination; LinkedFiles=$linkedCount; PrivateFiles=$copiedCount; ComparedFiles=$samples.Count; Baseline=$BaselineSave }
