# The complete suite, including the LocalOnly real-socket loopback classes CI skips (about
# twelve minutes, serial). Run this before pushing a release tag. The opt-in Soak stays out.
#
#   pwsh tools	est-full.ps1
param([string]$Filter)

$repo = Split-Path -Parent $PSScriptRoot
$expression = 'Category!=Soak'
if ($Filter) { $expression += "&FullyQualifiedName~$Filter" }
dotnet test (Join-Path $repo 'tests\KotorCoop.Tests') --filter $expression
exit $LASTEXITCODE
