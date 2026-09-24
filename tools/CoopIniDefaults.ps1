# Pure parsing/decision helpers for the coop.ini "upgrade trap": install.ps1 (PackageInstaller.cs,
# really) never overwrites an existing coop.ini -- by design, so a player's own edits (password,
# name, tuned flags) always survive an upgrade. But when the SHIPPED DEFAULT for a key changes
# (menu_button, effect_sync, trigger_relay and several others -- see $script:CoopIniDriftedDefaults
# below and docs/CONFIG.md's footnotes), a pre-existing coop.ini silently keeps the OLD value
# forever, with nothing telling the player their file is stale. That was previously only
# documented by hand; install.ps1 -UpdateDefaults (below) detects and offers to fix it
# automatically.
#
# Since the 2026-09 "player settings vs. developer switches" cleanup, profiles/coop.ini.example
# only lists the ~29 keys a player would ever want to change (docs/CONFIG.md's "Player settings"
# table) -- every developer switch that used to be spelled out in the shipped file now lives only
# in code and in docs/CONFIG.md's "Developer switches" table. That changes what this file compares
# against for each half of the job:
#   - Drifted (below) was ALREADY driven entirely by $script:CoopIniDriftedDefaults, a fixed table
#     of CODE defaults that changed after a key first shipped -- never by the shipped reference
#     file -- so it keeps working unchanged for both player AND developer keys.
#   - MissingKeys stays reference-driven (a key present in the shipped coop.ini.example but absent
#     from the player's own file) -- and since the shipped file is now player-keys-only, this is
#     automatically just "informational: which player settings your file predates" going forward.
#   - RemovableDeveloperKeys is new: a developer switch a player's (necessarily pre-cleanup) file
#     still sets EXPLICITLY, sitting at exactly its current shipped/verified default
#     ($script:CoopIniDeveloperKeyDefaults below) -- pure noise now that the line cannot come from
#     the shipped file any more. -UpdateDefaults offers to delete just those lines. A developer key
#     set to something OTHER than its current default is a deliberate tuning/bisect choice and is
#     never touched.
#
# This file has NO side effects when dot-sourced (it only defines the tables and four functions),
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
# trimmed, case-insensitive strings -- every key here is a plain 0/1 flag (or, for the two mode
# integers, a small number), the same tolerance CoopConfig.IsTruthy itself uses. Date is when the
# NEW value first shipped (git-log-verified against profiles/coop.ini.example's own history; see
# docs/CONFIG.md for the live-verification story behind each flip) -- purely informational, shown
# alongside the warning so a player can tell how stale their file is.
$script:CoopIniDriftedDefaults = @(
    [pscustomobject]@{
        Key      = 'menu_button'
        OldValue = '0'
        NewValue = '1'
        Date     = '2026-08-27'
        Meaning  = 'shows the in-game Co-op menu button'
    }
    [pscustomobject]@{
        Key      = 'effect_sync'
        OldValue = '0'
        NewValue = '1'
        Date     = '2026-08-27'
        Meaning  = 'forwards non-damage combat effects (stun, slow, knockdown, ...) to peers'
    }
    [pscustomobject]@{
        Key      = 'trigger_relay'
        OldValue = '0'
        NewValue = '1'
        Date     = '2026-08-27'
        Meaning  = "relays a generic trigger's OnEnter to peers"
    }
    [pscustomobject]@{
        Key      = 'map_reveal_sync'
        OldValue = '0'
        NewValue = '1'
        Date     = '2026-08-27'
        Meaning  = "reveals fog-of-war around a peer's live position"
    }
    [pscustomobject]@{
        Key      = 'script_item_sync'
        OldValue = '0'
        NewValue = '1'
        Date     = '2026-08-27'
        Meaning  = 'forwards a script (OnEnter/OnDeath/heartbeat) item grant to the other player'
    }
    [pscustomobject]@{
        Key      = 'companion_xp_sync'
        OldValue = '0'
        NewValue = '1'
        Date     = '2026-08-27'
        Meaning  = "shares each active companion's experience the same way the PC's own is"
    }
    [pscustomobject]@{
        Key      = 'companion_local_combat'
        OldValue = '0'
        NewValue = '1'
        Date     = '2026-08-24'
        Meaning  = "lets one released companion fight for real on a client"
    }
    [pscustomobject]@{
        Key      = 'dialogue_recruit_sync'
        OldValue = '0'
        NewValue = '1'
        Date     = '2026-08-24'
        Meaning  = 'recruits a companion through the real network path on the peer'
    }
    [pscustomobject]@{
        Key      = 'pazaak_detect'
        OldValue = '0'
        NewValue = '1'
        Date     = '2026-08-28'
        Meaning  = 'treats a running Pazaak hand as "in a minigame" too'
    }
    [pscustomobject]@{
        Key      = 'party_pool_sync'
        OldValue = '0'
        NewValue = '2'
        Date     = '2026-08-29'
        Meaning  = 'syncs the shared party item pool, host-authoritative redesign (mode 2)'
    }
    [pscustomobject]@{
        Key      = 'store_sync'
        OldValue = '0'
        NewValue = '2'
        Date     = '2026-08-29'
        Meaning  = 'syncs merchant store stock, host-authoritative redesign (mode 2)'
    }
    [pscustomobject]@{
        Key      = 'enemy_effect_sync'
        OldValue = '0'
        NewValue = '1'
        Date     = '2026-09-25'
        Meaning  = "relays a stun, fear or other effect the joiner casts on an enemy to the host"
    }
    [pscustomobject]@{
        Key      = 'partner_buffs'
        OldValue = '0'
        NewValue = '1'
        Date     = '2026-09-25'
        Meaning  = "lets a Force heal or buff you cast reach your partner"
    }
    [pscustomobject]@{
        Key      = 'threat_retarget'
        OldValue = '0'
        NewValue = '1'
        Date     = '2026-09-03'
        Meaning  = "HOST only: retargets a creature onto whichever peer's forwarded damage outweighs its current target's"
    }
    [pscustomobject]@{
        Key      = 'liveness_sync'
        OldValue = '0'
        NewValue = '1'
        Date     = '2026-09-03'
        Meaning  = 'reconciles a body left alive on one instance and dead on another'
    }
    [pscustomobject]@{
        Key      = 'join_mode'
        OldValue = 'save'
        NewValue = 'auto'
        Date     = '2026-09-03'
    [pscustomobject]@{
        Key      = 'world_transfer_udp_fallback'
        OldValue = '0'
        NewValue = '1'
        Date     = '2026-09-18'
        Meaning  = 'retries the first world hand-off over UDP when the TCP path is unreachable (the only internet path that worked in testing)'
    }
)

# Every developer switch's CURRENT shipped/verified default (docs/CONFIG.md's "Developer
# switches" table), lowercase key -> string value. Used ONLY to detect noise: since profiles/
# coop.ini.example no longer lists developer keys at all (the 2026-09 player-settings cleanup),
# a player's (necessarily older) coop.ini that still explicitly sets one of these to exactly this
# value has nothing left to override -- it is just a stale, harmless line. -UpdateDefaults offers
# to delete lines that match; a developer key set to anything ELSE is a deliberate tuning/bisect
# choice and is never touched or even flagged. Keys not listed here were never shipped explicitly
# in any coop.ini.example, so there is nothing for an existing player's file to have inherited --
# they simply are not candidates for removal.
$script:CoopIniDeveloperKeyDefaults = @{
    host_loss_release                  = '1'
    minigame_hide_peer                 = '1'
    minigame_spectate                  = '0'
    pazaak_detect                      = '1'
    minigame_wait_notice               = '1'
    minigame_lock_peers                = '1'
    minigame_lock_timeout_s            = '600'
    enemy_materialise                  = '1'
    local_combat                       = '1'
    local_combat_slots                 = '3'
    local_combat_deferred_notice       = '1'
    local_combat_engage_fallback       = '1'
    activate_render_on_start           = '1'
    bark_replay                        = '1'
    interaction_locks                  = '1'
    party_roster_sync                  = '1'
    party_select_host_only             = '1'
    galaxy_map_host_only               = '1'
    party_follow_refresh_final_node    = '0'
    party_follow_extend_final_node     = '0'
    party_follow_phase2_kick           = '0'
    party_follow_phase7_release        = '0'
    party_follow_stall_recovery        = '1'
    party_follow_stall_recovery_method = '3'
    party_together_fix                 = '1'
    client_door_refusal                = '1'
    transition_announce                = '1'
    transition_announce_scripted       = '1'
    dialogue_item_sync                 = '1'
    script_item_sync                   = '1'
    dialogue_fast_join                 = '1'
    party_fast_pump                    = '1'
    container_fast_pump                = '0'
    container_lock_arbitration         = '1'
    dialogue_join_teleport             = '1'
    dialogue_recruit_sync              = '1'
    session_save_host_refusal          = '0'
    client_load_rejoin                 = '1'
    host_load_readopt                  = '1'
    save_flush                         = '1'
    pause_button_hook                  = '1'
    local_combat_range                 = '20'
    damage_creator_attribution         = '0'
    threat_retarget                    = '1'
    partner_buffs                      = '1'
    enemy_effect_sync                  = '1'
    companion_local_combat             = '0'
    companion_local_combat_range       = '10'
    companion_xp_sync                  = '1'
    companion_gear_sync                = '1'
    companion_damage_sync              = '1'
    effect_sync                        = '1'
    map_reveal_sync                    = '1'
    liveness_sync                      = '1'
    journal_remove_sync                = '1'
    client_encounters                  = '1'
    trigger_relay                      = '1'
    trigger_relay_patterns             = 'k_p*_*'
    movie_relay                        = '1'
    frame_stats                        = '0'
    transition_close_client_world      = '1'
    party_pool_sync                    = '2'
    party_pool_trace                   = '0'
    solo_segment_spectate              = '1'
    solo_segment_any_module            = '0'
    store_sync                         = '2'
    client_temp_death_suppress         = '0'
}

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
# parsed coop.ini, what to warn about. Returns one object with three independent lists so a
# caller can act on any of them:
#   - Drifted: a key from the known-changed table (CODE defaults, not the reference file) still
#     holding its OLD value -- actionable, -UpdateDefaults fixes it.
#   - MissingKeys: present in the reference, entirely absent from the player's file --
#     informational only; a missing key already takes the CODE's own default, which is a normal,
#     supported state, not something to "fix". Since the reference is now the short player-keys-
#     only example, this is inherently about player settings only.
#   - RemovableDeveloperKeys: a developer switch the player's file still sets explicitly, at
#     exactly its current shipped default -- noise now that no shipped file lists it at all.
#     -UpdateDefaults offers to delete these lines outright.
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

    $removable = @()
    foreach ($key in $PlayerIni.Keys) {
        if (-not $script:CoopIniDeveloperKeyDefaults.ContainsKey($key)) { continue }

        $current = ([string]$PlayerIni[$key]).Trim()
        $default = $script:CoopIniDeveloperKeyDefaults[$key]
        if ($current -eq $default) {
            $removable += [pscustomobject]@{ Key = $key; Value = $current }
        }
    }

    return [pscustomobject]@{
        Drifted                = $drifted
        MissingKeys            = $missing
        RemovableDeveloperKeys = $removable
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

# Deletes entire lines whose key matches one of $Keys (case-insensitive) -- used for
# RemovableDeveloperKeys above: a developer switch sitting at its own current default is pure
# noise once no shipped coop.ini.example lists developer keys at all, so -UpdateDefaults removes
# the line rather than rewriting it. Every other line (including comments, blank lines and any
# key NOT in $Keys) is preserved verbatim, in order. Returns the number of lines actually removed.
function Remove-CoopIniKeys {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string[]]$Keys
    )

    if ($Keys.Count -eq 0) { return 0 }

    $lowerKeys = $Keys | ForEach-Object { $_.ToLowerInvariant() }
    $lines = @(Get-Content -LiteralPath $Path)
    $kept = [System.Collections.Generic.List[string]]::new()
    $removed = 0

    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        $isMatch = $false

        if ($trimmed.Length -gt 0 -and -not ($trimmed.StartsWith(';') -or $trimmed.StartsWith('#') -or $trimmed.StartsWith('['))) {
            $eq = $trimmed.IndexOf('=')
            if ($eq -gt 0) {
                $key = $trimmed.Substring(0, $eq).Trim().ToLowerInvariant()
                if ($lowerKeys -contains $key) { $isMatch = $true }
            }
        }

        if ($isMatch) { $removed++ } else { $kept.Add($line) }
    }

    if ($removed -gt 0) {
        Set-Content -LiteralPath $Path -Value $kept -Encoding UTF8
    }

    return $removed
}
