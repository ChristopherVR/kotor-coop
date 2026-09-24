namespace KotorCoop.Plugin;

/// <summary>
/// The few settings the PLUGIN itself needs from <c>coop.ini</c>, read in-process.
///
/// Deliberately a tiny private parser rather than a shared one: the plugin is NativeAOT x86
/// and cannot reference the agent's assemblies. Same file and same shape the agent's reader
/// accepts, so there is one file for the player to edit.
/// </summary>
internal static class PluginConfig
{
    private static string _directory = ".";

    public static void UseDirectory(string gameDirectory) => _directory = gameDirectory;

    /// <summary>Reads one key, or null when the file or key is absent.</summary>
    public static string? Read(string key)
    {
        try
        {
            string path = Path.Combine(_directory, "coop.ini");
            if (!File.Exists(path))
            {
                return null;
            }

            List<string> developer = [];
            List<string> unknown = [];
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';' or '[')
                {
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals <= 0 || !line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = line[(equals + 1)..].Trim();
                int comment = value.IndexOfAny([';', '#']);
                return comment >= 0 ? value[..comment].Trim() : value;
            }
        }
        catch
        {
            // A missing or unreadable coop.ini simply means defaults.
        }

        return null;
    }

    public static bool ReadBool(string key, bool fallback)
    {
        string? value = Read(key);
        if (value is null)
        {
            return fallback;
        }

        return value == "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    public static float ReadFloat(string key, float fallback) =>
        float.TryParse(Read(key), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float value)
            ? value
            : fallback;

    public static int ReadInt(string key, int fallback) =>
        int.TryParse(Read(key), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    // ── Player-facing vs. developer-switch classification ──────────────────────────────
    // Mirrors CoopConfig's own copy in KotorCoop.Session — cannot be shared, since this
    // project is NativeAOT x86 and cannot reference the agent's x64 assemblies. Kept as plain
    // string[] with a manual scan (no LINQ) for the same reason. See CoopConfig.cs's own
    // "Player-facing vs. developer-switch classification" remarks for the full rationale.

    /// <summary>Every key in the shipped profiles/coop.ini.example.</summary>
    private static readonly string[] PlayerFacingKeys =
    [
        "mode", "host", "port", "password", "name", "server_name", "join_mode",
        "auto_adopt_world", "menu_button", "follow_lead", "dialogue_auto_join",
        "dialogue_auto_join_range", "spectate_on_down", "control_return",
        "enemy_health", "enemy_damage", "xp_rate", "store_stock", "store_prices",
        "hide_party_portraits", "status_toasts", "status_indicator", "chat_overlay",
        "chat_overlay_opacity", "chat_overlay_anchor", "chat_overlay_lines",
        "chat_overlay_fade_ms", "steam_invites", "stun_servers", "upnp", "log_max_mb",
        "log_keep", "auto_start_agent", "dinput8_next", "gamepad", "gamepad_cursor_speed",
    ];

    /// <summary>Every other key either reader (CoopConfig.cs's kv lookups, or this file's own
    /// Read/ReadBool/ReadInt/ReadFloat call sites anywhere under src/KotorCoop.Plugin) actually
    /// consumes — a developer switch, not a player setting. See docs/CONFIG.md's "Developer
    /// switches" table.</summary>
    private static readonly string[] DeveloperKnownKeys =
    [
        "activate_render_on_start", "bark_replay", "bark_sync", "behaviour_override_reader",
        "class_grant_sync", "client_door_refusal", "client_encounters", "client_load_rejoin",
        "client_temp_death_suppress", "companion_damage_sync", "companion_gear_sync",
        "companion_local_combat", "companion_local_combat_range", "companion_xp_sync",
        "connect_timeout_ms", "container_fast_pump", "container_lock_arbitration",
        "container_locks", "damage_creator_attribution", "debug_hello_version", "dejitter",
        "delay_probe", "delay_probe_events", "dialog_state_watch", "dialogue_exclusive",
        "dialogue_fast_join", "dialogue_item_sync", "dialogue_join_teleport",
        "dialogue_recruit_sync", "disguise_sync", "effect_sync", "enemy_drive",
        "enemy_effect_sync", "enemy_materialise",
        "follow_stunt", "frame_stats", "galaxy_map_host_only", "gui_probe", "host_load_readopt",
        "host_loss_release", "host_loss_timeout_ms", "interaction_locks", "interp_ms",
        "leviathan_action_script_gate",
        "item_template_spawn_sync", "journal_remove_sync", "liveness_sync", "local_combat", "local_var_sync",
        "local_combat_bisect_mask", "local_combat_bypass_id", "local_combat_deferred_notice",
        "local_combat_diag_undriven_id", "local_combat_engage_fallback",
        "local_combat_engage_range", "local_combat_range", "local_combat_slots",
        "local_combat_targeting_us_fresh_s", "local_combat_timeout_s", "map_reveal_sync",
        "menu_guard_cap_ms", "menu_guard_cooldown_ms", "menu_guard_sync",
        "minigame_hide_peer", "minigame_lock_peers", "minigame_lock_timeout_s",
        "mine_sync", "minigame_modules", "minigame_spectate", "minigame_wait_notice", "minimap_peers",
        "mirror_smooth_ms", "movement", "movie_relay", "partner_buffs", "party_drive", "party_fast_pump",
        "party_follow", "party_follow_accept_final_node", "party_follow_advance_destnode",
        "party_follow_advance_destnode_phase6", "party_follow_advance_movingtopath",
        "party_follow_extend_final_node", "party_follow_finelog", "party_follow_force_walking",
        "party_follow_phase2_kick", "party_follow_phase7_release", "party_follow_pokestep",
        "party_follow_refresh_final_node", "party_follow_stall_recovery",
        "party_follow_stall_recovery_method", "party_pool_sync", "party_pool_trace",
        "phantom_gait_diagnostics",
        "party_roster_sync", "party_select_host_only", "party_together_fix",
        "pause_button_hook", "pause_sync", "pazaak_detect", "perf_trace", "phantom_targetable",
        "poison_sync", "removeparty_hook", "save_flush", "script_item_sync",
        "session_save_host_refusal", "smooth_ms", "solo_segment_any_module", "solo_segment_modules",
        "solo_segment_spectate", "stealth_sync", "stealth_visual_sync", "store_sync",
        "threat_retarget", "tick_rate", "transition_announce",
        "transition_announce_fade", "transition_announce_scripted",
        "transition_close_client_world", "trigger_relay", "trigger_relay_patterns",
    ];

    private static bool Contains(string[] keys, string key)
    {
        foreach (string candidate in keys)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool _loggedNonPlayerKeys;

    /// <summary>
    /// Scans coop.ini ONCE (guarded by <see cref="_loggedNonPlayerKeys"/> — every controller
    /// reads this class field-initialiser-eagerly, so without a guard this would re-scan and
    /// re-log once per controller constructed) and logs every key present that is not in
    /// <see cref="PlayerFacingKeys"/>: a known developer switch, or a genuinely unrecognised
    /// key. Call after <see cref="UseDirectory"/>, before the first controller is built.
    /// </summary>
    public static void LogNonPlayerKeys(Action<string> log)
    {
        if (_loggedNonPlayerKeys)
        {
            return;
        }

        _loggedNonPlayerKeys = true;

        try
        {
            string path = Path.Combine(_directory, "coop.ini");
            if (!File.Exists(path))
            {
                return;
            }

            List<string> developer = [];
            List<string> unknown = [];
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';' or '[')
                {
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                string key = line[..equals].Trim();
                string value = line[(equals + 1)..].Trim();
                int comment = value.IndexOfAny([';', '#']);
                if (comment >= 0)
                {
                    value = value[..comment].Trim();
                }

                if (Contains(PlayerFacingKeys, key))
                {
                    continue;
                }

                if (Contains(DeveloperKnownKeys, key))
                {
                    developer.Add($"{key}={value}");
                }
                else
                {
                    unknown.Add(key);
                }
            }

            // One line per category, not per key: a coop.ini written from the pre-2026-09-03
            // template carries a hundred developer keys, and a hundred startup lines reads
            // like a hundred problems. install.ps1 -UpdateDefaults offers to strip them.
            if (developer.Count > 0)
            {
                log($"coop.ini: {developer.Count} developer switch(es) set - not player settings, see docs/CONFIG.md: "
                    + string.Join(", ", developer));
            }

            if (unknown.Count > 0)
            {
                log($"coop.ini: {unknown.Count} unknown key(s) ignored: {string.Join(", ", unknown)}");
            }
        }
        catch
        {
            // Best-effort diagnostic only — never block plugin startup over a log line.
        }
    }
}
