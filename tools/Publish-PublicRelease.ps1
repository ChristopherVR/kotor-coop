# Rebuild and scan before uploading a package; no source/history mirror.
# Default is a draft. -Publish requires an explicit release-readiness acknowledgment.
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$NotesFile,
    [switch]$ReleaseReviewed,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
if (-not $ReleaseReviewed) { throw 'Review legal, asset, gameplay and scan readiness first, then pass -ReleaseReviewed.' }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid version.' }
$target = 'ChristopherVR/kotor-coop'
$repo = Split-Path $PSScriptRoot -Parent
if ((git -C $repo status --porcelain)) { throw 'Commit the reviewed source first; publication requires a clean checkout.' }
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify source checkout.' }
if ((Get-Content -LiteralPath (Join-Path $repo 'VERSION') -Raw).Trim() -cne $Version) { throw 'Version must match VERSION.' }
$dir = [IO.Path]::GetFullPath($Directory)
if (Test-Path -LiteralPath $dir) { throw 'Directory must be new; publication always rebuilds and rescans.' }
$notes = (Resolve-Path -LiteralPath $NotesFile).Path
# The report is informational, not an authentication token. Never trust a supplied
# JSON report as evidence that a scan happened: build and scan in this invocation.
$buildDir = Join-Path ([IO.Path]::GetTempPath()) ('kotor-public-build-' + [guid]::NewGuid())
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'package.ps1') -Version $Version -OutDir $buildDir -WarnAsError
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Scan-PublicPackage.ps1') -Archive (Join-Path $buildDir "kotor-coop-$Version-windows.zip") -Version $Version -OutputDirectory $dir
if ($LASTEXITCODE -ne 0) { throw 'Fresh publication scan failed.' }
$installerCheck = Join-Path $buildDir 'installer-check'
Expand-Archive -LiteralPath (Join-Path $dir "kotor-coop-$Version-windows.zip") -DestinationPath $installerCheck
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-package.ps1') -PackageDirectory $installerCheck -WorkDirectory (Join-Path $buildDir 'synthetic-installs')
if ($LASTEXITCODE -ne 0) { throw 'Exact scanned package installer verification failed.' }
$reportPath = Join-Path $dir 'scan-report.json'
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$age = ([datetime]::UtcNow - [datetime]::Parse($report.completedUtc).ToUniversalTime()).TotalHours
if ($report.schemaVersion -ne 1 -or $report.result -ne 'no-detections' -or $report.scanner -ne 'Microsoft Defender Antivirus' -or
    -not $report.archiveAndExtractedFilesScanned -or -not $report.remediationDisabled -or $age -lt 0 -or $age -gt 24) {
    throw 'A successful scan from the last 24 hours is required.'
}
$filename = "kotor-coop-$Version-windows.zip"
if ($report.package.name -cne $filename) { throw 'Scan report version mismatch.' }
$archive = Join-Path $dir $filename
if ((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() -cne $report.package.sha256) { throw 'Scanned package hash mismatch.' }
$checksums = Join-Path $dir 'SHA256SUMS.txt'
if ((Get-Content -LiteralPath $checksums -Raw).Trim() -cne "$($report.package.sha256)  $filename") { throw 'Checksum file mismatch.' }
$proof = Join-Path ([IO.Path]::GetTempPath()) ('kotor-publish-' + [guid]::NewGuid() + '.json')
python (Join-Path $PSScriptRoot 'publication/package_policy.py') $archive --version $Version --output $proof
if ($LASTEXITCODE -ne 0) { throw 'Package no longer matches reviewed source assets and notices.' }
$actual = Get-Content -LiteralPath $proof -Raw | ConvertFrom-Json
if (($actual.files | ConvertTo-Json -Compress) -cne ($report.package.files | ConvertTo-Json -Compress)) { throw 'Scan inventory does not match archive.' }
$metadata = gh repo view $target --json isPrivate,defaultBranchRef | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $metadata.isPrivate -or -not $metadata.defaultBranchRef.name) { throw 'Public repository is not ready.' }
$branch = $metadata.defaultBranchRef.name
$publicCommit = gh api "repos/$target/commits/$branch" --jq .sha
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve public commit.' }
if ((git -C $repo status --porcelain)) { throw 'Source changed during build/scan; review it before publishing.' }
$flags = @('--draft', '--latest=false')
if ($Publish) { $flags = @('--latest=false'); if ($Version.Contains('-')) { $flags += '--prerelease' } }
elseif ($Version.Contains('-')) { $flags += '--prerelease' }
gh release create "v$Version" $archive $reportPath $checksums --repo $target --target $publicCommit --title "KOTOR Co-op $Version" --notes-file $notes @flags
if ($LASTEXITCODE -ne 0) { throw 'Release creation failed; inspect GitHub before retrying. No existing release is overwritten.' }
