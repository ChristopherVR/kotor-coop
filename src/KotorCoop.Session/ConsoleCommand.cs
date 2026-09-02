using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>What an in-game console (or dashboard) line asks the session layer to do.</summary>
public enum CoopCommandKind
{
    /// <summary>Blank line or comment; do nothing.</summary>
    None,

    /// <summary>Join a host: <see cref="CoopCommand.Address"/> is the typed host.</summary>
    Connect,

    /// <summary>Begin hosting.</summary>
    Host,

    /// <summary>Tear down the current session.</summary>
    Disconnect,

    /// <summary>Restart the last session with the same parameters.</summary>
    Reconnect,

    /// <summary>Print the current session status.</summary>
    Status,

    /// <summary>Send a chat line to the other players; the text is in <see cref="CoopCommand.Address"/>.</summary>
    Say,

    /// <summary>Wipe the on-screen overlay log.</summary>
    Clear,

    /// <summary>List the co-op servers discovered on the LAN.</summary>
    Servers,

    /// <summary>List the available commands.</summary>
    Help,

    /// <summary>
    /// Answer a password prompt: retry the join that is waiting on one, with the text in
    /// <see cref="CoopCommand.Password"/>. Carries no address — the address is whichever join
    /// asked, which is the whole point of the verb (the player picked a row in the browser and
    /// should not have to retype an IP they never saw).
    /// </summary>
    Password,

    /// <summary>The line was understood as a command but is malformed; see <see cref="CoopCommand.Error"/>.</summary>
    Invalid,

    /// <summary>Not one of ours (some other console command); leave it alone.</summary>
    Unknown,

    /// <summary>Move this instance to another module through the plugin's travel channel;
    /// the module resref is in <see cref="CoopCommand.Address"/> and an optional waypoint
    /// tag rides in <see cref="CoopCommand.Password"/>.</summary>
    Warp,

    /// <summary>List the connected players and which module each is in.</summary>
    Who,

    /// <summary>Print the local player's module and position.</summary>
    Where,

    /// <summary>
    /// Dump the enemy census and the engine's own verdict on every id in it — the diagnostic
    /// for "an id in the census names nothing its own publisher can resolve".
    ///
    /// Answers from whichever side it is typed on: on the fight's OWNER it reports what this
    /// instance is publishing and whether its own object array holds each id; on a mirroring
    /// instance it reports what the owner sent and which of those ids resolve locally.
    /// An optional id in <see cref="CoopCommand.Address"/> narrows it to one creature.
    /// </summary>
    Census,

    /// <summary>
    /// Ask the PLUGIN what this engine knows about one object id — the pointer chain, both
    /// ways of reaching the object array, the engine's own lookup and an in-process
    /// transcription of it, all in one frame on the game thread. The id is in
    /// <see cref="CoopCommand.Address"/>.
    ///
    /// The companion to <see cref="Census"/>, which answers the same question from the
    /// AGENT's side. Running both is how a disagreement between them is localised.
    /// </summary>
    ResolveObject,

    /// <summary>
    /// Peek or fire a creature's +0x260 event script. <see cref="CoopCommand.Address"/> is the
    /// object id; <see cref="CoopCommand.Amount"/> is 0 for a safe read-only peek, nonzero to
    /// actually fire it through the engine's own RunScript dispatcher. See
    /// docs/research/shared-state-audit.md §2, "Strategy 2."
    /// </summary>
    FireScript,

    /// <summary>
    /// TEMPORARY DIAGNOSTIC: snaps a creature to a hardcoded position near a known hostile, so
    /// a genuinely non-census creature (a party member) can be given a valid, in-range target
    /// before firing its own +0x260 script. <see cref="CoopCommand.Address"/> is the object id
    /// to move. See docs/research/shared-state-audit.md §2, round 36. Remove once settled.
    /// </summary>
    MoveNear,

    /// <summary>
    /// <c>spawncreature &lt;template&gt; [count] [x y [z]]</c> (alias <c>spawn</c>) — DEV-ONLY
    /// TEST PRIMITIVE (docs/research/creature-liveness.md's 2026-09-03 checklist item 6; also
    /// used to stage a fight anywhere, e.g. docs/research/peer-damage-ledger.md §9.5). Spawns
    /// <see cref="CoopCommand.Amount"/> hostile creatures (default 1, max 32) from the .utc
    /// template resref in <see cref="CoopCommand.Address"/>, through the engine's own
    /// CreateObject path (<c>Odyssey.SpawnCreature</c> — the same call
    /// <c>MaterialisedEnemies</c>/<c>PhantomController</c> already make for their own stand-ins).
    /// Spread in a small grid next to the LOCAL PLAYER by default (see
    /// <c>KotorCoop.Plugin.SpawnCreatureGridRule</c>), or anchored on an explicit
    /// <see cref="CoopCommand.X"/>/<see cref="CoopCommand.Y"/>/<see cref="CoopCommand.Z"/> when
    /// the line named one — same origin-sentinel convention <see cref="MoveNear"/> already uses.
    /// Faction and every other field comes from the template itself; nothing is forced.
    ///
    /// Local only, and ONLY the local instance spawns: on a CLIENT the spawned creatures are
    /// local-only bodies this instance owns, not the host's, so peer sync will not carry them to
    /// anyone else — the intended use is on the HOST.
    /// </summary>
    SpawnCreature,

    /// <summary>
    /// Inject a transport fault on the live session so a disconnect can be reproduced with
    /// nobody touching a cable. The sub-verb is in <see cref="CoopCommand.Address"/> and its
    /// argument (percentage, milliseconds, seconds, direction) in
    /// <see cref="CoopCommand.Password"/>.
    /// </summary>
    NetSim,

    /// <summary>
    /// Prints what the next live round needs to MEASURE rather than guess about lag: the
    /// phantom interpolation delay actually in use, the last few real send/recv intervals
    /// (vs. the configured tick rate), and the agent's own pump cost (average/peak/budget).
    /// See docs/research/lag-audit.md.
    /// </summary>
    LagStats,

    /// <summary>
    /// Prints this HOST's public IP:port (as STUN found it) and whether UPnP mapped the port,
    /// for sharing with a joiner over the internet — roadmap item 24. A no-op verdict ("not
    /// hosting", "unknown yet") on a client or before discovery has answered; never a session
    /// action. Best-effort clipboard copy on Windows when an address is known.
    /// </summary>
    PublicAddress,

    // ---- staging cheats ---------------------------------------------------------
    //
    // Only the verbs whose ANSWER DEPENDS ON WHO TYPED THEM, plus the two that stage a
    // co-op scenario nothing else reaches. KOTOR's own console already ships giveitem,
    // givecredits, addexp, addlevel, revealmap and heal, all of which touch only the
    // typist's single-player state and are therefore already correct in co-op; duplicating
    // them here would add a second spelling and no correctness. See
    // docs/research/console-cheats.md.

    /// <summary>Toggle or report the local player's damage floor.
    /// <see cref="CoopCommand.Amount"/>: 1 on, 0 off, -1 report.</summary>
    God,

    /// <summary>Set the local player's hit points; <see cref="CoopCommand.Amount"/> 0 = to
    /// maximum.</summary>
    TopUp,

    /// <summary>Stand the local player up out of the co-op downed state.</summary>
    StandUp,

    /// <summary>
    /// DEV-ONLY: real damage through the engine's own path, to yourself or a party-roster
    /// companion — the live-test trigger for the downed/revive pipeline.
    /// <see cref="CoopCommand.Amount"/> is the damage; <see cref="CoopCommand.X"/> is the
    /// party-roster slot (0-8), or negative for "yourself" (the default when none is typed).
    /// See docs/research/death-handling.md's 2026-09-02 section.
    /// </summary>
    Hurt,

    /// <summary>
    /// DEV-ONLY: forces the SAME genuine temp-death state a real lethal hit produces, on
    /// yourself or a party-roster companion. <see cref="CoopCommand.X"/> is the party-roster
    /// slot (0-8), or negative for "yourself". See docs/research/death-handling.md's
    /// 2026-09-02 section.
    /// </summary>
    Down,

    /// <summary>Kill a creature through whichever authority path this instance owns. The
    /// target token (tag or id) is in <see cref="CoopCommand.Address"/>; empty means
    /// "the nearest hostile", resolved by the agent from its own enemy census.</summary>
    Slay,

    /// <summary>Move the local player to <see cref="CoopCommand.X"/>/<see cref="CoopCommand.Y"/>
    /// /<see cref="CoopCommand.Z"/> inside the current area.</summary>
    Teleport,

    /// <summary>Move the local player to a peer, named in <see cref="CoopCommand.Address"/>.</summary>
    GoTo,

    /// <summary>Put a blueprint on the floor at the local player's feet, where the item
    /// channel will replicate it to everyone.</summary>
    DropItem,

    /// <summary>List the staging cheats and the retail console cheats worth knowing.</summary>
    Cheats,

    /// <summary>
    /// Stage a cinematic onto a module transition — a TEST INSTRUMENT, not a convenience.
    /// <see cref="CoopCommand.Amount"/> carries a <see cref="StageMovieModes"/> value,
    /// <see cref="CoopCommand.Password"/> the comma-separated movie names, and
    /// <see cref="CoopCommand.Address"/> the destination module for the warp form.
    /// </summary>
    StageMovie,

    /// <summary>
    /// List the live items this instance can see, with their object ids and per-instance
    /// state. <see cref="CoopCommand.Amount"/> caps how many rows are printed.
    ///
    /// The point is picking a target: item identity can only be exercised by sending an item
    /// that genuinely differs from its blueprint, and nothing before this could say which of
    /// the hundreds of items in a loaded module those are.
    /// </summary>
    Items,

    /// <summary>Print one item's per-instance state; the id is in
    /// <see cref="CoopCommand.Address"/>. This is the READ-BACK half of an identity test —
    /// run it on the receiving instance against the object id its plugin log names.</summary>
    InspectItem,

    /// <summary>Poke one live item's per-instance state, so a sender has something worth
    /// sending. The id is in <see cref="CoopCommand.Address"/> and the fields in
    /// <see cref="CoopCommand.Item"/>.</summary>
    SetItem,

    /// <summary>
    /// Make the local player USE a door, so the ENGINE starts a module transition — the other
    /// half of the cinematic instrument, and the only half that opens the plugin's pre-store
    /// flush window. <see cref="CoopCommand.Address"/> carries the door's tag, or an object id
    /// in the three spellings <see cref="ConsoleCommand.TryParseObjectId"/> accepts; empty
    /// means "the nearest module transition door".
    /// </summary>
    UseDoor,

    /// <summary>Give a materialised enemy's local stand-in one item of its real corpse's
    /// actual loot. The owner's enemy id is in <see cref="CoopCommand.Address"/>, the resref
    /// in <see cref="CoopCommand.Password"/>, and the stack count in
    /// <see cref="CoopCommand.Amount"/>.</summary>
    GiveLoot,

    /// <summary>
    /// STAGING VERB: run one of the peer-effect channel's non-damage constructors directly,
    /// through <c>OdysseyEffects.ApplyEffect</c> — the SAME apply-side path a peer's forwarded
    /// effect calls on arrival. See docs/research/effect-sync.md and docs/research/console-cheats.md.
    ///
    /// <see cref="CoopCommand.Amount"/> is the <c>PeerEffectKind</c> numeric value (1..9);
    /// <see cref="CoopCommand.Address"/> is the target's object id, or null for "the nearest
    /// phantom"; <see cref="CoopCommand.EffectDurationMs"/>/<see cref="CoopCommand.EffectMagnitude"/>
    /// carry the effect's duration and (Slow only) its percent.
    ///
    /// Staging only, local apply: this never crosses the network by itself. Aimed at a peer's
    /// PHANTOM it IS the live test — <c>PhantomEffectHook</c> captures it landing there and the
    /// ordinary channel forwards it for real, so what the hook does with it afterward is what
    /// is actually being measured. Aimed at a local creature (including your own player, via
    /// <c>me</c>) it is a constructor smoke test only.
    /// </summary>
    ApplyEffect,

    /// <summary>
    /// Read-only. Lists the last few peer-effect records this agent has captured, forwarded
    /// or applied, read straight out of the shared block's rings — so a live pass can read
    /// the whole non-damage effect pipeline without grepping either log. Takes no arguments.
    /// </summary>
    Effects,

    /// <summary>
    /// TEST HOOK: plays <see cref="CoopCommand.Address"/> (a bare resref) RIGHT NOW on the
    /// local instance, through the same engine entry a mid-module <c>PlayMovie</c> call and
    /// the movie relay's own <c>PlayNow</c> path use (<c>OdysseyMovies.PlayMovieNow</c>) — see
    /// docs/research/cinematics.md §10.7. Follows <c>ApplyEffect</c>'s pattern exactly: staging
    /// only, local apply — this never crosses the network by itself, but the capturing hook and
    /// the movie-relay controller run exactly as they would for a peer's real script call, so
    /// the OTHER instance seeing <c>movie-relay: playing '...' now</c> is the actual test.
    /// </summary>
    MoviePlay,

    /// <summary>
    /// STAGING VERB: destroys a creature outright, through <c>Odyssey.DestroyObjectById</c> —
    /// the SAME engine call <c>LivenessReconcileController</c>'s own Destroy action already
    /// wraps. See docs/research/creature-liveness.md. <see cref="CoopCommand.Address"/> is the
    /// target's object id — no tag form, no "nearest" fallback; this stages a SPECIFIC id,
    /// typically one `census` already named as out-of-census. Follows <c>ApplyEffect</c>'s
    /// wiring and <c>Slay</c>'s authority rules exactly (refuses a player, phantom, companion,
    /// or plot creature), but is HOST-ONLY where <c>Slay</c> forwards — a mirroring instance is
    /// refused, not handed off, because there is no destroy-ledger to spend a forwarded destroy
    /// through.
    /// </summary>
    DestroyObject,

    /// <summary>
    /// Opens an ORDINARY (non-transition) door through the engine's own door-use action, so the
    /// generic door-sync path can be verified without a working walk/click primitive. <see
    /// cref="CoopCommand.Address"/> carries the door's tag, or an object id in the three
    /// spellings <see cref="ConsoleCommand.TryParseObjectId"/> accepts; empty means "the
    /// nearest door" (no module-transition filter — that is <c>usedoor</c>'s own guard). Never
    /// walks the player through it.
    /// </summary>
    OpenDoor,

    /// <summary>
    /// CONTAINER-SYNC STAGING VERB: makes the local player USE a placeable (chest, corpse,
    /// footlocker) through the engine's own use-object input, so the real OnUsed/GUI path runs
    /// and this instance's container-lock/animation machinery has something real to react to.
    /// <see cref="CoopCommand.Address"/> carries the placeable's tag, or an object id in the
    /// three spellings <see cref="ConsoleCommand.TryParseObjectId"/> accepts; empty means "the
    /// nearest container placeable". See docs/research/container-sync.md.
    /// </summary>
    UsePlaceable,

    /// <summary>
    /// CONTAINER-SYNC LIVE-PASS ADDITION: closes a placeable's loot panel through the engine's
    /// real <c>CloseInventory</c> call — the close-side counterpart to <see
    /// cref="UsePlaceable"/>, needed because that verb only replicates the network half of a
    /// real click and leaves no panel for a keystroke to close. <see cref="CoopCommand.Address"/>
    /// carries the placeable's tag or object id, same three spellings as <see
    /// cref="UsePlaceable"/>; empty means "the nearest container placeable". See
    /// docs/research/container-sync.md.
    /// </summary>
    ClosePlaceable,

    /// <summary>
    /// DEV-ONLY TEST PRIMITIVE: switches the LOCAL player's controlled character through
    /// <c>Odyssey.SwitchControlToPartySlot</c>/<c>SwitchControlToPc</c>, which call the
    /// ENGINE'S OWN <c>ChangeCharacter</c> funnel (0x005F6B60) — the exact function a real Tab
    /// press and portrait click reach, rather than poking a raw field. See
    /// docs/research/control-return.md. <see cref="CoopCommand.Amount"/> is the party-roster
    /// slot (0-8, the same index <c>partystate.ps1</c> reports — Trask is slot 0 in
    /// <c>end_m01aa</c>), or -1 for <c>pc</c>/<c>me</c>. Not a network command: this only ever
    /// touches the local instance's own control, exactly like a real Tab press would.
    /// </summary>
    Leader,

    /// <summary>
    /// <c>recruit &lt;slot&gt;</c> — DEV-ONLY TEST PRIMITIVE (docs/research/party-reconciliation.md).
    /// Forces NPC roster slot <see cref="CoopCommand.Amount"/> (0-8) into the local active
    /// party without a real recruitment conversation: poke the availability flag if needed,
    /// spawn a body at the player's own position if the slot has none, then
    /// <c>AddPartyMember</c>. Not a network command: local roster state only, same as
    /// <see cref="Leader"/>.
    /// </summary>
    Recruit,

    /// <summary>
    /// <c>skipline</c> — LIVE-TEST PRIMITIVE (docs/research/dialogue-skip-desync.md). Forces
    /// the local player's OWNED conversation's current line/VO to finish immediately,
    /// in-process, the same way a real skip keypress/click does — injected OS input never
    /// reaches this game at all. Meaningful only when the local player is the conversation
    /// OWNER (a follower does not drive VO timing); a no-op when there is no active
    /// conversation. Takes no arguments.
    /// </summary>
    SkipLine,

    /// <summary>
    /// <c>skipkey</c> — LIVE-TEST PRIMITIVE (docs/research/dialogue-skip-desync.md). Presses
    /// skip on the dialogue panel THROUGH THE ENGINE'S OWN INPUT DISPATCH
    /// (<c>CSWGuiDialog::HandleInputEvent(panel, 0x27, 1)</c>), which is the only route that
    /// passes through <c>DialogueSkipHook</c> — the gag that stops a SPECTATOR advancing
    /// somebody else's conversation. <see cref="SkipLine"/> cannot test that hook: it pokes the
    /// owner's object flag directly (never entering the input path) and refuses outright on a
    /// spectator, which is exactly the instance the hook is about. The answer line carries the
    /// hook's swallow count before and after, which is the oracle. Takes no arguments.
    /// </summary>
    SkipKey,

    /// <summary>
    /// <c>save</c> — asks the game to save through its own quicksave path
    /// (<c>CClientExoAppInternal::DoQuickSave</c>). "Applied" means REQUESTED: the engine
    /// writes the directory a tick or more later and may still refuse. Used by
    /// <c>join_mode = newcharacter</c> to obtain a first-time joiner's freshly created
    /// character (see docs/research/world-adoption.md) and useful on its own for producing a
    /// genuinely fresh save on the rig. Takes no arguments.
    /// </summary>
    Save,

    /// <summary>
    /// <c>selectreply &lt;slot&gt;</c> — LIVE-TEST PRIMITIVE (docs/research/dialogue-skip-desync.md).
    /// Commits reply slot <see cref="CoopCommand.Amount"/> on the local player's OWNED
    /// conversation, through <c>Odyssey.SelectDialogueReply</c> — the console's way to navigate
    /// past a reply-choice screen without real OS input, needed so <c>skipline</c> can be
    /// tested against an actual narration stretch rather than a screen with nothing to skip.
    /// </summary>
    SelectReply,

    /// <summary>
    /// <c>walkto &lt;x&gt; &lt;y&gt; [z]</c> — LIVE-TEST PRIMITIVE (docs/research/minigames.md).
    /// Queues a real <c>ActionMoveToLocation</c> for the local player through
    /// <c>Odyssey.MoveToLocation</c>, the same engine call <c>usedoor</c>'s walk already uses,
    /// but to a bare point rather than a door's threshold. A subarea trigger's <c>OnEnter</c>
    /// (event 0xC, docs/research/trigger-relay.md §2) only fires for a MOVING creature — a
    /// <c>tp</c> snap runs no movement update and crosses nothing — and `usedoor` only ever
    /// walks toward a door. This is the generic version, for reaching a trigger polygon (the
    /// swoop-track entrance) with no door involved.
    /// </summary>
    WalkTo,

    /// <summary>
    /// <c>loadgame &lt;slot&gt;</c> — LIVE-TEST PRIMITIVE (docs/research/session-save.md).
    /// Fires a GENUINE mid-session (pause-menu-equivalent) Load Game action, entirely
    /// in-process — no mouse clicks, no screenshots. <see cref="CoopCommand.Amount"/> is the
    /// save slot (0-based, most-recent-first, the same ordering
    /// <c>PluginMenuLoader.TryResolveSaveDirectory</c> and <c>agent -Save N</c> already use).
    /// Bypasses the pause-menu path (blocked by rig-tooling gaps, not a code defect) by
    /// replaying the engine function chain a real click ultimately calls, the same pattern
    /// <c>skipline</c>/<c>walkto</c>/<c>usedoor</c> already established. Local only: this never
    /// crosses the network by itself — <c>MaintainSessionSave</c>'s own digest/load-serial watch
    /// is what notices the load happened and, if the gates are on, drives
    /// <c>client_load_rejoin</c>/<c>host_load_readopt</c> exactly as it would for a real click.
    /// </summary>
    LoadGame,

    /// <summary>
    /// <c>quittomenu</c> — LIVE-TEST PRIMITIVE (docs/research/disconnect-reconnect.md §9).
    /// Tears THIS instance's world down and shows the main menu, through the engine's own
    /// <c>CClientExoAppInternal::ShutDownToMainMenu</c> — the same function a real "Quit Game
    /// -&gt; Yes" click ultimately reaches. Built for exactly the reason <c>loadgame</c> was:
    /// docs/research/session-save.md measured zero of 36 candidate clicks landing on a
    /// demo.ps1-repositioned window, and a blind keyboard-only navigation attempt wedged an
    /// instance badly enough to need a full rig restart. Exercises the Away flag's third shape
    /// (agent alive, no player object) without any of that risk. Takes no arguments. Local
    /// only: this never crosses the network by itself.
    /// </summary>
    QuitToMenu,

    /// <summary>
    /// <c>scriptgive &lt;resref&gt; [stack]</c> — LIVE-TEST PRIMITIVE
    /// (docs/research/dialogue-consequences.md's script-item-sync round). Gives an item straight
    /// into THIS instance's own player's inventory with no suppression-ledger entry recorded, on
    /// purpose — simulating exactly what a script (OnEnter/OnDeath/heartbeat) granting an item
    /// looks like to <c>ScriptItemGrantPump</c>'s own sampling diff, since this codebase has no
    /// other way to trigger that scenario on demand. <see cref="CoopCommand.Address"/> is the
    /// resref, <see cref="CoopCommand.Amount"/> the stack size (default 1). NOT the same as
    /// <c>giveloot</c>'s local-player sentinel, which IS suppressed on purpose (the feature's own
    /// negative control) — this verb exists specifically to bypass that. Local only.
    /// </summary>
    ScriptGive,

    /// <summary>
    /// <c>loottake [tag|id]</c> — LIVE-TEST PRIMITIVE (docs/research/dialogue-consequences.md's
    /// script-item-sync round, Controls 2/3: container/corpse loot, flagged "most likely to fail
    /// live" and never actually exercised by the prior round because no injected click can drive
    /// the container GUI). Same target resolution as <c>useplaceable</c> (blank = nearest
    /// container) via <see cref="CoopCommand.Address"/>, but instead of sending a UseObject
    /// message this removes the container's first item through the real engine
    /// <c>CSWSPlaceable::RemoveItem</c> call and gives an equivalent copy straight to the local
    /// player — reproducing both halves of a genuine container/corpse take (a real removal AND a
    /// real new inventory row) so the suppression-ledger feed point at
    /// <c>LootPump.PumpContainerLoot</c> is exercised the same way a real take would exercise it.
    /// Local only.
    /// </summary>
    LootTake,

    /// <summary>
    /// <c>pooladd &lt;resref&gt; [stack]</c> — LIVE-TEST PRIMITIVE (docs/research/party-pool-sync.md).
    /// Adds a resref stack straight to the shared party item pool through the engine's own
    /// <c>CItemRepository::InsertItem</c> — a human-driven use of the exact same
    /// <c>CheatKinds.PartyPoolAdd</c> channel <c>CoopCommandDispatcher.DrainAutomaticPartyPool</c>
    /// already uses to apply an incoming peer's pool change. <see cref="CoopCommand.Address"/> is
    /// the resref, <see cref="CoopCommand.Amount"/> the stack size (default 1). Local only — the
    /// party-pool sync channel is what carries the resulting change to a peer, this verb only
    /// stages the local half.
    /// </summary>
    PoolAdd,

    /// <summary>
    /// <c>poolremove &lt;resref&gt; [stack]</c> — LIVE-TEST PRIMITIVE (docs/research/party-pool-sync.md).
    /// Removes a resref stack from the shared party item pool through the engine's own
    /// <c>CItemRepository::RemoveItem</c>, the removal counterpart to <see cref="PoolAdd"/>.
    /// <see cref="CoopCommand.Address"/> is the resref, <see cref="CoopCommand.Amount"/> the stack
    /// size (default 1). Local only.
    /// </summary>
    PoolRemove,

    /// <summary>
    /// <c>grantxp &lt;id|me&gt; &lt;amount&gt;</c> — LIVE-TEST PRIMITIVE
    /// (docs/research/companion-state-sync.md's live test plan §5). Adds a signed XP delta to
    /// the named creature's <c>CSWSCreatureStats::experience</c>, simulating a script-granted
    /// award aimed at one creature (a companion or the local player) for testing
    /// <c>companion_xp_sync</c>'s merge. <see cref="CoopCommand.Address"/> is the object id (or
    /// <c>me</c> for the local player), <see cref="CoopCommand.Amount"/> the signed delta. Local
    /// only.
    /// </summary>
    GrantXp,

    /// <summary>
    /// <c>equip &lt;id|me&gt; &lt;resref&gt; [slot]</c> — LIVE-TEST PRIMITIVE
    /// (docs/research/companion-state-sync.md's live test plan §5). Re-equips the named creature
    /// with a real item through <c>Odyssey.EquipItemFromTemplate</c>, for testing
    /// <c>companion_gear_sync</c> against a live companion body. <see cref="CoopCommand.Address"/>
    /// is the object id (or <c>me</c>), <see cref="CoopCommand.Password"/> the resref,
    /// <see cref="CoopCommand.Amount"/> the NWScript inventory slot (1 body, 4 right weapon,
    /// 5 left weapon; 0/omitted defaults to the right-weapon slot). Local only.
    /// </summary>
    Equip,

    /// <summary>
    /// <c>grantgold &lt;amount&gt;</c> — LIVE-TEST PRIMITIVE (docs/research/progression-sync.md's
    /// live test plan). Adds a signed delta straight to the party wallet
    /// (<c>CSWPartyTable+0xFC</c>), through the SAME <c>KotorGameBridge.TryReadProgression</c>/
    /// <c>TryWriteProgression</c> field read/write the shipped <c>progression_sync</c> channel
    /// already uses — no plugin/cheat-channel round trip, purely an agent-side external memory
    /// write, matching that doc's own "safest write" recommendation. Simulates a vendor
    /// purchase/sale (a genuine one needs a GUI click this rig cannot reliably deliver) so
    /// <c>PartyPump.PumpProgression</c>'s own local-gold-change detection organically picks it
    /// up and forwards it, the same as it would a real trade. <see cref="CoopCommand.Amount"/>
    /// is the signed delta. Local only.
    /// </summary>
    GrantGold,

    /// <summary>
    /// <c>setglobal &lt;name&gt; &lt;int|true|false|on|off&gt;</c> — LIVE-TEST PRIMITIVE
    /// (docs/research/quest-sync.md's 2026-08-27 addendum). Writes a script global by name
    /// through the engine's own type-checked setter (<c>CSWGlobalVariableTable::SetValueNumber</c>/
    /// <c>SetValueBoolean</c>), not a raw table poke, so a rig can drive a globals-gated
    /// dispatch (a galaxy-map leg, <c>firescript k_pebn_galaxy</c>-style) without a GUI.
    /// <see cref="CoopCommand.Address"/> is the global's name; <c>true</c>/<c>false</c>/
    /// <c>on</c>/<c>off</c> selects the boolean setter (carried in <see cref="CoopCommand.Password"/>
    /// as <c>"bool"</c>), any other parseable integer selects the number setter (carried as
    /// <c>"number"</c>); <see cref="CoopCommand.Amount"/> is the value (0/1 for a boolean). Local
    /// only.
    /// </summary>
    SetGlobal,

    /// <summary>
    /// <c>pazaak [npc-tag|id] [deck]</c> — LIVE-TEST PRIMITIVE (docs/research/minigames.md's
    /// 2026-08-27 addendum). Starts a Pazaak hand through <c>CClientExoApp::StartPazaakGame</c>
    /// directly (no wager, no tutorial), so <c>pazaak_detect</c> can finally be verified with a
    /// real running game instead of only the negative (never-started) case. <see cref="CoopCommand.Address"/>
    /// is the opponent's tag or object id (blank means the engine's own default opponent);
    /// <see cref="CoopCommand.Amount"/> is the opponent's pazaak-deck index (default 0 — NEEDS-LIVE
    /// which index is right for a given save/NPC; see the research doc for the Taris Upper City
    /// cantina recipe). Local only.
    /// </summary>
    Pazaak,

    /// <summary>
    /// <c>openstore &lt;resref|tag|id&gt;</c> — LIVE-TEST PRIMITIVE (docs/research/store-sync.md's
    /// 2026-08-27 addendum). Opens the store GUI against a store resolved by object id or tag,
    /// through <c>CGuiInGame::ShowStoreGui</c> called directly (the same C++ entry
    /// <c>ExecuteCommandOpenStore</c> reaches, minus the VM's own argument-popping machinery
    /// store-sync.md's original pass declined to build a verb against), so <c>store_sync</c> can
    /// finally be verified without a manual click. <see cref="CoopCommand.Address"/> is the
    /// store's tag or object id — required, there is no "nearest store" sweep. Local only.
    /// </summary>
    OpenStore,

    /// <summary>
    /// <c>closegui</c> — LIVE-TEST PRIMITIVE (docs/research/shared-inventory-v2.md §14). Closes
    /// the full-screen in-game GUI (equipment / inventory / character / abilities / journal /
    /// map / options) through the engine's own <c>CSWGuiInGame::HideSWInGameGui</c>.
    ///
    /// It exists for <see cref="OpenStore"/>. That screen is what sets <c>CGuiInGame+0x30</c>,
    /// the precondition <c>ShowStoreGui</c> refuses on, and the refusal has blocked the CLIENT
    /// half of every live store round this project has run — four rounds, six consecutive
    /// refusals in the last. The documented <c>skipline</c>/<c>selectreply 0</c>/<c>partyselect
    /// ok</c> retry loop cannot clear it, because none of those three writes that field.
    ///
    /// Takes no arguments, and answers "already closed" as SUCCESS rather than a refusal — the
    /// caller asked for a state, not an event. Local only.
    /// </summary>
    CloseGui,

    /// <summary>
    /// <c>popmodal [count]</c> - LIVE-TEST PRIMITIVE (docs/research/shared-inventory-v2.md
    /// section 15). Pops panels off the GUI manager's own modal stack through the engine's own
    /// <c>CSWGuiManager::PopModalPanel</c>.
    ///
    /// The half <see cref="CloseGui"/> was missing, and the one that actually unblocks the
    /// client's <see cref="OpenStore"/>: the engine refuses <c>HideSWInGameGui</c>'s own
    /// teardown while that stack is occupied, so <c>closegui</c> alone could only ever REPORT
    /// the blocker. Section 15 traced what puts a panel there to this project's own menu-load
    /// path, which constructs a <c>CSWGuiSaveLoad</c> panel and never pops it.
    ///
    /// <see cref="CoopCommand.Amount"/> is how many to pop at most (default 1, clamped 1..8).
    /// An already-empty stack is success, not a refusal. Local only.
    /// </summary>
    PopModal,

    /// <summary>
    /// <c>partyselect ok</c> — LIVE-TEST PRIMITIVE (docs/research/galaxy-map-and-party-select.md's
    /// 2026-08-28 addendum). Confirms the native party-selection screen
    /// (<c>CSWGuiPartySelection</c>) with its current on-screen selection, through the panel's
    /// own OK/Done handler (<c>ConfirmPartySelection</c>) — the same code path a real click
    /// reaches, including the same message-box precondition <c>loadgame</c> already needs
    /// poked on a fresh all-console session. Meant for the HOST, the only side with a real
    /// unsuppressed copy of the screen (<c>party_select_host_only</c> tears the client's own
    /// copy down before a player ever sees it). Takes no arguments beyond the literal
    /// <c>ok</c>. Local only.
    /// </summary>
    PartySelectOk,

    /// <summary>
    /// <c>faction &lt;id|me&gt; [vsId|me]</c> — LIVE-TEST/DIAGNOSTIC PRIMITIVE
    /// (docs/research/combat-participation.md §10). Read-only: reports the engine's own
    /// <c>GetAIStateReputation</c> verdict (both directions) plus each side's raw
    /// <c>CSWSCreatureStats::faction_id</c> dword for the named pairing.
    /// <see cref="CoopCommand.Address"/> is the object to query (or <c>me</c>);
    /// <see cref="CoopCommand.Amount"/> carries the "versus" id (0/omitted = local player,
    /// since a signed int has no room for a second id/`me` token). Local only.
    /// </summary>
    Faction,

    /// <summary>
    /// <c>pazaak end</c> — LIVE-TEST PRIMITIVE (docs/research/minigames.md's live-pass entry).
    /// Ends whatever Pazaak hand is currently running on this instance through
    /// <c>CClientExoApp::EndPazaakGame</c> called directly, so <c>FlagInMinigame</c> can be
    /// watched clearing without a real wagered hand played to completion through the
    /// (uninjectable) GUI. Takes no arguments beyond the literal <c>end</c>. Local only.
    /// </summary>
    PazaakEnd,

    /// <summary>
    /// <c>buy &lt;store-tag|id&gt; &lt;resref&gt; [count]</c> — LIVE-TEST PRIMITIVE
    /// (docs/research/store-sync.md's 2026-08-28 addendum). Buys <c>count</c> (default 1)
    /// units of <c>resref</c> from the named store through <c>CSWGuiStore::BuyItem</c> — the
    /// REAL Buy-button click handler, so gold and item both move through the engine's own
    /// path. Requires the store's own GUI to already be open (<c>openstore</c> first) and
    /// matching the store named here — refused otherwise. <see cref="CoopCommand.Address"/> is
    /// the store's tag or object id, <see cref="CoopCommand.Password"/> the item resref,
    /// <see cref="CoopCommand.Amount"/> the count. Local only.
    /// </summary>
    Buy,

    /// <summary>
    /// <c>sell &lt;store-tag|id&gt; &lt;resref&gt; [count]</c> — the sell side of
    /// <see cref="Buy"/>: sells <c>count</c> units of <c>resref</c> out of the local player's
    /// own inventory to the named store through <c>CSWGuiStore::SellItem</c>. Same field
    /// layout and same open-GUI requirement as <see cref="Buy"/>. Local only.
    /// </summary>
    Sell,

    /// <summary>
    /// <c>startdialog &lt;npc-tag|id&gt; [dlg-resref]</c> — LIVE-TEST PRIMITIVE
    /// (docs/research/dialogue-sync.md's 2026-08-28 addendum). Starts a conversation between
    /// the local player and the named NPC through the engine's own
    /// <c>ActionStartConversation</c> VM routine, queued on the player exactly as a real click
    /// would be — so scripted conversations (e.g. <c>k_sta_bastila.dlg</c>) can be triggered
    /// without walking into their trigger, through the SAME action-queue/<c>BeginConversation</c>/
    /// <c>StartDialog</c> chain a real click reaches, observed by the existing dialogue hooks
    /// normally. <see cref="CoopCommand.Address"/> is the NPC's tag or object id;
    /// <see cref="CoopCommand.Password"/> is the optional dialog resref override (blank = the
    /// NPC's own default <c>Conversation</c> field). Local only.
    /// </summary>
    StartDialog,

    /// <summary>
    /// <c>removeparty &lt;slot&gt;</c> — DEV-ONLY LIVE-TEST PRIMITIVE
    /// (docs/research/star-forge-finale.md §11). Calls the engine's own real
    /// <c>RemovePartyMember(int nNPC)</c> VM routine on NPC roster slot
    /// <see cref="CoopCommand.Amount"/> (0-8), through <c>Odyssey.RemovePartyMemberBySlot</c> —
    /// the same parameter-stack dispatch pattern <see cref="StartDialog"/> established. Built so
    /// the plugin's script-agnostic <c>RemovePartyMemberHook</c> detour (the second solo-segment
    /// trigger) can be exercised without reaching <c>k_sta_bastila.dlg</c>'s own entry 70. A REAL
    /// removal, not a simulation — the companion stays recruited but leaves the active party, the
    /// same as a real script call. Local only.
    /// </summary>
    RemoveParty,

    /// <summary>
    /// <c>setgold &lt;amount&gt;</c> — DEV VERB (docs/research/store-sync.md's live pass). Sets
    /// the party's gold directly to <c>amount</c> (non-negative) through the real engine
    /// <c>CSWSCreature::SetGold</c> call, clamped server-side to 999,999,999. Exists to repair a
    /// save whose gold an unaffordable <c>buy</c> drove negative-then-wrapped, before this pass
    /// added <see cref="Buy"/>'s own affordability guard. Unlike <see cref="GrantGold"/> (a
    /// signed DELTA), this is an ABSOLUTE target — the shape a repair needs.
    /// <see cref="CoopCommand.Amount"/> carries the target value. Local only.
    /// </summary>
    SetGold,

    /// <summary>
    /// <c>followkick &lt;slot&gt;</c> — DEV-ONLY LIVE-TEST PRIMITIVE
    /// (docs/research/party-reconciliation.md Round 96). Dumps NPC roster slot
    /// <see cref="CoopCommand.Amount"/> (0-8)'s follow-record fields (phase, attempts, target,
    /// current path node) to the plugin log, then forces <c>record+4 = 10</c>
    /// (<c>TeleportForward</c>) UNCONDITIONALLY through
    /// <c>Odyssey.TryForceTeleportForward</c> — regardless of the companion's current phase,
    /// unlike the automatic <c>party_follow_phase2_kick</c> pass (phase 2 only). Built to test
    /// live whether the same recovery poke that targets a phase-2 stall also resumes the
    /// different phase-7 near-target freeze Round 96 found. Local only.
    /// </summary>
    FollowKick,

    /// <summary>
    /// <c>followstall &lt;slot&gt; [method]</c> — DEV-ONLY LIVE-TEST PRIMITIVE
    /// (docs/research/party-reconciliation.md Round 105). Dumps NPC roster slot's follow-record
    /// WEDGE SIGNATURE (phase, attempts, pathNode/destNode/writeCursor, and the two squared
    /// distances <c>PathWaitDest</c>'s own strict accept test compares at 0x00512BF2), then
    /// applies one of <c>FollowStallRule</c>'s four candidate recoveries UNCONDITIONALLY — no
    /// wedge check, no stall timer, no cooldown, unlike the automatic
    /// <c>party_follow_stall_recovery</c> pass.
    ///
    /// <c>method</c> is 1 phasereset, 2 followleader, 3 positionparty, 4 priortie; omitted means
    /// "whatever <c>party_follow_stall_recovery_method</c> selected". It exists so a live A/B can
    /// isolate all four candidates inside ONE game session — the automatic pass reads its method
    /// once at plugin construction, so isolating them through it alone would cost four rig
    /// restarts.
    ///
    /// ⚠ <see cref="CoopCommand.Amount"/> carries BOTH values, encoded as
    /// <c>method * 100 + slot</c> — <see cref="CoopCommand"/> has no second integer field and
    /// this dev-only verb is not worth widening a record every other verb shares. The dispatcher
    /// splits it back apart. Local only.
    /// </summary>
    FollowStall,


    /// <c>newgame</c> — opens the game's OWN character-creation screens from the main menu, by
    /// replaying <c>CSWGuiMainMenu::OnNewGamePicked</c>'s body (construct
    /// <c>CSWGuiClassSelection</c>, register it, mark the menu for teardown). The same door a
    /// click on NEW GAME opens; the player still uses the screens normally.
    ///
    /// Built because scripted mouse input is not trustworthy here — KOTOR's menu input dies
    /// intermittently and undiagnosably (the trap that made the menu-load replay exist), and a
    /// scripted click sequence was measured dropping a click mid-chargen and silently
    /// cancelling. Local only, and only meaningful at the main menu. See
    /// <c>KotorCoop.Plugin.CharGenDriveController</c>.
    /// </summary>
    NewGame,

    /// <summary>
    /// <c>chargen quick [class 0-5]</c> — picks a class through the panel's own
    /// <c>CSWGuiClassSelection::OnSelectButton</c> and applies the optimised template the
    /// Quick Character route applies (<c>SetVisiblePanel(2)</c> then
    /// <c>CSWGuiQuickPanel::DoQuickStats</c>). <see cref="CoopCommand.Amount"/> is the class
    /// index, default 0. Local only.
    /// </summary>
    ChargenQuick,

    /// <summary>
    /// <c>chargen accept</c> — presses Play: <c>CSWGuiMainCharGen::BeginPlayGame</c>, which is
    /// what writes the finished character to <c>TEMP:pifo</c> and starts the module. With a
    /// new-character join armed (<c>join_mode = newcharacter</c>) this is the moment the
    /// character is captured. Local only.
    /// </summary>
    ChargenAccept,

    /// <summary>
    /// <c>galaxymap open</c> — LIVE-TEST PRIMITIVE (Tier 2 item 18 task 2's own "Live test plan"
    /// follow-up, docs/research/galaxy-map-and-party-select.md's 2026-09-03 addendum). Opens
    /// this instance's own galaxy-map GUI through <c>CGuiInGame::ShowGalaxyMapGui</c>, called
    /// directly (fresh disasm this pass, see the research doc) — the same entry point
    /// <c>k_pebn_galaxy.nss</c>'s <c>infoterm001</c> console reaches, so the accept-button
    /// confirm refusal (<c>galaxy_map_host_only</c>, <c>GalaxyMapConfirmHook</c>) can finally be
    /// exercised live without a human clicking. Takes no arguments. Local only.
    /// </summary>
    GalaxyMapOpen,

    /// <summary>
    /// <c>galaxymap close</c> — the counterpart to <see cref="GalaxyMapOpen"/>, through
    /// <c>CGuiInGame::HideGalaxyMapGui</c> (fresh disasm this pass, bare <c>this</c>-only
    /// thiscall, no gate on the GUI manager's modal stack, unlike <c>HideSWInGameGui</c>).
    /// Already-closed answers APPLIED, same reasoning <see cref="CloseGui"/>'s own doc comment
    /// gives. Takes no arguments. Local only.
    /// </summary>
    GalaxyMapClose,

    /// <summary>
    /// <c>galaxymap stats</c> — reports <c>GalaxyMapConfirmHook</c>'s own
    /// <c>RefusedCount</c>/<c>AllowedCount</c> counters and whether the splice installed — a
    /// pure read, no engine call. The research doc's own "Live test plan" named these counters
    /// as existing but unreadable from any console verb; this is that missing read. Takes no
    /// arguments. Local only.
    /// </summary>
    GalaxyMapStats,

    /// <summary>
    /// <c>galaxymap select &lt;n&gt;</c> — writes <c>CSWPartyTable::SetSelectedPlanet(n)</c>
    /// directly (VERIFIED full disasm, docs/research/galaxy-map-and-party-select.md's
    /// 2026-09-03 `galaxyaccept` addendum), independent of whether the map screen is open. The
    /// answer to "what must happen before <see cref="GalaxyAccept"/> does anything" — the
    /// confirm script reads whatever this last wrote. <see cref="CoopCommand.Amount"/> carries
    /// the planet index, 0-15; out of range or unavailable/unselectable is reported as a no-op.
    /// Local only.
    /// </summary>
    GalaxyMapSelect,

    /// <summary>
    /// <c>galaxyaccept</c> — LIVE-TEST PRIMITIVE (docs/research/galaxy-map-and-party-select.md's
    /// 2026-09-03 `galaxyaccept` addendum). Presses Accept on the galaxy map THROUGH THE
    /// ENGINE'S OWN INPUT DISPATCH (<c>CSWGuiInGameGalaxyMap::HandleInputEvent(panel, 0x27,
    /// 1)</c>, vtable slot 0x3C) — the same shape <see cref="SkipKey"/> already uses for the
    /// dialogue panel, and the only route that reaches <c>GalaxyMapConfirmHook</c>'s splice
    /// (<see cref="GalaxyMapOpen"/>/<see cref="GalaxyMapClose"/> only ever call
    /// <c>ShowGalaxyMapGui</c>/<c>HideGalaxyMapGui</c> directly and never enter
    /// <c>HandleInputEvent</c> at all). A DELIBERATELY DIFFERENT SHAPE from the `galaxymap
    /// confirm` subcommand that pass investigated and did not ship: that would have jumped into
    /// the accept-button CASE HANDLER itself, a jump-table target with no return address to
    /// preserve; this calls <c>HandleInputEvent</c> ITSELF, a genuine callable function, so the
    /// full vanilla dispatch (including <c>GalaxyMapConfirmHook</c>'s own splice, spliced INSIDE
    /// that dispatch) runs exactly as a real click's would. Takes no arguments. Local only.
    /// </summary>
    GalaxyAccept,
}

/// <summary>
/// What a <c>moviewarp</c> line asks for. Mirrored in <c>MovieStaging.Mode*</c> on the plugin
/// side (which cannot reference this assembly), the same way <c>CheatKinds</c> mirrors the
/// shared block's kind constants.
/// </summary>
public static class StageMovieModes
{
    /// <summary>Arm the movies, then warp: they are written into the engine's queue on the
    /// frame the travel path fires, and nowhere else.</summary>
    public const int WarpWithMovies = 0;

    /// <summary>Write them into the queue now and leave them, so the NEXT transition of any
    /// kind — a door, a script, the escape pod — carries them.</summary>
    public const int QueueOnly = 1;

    /// <summary>Empty the queue again.</summary>
    public const int Clear = 2;

    /// <summary>Report what is queued and change nothing.</summary>
    public const int Show = 3;
}

/// <summary>
/// Which of an item's per-instance fields a <see cref="CoopCommandKind.SetItem"/> line named.
/// Null means "leave it alone", which is a first-class answer here: a `setitem` that named
/// nothing is a pure read, and one that named a stack must not silently reset the charges.
/// </summary>
public readonly record struct ItemFields(
    uint? StackSize = null,
    uint? Charges = null,
    uint? MaxCharges = null,
    uint? Upgrades = null,
    bool? Identified = null)
{
    public bool Any =>
        StackSize is not null || Charges is not null || MaxCharges is not null
        || Upgrades is not null || Identified is not null;
}

/// <summary>
/// A parsed console command. <see cref="Address"/> and <see cref="Password"/> carry the
/// arguments for <see cref="CoopCommandKind.Connect"/>/<see cref="CoopCommandKind.Host"/>;
/// <see cref="Error"/> explains an <see cref="CoopCommandKind.Invalid"/> line.
/// </summary>
public readonly record struct CoopCommand(
    CoopCommandKind Kind,
    string? Address = null,
    string? Password = null,
    string Error = "",
    int Amount = 0,
    float X = 0f,
    float Y = 0f,
    float Z = 0f,
    ItemFields Item = default,
    uint EffectDurationMs = 0,
    uint EffectMagnitude = 0)
{
    /// <summary>
    /// Resolves <see cref="Address"/> to an endpoint, applying <paramref name="defaultPort"/>
    /// when the typed host omits one. Returns false for a host-mode or malformed command.
    /// </summary>
    public bool TryResolveEndpoint(int defaultPort, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.Loopback, defaultPort);

        if (string.IsNullOrWhiteSpace(Address))
        {
            return false;
        }

        string candidate = Address.Contains(':') ? Address : $"{Address}:{defaultPort}";
        if (IPEndPoint.TryParse(candidate, out IPEndPoint? parsed))
        {
            endpoint = parsed;
            return true;
        }

        // A bare hostname (not an IP literal): resolve it.
        string hostPart = Address;
        int port = defaultPort;
        int colon = Address.LastIndexOf(':');
        if (colon > 0 && int.TryParse(Address[(colon + 1)..], out int typedPort))
        {
            hostPart = Address[..colon];
            port = typedPort;
        }

        try
        {
            IPAddress[] resolved = Dns.GetHostAddresses(hostPart);
            if (resolved.Length > 0)
            {
                endpoint = new IPEndPoint(resolved[0], port);
                return true;
            }
        }
        catch
        {
            // Unresolvable; fall through to false.
        }

        return false;
    }
}

/// <summary>
/// Parses console/overlay command lines into <see cref="CoopCommand"/>. The grammar is
/// deliberately tiny and forgiving — this is typed by a player mid-game, not scripted:
///   <c>connect 1.2.3.4</c> · <c>connect 1.2.3.4:7777 secret</c> · <c>host</c> ·
///   <c>disconnect</c> · <c>reconnect</c> · <c>status</c> · <c>help</c>.
/// A leading <c>coop</c> (or <c>/coop</c>) prefix is accepted and stripped, so the same
/// parser serves both a dedicated overlay and a shared console.
///
/// The grammar itself is a declarative table (<see cref="VerbTable"/>): each verb maps to a
/// small parse method living in one of the <c>ConsoleCommand.*.cs</c> partial-class files,
/// grouped by area (session/connect verbs, cheats, items, movies, doors, effects, debug).
/// <see cref="Parse"/> only does prefix-stripping and the table lookup; every verb's actual
/// argument grammar and error text is unchanged from before the split.
/// </summary>
public static partial class ConsoleCommand
{
    /// <summary>One verb-table row: every alias for a verb, and the method that parses its
    /// arguments (<paramref name="rest"/> is already sliced past the verb word).</summary>
    private readonly record struct VerbSpec(string[] Verbs, Func<string[], CoopCommand> Parse);

    /// <summary>
    /// The whole grammar, one row per verb. Each parse method lives in the
    /// <c>ConsoleCommand.*.cs</c> file for its area — this table is just the index. Order
    /// here has no effect on parsing (it is a dictionary lookup); it follows the original
    /// switch's order so a diff against history stays easy to read.
    ///
    /// ⚠ Must be declared (and therefore field-initialised) BEFORE <see cref="VerbTable"/>:
    /// static field initialisers run in textual declaration order, and <see cref="BuildVerbTable"/>
    /// reads this array.
    /// </summary>
    private static readonly VerbSpec[] VerbSpecs =
    [
        new(["connect", "join"], ParseConnect),
        new(["host"], ParseHost),
        new(["password", "pw"], ParsePassword),
        new(["disconnect", "dc", "leave"], ParseDisconnect),
        new(["reconnect", "rc"], ParseReconnect),
        new(["say", "chat", "msg"], ParseSay),
        new(["clear", "cls"], ParseClear),
        new(["servers", "browse", "list"], ParseServers),
        new(["status", "stat"], ParseStatus),
        new(["lagstats"], ParseLagStats),
        new(["public_address", "publicaddress", "pubaddr"], ParsePublicAddress),
        new(["help", "?", "commands"], ParseHelp),
        new(["warp"], ParseWarp),
        new(["netsim", "net"], ParseNetSim),
        new(["who", "peers"], ParseWho),
        new(["where"], ParseWhere),
        new(["census"], ParseCensus),
        new(["resolve"], ParseResolve),
        new(["firescript"], ParseFireScript),
        new(["movenear"], ParseMoveNear),
        new(["spawncreature", "spawn"], ParseSpawnCreature),
        new(["god"], ParseGod),
        new(["topup"], ParseTopUp),
        new(["standup", "getup"], ParseStandUp),
        new(["hurt"], ParseHurt),
        new(["down"], ParseDown),
        new(["slay"], ParseSlay),
        new(["tp"], ParseTeleport),
        new(["walkto"], ParseWalkTo),
        new(["goto"], ParseGoTo),
        new(["dropitem"], ParseDropItem),
        new(["moviewarp"], ParseStageMovie),
        new(["usedoor"], ParseUseDoor),
        new(["opendoor"], ParseOpenDoor),
        new(["useplaceable"], ParseUsePlaceable),
        new(["closeplaceable"], ParseClosePlaceable),
        new(["items"], ParseItems),
        new(["item"], ParseItem),
        new(["setitem"], ParseSetItem),
        new(["giveloot"], ParseGiveLoot),
        new(["applyeffect"], ParseApplyEffect),
        new(["effects"], ParseEffects),
        new(["movieplay"], ParseMoviePlay),
        new(["destroyobject", "destroy"], ParseDestroyObject),
        new(["leader"], ParseLeader),
        new(["recruit"], ParseRecruit),
        new(["skipline"], ParseSkipLine),
        new(["skipkey"], ParseSkipKey),
        new(["save"], ParseSave),
        new(["selectreply"], ParseSelectReply),
        new(["loadgame"], ParseLoadGame),
        new(["quittomenu"], ParseQuitToMenu),
        new(["scriptgive"], ParseScriptGive),
        new(["loottake"], ParseLootTake),
        new(["pooladd"], ParsePoolAdd),
        new(["poolremove"], ParsePoolRemove),
        new(["grantxp"], ParseGrantXp),
        new(["equip"], ParseEquip),
        new(["grantgold"], ParseGrantGold),
        new(["setglobal"], ParseSetGlobal),
        new(["pazaak"], ParsePazaak),
        new(["openstore"], ParseOpenStore),
        new(["closegui"], ParseCloseGui),
        new(["popmodal"], ParsePopModal),
        new(["partyselect"], ParsePartySelect),
        new(["faction"], ParseFaction),
        new(["buy"], ParseBuy),
        new(["sell"], ParseSell),
        new(["startdialog"], ParseStartDialog),
        new(["removeparty"], ParseRemoveParty),
        new(["setgold"], ParseSetGold),
        new(["followkick"], ParseFollowKick),
        new(["followstall"], ParseFollowStall),
        new(["cheats"], ParseCheats),
        new(["newgame"], ParseNewGame),
        new(["chargen"], ParseChargen),
        new(["galaxymap"], ParseGalaxyMap),
        new(["galaxyaccept"], ParseGalaxyAccept),
    ];

    /// <summary>Built once from <see cref="VerbSpecs"/>; every alias gets its own entry so the
    /// lookup in <see cref="Parse"/> stays a single dictionary hit.</summary>
    private static readonly Dictionary<string, Func<string[], CoopCommand>> VerbTable = BuildVerbTable();

    private static Dictionary<string, Func<string[], CoopCommand>> BuildVerbTable()
    {
        var table = new Dictionary<string, Func<string[], CoopCommand>>(StringComparer.Ordinal);
        foreach (VerbSpec spec in VerbSpecs)
        {
            foreach (string verb in spec.Verbs)
            {
                table[verb] = spec.Parse;
            }
        }

        return table;
    }

    public static CoopCommand Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return new CoopCommand(CoopCommandKind.None);
        }

        string[] parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int index = 0;

        // Optional "coop"/"/coop" prefix so it works as a sub-command of a shared console.
        string first = parts[index].TrimStart('/');
        if (first.Equals("coop", StringComparison.OrdinalIgnoreCase))
        {
            index++;
            if (index >= parts.Length)
            {
                return new CoopCommand(CoopCommandKind.Help);
            }
        }

        string verb = parts[index].TrimStart('/').ToLowerInvariant();
        string[] rest = parts[(index + 1)..];

        return VerbTable.TryGetValue(verb, out Func<string[], CoopCommand>? parse)
            ? parse(rest)
            : new CoopCommand(CoopCommandKind.Unknown);
    }

    /// <summary>One-line help listing the commands, for the overlay and dashboard.</summary>
    public const string HelpText =
        "commands: servers | connect <ip|#n> [password] | password <pw> | host | disconnect | reconnect | say <msg> | clear | status | lagstats | public_address | warp <module> | who | where | census | resolve <id> | cheats | netsim <off|status|loss|latency|blackout> | help (see `cheats` for applyeffect/effects and the rest of the staging verbs)";
}
