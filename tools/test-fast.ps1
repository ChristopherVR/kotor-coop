# The everyday test run: everything except the slow real-socket loopback/integration classes
# (tagged [Trait("Category", "Slow")]) and the opt-in soak. About a minute instead of ten.
# CI and a pre-push check still run the full suite (Category!=Soak).
#
#   pwsh tools\test-fast.ps1                 # fast tier
#   pwsh tools\test-fast.ps1 -Filter Store   # fast tier, names containing "Store"
param([string]$Filter)

$repo = Split-Path -Parent $PSScriptRoot
$expression = 'Category!=Slow&Category!=Soak'
if ($Filter) { $expression += "&FullyQualifiedName~$Filter" }
dotnet test (Join-Path $repo 'tests\KotorCoop.Tests') --filter $expression
exit $LASTEXITCODE
