# Restore only the mutable trees used by isolated mod tests, by exact file comparison.
# The installed co-op binaries and immutable hardlinked resource trees are not selected.
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [Parameter(Mandatory)][string]$BackupDirectory,
    [switch]$RestoreSaves
)
$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path -LiteralPath $GameDirectory).Path.TrimEnd('\')
$backupRoot = (Resolve-Path -LiteralPath $BackupDirectory).Path.TrimEnd('\')
foreach ($root in @($gameRoot,$backupRoot)) {
    $ancestor = $root
    while ($ancestor) {
        if ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked restore ancestor: $ancestor" }
        $next = Split-Path $ancestor
        if ($next -eq $ancestor) { break }; $ancestor = $next
    }
}
if ($gameRoot -eq $backupRoot -or $backupRoot.StartsWith($gameRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Backup must be separate from the game install.' }
foreach ($process in Get-Process -Name swkotor -ErrorAction SilentlyContinue) {
    if ($process.Path -and [IO.Path]::GetDirectoryName($process.Path) -eq $gameRoot) { throw 'Stop this install before restoring its files.' }
}
$trees = @('Override','modules') + ($RestoreSaves ? @('saves') : @())
$rootNames = @('dialog.tlk','coop.ini','swkotor.ini')
$copies = @(); $extras = @()
foreach ($tree in $trees) {
    $savedTree = Join-Path $backupRoot $tree
    $liveTree = Join-Path $gameRoot $tree
    if (!(Test-Path -LiteralPath $savedTree -PathType Container)) { throw "Missing backup tree: $savedTree" }
    $savedEntries = @(Get-Item -LiteralPath $savedTree) + @(Get-ChildItem -LiteralPath $savedTree -Recurse -Force)
    $liveEntries = (Test-Path -LiteralPath $liveTree) ? (@(Get-Item -LiteralPath $liveTree) + @(Get-ChildItem -LiteralPath $liveTree -Recurse -Force)) : @()
    foreach ($entry in @($savedEntries + $liveEntries)) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked mutable path: $($entry.FullName)" }
    }
    $savedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $savedEntries | Where-Object { !$_.PSIsContainer }) {
        $relative = [IO.Path]::GetRelativePath($savedTree,$entry.FullName)
        $savedNames.Add($relative) > $null
        $copies += @{ Source=$entry.FullName; Target=[IO.Path]::GetFullPath((Join-Path $liveTree $relative)); Hash=(Get-FileHash -LiteralPath $entry.FullName).Hash }
    }
    foreach ($entry in $liveEntries | Where-Object { !$_.PSIsContainer }) {
        if (!$savedNames.Contains([IO.Path]::GetRelativePath($liveTree,$entry.FullName))) { $extras += [IO.Path]::GetFullPath($entry.FullName) }
    }
}
foreach ($name in $rootNames) {
    $source = Join-Path $backupRoot $name; $target = Join-Path $gameRoot $name
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing backup file: $source" }
    foreach ($path in @($source,$target)) {
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked root file: $path" }
    }
    $copies += @{Source=$source;Target=$target;Hash=(Get-FileHash -LiteralPath $source).Hash}
}
# Validate every absolute mutation target before the first write or deletion.
foreach ($target in @($copies.Target) + $extras) {
    if (!$target.StartsWith($gameRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw "Escaping restore target: $target" }
}
foreach ($copy in $copies) {
    New-Item -ItemType Directory -Path (Split-Path $copy.Target) -Force > $null
    Copy-Item -LiteralPath $copy.Source -Destination $copy.Target -Force
    if ($copy.Hash -ne (Get-FileHash -LiteralPath $copy.Target).Hash -or $copy.Hash -ne (Get-FileHash -LiteralPath $copy.Source).Hash) { throw 'Backup changed or restored bytes differ; extra files have not been removed.' }
}
foreach ($target in $extras) { Remove-Item -LiteralPath $target -Force }
[pscustomobject]@{ Game=$gameRoot; VerifiedFiles=$copies.Count; RemovedTestFiles=$extras.Count; SavesRestored=[bool]$RestoreSaves }
