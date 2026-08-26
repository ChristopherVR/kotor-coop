using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Connection settings read from <c>coop.ini</c> beside the game executable, for the
/// in-game Connect flow: the player presses a menu button and the session starts from
/// these values. A file rather than typed-in-game input because KOTOR's edit control has
/// no password masking (see docs/research/connect-flow.md) — the IP can be typed in-game,
/// but the password should not be shown, so it lives here.
///
/// Format is a plain <c>key = value</c> INI, optionally under a <c>[coop]</c> header:
/// <code>
///   [coop]
///   mode = join        ; or host
///   host = 192.168.1.20
///   port = 27140
///   password = swordfish
///   name = Malak
///   menu_button = 1    ; the in-game Co-op menu button (default on; 0 hides it)
///   minigame_hide_peer = 1   ; park a peer's phantom while they swoop-race/man a turret
///   minigame_spectate = 0    ; watch a racing/turret peer instead (not yet wired)
///   transition_announce = 1    ; race a follow ahead of the settle debounce (default ON,
///                               ; live-verified 2026-08-21 - docs/research/transition-smoothness.md)
///   client_door_refusal = 1    ; refuse a client's own premature door crossing (default ON,
///                               ; live-verified 2026-08-22 once the party-gate defect that had
///                               ; blocked the decisive check was root-caused and fixed - see
///                               ; the same doc's 2026-08-22 live-pass section)
/// </code>
/// </summary>
public sealed record CoopConfig
{
    public bool Host { get; init; }

    public string HostAddress { get; init; } = "127.0.0.1";

    /// <summary>
    /// Whether the file actually named a host, as opposed to <see cref="HostAddress"/>
    /// holding its 127.0.0.1 default. A bare Co-op button press with no session to
    /// reconnect to needs this to tell "join the configured server" apart from "nothing
    /// is configured — point the player at the server browser".
    /// </summary>
    public bool HasHostAddress { get; init; }

    public int Port { get; init; } = ProtocolConstants.DefaultPort;

    public string Password { get; init; } = string.Empty;

    public string? Name { get; init; }

    /// <summary>
    /// What this host calls itself in other players' server browsers. Distinct from
    /// <see cref="Name"/>, which is the CHARACTER's name on the wire and in the selection
    /// band: a server wants a name for the session ("Chris's Tuesday game"), not for the
    /// person. Unset falls back to <see cref="Name"/>, which is what every existing install
    /// already advertises.
    /// </summary>
    public string? ServerName { get; init; }

    /// <summary>Whether the main-menu Co-op button shows. ON by default — the button is part
    /// of the install, and the plugin shows it on its own (it reads this same key from
    /// coop.ini in-process); the agent's write of this value into the shared block is a
    /// force-on override, kept for diagnostics. <c>menu_button = 0</c> hides it.</summary>
    public bool MenuButton { get; init; } = true;

    /// <summary>
    /// How often state is sent, in Hz. Every millisecond here is visible latency on the
    /// peer's movement, so it is exposed for tuning: raise it if peers feel behind, lower it
    /// if the network or CPU cannot keep up. 0 keeps the built-in default.
    /// </summary>
    public int TickRate { get; init; }

    /// <summary>
    /// How far in the past peers are rendered, in milliseconds. This is the OTHER half of
    /// perceived lag and the one worth trying first: it buys smoothness by always having a
    /// later snapshot to interpolate toward, which a LAN barely needs. 0 means "no buffer,
    /// show the newest position" — lowest latency, and jittery only if packets arrive
    /// unevenly. Negative means "keep the built-in default".
    /// </summary>
    public int InterpolationDelayMs { get; init; } = -1;

    /// <summary>
    /// Mirror a peer's pause into this game. On by default. Turn it off if a pause ever
    /// sticks: without the mirror each game pauses only for its own player, which is worse
    /// co-op but can never wedge.
    /// </summary>
    public bool PauseSync { get; init; } = true;

    /// <summary>
    /// While a peer is inside the engine's own minigame mode (a swoop race or a forced
    /// turret sequence — <c>PlayerState.FlagInMinigame</c>, docs/research/minigames.md),
    /// park that peer's phantom instead of mirroring it — the same treatment a peer in a
    /// different module already gets. On by default: a phantom standing frozen through an
    /// entire swoop track is worse than one that briefly disappears. <c>minigame_hide_peer =
    /// 0</c> keeps mirroring it anyway.
    /// </summary>
    public bool HidePeersInMinigame { get; init; } = true;

    /// <summary>
    /// Hand this instance's camera to a peer while they race or man a turret, the way
    /// <c>SpectateController</c> already does for a downed player. NOT YET WIRED — the
    /// camera handoff needs a trigger distinct from "downed" and is left as the documented
    /// next step (docs/research/minigames.md, item 8c). Read and stored now so the key
    /// exists in coop.ini ahead of that work landing. Off by default.
    /// </summary>
    public bool SpectateMinigame { get; init; }

    /// <summary>Show the always-on in-game chat overlay.</summary>
    public bool ChatOverlay { get; init; } = true;

    /// <summary>Chat overlay opacity, 0..100. 75 stays readable on the game's bright interiors;
    /// lower it for a fainter overlay.</summary>
    public int ChatOverlayOpacity { get; init; } = 75;

    /// <summary>Chat overlay corner: top-left, top-right, bottom-left, bottom-right.</summary>
    public string ChatOverlayAnchor { get; init; } = "bottom-left";

    /// <summary>Lines shown at once.</summary>
    public int ChatOverlayLines { get; init; } = 5;

    /// <summary>Milliseconds a line stays before it fades; 0 keeps it forever.</summary>
    public int ChatOverlayFadeMs { get; init; } = 9000;

    /// <summary>
    /// How long a client's connect attempt waits for ANY reply before giving up and
    /// reporting "no reply from &lt;host&gt;" (coop.ini <c>connect_timeout_ms</c>). 0 or
    /// unset keeps <see cref="ProtocolConstants.DefaultConnectTimeoutMs"/>.
    /// </summary>
    public int ConnectTimeoutMs { get; init; }
    /// Show the persistent connection indicator ("co-op: connected, 2 players" /
    /// "co-op: reconnecting..." / "co-op: disconnected"), reposted only when it changes. On
    /// by default. Before this the ~ console's <c>status</c> verb was the only way to see any
    /// of it, so a player not actively typing had no idea their partner's link dropped until
    /// something else went visibly wrong. Consumed by the agent's
    /// <c>ConnectionStatusAnnouncer</c>.
    /// </summary>
    public bool StatusToasts { get; init; } = true;

    /// <summary>
    /// Whether an item a reward node places into the local conversation owner's inventory is
    /// diffed and given to the other player too — docs/research/dialogue-consequences.md §3.
    /// On by default: without it, `GetItemPossessedBy(GetFirstPC())` gates elsewhere in the
    /// story silently fail on the peer who did not own the conversation.
    /// </summary>
    public bool DialogueItemSync { get; init; } = true;

    /// <summary>
    /// Whether the dialogue channel is pumped on the agent's real-time (per-poll-tick) path
    /// instead of the ~150 ms combat cadence, and whether an unchanged read is deduped against
    /// a resend rather than republished on a fixed timer — docs/research/dialogue-join-latency.md
    /// has the measured before/after. Default ON since 2026-08-23: docs/research/dialogue-skip-
    /// desync.md's live A/B on the exact skip-desync repro found the 150 ms cadence genuinely
    /// DROPS content (a rapid-skip burst delivered only the first and last node; the
    /// intermediate lines never reached the follower at all), while the fast path caught every
    /// node with zero coalescing on the identical burst. <c>dialogue_fast_join = 0</c> reverts
    /// to the old cadence.
    /// </summary>
    public bool DialogueFastJoin { get; init; } = true;

    /// <summary>
    /// Whether the party channel (<c>PumpParty</c>/<c>PumpPartyRoster</c>) is pumped on the
    /// agent's real-time (per-poll-tick) path instead of the ~150 ms combat cadence — the same
    /// move <see cref="DialogueFastJoin"/> made, for the same reason: two independently-
    /// configured 150 ms windows (this pump, then <c>PartyPresenceController</c>'s own
    /// <c>mirror_smooth_ms</c> smoother) stack back to back on the companion-mirror path,
    /// docs/research/lag-audit.md's suspect #2 for it reading "laggier" than a peer's own
    /// avatar. Two earlier live A/Bs (2026-08-23, combat and non-combat) found no benefit or a
    /// regression — both were confounded by the companion follow-AI stall bug of the time: a
    /// companion that never moves has no position lag to reduce. With that bug fixed
    /// (rounds 80/84/88), a third A/B against a companion genuinely walking/following
    /// (2026-08-24, docs/research/lag-audit.md's own dated entry) measured a real, reproduced
    /// ~45-55% cut in companion-position lag (median ~160 ms vs ~320-350 ms across two
    /// independent legs each) with no frame-time regression in either a non-combat or a light
    /// combat check. ON by default since that result. <c>party_fast_pump = 0</c> reverts to the
    /// old cadence.
    /// </summary>
    public bool PartyFastPump { get; init; } = true;

    /// <summary>
    /// Whether <c>PumpPlaceables</c> (the DURABLE container/door open-or-locked STATE — what
    /// keeps a closed container looking closed rather than getting reasserted open, the
    /// round-66 <c>ContainerCloseHook</c> fix) is pumped on the agent's real-time (per-poll-tick)
    /// path instead of the ~1000 ms heavy-world-state cadence. <c>PumpInteractionLocks</c> (the
    /// open/close ANIMATION and the use-lock itself) already rides the ~150 ms combat cadence, so
    /// before this flag a close could take 1-2+ seconds end to end: the animation and the lock
    /// released quickly, but the state that actually keeps the container shut for a peer lagged a
    /// full heavy-pass cycle behind it — live-confirmed 2026-08-23 (docs/research/container-sync.md),
    /// matching the "very big delay... closing biggest" report. Same rollout shape as
    /// <see cref="DialogueFastJoin"/>/<see cref="PartyFastPump"/>: OFF by default so a live A/B
    /// pass can compare it against the existing cadence. <c>container_fast_pump = 1</c> turns it
    /// on. Door/placeable state for an object nobody has recently touched is unaffected either
    /// way — <c>CollectChangedPlaceableState</c> only ever reports a real change, so running it
    /// more often costs nothing beyond the extra poll when nothing changed.
    /// </summary>
    public bool ContainerFastPump { get; init; }

    /// <summary>
    /// Whether the agent's <c>SessionSaveWatch</c>'s host-side edge (roadmap item
    /// 14 phase 1, design §3.2 item 5) actually tears the session down when it fires. OFF by
    /// default — live-verified 2026-08-21 (shared-state-audit.md §2 round 53) to false-positive
    /// on an ORDINARY module transition, not only on a genuine "Load Game to a different save"
    /// action: KOTOR's own engine autosaves on every module load, which rewrites the exact save
    /// directory <see cref="KotorCoop.Memory.SaveDigest.FindSessionSave"/> is watching, so
    /// <c>SaveIdentityDigest</c> changes — and the host gets disconnected from its own session —
    /// the moment it walks (or warps) through a transition, with no different save ever chosen.
    /// The detector cannot currently tell "this session's own autosave, because play continued"
    /// apart from "a genuinely different save was loaded"; distinguishing them needs a real
    /// save/load engine hook (session-save.md §"Phase 1 shipped", item 3's own deferred note),
    /// which is out of scope here. Until that lands, the edge is still detected and logged
    /// (harmless) but does not disconnect anyone unless explicitly turned back on.
    /// </summary>
    public bool SessionSaveHostRefusal { get; init; }

    /// <summary>
    /// Roadmap item 14 phase 2 (docs/research/session-save.md's Phase 2 section): whether a
    /// CLIENT's own <see cref="KotorCoop.Session.SaveOrLoadClassifier"/> verdict of
    /// <see cref="KotorCoop.Session.SaveOrLoadVerdict.Load"/> actually tears the session down
    /// and re-runs world adoption against the host, rather than merely being logged. OFF by
    /// default, like every roadmap-14-phase-2 action gate: the classifier's own
    /// trans_pending-based distinction (<c>KotorCoop.Plugin.WorldLoadRule</c>) is INFERRED, not
    /// live-verified, and the rejoin action itself
    /// (<c>SessionManager.RejoinAfterLoadAsync</c>/<c>KotorGameBridge.TryRequestAdoptedWorldLoad</c>)
    /// reuses <c>MenuLoadController</c>'s main-menu-only replay with no measurement of whether
    /// it actually fires while a live session was already running. Live-verified 2026-08-24 on a
    /// two-instance rig: firing <c>loadgame</c> on the client produces the correct rejoin
    /// sequence and converges cleanly on the host's module with the character intact, with a
    /// clean regression check confirmed with the gate off — see docs/research/session-save.md's
    /// round 87/88 entries. ON by default as of that result. <c>client_load_rejoin = 0</c> turns
    /// it back off.
    /// </summary>
    public bool ClientLoadRejoin { get; init; } = true;

    /// <summary>
    /// Roadmap item 14 phase 2: whether a HOST's own load verdict, instead of the (already
    /// flag-gated) <see cref="SessionSaveHostRefusal"/> disconnect, announces the reload and
    /// has every connected CLIENT run the SAME rejoin <see cref="ClientLoadRejoin"/> gates —
    /// design doc §3.2's "reuse the join path, do not build a new one", run from the host's
    /// side. Takes priority over <see cref="SessionSaveHostRefusal"/> when both are on (a
    /// working re-adoption is strictly better than a refusal; the refusal stays available as
    /// the safe fallback if re-adoption itself is ever turned back off). Live-verified
    /// 2026-08-24 alongside <see cref="ClientLoadRejoin"/>: firing <c>loadgame</c> on the host
    /// produces "host loaded a save - everyone re-joins" and the client's world-transfer fetch
    /// succeeds cleanly — see docs/research/session-save.md's round 87/88 entries. ON by default
    /// as of that result. <c>host_load_readopt = 0</c> turns it back off.
    /// </summary>
    public bool HostLoadReadopt { get; init; } = true;

    /// <summary>
    /// Whether a follower acts on a host's announced transition destination immediately,
    /// instead of waiting for the host's published module to change and the module-settle
    /// debounce to elapse — docs/research/transition-smoothness.md. ON by default:
    /// live-verified 2026-08-21 (Interval A dropped from ~1.6 s to ~180 ms on a bare
    /// end_m01aa -> end_m01ab door crossing, and the movie-carrying case was also exercised
    /// with no regression). Set <c>transition_announce = 0</c> to opt out.
    /// </summary>
    public bool TransitionAnnounceEnabled { get; init; }

    /// <summary>
    /// Whether a client's own engine-native door crossing is refused early (before the load
    /// starts) while following the host — docs/research/transition-smoothness.md's client-
    /// first-door case. ON by default (an absent key enables it; <c>client_door_refusal = 0</c>
    /// disables). The decisive live check (§6.3) has not yet been exercised, because a client's
    /// own <c>CSWParty</c> hits an unrelated party-together gate first — see the doc's live-pass
    /// section.
    /// </summary>
    public bool ClientDoorRefusalEnabled { get; init; }

    /// <summary>
    /// Who may lead an area transition that the OTHER side follows immediately via the announce
    /// channel — docs/research/transition-smoothness.md's 2026-08-22 "Lead-swap" entry. coop.ini
    /// <c>follow_lead</c>: <c>any</c> (the default) lets either player's committed door crossing
    /// become an announce the other follows, mirroring the pre-existing host-leads path
    /// symmetrically, gated by <see cref="KotorCoop.Session.LeadFollowRule"/> so the host is
    /// never pulled out of its own transition, a conversation, or a real fight. <c>host</c>
    /// restores exactly today's behaviour: only the host is ever followed, and
    /// <see cref="ClientDoorRefusalEnabled"/> refuses every premature client crossing as before.
    /// </summary>
    public FollowLeadMode FollowLead { get; init; } = FollowLeadMode.Any;

    /// <summary>
    /// Relays a mid-module <c>PlayMovie</c> call (no transition involved) to peers, and acts
    /// on one a peer announces. Off by default, live-verification pending — the same posture
    /// <c>PeerEffect</c> and <c>CreatureLiveness</c> shipped with. coop.ini <c>movie_relay</c>.
    /// See docs/research/trigger-relay.md §6 and docs/research/cinematics.md's 2026-08-22
    /// section.
    /// </summary>
    public bool MovieRelayEnabled { get; init; }

    /// <summary>The anchor as the plugin's numeric code (0=TL, 1=TR, 2=BL, 3=BR).</summary>
    public uint ChatOverlayAnchorCode => ChatOverlayAnchor.Trim().ToLowerInvariant() switch
    {
        "top-right" or "topright" or "tr" => 1,
        "bottom-left" or "bottomleft" or "bl" => 2,
        "bottom-right" or "bottomright" or "br" => 3,
        _ => 0,
    };

    /// <summary>The client endpoint implied by <see cref="HostAddress"/> and
    /// <see cref="Port"/>, or null if the address does not resolve.</summary>
    public IPEndPoint? ResolveEndPoint()
    {
        if (IPAddress.TryParse(HostAddress, out IPAddress? ip))
        {
            return new IPEndPoint(ip, Port);
        }

        try
        {
            IPAddress[] resolved = Dns.GetHostAddresses(HostAddress);
            return resolved.Length > 0 ? new IPEndPoint(resolved[0], Port) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The conventional path: coop.ini in the game directory.</summary>
    public static string PathFor(string gameDirectory) => Path.Combine(gameDirectory, "coop.ini");

    /// <summary>Reads coop.ini if present. Returns null when the file does not exist, so the
    /// caller can fall back to command-line options.</summary>
    public static CoopConfig? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        Dictionary<string, string> kv = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';' or '[')
            {
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            // Strip an inline comment, but not one inside a value that has no comment char.
            string value = line[(eq + 1)..].Trim();
            int comment = value.IndexOfAny([';', '#']);
            if (comment >= 0)
            {
                value = value[..comment].Trim();
            }

            kv[line[..eq].Trim()] = value;
        }

        bool host = kv.TryGetValue("mode", out string? mode)
            && mode.Equals("host", StringComparison.OrdinalIgnoreCase);

        return new CoopConfig
        {
            Host = host,
            HostAddress = kv.GetValueOrDefault("host", "127.0.0.1"),
            HasHostAddress = kv.TryGetValue("host", out string? hostValue) && hostValue.Length > 0,
            Port = int.TryParse(kv.GetValueOrDefault("port"), out int port) ? port : ProtocolConstants.DefaultPort,
            Password = kv.GetValueOrDefault("password", string.Empty),
            Name = kv.GetValueOrDefault("name"),
            ServerName = kv.GetValueOrDefault("server_name"),
            MenuButton = !kv.ContainsKey("menu_button") || IsTruthy(kv.GetValueOrDefault("menu_button")),
            TickRate = int.TryParse(kv.GetValueOrDefault("tick_rate"), out int tick)
                ? Math.Clamp(tick, 5, 120)
                : 0,
            InterpolationDelayMs = int.TryParse(kv.GetValueOrDefault("interp_ms"), out int interp)
                ? Math.Clamp(interp, 0, 500)
                : -1,
            PauseSync = !kv.ContainsKey("pause_sync") || IsTruthy(kv.GetValueOrDefault("pause_sync")),
            HidePeersInMinigame = !kv.ContainsKey("minigame_hide_peer")
                || IsTruthy(kv.GetValueOrDefault("minigame_hide_peer")),
            SpectateMinigame = IsTruthy(kv.GetValueOrDefault("minigame_spectate")),
            ChatOverlay = !kv.ContainsKey("chat_overlay") || IsTruthy(kv.GetValueOrDefault("chat_overlay")),
            ChatOverlayOpacity = int.TryParse(kv.GetValueOrDefault("chat_overlay_opacity"), out int op)
                ? Math.Clamp(op, 0, 100)
                : 75,
            ChatOverlayAnchor = kv.GetValueOrDefault("chat_overlay_anchor", "bottom-left"),
            ChatOverlayLines = int.TryParse(kv.GetValueOrDefault("chat_overlay_lines"), out int cl)
                ? Math.Clamp(cl, 1, 12)
                : 5,
            ChatOverlayFadeMs = int.TryParse(kv.GetValueOrDefault("chat_overlay_fade_ms"), out int fm)
                ? Math.Max(fm, 0)
                : 9000,
            ConnectTimeoutMs = int.TryParse(kv.GetValueOrDefault("connect_timeout_ms"), out int ct)
                ? Math.Max(ct, 0)
                : 0,
            StatusToasts = !kv.ContainsKey("status_toasts") || IsTruthy(kv.GetValueOrDefault("status_toasts")),
            DialogueItemSync = !kv.ContainsKey("dialogue_item_sync")
                || IsTruthy(kv.GetValueOrDefault("dialogue_item_sync")),
            DialogueFastJoin = !kv.ContainsKey("dialogue_fast_join")
                || IsTruthy(kv.GetValueOrDefault("dialogue_fast_join")),
            PartyFastPump = !kv.ContainsKey("party_fast_pump")
                || IsTruthy(kv.GetValueOrDefault("party_fast_pump")),
            ContainerFastPump = IsTruthy(kv.GetValueOrDefault("container_fast_pump")),
            SessionSaveHostRefusal = IsTruthy(kv.GetValueOrDefault("session_save_host_refusal")),
            ClientLoadRejoin = !kv.ContainsKey("client_load_rejoin")
                || IsTruthy(kv.GetValueOrDefault("client_load_rejoin")),
            HostLoadReadopt = !kv.ContainsKey("host_load_readopt")
                || IsTruthy(kv.GetValueOrDefault("host_load_readopt")),
            TransitionAnnounceEnabled = !kv.ContainsKey("transition_announce")
                || IsTruthy(kv.GetValueOrDefault("transition_announce")),
            ClientDoorRefusalEnabled = !kv.ContainsKey("client_door_refusal")
                || IsTruthy(kv.GetValueOrDefault("client_door_refusal")),
            FollowLead = kv.GetValueOrDefault("follow_lead", "any")
                .Trim().Equals("host", StringComparison.OrdinalIgnoreCase)
                ? FollowLeadMode.Host
                : FollowLeadMode.Any,
            MovieRelayEnabled = IsTruthy(kv.GetValueOrDefault("movie_relay")),
        };
    }

    private static bool IsTruthy(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));
}
