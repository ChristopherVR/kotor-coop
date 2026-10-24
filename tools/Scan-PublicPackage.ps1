# Scan the exact Windows package and its allowlisted extracted contents.
# No exclusions, remediation, or third-party sample uploads are configured here.
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$archivePath = (Resolve-Path -LiteralPath $Archive).Path
$out = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $out) { throw 'OutputDirectory must be new; stale reports cannot be reused.' }
New-Item -ItemType Directory -Path $out | Out-Null
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('kotor-public-scan-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $scratch | Out-Null
$inventoryPath = Join-Path $scratch 'inventory.json'
$extracted = Join-Path $scratch 'contents'
python (Join-Path $PSScriptRoot 'publication/package_policy.py') $archivePath --version $Version --output $inventoryPath --extract $extracted
if ($LASTEXITCODE -ne 0) { throw 'Publication package policy failed.' }
$inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $extracted 'KotorCoop.Agent.exe')).ProductVersion
if ($productVersion -cne $Version) { throw 'Agent executable version does not match the numbered release.' }
# Defender custom scanning with DisableRemediation ignores file exclusions and scans archives.
# A zero exit without this flag could also mean a detection was remediated.
Update-MpSignature -ErrorAction Stop
$status = Get-MpComputerStatus
if (-not $status.AMServiceEnabled -or -not $status.AntivirusEnabled) { throw 'Defender is unavailable.' }
if (([datetime]::UtcNow - $status.AntivirusSignatureLastUpdated.ToUniversalTime()).TotalHours -gt 48) {
    throw 'Defender signatures are older than 48 hours.'
}
$platform = Join-Path $env:ProgramData 'Microsoft/Windows Defender/Platform'
$scanner = Get-ChildItem -LiteralPath $platform -Directory -ErrorAction SilentlyContinue |
    Where-Object Name -Match '^\d' | Sort-Object { [version]($_.Name -split '-')[0] } -Descending |
    ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $scanner) { $scanner = Join-Path $env:ProgramFiles 'Windows Defender/MpCmdRun.exe' }
if (-not (Test-Path -LiteralPath $scanner)) { throw 'Defender scanner executable is missing.' }
$started = [datetime]::UtcNow
foreach ($target in @($archivePath, $extracted)) {
    $scanOutput = & $scanner -Scan -ScanType 3 -File $target -DisableRemediation 2>&1
    $exit = $LASTEXITCODE
    $scanOutput | Add-Content -LiteralPath (Join-Path $scratch 'defender-private.log')
    if ($exit -ne 0) { throw "Defender did not return a clean completed scan (exit $exit). Private diagnostics: $scratch" }
}
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inventory.sha256) {
    throw 'Archive changed during scan.'
}
foreach ($file in $inventory.files) {
    if ((Get-FileHash -LiteralPath (Join-Path $extracted $file.name) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
        throw "Extracted file changed during scan: $($file.name)"
    }
}
Copy-Item -LiteralPath $archivePath -Destination (Join-Path $out $inventory.name)
if ((Get-FileHash -LiteralPath (Join-Path $out $inventory.name)).Hash.ToLowerInvariant() -ne $inventory.sha256) { throw 'Copy hash mismatch.' }
$report = [ordered]@{
    schemaVersion = 1; scanner = 'Microsoft Defender Antivirus'; result = 'no-detections';
    startedUtc = $started.ToString('o'); completedUtc = [datetime]::UtcNow.ToString('o');
    engineVersion = $status.AMEngineVersion; signatureVersion = $status.AntivirusSignatureVersion;
    signaturesUpdatedUtc = $status.AntivirusSignatureLastUpdated.ToUniversalTime().ToString('o');
    archiveAndExtractedFilesScanned = $true; remediationDisabled = $true;
    limitation = 'No detections in these exact bytes at scan time. This is not a guarantee of safety, code signing, or legal clearance.';
    package = $inventory
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'scan-report.json') -Encoding utf8
"$($inventory.sha256)  $($inventory.name)" | Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii
"Publication scan completed: $out"
# The bounded scratch directory is retained locally for review; never uploaded.
