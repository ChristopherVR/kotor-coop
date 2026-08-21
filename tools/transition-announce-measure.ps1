# Measures the announced-transitions live test (docs/research/transition-smoothness.md §6):
# END_M01AA -> END_M01AB via end_door10, before/after the coop.ini `transition_announce` gate.
#
# Reads the two instances' OWN kotorcoop-plugin*.log files (millisecond timestamps, written by
# PluginHost.Log — not the agent's own console log, which is second-precision) and reports:
#
#   Interval A: host door-use (its "area transition pending:" line, the fade beginning) to the
#               FOLLOWER's own "travel: moving to module" line (when its warp actually starts).
#   Interval B: the same host mark to the follower's own "epoch: module changed" line (arrival).
#
# With `transition_announce=0` (the default), Interval A is the avoidable latency this feature
# closes — expect several seconds. With `transition_announce=1`, expect Interval A to collapse
# to roughly the door's own 0.5 s fade plus one agent tick. Run this script once per coop.ini
# setting and compare the two printed Interval A numbers directly.
#
# Usage:
#   pwsh tools\transition-announce-measure.ps1 `
#       -HostLog 'C:\GOG Games\Star Wars KOTOR (GOG)\kotorcoop-plugin.log' `
#       -FollowerLog 'D:\Games\swkotor-gog-p2\kotorcoop-plugin.log' `
#       -DestinationModule end_m01ab
#
# Point -HostLog/-FollowerLog at whichever install actually played each role for the run being
# measured (the door-use side is "host" here regardless of which coop.ini `mode` it used, since
# a follow only ever chases the SESSION host — see CoopHost.HostPeerId).

param(
    [Parameter(Mandatory = $true)] [string]$HostLog,
    [Parameter(Mandatory = $true)] [string]$FollowerLog,
    [string]$DestinationModule = 'end_m01ab',

    # Only lines at or after this timestamp are considered, so measuring a SECOND run in the
    # same long-lived log file does not accidentally pick up the first run's marks. Format
    # matches the plugin log's own "HH:mm:ss.fff"; omit for "the whole file".
    [string]$Since = ''
)

function Get-LineTime {
    param([string]$Path, [string]$Pattern, [string]$SinceTime)

    if (-not (Test-Path $Path)) {
        throw "log not found: $Path"
    }

    $sinceTs = $null
    if ($SinceTime) {
        $sinceTs = [TimeSpan]::Parse($SinceTime)
    }

    foreach ($line in Get-Content -Path $Path) {
        if ($line -notmatch '^\[(?<ts>\d{2}:\d{2}:\d{2}\.\d{3})\]\s') { continue }
        # Captured before the pattern test below, which runs its own -match and would
        # otherwise overwrite $Matches (and the "ts" group with it).
        $tsText = $Matches.ts

        if ($line -notmatch $Pattern) { continue }

        $ts = [TimeSpan]::Parse($tsText)
        if ($sinceTs -and $ts -lt $sinceTs) { continue }

        return [pscustomobject]@{ Time = $ts; Line = $line.Trim() }
    }

    return $null
}

Write-Host "Host log:     $HostLog"
Write-Host "Follower log: $FollowerLog"
Write-Host "Destination:  $DestinationModule"
Write-Host ''

# The fade beginning — the earliest signal EITHER path (before or after this feature) has for
# "a transition just started". Present in both the before and after runs, unlike `announce:`,
# which only appears when the gate is on — using this mark keeps the two measurements
# comparable on the exact same basis.
$hostMark = Get-LineTime -Path $HostLog -Pattern 'area transition pending:' -SinceTime $Since
if (-not $hostMark) {
    Write-Error "no 'area transition pending:' line found in the host log — did the door actually fire? (see cinematics.md §8.3a: the party-together gate silently cancels a door use with no trace at all)"
    exit 1
}
Write-Host "Host door-use (fade begins):  $($hostMark.Line)"

$warpStart = Get-LineTime -Path $FollowerLog -Pattern "travel: moving to module '$DestinationModule'" -SinceTime $hostMark.Time.ToString()
if (-not $warpStart) {
    Write-Error "no 'travel: moving to module ''$DestinationModule''' line found in the follower log after the host's door-use — the follow never fired at all in this run"
    exit 1
}
Write-Host "Follower warp starts:         $($warpStart.Line)"

$arrival = Get-LineTime -Path $FollowerLog -Pattern 'epoch: module changed' -SinceTime $warpStart.Time.ToString()
if ($arrival) {
    Write-Host "Follower arrives (epoch):     $($arrival.Line)"
}

$intervalA = ($warpStart.Time - $hostMark.Time).TotalMilliseconds
Write-Host ''
Write-Host "Interval A (host door-use -> follower warp start): $([math]::Round($intervalA)) ms"

if ($arrival) {
    $intervalB = ($arrival.Time - $hostMark.Time).TotalMilliseconds
    Write-Host "Interval B (host door-use -> follower arrival):    $([math]::Round($intervalB)) ms"
}

Write-Host ''
Write-Host 'Compare Interval A across a transition_announce=0 run and a transition_announce=1 run of this same script.'
Write-Host 'The "after" run should also show an announce: line in the host log, timestamped between the door-use mark and the warp-start line.'
