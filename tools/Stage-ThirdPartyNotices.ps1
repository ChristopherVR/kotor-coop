param(
    [Parameter(Mandatory)][string]$AgentDepsPath,
    [Parameter(Mandatory)][string]$PluginAssetsPath,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$noticeRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'third-party'
$manifest = Get-Content (Join-Path $noticeRoot 'manifest.json') -Raw | ConvertFrom-Json
foreach ($entry in @(@('NOTICES.txt', $manifest.noticesSha256), @('grammar-origins.json', $manifest.grammarOriginsSha256))) {
    if ((Get-FileHash -LiteralPath (Join-Path $noticeRoot $entry[0])).Hash -ne $entry[1]) {
        throw "Notice manifest hash mismatch: $($entry[0])"
    }
}
$deps = Get-Content -LiteralPath $AgentDepsPath -Raw | ConvertFrom-Json -AsHashtable
$actual = @($deps.libraries.Keys | Where-Object { $_ -notmatch '^KotorCoop\.' } | Sort-Object)
$difference = @(Compare-Object -ReferenceObject @($manifest.packages) -DifferenceObject $actual)
if ($difference.Count) {
    throw ('Published dependency versions changed; update the notice audit: ' + ($difference.InputObject -join ', '))
}
$plugin = Get-Content -LiteralPath $PluginAssetsPath -Raw | ConvertFrom-Json -AsHashtable
if (-not $plugin.libraries.ContainsKey('runtime.win-x86.Microsoft.DotNet.ILCompiler/10.0.12')) {
    throw 'NativeAOT runtime version changed; update its notices.'
}
Copy-Item -LiteralPath (Join-Path $noticeRoot 'NOTICES.txt') -Destination $Destination
