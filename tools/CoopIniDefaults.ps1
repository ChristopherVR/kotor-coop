# Pure parsing/decision helpers for the coop.ini "upgrade trap": install.ps1 (PackageInstaller.cs,
# really) never overwrites an existing coop.ini -- by design, so a player's own edits (password,
# name, tuned flags) always survive an upgrade. But when the SHIPPED DEFAULT for a key changes
# (menu_button, effect_sync, trigger_relay all flipped 0 -> 1 on 2026-08-27 -- see docs/CONFIG.md's
# footnotes 4/5/7 and TROUBLESHOOTING.md's "Co-op button doesn't appear"), a pre-2026-08-27 coop.ini
# silently keeps the OLD value forever, with nothing telling the player their file is stale. That
# was previously only documented by hand; install.ps1 -UpdateDefaults (below) detects and offers
# to fix it automatically.
#
# This file has NO side effects when dot-sourced (it only defines the table and three functions),
# specifically so the decision logic is exercisable on its own without running an install:
#
#   . tools\CoopIniDefaults.ps1
#   $player = ConvertFrom-CoopIni -Path 'C:\GOG Games\Star Wars KOTOR (GOG)\coop.ini'
#   $shipped = ConvertFrom-CoopIni -Path 'coop.ini'   # the one that ships beside install.ps1
#   Get-CoopIniDrift -PlayerIni $player -ReferenceIni $shipped
#
# See tools/README.md's "Manual verification" note for a step-by-step check against a real file
# (this repo has no Pester harness to automate it yet).

# The list to extend the next time a shipped default changes. OldValue/NewValue are compared as
# trimmed, case-insensitive strings -- every key here is a plain 0/1 flag, the same tolerance
# CoopConfig.IsTruthy itself uses.
$script:CoopIniDriftedDefaults = @(
    [pscustomobject]@{
        Key      = 'menu_button'
        OldValue = '0'
        NewValue = '1'
        Meaning  = 'shows the in-game Co-op menu button'
    }
    [pscustomobject]@{
        Key      = 'effect_sync'
        OldValue = '0'
        NewValue = '1'
        Meaning  = 'forwards non-damage combat effects (stun, slow, knockdown, ...) to peers'
    }
    [pscustomobject]@{
        Key      = 'trigger_relay'
        OldValue = '0'
        NewValue = '1'
        Meaning  = "relays a generic trigger's OnEnter to peers"
    }
)

# Parses a coop.ini-shaped file into an ordered map of LOWERCASE key -> raw string value.
# Tolerant of ; and # comments, blank lines and a [section] header (coop.ini has at most one,
# [coop]); a commented-out "# key = value" line is skipped entirely -- a commented key was never
# actually set, so it must never be read as present. Returns an empty map for a missing file
# rather than throwing: "no file yet" and "empty file" are the same answer to every caller here.
function ConvertFrom-CoopIni {
    param([Parameter(Mandatory)][string]$Path)

    $result = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Path)) { return $result }

    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0) { continue }
        if ($trimmed.StartsWith(';') -or $trimmed.StartsWith('#') -or $trimmed.StartsWith('[')) { continue }

        $eq = $trimmed.IndexOf('=')
        if ($eq -lt 0) { continue }

        $key = $trimmed.Substring(0, $eq).Trim().ToLowerInvariant()
        $value = $trimmed.Substring($eq + 1).Trim()
        if ($key.Length -eq 0) { continue }

        $result[$key] = $value
    }

    return $result
}

# The pure decision: given the player's parsed coop.ini and (optionally) the shipped reference's
# parsed coop.ini, what to warn about. Returns one object with two independent lists so a caller
# can act on either: Drifted (a key from the known-changed table still holding its OLD value --
# actionable, -UpdateDefaults fixes it) and MissingKeys (present in the reference, entirely
# absent from the player's file -- informational only; a missing key already takes the CODE's
# own default, which is a normal, supported state, not something to "fix").
function Get-CoopIniDrift {
    param(
        [Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$PlayerIni,
        [System.Collections.Specialized.OrderedDictionary]$ReferenceIni
    )

    $drifted = @()
    foreach ($entry in $script:CoopIniDriftedDefaults) {
        $lowerKey = $entry.Key.ToLowerInvariant()
        if (-not $PlayerIni.Contains($lowerKey)) { continue } # absent = takes the code default, already current

        $current = ([string]$PlayerIni[$lowerKey]).Trim()
        if ($current -eq $entry.OldValue) {
            $drifted += $entry
        }
    }

    $missing = @()
    if ($ReferenceIni) {
        foreach ($key in $ReferenceIni.Keys) {
            if (-not $PlayerIni.Contains($key)) { $missing += $key }
        }
    }

    return [pscustomobject]@{
        Drifted     = $drifted
        MissingKeys = $missing
    }
}

# Rewrites ONLY the drifted keys' lines in place, preserving every other line verbatim -- order,
# comments, blank lines, and the untouched lines' own casing/spacing -- a targeted line
# replacement, not a rebuild. Matches "key = value" and "key=value" (coop.ini.example uses both
# styles) case-insensitively on the key name; the rewritten line always uses "key = value".
# Returns the number of lines actually changed, so the caller can report exactly what happened.
function Update-CoopIniDefaults {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object[]]$Drifted
    )

    if ($Drifted.Count -eq 0) { return 0 }

    $lines = @(Get-Content -LiteralPath $Path)
    $changed = 0

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $trimmed = $lines[$i].Trim()
        if ($trimmed.Length -eq 0) { continue }
        if ($trimmed.StartsWith(';') -or $trimmed.StartsWith('#') -or $trimmed.StartsWith('[')) { continue }

        $eq = $trimmed.IndexOf('=')
        if ($eq -lt 0) { continue }

        $key = $trimmed.Substring(0, $eq).Trim()
        $match = $Drifted | Where-Object { $_.Key -eq $key.ToLowerInvariant() } | Select-Object -First 1
        if (-not $match) { continue }

        $lines[$i] = "$key = $($match.NewValue)"
        $changed++
    }

    if ($changed -gt 0) {
        Set-Content -LiteralPath $Path -Value $lines -Encoding UTF8
    }

    return $changed
}
