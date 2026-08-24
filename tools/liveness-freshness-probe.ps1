# Paired census/resolve sampling for the DestroyObject-scan-freshness question
# docs/research/creature-liveness.md has been chasing since the third 2026-08-22 live pass.
#
# WHY THIS SCRIPT EXISTS: every prior attempt to characterise "does the agent's external scan
# (KotorGameBridge.EnumerateCreatures -> LiveObjectGate.Confirm) notice a DestroyObject'd body
# promptly" measured the two sides SECONDS OR MINUTES APART, through DIFFERENT commands, and in
# at least one case (the third 2026-08-22 pass) inferred the answer from AGGREGATE census-count
# ARITHMETIC rather than asking about the one id directly - a methodology gap the 2026-08-24
# disassembly round found and flagged (see that entry: "it never ran `census 0x3B`... the exact
# per-id diagnostic"). This script closes that gap mechanically: it samples BOTH diagnostics for
# the SAME id, back to back, on a fixed cadence, for a long window, and flags the instant (if
# ever) they disagree.
#
# It also exists to test a NEW theory the 2026-08-24 round's disassembly of
# `CServerAIMaster::UpdateState` (0x4B0B70) raised but could not test live: `DestroyObject`'s
# actual array-unlink runs off a DELAYED EVENT (`AddEventDeltaTime` -> the AI Master's own event
# queue, drained by `UpdateState`), and `UpdateState` drains that queue - and does an unrelated,
# population-sized object-list scan - under a REAL WALL-CLOCK TIME BUDGET per call
# (`CExoTimers::GetHighResolutionTimer`, ~10,000 raw units, gating a while-loop around both the
# object-list scan and the event-queue drain). A busy module could starve the drain for many
# ticks, deferring the actual delete well past "the very next frame" - which would make the
# staleness this document has chased a LOAD-CORRELATED PROPERTY OF THE BASE ENGINE, not a bug
# in this codebase's own scan/cache pipeline. This script's -Label parameter exists so the SAME
# recipe can be run in a characterised high-population module (tat_m20aa, 31 fixed hostiles -
# see the 2026-08-22 entry) and a characterised LOW-population one, and the two runs' CSVs
# compared for a correlation between population and convergence time.
#
# WHY IT READS THE AGENT LOG FOR THE census HALF, NOT THE CONSOLE REPLY: `census 0x<id>` fans
# out to TWO `Reply()` calls inside `CoopCommandDispatcher.DescribeId` - the array/scan line
# first, then a second "listed hp .../not listed in this census" line that OVERWRITES it in the
# single-slot console-reply field (`Reply()`'s own doc comment: "only the last line survives in
# the block, and all of them survive in the log"). The array/scan line - the exact
# `KotorGameBridge.TryResolveObjectHere` answer this whole investigation is about - is only
# recoverable from the agent's own log file. `resolve 0x<id>` has no such problem
# (`CheatController.Resolve` calls `Answer()` exactly once), so that half reads the console
# reply directly, matching how every prior live pass in this document actually used it.
#
#   # stage the destroy yourself first, then just sample:
#   pwsh tools\blockpoke.ps1 -ProcId 1234 console 'destroyobject 0x9'
#   pwsh tools\liveness-freshness-probe.ps1 -ProcId 1234 -ObjectId 0x9 `
#       -AgentLogPath "$env:TEMP\kotorcoop-demo\agent1.log" -Label tat_m20aa-31pop
#
#   # or let the script stage it (destroyobject or slay) as sample #0:
#   pwsh tools\liveness-freshness-probe.ps1 -ProcId 1234 -ObjectId 0x9 -Stage destroyobject `
#       -AgentLogPath "$env:TEMP\kotorcoop-demo\agent1.log" `
#       -Label tat_m20aa-31pop -DurationSec 240 -IntervalSec 15
#
# Output: one CSV row per sample (elapsed seconds, agent `census 0x<id>` array-held answer,
# plugin `resolve 0x<id>` answer, whether the two AGREE, the OWNER census count for population
# context) to -OutCsv (default tools\liveness-freshness-<Label>-<timestamp>.csv), plus a running
# console log and a summary at the end: first-gone timestamp per side, whether they ever
# disagreed at the same sampled instant, and total time to agreement.
param(
    [Parameter(Mandatory)][int]$ProcId,
    [Parameter(Mandatory)][string]$ObjectId,
    [Parameter(Mandatory)][string]$AgentLogPath,
    [ValidateSet('none', 'destroyobject', 'slay')]
    [string]$Stage = 'none',
    [int]$DurationSec = 240,
    [int]$IntervalSec = 15,
    [string]$Label = 'run',
    [string]$OutCsv = ''
)

$ErrorActionPreference = 'Stop'
$blockpoke = Join-Path $PSScriptRoot 'blockpoke.ps1'

if ($OutCsv -eq '') {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutCsv = Join-Path $PSScriptRoot "liveness-freshness-$Label-$stamp.csv"
}

# Tracks how far into the agent log we have already read, so re-issuing `census <id>` and
# grabbing "the last matching line" never picks up a PREVIOUS sample's answer by accident.
$script:AgentLogOffset = 0
if (Test-Path $AgentLogPath) {
    $script:AgentLogOffset = (Get-Item $AgentLogPath).Length
}

function Invoke-ConsoleReply {
    param([string]$Command)

    # blockpoke's own 'console' verb already writes the command and blocks (up to 6s) for the
    # reply SERIAL to change before returning - see tools/blockpoke.ps1's own comment on why
    # that beats reading text alone (a stale reply from the PREVIOUS command would otherwise be
    # misread as this one's answer). We just need the printed 'reply:' line back out of it.
    $lines = & $blockpoke -ProcId $ProcId console $Command
    $replyLine = $lines | Where-Object { $_ -match "^\s*reply:\s*'(.*)'\s*$" } | Select-Object -Last 1
    if ($replyLine -and $replyLine -match "^\s*reply:\s*'(.*)'\s*$") {
        return $Matches[1]
    }
    return ''
}

function Get-NewAgentLogLines {
    # Reads only what has been APPENDED to the agent log since the last call, so consecutive
    # samples cannot cross-read each other's answers.
    if (-not (Test-Path $AgentLogPath)) { return @() }

    $stream = [System.IO.File]::Open($AgentLogPath, 'Open', 'Read', 'ReadWrite')
    try {
        if ($stream.Length -lt $script:AgentLogOffset) {
            # Log rotated/truncated under us - fall back to reading from the start.
            $script:AgentLogOffset = 0
        }

        $stream.Seek($script:AgentLogOffset, 'Begin') | Out-Null
        $reader = [System.IO.StreamReader]::new($stream)
        $text = $reader.ReadToEnd()
        $script:AgentLogOffset = $stream.Position
        if ([string]::IsNullOrEmpty($text)) { return @() }
        return $text -split "`r?`n" | Where-Object { $_.Length -gt 0 }
    } finally {
        $stream.Dispose()
    }
}

function Get-CensusForId {
    param([string]$Id)

    # Fire the command (its OWN console-reply text is not the interesting half here - see the
    # header comment - but sending it is what makes DescribeId write the array/scan line to the
    # agent log in the first place), then read the array/scan line back from the log.
    Invoke-ConsoleReply -Command "census $Id" | Out-Null
    Start-Sleep -Milliseconds 200

    $newLines = Get-NewAgentLogLines
    $idNoPrefix = $Id -replace '^0x', ''
    $pattern = "census 0x0*$idNoPrefix\b.*:\s*array"
    $hit = $newLines | Where-Object { $_ -match $pattern } | Select-Object -Last 1

    if (-not $hit) { return @{ Held = $null; Raw = '(no array/scan line seen in log window)' } }
    if ($hit -match 'array NOT HELD') { return @{ Held = $false; Raw = $hit } }
    if ($hit -match 'array (0x[0-9A-Fa-f]+)') { return @{ Held = $true; Address = $Matches[1]; Raw = $hit } }
    return @{ Held = $null; Raw = $hit }
}

function Get-ResolveForId {
    param([string]$Id)

    # CheatController.Resolve's single Answer() line: "0x<id> resolves to 0x<addr> here; chain
    # in the plugin log" - 0x0 means gone, per Odyssey.ResolveServerObject's own "resolved to
    # 0x0" contract the 2026-08-24 round's layout-check ruling leans on.
    $reply = Invoke-ConsoleReply -Command "resolve $Id"
    if ($reply -match 'resolves to (0x[0-9A-Fa-f]+)') {
        $addr = $Matches[1]
        return @{ Held = ($addr -ne '0x0'); Address = $addr; Raw = $reply }
    }
    return @{ Held = $null; Raw = $reply }
}

function Get-OwnerCensusCount {
    # census (no id) - "census: OWNER <N> id(s) '<module>': ..." - population context. Same
    # overwrite risk as the per-id case if there happen to be zero unresolved entries (the
    # common case once a module has settled), so this one IS safe to read from the console
    # reply most of the time, but we check the log first for the same reason as Get-CensusForId
    # and fall back to the console reply if nothing landed in the log window.
    Invoke-ConsoleReply -Command 'census' | Out-Null
    Start-Sleep -Milliseconds 200

    $newLines = Get-NewAgentLogLines
    $hit = $newLines | Where-Object { $_ -match 'census: (OWNER|MIRROR) \d+ id' } | Select-Object -Last 1
    if ($hit -and $hit -match '(OWNER|MIRROR) (\d+) id') { return [int]$Matches[2] }
    return -1
}

"probe: pid $ProcId, id $ObjectId, label '$Label', $DurationSec s @ ${IntervalSec}s intervals -> $OutCsv"
"agent log: $AgentLogPath (starting from offset $script:AgentLogOffset)"

if ($Stage -ne 'none') {
    $stageId = $ObjectId -replace '^0x', ''
    $stageReply = Invoke-ConsoleReply -Command "$Stage 0x$stageId"
    "staged: $Stage 0x$stageId -> '$stageReply'"
}

$rows = [System.Collections.Generic.List[object]]::new()
$start = Get-Date
$firstAgentGone = $null
$firstPluginGone = $null
$disagreements = 0

while (((Get-Date) - $start).TotalSeconds -lt $DurationSec) {
    $elapsed = [math]::Round(((Get-Date) - $start).TotalSeconds, 1)

    $census = Get-CensusForId -Id $ObjectId
    $resolve = Get-ResolveForId -Id $ObjectId
    $population = Get-OwnerCensusCount

    $agree = if ($null -eq $census.Held -or $null -eq $resolve.Held) { 'unknown' }
             elseif ($census.Held -eq $resolve.Held) { 'agree' }
             else { 'DISAGREE' }

    if ($agree -eq 'DISAGREE') { $disagreements++ }
    if ($null -eq $firstAgentGone -and $census.Held -eq $false) { $firstAgentGone = $elapsed }
    if ($null -eq $firstPluginGone -and $resolve.Held -eq $false) { $firstPluginGone = $elapsed }

    $row = [pscustomobject]@{
        ElapsedSec       = $elapsed
        Timestamp        = (Get-Date -Format 'HH:mm:ss.fff')
        AgentHeld        = $census.Held
        PluginHeld       = $resolve.Held
        Agreement        = $agree
        OwnerCensusCount = $population
        AgentRaw         = $census.Raw
        PluginRaw        = $resolve.Raw
    }
    $rows.Add($row)

    $flag = if ($agree -eq 'DISAGREE') { '  <-- DISAGREEMENT' } else { '' }
    "  t=${elapsed}s  agent(census)=held:$($census.Held)  plugin(resolve)=held:$($resolve.Held)  pop=$population$flag"

    $rows | Export-Csv -Path $OutCsv -NoTypeInformation -Force

    $sinceStart = ((Get-Date) - $start).TotalSeconds
    $nextBoundary = [math]::Ceiling($sinceStart / $IntervalSec) * $IntervalSec
    $sleepFor = $nextBoundary - $sinceStart
    if ($sleepFor -gt 0) { Start-Sleep -Seconds $sleepFor }
}

""
"--- summary ($Label) ---"
"first AGENT (census)  reported gone at: $(if ($null -ne $firstAgentGone) { "${firstAgentGone}s" } else { 'never (still held at end of window)' })"
"first PLUGIN (resolve) reported gone at: $(if ($null -ne $firstPluginGone) { "${firstPluginGone}s" } else { 'never (still held at end of window)' })"
"disagreements observed: $disagreements $(if ($disagreements -gt 0) { '<-- the reproducible signal this document has been missing' } else { '(none - the two sides never diverged at a sampled instant)' })"
"csv: $OutCsv"

if ($disagreements -eq 0 -and $null -ne $firstAgentGone -and $null -ne $firstPluginGone) {
    $skew = [math]::Abs($firstAgentGone - $firstPluginGone)
    "both sides agree throughout; the id disappeared roughly together (skew ${skew}s, bounded by this script's own $IntervalSec s sampling grid, not a measured race)."
}
