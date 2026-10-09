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
///   port = 27140       ; the shipped profiles/coop.ini.example sets this explicitly; the
///                       ; code's own fallback when the key is absent is
///                       ; ProtocolConstants.DefaultPort (27015) — see docs/CONFIG.md footnote 8
///   password = swordfish
///   name = Malak
///   join_mode = auto         ; auto (default): the first join goes through the game's own
///                             ; character creation and the character it makes is the co-op
///                             ; character on every join after (saves\kotorcoop-character.*);
///                             ; newcharacter = force a fresh one even when one exists;
///                             ; save = carry the PC of this install's own most recent save
///   auto_adopt_world = 1     ; a joiner whose world does not match the host's adopts the
///                             ; host's world automatically instead of playing degraded
///                             ; (default on) - docs/research/world-adoption.md
///   menu_button = 1    ; the in-game Co-op menu button (default on; 0 hides it)
///   minigame_hide_peer = 1   ; park a peer's phantom while they swoop-race/man a turret
///   minigame_spectate = 0    ; watch a racing/turret peer instead (not yet wired)
///   pazaak_detect = 0        ; treat Pazaak as "in a minigame" too (NEEDS-LIVE, default off)
///   transition_announce = 1    ; race a follow ahead of the settle debounce (default ON,
///                               ; live-verified - docs/research/transition-smoothness.md)
///   client_door_refusal = 1    ; refuse a client's own premature door crossing (default ON,
///                               ; live-verified - docs/research/transition-smoothness.md's
///                               ; live-pass section)
///   party_pool_sync = 2        ; sync the shared party item pool: 0 off, 1 the legacy op-log
///                               ; (docs/research/party-pool-sync.md), 2 the host-authoritative
///                               ; redesign (default, live-verified since 2026-08-29) -
///                               ; docs/research/shared-inventory-v2.md
///   store_sync = 2             ; sync merchant store stock: same 0/1/2 modes as party_pool_sync,
///                               ; 2 is the default (live-verified since 2026-08-29) -
///                               ; docs/research/store-sync.md and docs/research/shared-inventory-v2.md
///   solo_segment_spectate = 1  ; the OWNER's fight, the other player spectates during a
///                               ; story-forced solo segment (default ON, live-verified) -
///                               ; docs/research/star-forge-finale.md
///   stun_servers = stun.l.google.com:19302,stun1.l.google.com:19302   ; HOST only: public
///                               ; address discovery (roadmap item 24). This is the default
///                               ; when the key is absent; set `stun_servers = ` (empty) to
///                               ; disable it - see docs/research/network-topology.md's
///                               ; privacy note (contacts a third party).
///   upnp = 1                   ; HOST only: attempt a UPnP router port mapping. Default on,
///                               ; fails soft (0 to disable and forward the port by hand).
///   steam_invites = 1          ; Steam friends-list "Join Game". Default on and INERT
///                               ; without a player-supplied steam_api64.dll, without Steam
///                               ; running, or without owning app 32370 - see
///                               ; docs/research/steam-support.md.
/// </code>
/// </summary>
public sealed record CoopConfig
{
    public bool Host { get; init; }

    /// <summary>
    /// How a JOINER gets a character into the host's world — coop.ini <c>join_mode</c>,
    /// default <see cref="JoinMode.Auto"/>: the first join on an install goes through the
    /// retail New Game / character-creation screens, the character that comes out is kept as
    /// the mod-owned co-op character (<c>saves\kotorcoop-character.*</c>), and every later
    /// join carries THAT into the host's world — never a single-player save's PC.
    /// <see cref="JoinMode.NewCharacter"/> forces a fresh character even when one exists;
    /// <see cref="JoinMode.Save"/> is the pre-2026-09-03 "carry my own most recent save's PC"
    /// path. Ignored when hosting. See <see cref="JoinModeRule"/> and
    /// docs/research/world-adoption.md.
    /// </summary>
    public JoinMode JoinMode { get; init; } = JoinMode.Auto;

    /// <summary>
    /// Whether a joiner whose world does not fingerprint the same as the host's adopts the
    /// host's world automatically — coop.ini <c>auto_adopt_world</c>, ON by default.
    ///
    /// OFF restores the pre-adoption behaviour exactly: the join is still admitted, but
    /// <c>ContentCompatibility.ApplyWorldMismatch</c> turns every object-id-keyed channel
    /// (enemies, doors, placeables, container loot, encounters) off and the two players share
    /// little more than chat and presence. ON is the default because that degraded session is
    /// almost never what anyone wanted, and adoption never touches the joiner's own saves —
    /// only the autosave slot.
    /// </summary>
    public bool AutoAdoptWorld { get; init; } = true;

    /// <summary>
    /// Whether a client whose world-transfer TCP connect fails outright falls back to a
    /// UDP-only path for the same fetch — coop.ini <c>world_transfer_udp_fallback</c>, OFF by
    /// default; live-verified end to end 2026-09-10 across two real machines over a public-
    /// internet tunnel. Only ever tried after a fast, dedicated connect probe finds the TCP
    /// listener genuinely unreachable (not a slow link, not an app-level refusal like a wrong
    /// password — those still surface exactly as before); the ordinary TCP path
    /// (<see cref="KotorCoop.Session.WorldTransfer"/>) is unchanged either way. Found live
    /// 2026-09-10: a UDP-only tunnel/NAT path forwards the co-op session's own UDP traffic fine
    /// but silently drops the separate TCP world-transfer listener, so <see cref="AutoAdoptWorld"/>
    /// could never complete over such a path. See docs/research/network-topology.md's UDP
    /// world-transfer section.
    /// </summary>
    public bool WorldTransferUdpFallback { get; init; }

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
    /// coop.ini <c>dejitter</c>, OFF by default — file received avatar snapshots on a
    /// regularised playback timeline (inter-arrival gaps quantised to the sender's send
    /// period, byte-identical repeats not sampled at all) rather than stamping each one with
    /// the moment it happened to land. See
    /// <see cref="KotorCoop.Protocol.SnapshotTimingRule"/>: the sender's pump timer and its
    /// send timer are two free-running clocks at the same nominal rate, and the beat between
    /// them is what a player sees as a peer moving in bursts. Unit-proven but not yet
    /// live-confirmed — see <see cref="SessionSettings.DejitterEnabled"/> for why the live A/B
    /// could not settle it and what a conclusive round needs.
    /// </summary>
    public bool Dejitter { get; init; }

    /// <summary>
    /// coop.ini <c>perf_trace</c>, OFF by default — once-a-second pump/network/phantom
    /// telemetry into the agent log (tick count, tick cost split by phase, cross-process
    /// read/write calls and bytes, packets and bytes both ways, and the applied per-peer
    /// phantom position delta). A rig-measurement instrument, not a player-facing setting:
    /// it turns on <see cref="KotorCoop.Memory.MemoryTelemetry"/>'s counting and writes a line
    /// a second forever. See <c>PerfTrace</c> and docs/research/lag-audit.md.
    /// </summary>
    public bool PerfTrace { get; init; }

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
    /// While a PEER is inside a minigame, hold this player still with an on-screen
    /// "&lt;name&gt; is racing - waiting", and refuse to follow them onto the single-seat track
    /// module. The swoop race is one bike: the product rule is that the player who starts it
    /// races and everybody else waits, rather than wandering a world the racer's engine has
    /// swapped out from under them (or being warped onto a track they cannot drive — measured
    /// live 2026-08-29, the follow-warp into <c>tar_m03mg</c> was the last thing the following
    /// instance did). On by default. <c>minigame_lock_peers = 0</c> restores the old
    /// free-for-all. See docs/research/minigames.md §24.
    /// </summary>
    public bool LockPeersDuringMinigame { get; init; } = true;

    /// <summary>
    /// Seconds a minigame peer-lock may stand before releasing itself regardless of the
    /// owner's flag — the safety valve for a peer whose agent died mid-race and left its last
    /// published state behind forever. <c>minigame_lock_timeout_s</c>, default 600; 0 disables
    /// the timeout entirely.
    /// </summary>
    public int MinigameLockTimeoutSeconds { get; init; } = 600;

    /// <summary>
    /// Extra module names to treat as single-seat minigame tracks, on top of the four a stock
    /// install ships (<c>MinigameModuleRule.BuiltIn</c>) — for a mod with its own.
    /// <c>minigame_modules</c>, comma-separated, empty by default.
    /// </summary>
    public IReadOnlyList<string> ExtraMinigameModules { get; init; } = [];

    /// <summary>
    /// Hand this instance's camera to a peer while they race or man a turret, the way
    /// <c>SpectateController</c> already does for a downed player. NOT YET WIRED — the
    /// camera handoff needs a trigger distinct from "downed" and is left as the documented
    /// next step (docs/research/minigames.md, item 8c). Read and stored now so the key
    /// exists in coop.ini ahead of that work landing. Off by default.
    /// </summary>
    public bool SpectateMinigame { get; init; }

    /// <summary>
    /// Treat Pazaak (a GUI panel, not <c>CSWCModule::GetMiniGame</c> — docs/research/
    /// minigames.md §4) as "in a minigame" too: a non-null <c>CClientExoAppInternal+0x70</c>
    /// (<c>CPazaakData*</c>) sets <see cref="PlayerState.FlagInMinigame"/> through the exact
    /// same wire path swoop/turret already uses, so a Pazaak game parks the peer's phantom
    /// (or shows the wait notice) the same way a race does. <b>NEEDS-LIVE</b>: the offset is
    /// read from the symbol database and cross-checked against the disassembled
    /// <c>CClientExoApp::GetPazaakIsRunning</c>, but has not been watched flip live in a
    /// running session — off by default until it has. <c>pazaak_detect = 1</c> turns it on.
    /// The plugin reads the identical key in-process (<c>PluginConfig</c>) for its own local
    /// edge log; this copy is what actually makes the WIRE flag true for peers.
    /// </summary>
    public bool PazaakDetect { get; init; }

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

    /// <summary>
    /// Whether a CLIENT that loses its host runs the full plugin-side lock release (control
    /// lock, party-select suppression, follow mirroring, dialogue spectate, minigame lock, ...)
    /// instead of leaving every lock held until a manual disconnect — coop.ini
    /// <c>host_loss_release</c>. ON by default — see <see cref="SessionSettings.HostLossReleaseEnabled"/>
    /// for the full reasoning and docs/research/disconnect-reconnect.md's "host loss" section.
    /// Ignored by a host.
    /// </summary>
    public bool HostLossRelease { get; init; } = true;

    /// <summary>
    /// How long a CLIENT'S host may stay silent before the <see cref="HostLossRelease"/> pass
    /// fires — coop.ini <c>host_loss_timeout_ms</c>. 0 or unset keeps
    /// <see cref="ProtocolConstants.PeerTimeoutMs"/> (5000), the same instant the existing
    /// silence-based reconnect logic already gives up and starts re-Helloing. See
    /// <see cref="SessionSettings.HostLossTimeoutMs"/>.
    /// </summary>
    public int HostLossTimeoutMs { get; init; }

    /// <summary>
    /// How long a CLIENT'S host may sit flagged away (main menu, load screen — still sending)
    /// before that counts as host loss and the <see cref="HostLossRelease"/> pass fires —
    /// coop.ini <c>host_away_timeout_ms</c>. Default <see cref="HostAwayRule.DefaultTimeoutMs"/>
    /// (20 s); 0 disables this one trigger. See <see cref="SessionSettings.HostAwayTimeoutMs"/>.
    /// </summary>
    public int HostAwayTimeoutMs { get; init; } = HostAwayRule.DefaultTimeoutMs;

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
    /// Whether an item a KOTOR script places directly into the local PC's inventory OUTSIDE
    /// any conversation (an OnEnter trigger, an OnDeath script, a cutscene, a heartbeat
    /// script) is diffed and forwarded the same way <see cref="DialogueItemSync"/> already
    /// forwards a reward node's grant — docs/research/dialogue-consequences.md's
    /// script-item-sync section. ON by default since 2026-08-27: all three of the channel's own
    /// required live controls (a genuine script grant via <c>scriptgive</c>, a manual
    /// <c>giveloot</c> against the local player, and — the one flagged "most likely to fail" and
    /// finally exercised via the new <c>loottake</c> primitive — a real container/corpse take)
    /// are now live-confirmed suppressed correctly. Getting there needed a real fix: the
    /// container/corpse-loot control DID double-report on the first live run, because
    /// <c>PumpContainerLoot</c> and this channel's own sample ran on independently-clocked ~1s
    /// timers with no ordering guarantee, so a suppression entry could land after the very
    /// report it was supposed to prevent — see <c>CoopAgent.PumpOnce</c>'s own remarks on why
    /// <c>PumpContainerLoot</c> now runs immediately ahead of the script-item sample every tick.
    /// <c>script_item_sync = 0</c> turns it back off.
    /// </summary>
    public bool ScriptItemSync { get; init; } = true;

    /// <summary>coop.ini <c>inventory_mode</c> (`personal`/`shared`). True ("personal", the
    /// default) means each engine's own party backpack stays its own — the party-pool channel
    /// mirrors only a quest item between peers; false ("shared") is today's behaviour, every
    /// stack mirrors. See docs/research/personal-inventory.md.</summary>
    public bool InventoryPersonal { get; init; } = true;

    /// <summary>coop.ini <c>credits_mode</c> (`personal`/`shared`). True ("personal") means
    /// each side's own gold stays its own, no ledger merge; false ("shared", today's behaviour)
    /// merges into one purse. Absent from coop.ini, this follows <see cref="InventoryPersonal"/>
    /// (see the parse in <see cref="TryLoad"/>) rather than a fixed default of its own, so a
    /// player who only sets <c>inventory_mode = shared</c> does not end up with a half-personal,
    /// half-shared economy by accident.</summary>
    public bool CreditsPersonal { get; init; } = true;

    /// <summary>coop.ini <c>class_grant_sync</c> — relay a script's <c>AddMultiClass</c> (the
    /// Jedi class from Dorak's path choice) to every other player's character. OFF by default,
    /// NEEDS-LIVE; must match the plugin's own <c>PluginConfig.ReadBool("class_grant_sync",
    /// false)</c> fallback. docs/research/class-and-power-grants.md.</summary>
    public bool ClassGrantSync { get; init; } = true;

    /// <summary>coop.ini <c>first_saber_color_sync</c> — give every OTHER player their OWN
    /// class's colour when any one player finishes Zhar's saber-construction reward (which
    /// otherwise reaches nobody else at all — see docs/research/class-and-power-grants.md §11).
    /// ON by default since a live two-player pass on 2026-09-25. Purely local (no plugin key to
    /// keep in sync with — this never touches the native side).</summary>
    public bool FirstSaberColorSync { get; init; } = true;

    /// <summary>coop.ini <c>enemy_effect_sync</c> — relay a non-damage effect a CLIENT casts on a
    /// mirrored enemy (Stasis, Stun, Fear, Whirlwind, Force Push, Slow, Paralysis, Sleep, a
    /// grenade rider) to the host, which replays it on the enemy's real body. OFF by default,
    /// NEEDS-LIVE: the apply half has never been run against an enemy body. Three readers must
    /// agree on that default — this record and its parser, the agent's
    /// <c>AgentComposition</c> fallback and the plugin's <c>PluginConfig.ReadBool
    /// ("enemy_effect_sync", true)</c>. docs/research/enemy-effects.md.</summary>
    public bool EnemyEffectSync { get; init; }

    /// <summary>coop.ini <c>placed_mine_sync</c> — replicate floor mines a party member PLACES
    /// (docs/research/mines.md §14): the host names each with a session serial, every peer builds
    /// a replica through the engine's own construction sequence, and only the host's copy can
    /// detonate on an enemy. OFF by default, NEEDS-LIVE: the replica build is a new engine write.
    /// Three readers must agree on that default — this record and its parser, the agent's
    /// <c>AgentComposition</c> fallback and the plugin's <c>PluginConfig.ReadBool
    /// ("placed_mine_sync", false)</c>. Both machines must set it.</summary>
    public bool PlacedMineSync { get; init; }

    /// <summary>
    /// coop.ini <c>menu_guard_sync</c>: while a player has a full-screen menu (inventory, map,
    /// character sheet, journal, equipment, options) open, their published <c>PlayerState</c>
    /// carries <c>PlayerState.FlagMenuGuard</c> for a bounded window
    /// (<see cref="MenuGuardCapMs"/>/<see cref="MenuGuardCooldownMs"/>, <see cref="MenuGuardRule"/>),
    /// and a peer's plugin holds their stand-in out of local target selection and drops any hit
    /// that still lands on it — the parity fix for docs/limitations.md's "Pausing" row: opening
    /// a menu freezes combat in single-player KOTOR, but only pauses the OPENER's own game here,
    /// so their stand-in kept eating hits on every other screen. ON by default: it reuses the
    /// same faction-flip + PacifyCreature lever <c>stealth_sync</c> already uses (no new engine
    /// write), and the bounded window means it can never become a standing shield. Read on BOTH
    /// sides — the opener's own agent (whether to run the timer and publish the bit at all) and
    /// a peer's plugin (whether to honour a bit it receives, <c>PluginConfig.ReadBool
    /// ("menu_guard_sync", true)</c>) — the same "one coop.ini key, two files" pattern
    /// <c>solo_segment_spectate</c>/<c>minigame_hide_peer</c> use. See
    /// docs/research/effect-sync.md §13.
    /// </summary>
    public bool MenuGuardSyncEnabled { get; init; } = true;

    /// <summary>
    /// How long a single menu opening stays protected before the shield lapses even if the menu
    /// is still open — coop.ini <c>menu_guard_cap_ms</c>, default
    /// <see cref="MenuGuardRule.DefaultCapMs"/> (12 s, inside the 10-15 s window the feature was
    /// scoped to). Stops a player parking on the inventory screen for permanent invulnerability.
    /// Agent-side only (the plugin never reads this — it only reacts to the wire bit).
    /// </summary>
    public int MenuGuardCapMs { get; init; } = MenuGuardRule.DefaultCapMs;

    /// <summary>
    /// How long AFTER a menu opening burns through <see cref="MenuGuardCapMs"/> before the next
    /// opening can be protected again — coop.ini <c>menu_guard_cooldown_ms</c>, default
    /// <see cref="MenuGuardRule.DefaultCooldownMs"/> (20 s). Closing and immediately reopening
    /// the menu does not reset this: <see cref="MenuGuardRule.TrackCooldown"/> keys off the cap
    /// being exhausted, not off the menu closing, so a capped-out opening cannot be renewed by
    /// tapping the menu key. A short check well under the cap never triggers a cooldown at all.
    /// </summary>
    public int MenuGuardCooldownMs { get; init; } = MenuGuardRule.DefaultCooldownMs;

    /// <summary>
    /// Whether the dialogue channel is pumped on the agent's real-time (per-poll-tick) path
    /// instead of the ~150 ms combat cadence, and whether an unchanged read is deduped against
    /// a resend rather than republished on a fixed timer — docs/research/dialogue-join-latency.md
    /// has the measured before/after. ON by default: the 150 ms cadence was found to genuinely
    /// DROP content on a rapid-skip burst (only the first and last node arrived; intermediate
    /// lines never reached the follower), while the fast path caught every node with zero
    /// coalescing — see docs/research/dialogue-skip-desync.md's live A/B.
    /// <c>dialogue_fast_join = 0</c> reverts to the old cadence.
    /// </summary>
    public bool DialogueFastJoin { get; init; } = true;

    /// <summary>
    /// Whether the party channel (<c>PumpParty</c>/<c>PumpPartyRoster</c>) is pumped on the
    /// agent's real-time (per-poll-tick) path instead of the ~150 ms combat cadence — the same
    /// move <see cref="DialogueFastJoin"/> made, for the same reason: two independently-
    /// configured 150 ms windows (this pump, then <c>PartyPresenceController</c>'s own
    /// <c>mirror_smooth_ms</c> smoother) stack back to back on the companion-mirror path,
    /// docs/research/lag-audit.md's suspect #2 for it reading "laggier" than a peer's own
    /// avatar. Earlier A/Bs were confounded by the companion follow-AI stall bug of the time
    /// (a companion that never moves has no position lag to reduce); once that bug was fixed, a
    /// live A/B against a companion genuinely walking/following measured a real, reproduced
    /// ~45-55% cut in companion-position lag with no frame-time regression — see
    /// docs/research/lag-audit.md. ON by default since that result. <c>party_fast_pump = 0</c>
    /// reverts to the old cadence.
    /// </summary>
    public bool PartyFastPump { get; init; } = true;

    /// <summary>
    /// Whether <c>PumpPlaceables</c> (the DURABLE container/door open-or-locked STATE — what
    /// keeps a closed container looking closed rather than getting reasserted open, see the
    /// <c>ContainerCloseHook</c> fix in docs/research/container-sync.md) is pumped on the
    /// agent's real-time (per-poll-tick) path instead of the ~1000 ms heavy-world-state cadence.
    /// <c>PumpInteractionLocks</c> (the open/close ANIMATION and the use-lock itself) already
    /// rides the ~150 ms combat cadence, so before this flag a close could take 1-2+ seconds end
    /// to end: the animation and the lock released quickly, but the state that actually keeps
    /// the container shut for a peer lagged a full heavy-pass cycle behind it — live-confirmed,
    /// matching the "very big delay... closing biggest" report; see
    /// docs/research/container-sync.md. Same rollout shape as
    /// <see cref="DialogueFastJoin"/>/<see cref="PartyFastPump"/>: OFF by default so a live A/B
    /// pass can compare it against the existing cadence. <c>container_fast_pump = 1</c> turns it
    /// on. Door/placeable state for an object nobody has recently touched is unaffected either
    /// way — <c>CollectChangedPlaceableState</c> only ever reports a real change, so running it
    /// more often costs nothing beyond the extra poll when nothing changed.
    /// </summary>
    public bool ContainerFastPump { get; init; }

    /// <summary>
    /// Floor-mine / trap-trigger sync (coop.ini <c>mine_sync</c>): a GIT-placed mine that one
    /// instance disarms, recovers or detonates is removed on the peer through the engine's own
    /// DestroyObject. ON by default since 2026-09-19 (run live both ways: a walk-on detonation and a
    /// host destroy mirrored, and four warps produced no false removal; docs/research/mines.md 9).
    /// The publish half infers a removal from a trigger going absent. Gates BOTH halves on this instance
    /// (agent publish, plugin apply); the three default readers agree on ON (PluginConfig
    /// fallback, this record's absent-key parse, AgentComposition's <c>?? true</c>).
    /// </summary>
    public bool MineSync { get; init; } = true;

    /// <summary>
    /// Per-object local-variable replication (coop.ini <c>local_var_sync</c>): an allowlisted
    /// SetLocalBoolean/SetLocalNumber on a static GIT object (docs/research/quest-sync.md §8.2)
    /// is captured by the plugin, carried on the reliable flag channel, and applied to the
    /// peer's copy of the object through the engine's own setter. ON by default after
    /// stock VM capture/apply, snapshot/world-edge and combat exclusion checks;
    /// an explicit false disables both the agent pump and plugin capture/apply.
    /// </summary>
    public bool LocalVarSync { get; init; } = true;

    /// <summary>
    /// Whether the agent's <c>SessionSaveWatch</c>'s host-side edge (roadmap item
    /// 14 phase 1, design §3.2 item 5) actually tears the session down when it fires. OFF by
    /// default — live-verified (docs/research/shared-state-audit.md §2) to false-positive
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
    /// it actually fires while a live session was already running. Live-verified on a
    /// two-instance rig: firing <c>loadgame</c> on the client produces the correct rejoin
    /// sequence and converges cleanly on the host's module with the character intact, with a
    /// clean regression check confirmed with the gate off — see docs/research/session-save.md.
    /// ON by default as of that result. <c>client_load_rejoin = 0</c> turns it back off.
    /// </summary>
    public bool ClientLoadRejoin { get; init; } = true;

    /// <summary>
    /// Roadmap item 14 phase 2: whether a HOST's own load verdict, instead of the (already
    /// flag-gated) <see cref="SessionSaveHostRefusal"/> disconnect, announces the reload and
    /// has every connected CLIENT run the SAME rejoin <see cref="ClientLoadRejoin"/> gates —
    /// design doc §3.2's "reuse the join path, do not build a new one", run from the host's
    /// side. Takes priority over <see cref="SessionSaveHostRefusal"/> when both are on (a
    /// working re-adoption is strictly better than a refusal; the refusal stays available as
    /// the safe fallback if re-adoption itself is ever turned back off). Live-verified alongside
    /// <see cref="ClientLoadRejoin"/>: firing <c>loadgame</c> on the host produces "host loaded a
    /// save - everyone re-joins" and the client's world-transfer fetch succeeds cleanly — see
    /// docs/research/session-save.md. ON by default as of that result.
    /// <c>host_load_readopt = 0</c> turns it back off.
    /// </summary>
    public bool HostLoadReadopt { get; init; } = true;

    /// <summary>
    /// coop.ini <c>character_capture_interval_min</c> (client only, 2026-09-18): how often, in
    /// minutes, a joined client asks its own engine for a quicksave and captures its co-op
    /// character into the sidecar — only while not in combat, a conversation or a minigame
    /// (<see cref="CharacterCaptureCadenceRule"/>). Until this the character was captured only
    /// at door autosaves and on a clean disconnect, so a crash lost everything since the last
    /// door (docs/review-seamless-2026-09-18.md §2). Default 10; 0 turns the cadence off (the
    /// pre-disconnect quicksave stays). Clamped to a day.
    /// </summary>
    public int CharacterCaptureIntervalMin { get; init; } = 3;

    /// <summary>
    /// Whether a follower acts on a host's announced transition destination immediately,
    /// instead of waiting for the host's published module to change and the module-settle
    /// debounce to elapse — docs/research/transition-smoothness.md. ON by default:
    /// live-verified (Interval A dropped from ~1.6 s to ~180 ms on a bare end_m01aa ->
    /// end_m01ab door crossing, and the movie-carrying case was also exercised with no
    /// regression). Set <c>transition_announce = 0</c> to opt out.
    /// </summary>
    public bool TransitionAnnounceEnabled { get; init; } = true;

    /// <summary>
    /// Whether a client's own engine-native door crossing is refused early (before the load
    /// starts) while following the host — docs/research/transition-smoothness.md's client-
    /// first-door case. ON by default (an absent key enables it; <c>client_door_refusal = 0</c>
    /// disables). The decisive live check (§6.3) has not yet been exercised, because a client's
    /// own <c>CSWParty</c> hits an unrelated party-together gate first — see the doc's live-pass
    /// section.
    /// </summary>
    public bool ClientDoorRefusalEnabled { get; init; } = true;

    /// <summary>
    /// Whether a leader's announced transition INTO a <c>STUNT_*</c> cutscene module is replayed
    /// on this engine (the same scripted leg, run natively, so this player sees the vision /
    /// romance payoff / credits chain too) rather than refused —
    /// docs/research/cinematics.md's 2026-09-18 section. ON by default (an absent key enables
    /// it; <c>follow_stunt = 0</c> restores the pre-2026-09-18 refusal). Agent-only, like
    /// <c>follow_lead</c>: the plugin has no decision to make (the replay is the ordinary travel
    /// request), so there is no plugin reader to keep in step. NEEDS-LIVE.
    /// </summary>
    public bool FollowStunt { get; init; } = true;

    /// <summary>
    /// Who may lead an area transition that the OTHER side follows immediately via the announce
    /// channel — docs/research/transition-smoothness.md's "Lead-swap" entry. coop.ini
    /// <c>follow_lead</c>: <c>any</c> (the default) lets either player's committed door crossing
    /// become an announce the other follows, mirroring the pre-existing host-leads path
    /// symmetrically, gated by <see cref="KotorCoop.Session.LeadFollowRule"/> so the host is
    /// never pulled out of its own transition, a conversation, or a real fight. <c>host</c>
    /// restores exactly today's behaviour: only the host is ever followed, and
    /// <see cref="ClientDoorRefusalEnabled"/> refuses every premature client crossing as before.
    /// </summary>
    public FollowLeadMode FollowLead { get; init; } = FollowLeadMode.Any;

    // ── Player-count scaling (docs/research/player-count-scaling.md) ─────────────────────
    // Five SETTINGS, each PlayerCountScalingRule.Auto (0, "resolve from the live player
    // count") or a fixed percent clamped 50-400. Health defaults to vanilla 100% because
    // saved scaled maxima are not yet idempotent across load; `auto` remains an explicit opt-in.
    // Host-authoritative on the wire
    // (BehaviourDigest); resolved to a percent by CoopAgent's PlayerCountScaling every tick.
    // Defaults must agree across the three readers (docs/CONFIG.md footnote ³): this record,
    // TryLoad's parse below, AgentComposition.Compose's `?? fallback`, and the shipped
    // profiles/coop.ini.example.

    /// <summary>coop.ini <c>enemy_health</c>: enemy hit points, percent of vanilla, or
    /// <c>auto</c>. Default <c>auto</c>: the max-hit-point write and its save/load baseline
    /// (docs/research/enemy-health-persistence.md) are live-verified.</summary>
    public int EnemyHealth { get; init; } = PlayerCountScalingRule.Auto;

    /// <summary>coop.ini <c>enemy_damage</c>: damage enemies deal to players, percent of
    /// vanilla, or <c>auto</c>. Default <c>auto</c>; the in-hook rewrite was live-verified with
    /// the two-player 115% curve.</summary>
    public int EnemyDamage { get; init; } = PlayerCountScalingRule.Auto;

    /// <summary>coop.ini <c>xp_rate</c>: the host's own experience gains, percent of vanilla,
    /// or <c>auto</c> (the auto curve is 100 at every player count). Default auto.</summary>
    public int XpRate { get; init; } = PlayerCountScalingRule.Auto;

    /// <summary>coop.ini <c>store_stock</c>: finite shop stock, percent of vanilla, or
    /// <c>auto</c>. Default auto.</summary>
    public int StoreStock { get; init; } = PlayerCountScalingRule.Auto;

    /// <summary>coop.ini <c>store_prices</c>: buy and sell prices as a percent of the retail
    /// result; default 100 preserves vanilla. <c>auto</c> currently resolves to neutral 100.</summary>
    public int StorePrices { get; init; } = PlayerCountScalingRule.Vanilla;

    /// <summary>
    /// Relays a mid-module <c>PlayMovie</c> call (no transition involved) to peers, and acts
    /// on one a peer announces. ON by default (an absent key enables it; <c>movie_relay = 0</c>
    /// disables) — matching the plugin's own copy of this flag
    /// (<see cref="KotorCoop.Plugin.MidModuleMovieController"/>), live-verified end to end for
    /// both the play-now and defer paths. coop.ini <c>movie_relay</c>. See
    /// docs/research/trigger-relay.md §6 and docs/research/cinematics.md.
    /// </summary>
    public bool MovieRelayEnabled { get; init; } = true;

    /// <summary>
    /// coop.ini <c>stun_servers</c> (roadmap item 24, "public address discovery"): comma-
    /// separated STUN servers (<c>host:port</c>) a HOST asks for its own public IP:port, so the
    /// status line and <c>public_address</c> can show an internet-reachable address to share.
    /// Defaults to <see cref="PublicAddressDiscovery.DefaultServers"/> when the key is ABSENT;
    /// an explicitly EMPTY value (<c>stun_servers = </c>) is what actually disables discovery —
    /// see <see cref="PublicAddressDiscovery.ParseServers"/>, which turns either form of
    /// "nothing configured" into an empty list, and this class's own <see cref="TryLoad"/>,
    /// which is the only place that can tell "absent" apart from "present and blank". Ignored
    /// by a client. ⚠ This contacts a third party — see docs/research/network-topology.md's
    /// 2026-08-27 "Internet play" section for the privacy note.
    /// </summary>
    public string StunServers { get; init; } = PublicAddressDiscovery.DefaultServers;

    /// <summary>
    /// coop.ini <c>upnp</c> (roadmap item 24): whether a HOST attempts a UPnP IGD port mapping
    /// for its session port. ON by default; fails soft — a router with UPnP off or absent just
    /// gets "forward the port yourself" in the status line, not an error. Ignored by a client.
    /// </summary>
    public bool Upnp { get; init; } = true;

    /// <summary>
    /// coop.ini <c>steam_invites</c>: whether the agent offers Steam's own friends-list
    /// "Join Game" — a host publishes a <c>connect</c> rich-presence string, and an incoming
    /// join request is routed onto the ordinary join path.
    ///
    /// ON by default and INERT unless three separate things are true: the player supplied a
    /// <c>steam_api64.dll</c> (this project cannot redistribute Valve's), Steam is running,
    /// and the account owns app 32370. A GOG install satisfies none of them, gets one log
    /// line, and behaves exactly as it did before this key existed — which is why ON by
    /// default is safe. Set to 0 to stop the agent from talking to Steam at all, including
    /// the "you are playing KOTOR" presence that owning the app implies. See
    /// docs/research/steam-support.md §12.
    /// </summary>
    public bool SteamInvites { get; init; } = true;

    // ── Digest-only keys ────────────────────────────────────────────────────────────────
    // Everything below is a PLUGIN-side key (docs/CONFIG.md's "Plugin-side keys" table,
    // PluginConfig.cs + per-controller reads) that this agent-side reader never acted on
    // before now. They are parsed here ONLY so AgentComposition can fold them into this
    // install's outgoing BehaviourDigest (docs/research/mod-compatibility.md,
    // "Behaviour-flag negotiation") — coop.ini is one shared file both readers already parse
    // independently, and CoopConfig is the one component that builds Hello/Welcome, so it is
    // the natural (if slightly unusual) place to also read a key it does not otherwise
    // consume. None of these fields feed any pump or write in KotorCoop.Session; PluginConfig
    // remains the sole consumer that actually acts on them locally.

    /// <summary>coop.ini <c>local_combat</c> (plugin key; read here only for negotiation —
    /// see "Digest-only keys" above). Default ON, matching the plugin's own fallback.</summary>
    public bool LocalCombat { get; init; } = true;

    /// <summary>coop.ini <c>companion_local_combat</c> (plugin key; digest-only here).
    /// Default OFF, matching the plugin's own code fallback (the shipped coop.ini.example
    /// sets it to 1 — see docs/CONFIG.md footnote 3).</summary>
    public bool CompanionLocalCombat { get; init; }

    /// <summary>coop.ini <c>effect_sync</c> (plugin key; digest-only here). Default ON.</summary>
    public bool EffectSync { get; init; } = true;

    /// <summary>coop.ini <c>trigger_relay</c> (plugin key; digest-only here). Default ON.</summary>
    public bool TriggerRelay { get; init; } = true;

    /// <summary>coop.ini <c>client_encounters</c> (plugin key; digest-only here). Default ON.</summary>
    public bool ClientEncounters { get; init; } = true;

    /// <summary>coop.ini <c>dialogue_recruit_sync</c> (plugin key; digest-only here). Default
    /// ON since 2026-09-18, matching the plugin's own code fallback
    /// (<c>PartyRosterController</c>). ⚠ These three digest-only defaults MUST match the plugin's
    /// <c>PluginConfig.ReadBool</c> fallbacks: the digest is what the host-authoritative override
    /// channel compares, so a digest that says OFF while the plugin runs ON (or the reverse)
    /// either hides a real divergence from <c>W_CFG</c> or overrides a client to the wrong
    /// value (docs/review-seamless-2026-09-18.md, blocker #3).</summary>
    public bool DialogueRecruitSync { get; init; } = true;

    /// <summary>coop.ini <c>liveness_sync</c> (plugin key; digest-only here). Default ON since
    /// 2026-09-18 (was OFF here while the plugin doc said ON — see above).</summary>
    public bool LivenessSync { get; init; } = true;

    /// <summary>coop.ini <c>map_reveal_sync</c> (plugin key; digest-only here). Default ON,
    /// matching <c>MapRevealController</c> (the digest said OFF until 2026-09-18).</summary>
    public bool MapRevealSync { get; init; } = true;

    /// <summary>
    /// Whether each active companion's experience is shared like the PC's own —
    /// see <see cref="KotorCoop.Session.AgentSettings.SyncCompanionXp"/>. Live-verified
    /// 2026-08-27, default ON (docs/research/companion-state-sync.md §6). <c>companion_xp_sync
    /// = 0</c> turns it off.
    /// </summary>
    public bool CompanionXpSync { get; init; } = true;

    /// <summary>
    /// Whether a client dresses its own copy of a host-driven companion in that companion's
    /// real equipped gear — see
    /// <see cref="KotorCoop.Session.AgentSettings.SyncCompanionGear"/>. Live-verified
    /// 2026-08-28, default ON (docs/research/companion-state-sync.md §12: the bit-flag
    /// weapon-slot fix and connect-time/mid-session propagation both confirmed clean, all four
    /// slots, sub-3s latency). <c>companion_gear_sync = 0</c> turns it off.
    /// </summary>
    public bool CompanionGearSync { get; init; } = true;

    /// <summary>
    /// Whether a client's own damage TAKEN by a <c>companion_local_combat</c>-released
    /// companion is forwarded to the host, which spends it as real damage on its own
    /// authoritative body — see
    /// <see cref="KotorCoop.Session.AgentSettings.SyncCompanionDamage"/>. Default ON; inert
    /// unless <c>companion_local_combat</c> (itself OFF by default) has actually released a
    /// companion. <c>companion_damage_sync = 0</c> turns it off. See
    /// docs/research/companion-state-sync.md §13.
    /// </summary>
    public bool CompanionDamageSync { get; init; } = true;

    /// <summary>
    /// Syncs the shared party item pool (<c>CSWPartyTable+0x118</c>) both ways. ON by default
    /// since 2026-08-29 — see <see cref="PartyPoolSyncMode"/>, which carries the mode this
    /// actually runs in (2, the host-authoritative redesign) and the live evidence for the flip.
    /// <c>party_pool_sync = 0</c> turns it off. See docs/research/party-pool-sync.md and
    /// docs/research/shared-inventory-v2.md.
    /// </summary>
    public bool PartyPoolSyncEnabled { get; init; } = true;

    /// <summary>
    /// <c>party_pool_sync</c> parsed as a small integer rather than a bare bool: <c>0</c> off,
    /// <c>1</c> the v1 op-log above (byte-for-byte unchanged — a bare
    /// truthy value like the historical <c>party_pool_sync = 1</c> still parses to <c>1</c>, so
    /// no shipped config silently changes meaning), <c>2</c> the host-authoritative "absolute
    /// snapshot + client intents" redesign (docs/research/shared-inventory-v2.md). Independent
    /// of <see cref="PartyPoolSyncEnabled"/> above (kept, unchanged, for every existing call
    /// site and test that only ever needed "is this channel on at all"); see
    /// <see cref="AgentSettings.EffectivePartyPoolSyncMode"/> for how the two are reconciled
    /// when only one of them was set.
    ///
    /// <b>2026-08-29 — flipped ON, at mode 2.</b> Live-verified on a two-instance rig, twice:
    /// three rapid host deposits mirrored one-for-one, a client deposit and a withdrawal each
    /// applied exactly once, two five-minute idles with zero publishes and zero engine writes on
    /// either side, and both instances converging on identical item totals. The verification was
    /// deliberately run with <c>dialogue_item_sync</c>/<c>script_item_sync</c> at their own
    /// shipped defaults (ON) rather than isolated, which is what caught the last cross-channel
    /// echo: a party-pool add lands in the PC's own item repository
    /// (docs/research/shared-inventory-v2.md §15.1), so <c>Pumps.ScriptItemGrantPump</c> read our
    /// own pool applies as fresh script grants and re-broadcast them, and the peer materialised a
    /// second copy through giveloot. Closed by <see cref="CoopAgent.NoteLocalPartyPoolAdd"/>.
    /// </summary>
    public int PartyPoolSyncMode { get; init; } = 2;

    /// <summary>
    /// <c>party_pool_trace</c>: rig-only, per-tick diagnostic logging for BOTH mode-2 shared
    /// inventory pumps (<c>Pumps/PartyPoolPumpV2.cs</c> and its store sibling
    /// <c>Pumps/StorePumpV2.cs</c> — one key, since the two share every bookkeeping mechanism and
    /// a failure in one is diagnosed with the other's lines beside it). Prints
    /// actual/belief/snapshot/rawMovement/expected/outstanding/published/target/reconcile per key
    /// on every tick that key has ANY non-trivial state, which is what
    /// docs/research/shared-inventory-v2.md §10's own "concrete next steps" asked for after three
    /// live rounds each had to infer a root cause from timestamps alone. OFF by default: on a
    /// busy pool this is several lines a second, useful on a rig and nowhere else.
    /// </summary>
    public bool PartyPoolTrace { get; init; }

    /// <summary>
    /// Story-forced solo segments (the Star Forge's Bastila and Malak duels —
    /// <see cref="KotorCoop.Protocol.SoloSegmentCatalogue"/>, docs/research/star-forge-finale.md
    /// §3.3/§3.5): while ON, the fighting player's OWN agent hides every peer from what it
    /// writes locally the instant its plugin reports it owns an active segment, and the other
    /// player's plugin parks their input/camera on the fight instead of letting them wander
    /// into a 1-on-1 duel the base game designed for one player. ON by default — live-verified
    /// 2026-08-28 (docs/research/star-forge-finale.md §12): the burst trigger begins a segment,
    /// the owner's roster genuinely empties, the peer's phantom hides, the spectator's input
    /// parks and camera follows, a real refill ends it. coop.ini <c>solo_segment_spectate = 0</c>
    /// turns it off. Shares the SAME key with the plugin's own independent read
    /// (<c>SoloSegmentController</c>/<c>SoloSegmentSpectateController</c>,
    /// <c>PluginConfig.ReadBool</c>), the same "one coop.ini key, two files" pattern
    /// <c>minigame_hide_peer</c>/<c>pazaak_detect</c> already use.
    /// </summary>
    public bool SoloSegmentSpectateEnabled { get; init; }
    /// Syncs merchant store stock both ways. ON by default since 2026-08-29 — see
    /// <see cref="StoreSyncMode"/> for the mode it runs in (2) and the live evidence.
    /// <c>store_sync = 0</c> turns it off. See docs/research/store-sync.md and
    /// docs/research/shared-inventory-v2.md.
    /// </summary>
    public bool StoreSyncEnabled { get; init; } = true;

    /// <summary>
    /// <c>store_sync</c> parsed as a small integer — the store channel's mirror of
    /// <see cref="PartyPoolSyncMode"/>, identical reasoning.
    ///
    /// <b>2026-08-29 — flipped ON, at mode 2</b>, on THREE clean live sessions in a row: each a
    /// client buy, a client sell-back, a host re-buy that succeeded exactly once, a second host
    /// re-buy correctly refused ("no stock matching that resref"), and 155 seconds of idle with
    /// zero engine writes on either side. Every write in all three was accounted for — one per
    /// real transaction, none spurious. The last wobble (docs/research/shared-inventory-v2.md
    /// §15.5: the client re-inserting what it had just bought) was root-caused to the host's own
    /// snapshot regressing under a pre-apply scan and is closed — see
    /// <c>CoopHost._storeIntentEffectV2</c>.
    /// </summary>
    public int StoreSyncMode { get; init; } = 2;

    /// <summary>
    /// DEBUG/TEST-ONLY — <c>debug_hello_version</c>, default 0 (off). Nonzero overrides the
    /// version byte a CLIENT writes into its Hello, and its Hello alone — see
    /// <see cref="SessionSettings.DebugHelloVersionOverride"/> for the full reasoning and
    /// docs/research/disconnect-reconnect.md's 2026-09-02 section for the live recipe this
    /// exists to run. Never documented alongside the ordinary keys above; see docs/CONFIG.md's
    /// "Debug / test-only keys" subsection instead.
    /// </summary>
    public byte DebugHelloVersionOverride { get; init; }

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
            return EndpointAddressRule.PreferIpv4(resolved) is IPAddress address
                ? new IPEndPoint(address, Port) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The conventional path: coop.ini in the game directory.</summary>
    public static string PathFor(string gameDirectory) => Path.Combine(gameDirectory, "coop.ini");

    // ── Player-facing vs. developer-switch classification ──────────────────────────────
    // "Restrict the coop.ini options for users that is unnecessary" (the docs/CONTRIBUTING.md-
    // linked issue this answers): profiles/coop.ini.example only lists keys a PLAYER would
    // ever want to change. Everything else a reader consumes is a developer switch — a tuning
    // knob or bisect lever for the test rig, shipped at its verified value, documented in
    // docs/CONFIG.md's "Developer switches" table rather than the short example. TryLoad logs
    // (once per load) whenever coop.ini actually sets one, so a copy-pasted or hand-added
    // developer key never sits there silently affecting behaviour with nothing pointing at it.

    /// <summary>Every key in the shipped profiles/coop.ini.example — the exact set a player is
    /// meant to see and edit. Kept as a plain array (mirrored in PluginConfig's own copy for
    /// the NativeAOT plugin, which cannot reference this assembly) rather than sourced from the
    /// example file itself, so this compiles standalone; CoopIniExampleDocSyncTests is what
    /// keeps the two from drifting apart.</summary>
    private static readonly string[] PlayerFacingKeys =
    [
        "mode", "host", "port", "password", "name", "server_name", "join_mode",
        "auto_adopt_world", "menu_button", "follow_lead", "dialogue_auto_join",
        "dialogue_auto_join_range", "spectate_on_down", "control_return",
        "inventory_mode", "credits_mode",
        "enemy_health", "enemy_damage", "xp_rate", "store_stock", "store_prices",
        "hide_party_portraits", "status_toasts", "status_indicator", "chat_overlay",
        "chat_overlay_opacity", "chat_overlay_anchor", "chat_overlay_lines",
        "chat_overlay_fade_ms", "steam_invites", "stun_servers", "upnp",
        "world_transfer_udp_fallback", "log_max_mb",
        "log_keep", "auto_start_agent", "dinput8_next", "gamepad", "gamepad_cursor_speed",
    ];

    /// <summary>Every other key either reader (this one, or PluginConfig.Read/ReadBool/ReadInt/
    /// ReadFloat anywhere under src/KotorCoop.Plugin) actually consumes — a developer switch,
    /// not a player setting. See docs/CONFIG.md's "Developer switches" table; a key consumed by
    /// a reader but missing from both this list and PlayerFacingKeys is exactly what
    /// CoopIniExampleDocSyncTests' undocumented-key check exists to catch.</summary>
    private static readonly string[] DeveloperKnownKeys =
    [
        "activate_render_on_start", "bark_replay", "bark_sync", "behaviour_override_reader",
        "character_capture_interval_min", "chat_messages_screen", "class_grant_sync", "client_door_refusal",
        "client_encounters", "client_load_rejoin", "client_temp_death_suppress",
        "companion_damage_sync", "companion_equip_host_only", "companion_gear_sync",
        "companion_local_combat", "companion_local_combat_range", "companion_xp_sync",
        "connect_timeout_ms", "container_fast_pump", "container_lock_arbitration",
        "container_locks", "damage_creator_attribution", "debug_hello_version", "dejitter",
        "delay_probe", "delay_probe_events", "dialog_state_watch", "dialogue_exclusive",
        "dialogue_fast_join", "dialogue_item_sync", "dialogue_join_teleport", "script_destroy_guard", "effect_expiry_fence",
        "dialogue_recruit_sync", "disguise_sync", "effect_sync", "enemy_drive",
        "enemy_effect_sync", "enemy_materialise",
        "follow_stunt", "frame_stats", "galaxy_map_host_only", "gui_probe", "host_load_readopt", "host_spawns_only",
        "host_loss_release", "host_loss_timeout_ms", "interaction_locks", "interp_ms",
        "isolate_real_input",
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
        "party_roster_sync", "party_select_host_only", "party_select_relay", "party_together_fix",
        "pause_button_hook", "pause_sync", "pazaak_detect", "perf_trace", "phantom_targetable",
        "placed_mine_sync",
        "poison_sync", "removeparty_hook", "save_flush", "script_item_sync",
        "session_save_host_refusal", "smooth_ms", "solo_segment_any_module", "solo_segment_modules",
        "solo_segment_spectate", "stealth_partner_visual", "stealth_sync", "stealth_visual_sync", "store_sync",
        "threat_retarget", "tick_rate", "transition_announce",
        "transition_announce_fade", "transition_announce_scripted",
        "transition_close_client_world", "trigger_relay", "trigger_relay_patterns",
    ];

    /// <summary>True for a key in the shipped example — silent, never logged.</summary>
    internal static bool IsPlayerFacingKey(string key) => Contains(PlayerFacingKeys, key);

    /// <summary>True for a key some reader consumes but the shipped example does not list —
    /// logged once as a developer switch. False for a key nobody asks for at all (logged as
    /// unknown instead).</summary>
    internal static bool IsKnownDeveloperKey(string key) => Contains(DeveloperKnownKeys, key);

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

    /// <summary>Splits every key coop.ini actually sets into developer switches (known to some
    /// reader, just not a player setting) and genuinely unknown keys (no reader — agent or
    /// plugin — asks for this at all), skipping player-facing keys entirely. Pure and testable
    /// independent of where the result gets logged.</summary>
    internal static (List<string> DeveloperSwitches, List<string> UnknownKeys) ClassifyNonPlayerKeys(
        IEnumerable<string> presentKeys)
    {
        List<string> developer = [];
        List<string> unknown = [];
        foreach (string key in presentKeys)
        {
            if (IsPlayerFacingKey(key))
            {
                continue;
            }

            (IsKnownDeveloperKey(key) ? developer : unknown).Add(key);
        }

        return (developer, unknown);
    }

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

        LogNonPlayerKeys(kv);

        bool host = kv.TryGetValue("mode", out string? mode)
            && mode.Equals("host", StringComparison.OrdinalIgnoreCase);

        // Tolerant the same way JoinModeRule.Parse is: anything other than the literal
        // "shared" (case-insensitive) - absent, misspelled, "personal" itself - resolves to the
        // new default, personal. credits_mode, left unset, follows whatever inventory_mode just
        // resolved to rather than a fixed default of its own - see CreditsPersonal's own remarks.
        bool inventoryPersonal = !string.Equals(
            kv.GetValueOrDefault("inventory_mode"), "shared", StringComparison.OrdinalIgnoreCase);
        bool creditsPersonal = kv.TryGetValue("credits_mode", out string? creditsModeRaw)
            ? !string.Equals(creditsModeRaw, "shared", StringComparison.OrdinalIgnoreCase)
            : inventoryPersonal;

        return new CoopConfig
        {
            Host = host,

            // Tolerant on purpose (JoinModeRule.Parse): a misspelled value must degrade to
            // the default, never to a mode the player did not ask for.
            JoinMode = JoinModeRule.Parse(kv.GetValueOrDefault("join_mode")),
            AutoAdoptWorld = !kv.ContainsKey("auto_adopt_world")
                || IsTruthy(kv.GetValueOrDefault("auto_adopt_world")),
            WorldTransferUdpFallback = !kv.ContainsKey("world_transfer_udp_fallback") || IsTruthy(kv.GetValueOrDefault("world_transfer_udp_fallback")),
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
            Dejitter = IsTruthy(kv.GetValueOrDefault("dejitter")),
            PerfTrace = IsTruthy(kv.GetValueOrDefault("perf_trace")),
            PauseSync = !kv.ContainsKey("pause_sync") || IsTruthy(kv.GetValueOrDefault("pause_sync")),
            HidePeersInMinigame = !kv.ContainsKey("minigame_hide_peer")
                || IsTruthy(kv.GetValueOrDefault("minigame_hide_peer")),
            LockPeersDuringMinigame = !kv.ContainsKey("minigame_lock_peers")
                || IsTruthy(kv.GetValueOrDefault("minigame_lock_peers")),
            MinigameLockTimeoutSeconds =
                int.TryParse(kv.GetValueOrDefault("minigame_lock_timeout_s"), out int mgTimeout)
                    ? Math.Clamp(mgTimeout, 0, 7200)
                    : 600,
            ExtraMinigameModules =
                [.. (kv.GetValueOrDefault("minigame_modules") ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            SpectateMinigame = IsTruthy(kv.GetValueOrDefault("minigame_spectate")),
            PazaakDetect = !kv.ContainsKey("pazaak_detect") || IsTruthy(kv.GetValueOrDefault("pazaak_detect")),
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
            HostLossRelease = !kv.ContainsKey("host_loss_release")
                || IsTruthy(kv.GetValueOrDefault("host_loss_release")),
            HostLossTimeoutMs = int.TryParse(kv.GetValueOrDefault("host_loss_timeout_ms"), out int hlt)
                ? Math.Max(hlt, 0)
                : 0,
            HostAwayTimeoutMs = int.TryParse(kv.GetValueOrDefault("host_away_timeout_ms"), out int hat)
                ? Math.Max(hat, 0)
                : HostAwayRule.DefaultTimeoutMs,
            StatusToasts = !kv.ContainsKey("status_toasts") || IsTruthy(kv.GetValueOrDefault("status_toasts")),
            DialogueItemSync = !kv.ContainsKey("dialogue_item_sync")
                || IsTruthy(kv.GetValueOrDefault("dialogue_item_sync")),
            ScriptItemSync = !kv.ContainsKey("script_item_sync")
                || IsTruthy(kv.GetValueOrDefault("script_item_sync")),
            InventoryPersonal = inventoryPersonal,
            CreditsPersonal = creditsPersonal,
            ClassGrantSync = !kv.ContainsKey("class_grant_sync") || IsTruthy(kv.GetValueOrDefault("class_grant_sync")),
            FirstSaberColorSync = !kv.ContainsKey("first_saber_color_sync") || IsTruthy(kv.GetValueOrDefault("first_saber_color_sync")),
            DialogueFastJoin = !kv.ContainsKey("dialogue_fast_join")
                || IsTruthy(kv.GetValueOrDefault("dialogue_fast_join")),
            PartyFastPump = !kv.ContainsKey("party_fast_pump")
                || IsTruthy(kv.GetValueOrDefault("party_fast_pump")),
            ContainerFastPump = IsTruthy(kv.GetValueOrDefault("container_fast_pump")),
            MineSync = !kv.ContainsKey("mine_sync") || IsTruthy(kv.GetValueOrDefault("mine_sync")),
            LocalVarSync = !kv.ContainsKey("local_var_sync") || IsTruthy(kv.GetValueOrDefault("local_var_sync")),
            SessionSaveHostRefusal = IsTruthy(kv.GetValueOrDefault("session_save_host_refusal")),
            ClientLoadRejoin = !kv.ContainsKey("client_load_rejoin")
                || IsTruthy(kv.GetValueOrDefault("client_load_rejoin")),
            HostLoadReadopt = !kv.ContainsKey("host_load_readopt")
                || IsTruthy(kv.GetValueOrDefault("host_load_readopt")),
            CharacterCaptureIntervalMin =
                int.TryParse(kv.GetValueOrDefault("character_capture_interval_min"), out int captureMin)
                    ? Math.Clamp(captureMin, 0, 24 * 60)
                    : 3,
            TransitionAnnounceEnabled = !kv.ContainsKey("transition_announce")
                || IsTruthy(kv.GetValueOrDefault("transition_announce")),
            ClientDoorRefusalEnabled = !kv.ContainsKey("client_door_refusal")
                || IsTruthy(kv.GetValueOrDefault("client_door_refusal")),
            FollowStunt = !kv.ContainsKey("follow_stunt")
                || IsTruthy(kv.GetValueOrDefault("follow_stunt")),
            FollowLead = kv.GetValueOrDefault("follow_lead", "any")
                .Trim().Equals("host", StringComparison.OrdinalIgnoreCase)
                ? FollowLeadMode.Host
                : FollowLeadMode.Any,

            // Player-count scaling: "auto", a percent (with or without %), clamped 50-400;
            // absent or unparseable -> the record's own default (see the property group).
            EnemyHealth = PlayerCountScalingRule.ParseSetting(
                kv.GetValueOrDefault("enemy_health"), PlayerCountScalingRule.Auto),
            EnemyDamage = PlayerCountScalingRule.ParseSetting(
                kv.GetValueOrDefault("enemy_damage"), PlayerCountScalingRule.Auto),
            XpRate = PlayerCountScalingRule.ParseSetting(
                kv.GetValueOrDefault("xp_rate"), PlayerCountScalingRule.Auto),
            StoreStock = PlayerCountScalingRule.ParseSetting(
                kv.GetValueOrDefault("store_stock"), PlayerCountScalingRule.Auto),
            StorePrices = PlayerCountScalingRule.ParseSetting(
                kv.GetValueOrDefault("store_prices"), PlayerCountScalingRule.Vanilla),
            MovieRelayEnabled = !kv.ContainsKey("movie_relay") || IsTruthy(kv.GetValueOrDefault("movie_relay")),

            // Absent key -> the default server list; PRESENT-but-empty ("stun_servers = ")
            // -> the empty string, which PublicAddressDiscovery.ParseServers turns into an
            // empty list and therefore disables discovery. See StunServers's own doc comment.
            StunServers = kv.TryGetValue("stun_servers", out string? stunServers)
                ? stunServers
                : PublicAddressDiscovery.DefaultServers,
            Upnp = !kv.ContainsKey("upnp") || IsTruthy(kv.GetValueOrDefault("upnp")),
            SteamInvites = !kv.ContainsKey("steam_invites")
                || IsTruthy(kv.GetValueOrDefault("steam_invites")),

            // Digest-only (see the field group's own doc comment above): plugin-owned keys,
            // parsed here purely so the outgoing BehaviourDigest can compare them.
            LocalCombat = !kv.ContainsKey("local_combat") || IsTruthy(kv.GetValueOrDefault("local_combat")),
            CompanionLocalCombat = IsTruthy(kv.GetValueOrDefault("companion_local_combat")),
            EnemyEffectSync = !kv.ContainsKey("enemy_effect_sync") || IsTruthy(kv.GetValueOrDefault("enemy_effect_sync")),
            PlacedMineSync = IsTruthy(kv.GetValueOrDefault("placed_mine_sync")),
            MenuGuardSyncEnabled = !kv.ContainsKey("menu_guard_sync")
                || IsTruthy(kv.GetValueOrDefault("menu_guard_sync")),
            MenuGuardCapMs = int.TryParse(kv.GetValueOrDefault("menu_guard_cap_ms"), out int mgc)
                ? Math.Max(mgc, 0)
                : MenuGuardRule.DefaultCapMs,
            MenuGuardCooldownMs = int.TryParse(kv.GetValueOrDefault("menu_guard_cooldown_ms"), out int mgd)
                ? Math.Max(mgd, 0)
                : MenuGuardRule.DefaultCooldownMs,
            EffectSync = !kv.ContainsKey("effect_sync") || IsTruthy(kv.GetValueOrDefault("effect_sync")),
            TriggerRelay = !kv.ContainsKey("trigger_relay") || IsTruthy(kv.GetValueOrDefault("trigger_relay")),
            ClientEncounters = !kv.ContainsKey("client_encounters")
                || IsTruthy(kv.GetValueOrDefault("client_encounters")),
            DialogueRecruitSync = !kv.ContainsKey("dialogue_recruit_sync") || IsTruthy(kv.GetValueOrDefault("dialogue_recruit_sync")),
            LivenessSync = !kv.ContainsKey("liveness_sync") || IsTruthy(kv.GetValueOrDefault("liveness_sync")),
            MapRevealSync = !kv.ContainsKey("map_reveal_sync") || IsTruthy(kv.GetValueOrDefault("map_reveal_sync")),

            CompanionXpSync = !kv.ContainsKey("companion_xp_sync")
                || IsTruthy(kv.GetValueOrDefault("companion_xp_sync")),
            CompanionGearSync = !kv.ContainsKey("companion_gear_sync")
                || IsTruthy(kv.GetValueOrDefault("companion_gear_sync")),
            CompanionDamageSync = !kv.ContainsKey("companion_damage_sync")
                || IsTruthy(kv.GetValueOrDefault("companion_damage_sync")),
            // ON by default at mode 2 (2026-08-29) — the ON-by-default idiom, widened to the
            // small-integer parse: an absent key means the shipped default, a present one means
            // exactly what it says, including 0.
            PartyPoolSyncEnabled = !kv.ContainsKey("party_pool_sync")
                || ParseSyncMode(kv.GetValueOrDefault("party_pool_sync")) != 0,
            PartyPoolSyncMode = !kv.ContainsKey("party_pool_sync")
                ? 2
                : ParseSyncMode(kv.GetValueOrDefault("party_pool_sync")),
            PartyPoolTrace = IsTruthy(kv.GetValueOrDefault("party_pool_trace")),
            SoloSegmentSpectateEnabled = !kv.ContainsKey("solo_segment_spectate")
                || IsTruthy(kv.GetValueOrDefault("solo_segment_spectate")),
            StoreSyncEnabled = !kv.ContainsKey("store_sync")
                || ParseSyncMode(kv.GetValueOrDefault("store_sync")) != 0,
            StoreSyncMode = !kv.ContainsKey("store_sync")
                ? 2
                : ParseSyncMode(kv.GetValueOrDefault("store_sync")),

            // DEBUG/TEST-ONLY — see the property's own doc comment. Absent or 0 means off;
            // clamped to a byte since that is all the wire's version field ever was.
            DebugHelloVersionOverride = int.TryParse(kv.GetValueOrDefault("debug_hello_version"), out int hv)
                ? (byte)Math.Clamp(hv, 0, 255)
                : (byte)0,
        };
    }

    /// <summary>Serializes reporting when coop.ini is parsed: developer switches are
    /// retained for F1 and the support log; genuinely unrecognised keys still emit
    /// a deduplicated <see cref="Console.WriteLine"/> warning.</summary>
    private static readonly object ReportGate = new();
    private static string _lastUnknownLine = string.Empty;

    /// <summary>The developer switches the last parsed coop.ini sets, as "key=value" — shown
    /// in the agent window's F1 details instead of inline in the log.</summary>
    public static IReadOnlyList<string> DeveloperSwitches { get; private set; } = [];

    private static void LogNonPlayerKeys(IReadOnlyDictionary<string, string> kv)
    {
        (List<string> developer, List<string> unknown) = ClassifyNonPlayerKeys(kv.Keys);

        // One SHORT line per category, and each only once per process: coop.ini is parsed
        // several times at startup, and the old line listed every key=value inline — a
        // hundred-key wall of text printed three times into the agent window (live
        // 2026-09-26). The full list lives in DeveloperSwitches for the F1 details view.
        // install.ps1 -UpdateDefaults offers to strip them.
        lock (ReportGate)
        {
            DeveloperSwitches = developer.Select(key => $"{key}={kv[key]}").ToList();

            // F1 lists these settings; the agent records them in its support log.
            // Do not print engineering detail before the dashboard starts filtering.

            string unknownLine = unknown.Count == 0
                ? string.Empty
                : $"coop.ini: ignoring {unknown.Count} setting(s) this version does not know: {string.Join(", ", unknown)}";
            if (unknownLine.Length > 0 && unknownLine != _lastUnknownLine)
            {
                Console.WriteLine(unknownLine);
            }

            _lastUnknownLine = unknownLine;
        }
    }

    private static bool IsTruthy(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses <c>party_pool_sync</c>/<c>store_sync</c> as a small integer (0/1/2):
    /// <c>int.TryParse</c> first (clamped to [0, 2] so a stray large number cannot select a mode
    /// that does not exist), falling back to the existing truthy-string parse mapped to
    /// <c>1</c> — so an existing <c>= true</c>/<c>= on</c>/<c>= 1</c> config keeps meaning
    /// exactly what it always meant (v1), and only an explicit <c>= 2</c> opts into
    /// docs/research/shared-inventory-v2.md's redesign.</summary>
    private static int ParseSyncMode(string? value)
    {
        if (int.TryParse(value, out int mode))
        {
            return Math.Clamp(mode, 0, 2);
        }

        return IsTruthy(value) ? 1 : 0;
    }
}
