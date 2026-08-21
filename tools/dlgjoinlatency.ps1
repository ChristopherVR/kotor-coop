# Measures the JOIN-LATENCY metrics tools\dlgprobe.ps1 does not print: millisecond timing
# between the conversation OWNER's own StartDialog and the JOINER's own StartDialog (the
# instant the engine's letterbox/camera transition begins locally) and the joiner's first
# successfully-driven node, plus the self-reported content-delivery number
# DialogueSpectateController already logs per follow. dlgprobe.ps1 samples once a second,
# which is fine for "did one conversation hold" but too coarse for "how many milliseconds did
# the subtitles take" - this reads the same plugin log lines dlgprobe.ps1 already produces and
# diffs their timestamps instead.
#
# Meant to be run once with coop.ini dialogue_fast_join=0 on BOTH installs and once with =1,
# to produce the before/after this feature's docs/research/dialogue-join-latency.md needs. The
# two installs are one machine's two GOG copies, so their log timestamps share a wall clock -
# the cross-process diffs below are valid for exactly that reason.
#
#   pwsh tools\dlgjoinlatency.ps1                      # host starts, client joins (default)
#   pwsh tools\dlgjoinlatency.ps1 -FromClient           # client starts, host joins
#   pwsh tools\dlgjoinlatency.ps1 -Tag end_bridgerep1 -Seconds 20
#
# Appends one row per run to dlgjoinlatency.csv (created on first run) so a handful of runs on
# each side of the A/B can be averaged instead of eyeballed one at a time.
param(
    [string]$Tag = 'end_trask',
    [int]$Seconds = 20,
    [switch]$FromClient,
    [string]$Log1 = 'C:\GOG Games\Star Wars KOTOR (GOG)\kotorcoop-plugin.log',
    [string]$Log2 = 'D:\Games\swkotor-gog-p2\kotorcoop-plugin.log',
    [string]$Csv = (Join-Path $PSScriptRoot 'dlgjoinlatency.csv')
)

$ErrorActionPreference = 'Stop'

function LineCount($p) { if (Test-Path $p) { @(Get-Content $p).Count } else { 0 } }

function NewLines($p, $since) {
    if (-not (Test-Path $p)) { return @() }
    $all = @(Get-Content $p)
    if ($all.Count -le $since) { return @() }
    return $all[$since..($all.Count - 1)]
}

# Plugin log lines are "[HH:mm:ss.fff] <message>" (the leading "[" is part of the live format
# - dlgprobe.ps1 and every doc quoting these lines already show it). $null on anything else, so
# a caller can tell "no timestamp found" apart from "found, but arithmetic underflowed".
function ParseTimestamp($line) {
    if ($null -ne $line -and $line -match '^\s*\[?(\d{2}):(\d{2}):(\d{2})\.(\d{3})') {
        return [TimeSpan]::new(0, [int]$Matches[1], [int]$Matches[2], [int]$Matches[3], [int]$Matches[4])
    }
    return $null
}

$ownerLog  = if ($FromClient) { $Log2 } else { $Log1 }
$joinLog   = if ($FromClient) { $Log1 } else { $Log2 }
$ownerMark = LineCount $ownerLog
$joinMark  = LineCount $joinLog

# The actual open+join+node-drive sequence, and dlgprobe.ps1's own node/serial verdict, are
# reused verbatim rather than reimplemented - this script only adds the millisecond timing its
# 1 Hz sampling cannot resolve.
& (Join-Path $PSScriptRoot 'dlgprobe.ps1') -Tag $Tag -Seconds $Seconds -FromClient:$FromClient -Log1 $Log1 -Log2 $Log2

$ownerNew = NewLines $ownerLog $ownerMark
$joinNew  = NewLines $joinLog $joinMark

# T0: the owner's OWN StartDialog for this test conversation - DialogueGateHook's counter fires
# the instant the engine reaches it, which for the owner is "started talking".
$t0Line = $ownerNew | Where-Object { $_ -match 'engine reached StartDialog' } | Select-Object -First 1

# T1: the joiner's OWN StartDialog. This is the moment the ENGINE's own letterbox/camera
# transition begins LOCALLY on the joiner's screen, whether this run reached it by a click on
# the same NPC or by adopting a simultaneously-started conversation.
$t1Line = $joinNew | Where-Object { $_ -match 'engine reached StartDialog' } | Select-Object -First 1

# T2: the joiner's first successfully-driven node - DialogueSpectateController logs this only
# once the engine has actually MOVED (re-read, not a reported success; see DialogueNodeFollower)
# - so this is the earliest point a real line could have been on screen.
$t2Line = $joinNew | Where-Object { $_ -match 'followed the owner \d+ -> \d+' } | Select-Object -First 1

# The self-reported content-delivery number ReportContentLatency already logs once per follow.
# Prefer this one when comparing before/after: it is ONE instance timing itself against its own
# clock, so it needs no cross-process timestamp diff and cannot be thrown off by clock skew
# between the two installs the way T0/T1/T2 above technically could be.
$contentLine = $joinNew |
    Where-Object { $_ -match 'delivered content (\d+) ms after opening \((\d+) ms after the request\)' } |
    Select-Object -First 1

function Ms($fromLine, $toLine) {
    $from = ParseTimestamp $fromLine
    $to = ParseTimestamp $toLine
    if ($null -eq $from -or $null -eq $to) { return $null }
    return ($to - $from).TotalMilliseconds
}

$startToGuiOpenMs = Ms $t0Line $t1Line
$startToNodeMs = Ms $t0Line $t2Line
$contentAfterOpenMs = $null
$contentAfterRequestMs = $null
if ($contentLine -match 'delivered content (\d+) ms after opening \((\d+) ms after the request\)') {
    $contentAfterOpenMs = [int]$Matches[1]
    $contentAfterRequestMs = [int]$Matches[2]
}

''
'--- join-latency metrics (dialogue_fast_join gated in coop.ini on both installs) ---'
"owner StartDialog -> joiner StartDialog (GUI open):  $(if ($null -ne $startToGuiOpenMs) { "$startToGuiOpenMs ms" } else { 'NOT OBSERVED - check -Seconds/-Tag, or whether the join actually happened' })"
"owner StartDialog -> joiner's first driven node:     $(if ($null -ne $startToNodeMs) { "$startToNodeMs ms" } else { 'NOT OBSERVED' })"
"joiner content latency (self-reported):              $(if ($null -ne $contentAfterOpenMs) { "$contentAfterOpenMs ms after opening, $contentAfterRequestMs ms after the request" } else { 'NOT OBSERVED - the follow may not have completed within -Seconds' })"

$row = [pscustomobject]@{
    Timestamp             = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    Tag                   = $Tag
    FromClient            = [bool]$FromClient
    StartToGuiOpenMs      = $startToGuiOpenMs
    StartToNodeMs         = $startToNodeMs
    ContentAfterOpenMs    = $contentAfterOpenMs
    ContentAfterRequestMs = $contentAfterRequestMs
}

$writeHeader = -not (Test-Path $Csv)
$row | Export-Csv -Path $Csv -Append -NoTypeInformation -Force
''
if ($writeHeader) { "row appended to $Csv (header written)" } else { "row appended to $Csv" }
