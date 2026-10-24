# Export approved source/docs/site from immutable Git blobs. Does not commit or push.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$SourceRef,
    [switch]$DryRun,
    [switch]$Check,
    [switch]$AdoptExisting
)
$ErrorActionPreference = 'Stop'
if ($DryRun -and $Check) { throw 'Choose -DryRun or -Check, not both.' }
$repo = Split-Path $PSScriptRoot -Parent
$arguments = @((Join-Path $PSScriptRoot 'publication/export_public.py'), '--source', $repo, '--destination', $Destination)
if ($SourceRef) { $arguments += @('--source-ref', $SourceRef) }
if ($DryRun) { $arguments += '--dry-run' }
if ($Check) { $arguments += '--check' }
if ($AdoptExisting) { $arguments += '--adopt-existing' }
& python @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
