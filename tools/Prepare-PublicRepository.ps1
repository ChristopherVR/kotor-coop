# Make a new, curated directory. Never clone, mirror, copy .git, or export git history.
param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $out) { throw 'Destination must be new; inspect any existing public checkout explicitly.' }
$files = [ordered]@{
    'README.md' = 'tools/publication/public/README.md'
    'SECURITY.md' = 'tools/publication/public/SECURITY.md'
    'LICENSE' = 'LICENSE'
    'media/kotor-coop-overview.mp4' = 'artifacts/public-trailer/kotor-coop-overview.mp4'
    'media/poster.jpg' = 'artifacts/public-trailer/poster.jpg'
    'media/transcript.txt' = 'artifacts/public-trailer/transcript.txt'
    'media/provenance.json' = 'artifacts/public-trailer/provenance.json'
    'media/DejaVu-LICENSE.txt' = 'media/trailer/fonts/LICENSE.txt'
}
foreach ($source in $files.Values) {
    $item = Get-Item -LiteralPath (Join-Path $root $source)
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked publication input: $source" }
}
$provenance = Get-Content (Join-Path $root 'artifacts/public-trailer/provenance.json') -Raw | ConvertFrom-Json
foreach ($name in @('kotor-coop-overview.mp4', 'poster.jpg')) {
    if ((Get-FileHash -LiteralPath (Join-Path $root "artifacts/public-trailer/$name")).Hash.ToLowerInvariant() -cne $provenance.sha256.$name) {
        throw "Trailer provenance mismatch: $name"
    }
}
New-Item -ItemType Directory -Path $out | Out-Null
foreach ($entry in $files.GetEnumerator()) {
    $target = Join-Path $out $entry.Key
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $entry.Value) -Destination $target
}
@('/support/', '*.log', '*.dmp', '*.zip', '*.exe', '*.dll', '.env*') | Set-Content (Join-Path $out '.gitignore') -Encoding ascii
Get-ChildItem -LiteralPath $out -Recurse -File | ForEach-Object {
    [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($out, $_.FullName); SHA256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
}
