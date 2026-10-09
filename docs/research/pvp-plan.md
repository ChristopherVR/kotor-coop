# PvP: combined plan (2026-10-09)

Synthesis of four research passes, all read-only (code, docs, game files; nothing run in game):
[pvp-product.md](pvp-product.md) (rules and player experience),
[pvp-engine-feasibility.md](pvp-engine-feasibility.md) (what the engine allows),
[pvp-networking.md](pvp-networking.md) (authority, latency, wire),
[pvp-arenas.md](pvp-arenas.md) (where to fight). Where they disagree, this file says which way
and why. Phase 1 (the duel MVP) was built the same day and is unit- and loopback-tested but
never watched in the game: see "Phase 1 as built" and the NEEDS-LIVE checklist at the end.
Phases 3 and 4 (the arena) were built after it, also unit- and loopback-tested and never run in the game: see "Phases 3 and 4 as built" and its NEEDS-LIVE checklist at the end. Phase 2 (fidelity) is not built.

## Verdict

Feasible, and cheaper than it sounds, because co-op already carries most of a fight between two
real characters: the partner's body in your game already forwards any damage it takes to the
partner's real character (`PhantomHitHook` -> `PhantomDamage` -> `IncomingDamageController`).
What is missing is consent and round state, turning the partner hostile only during a fight,
and making sure a PvP defeat never reaches the campaign's death, game-over or wipe recovery.

## Decisions

| Question | Decision | Why |
| --- | --- | --- |
| First format | Consensual 1v1 duel in the campaign world (`pvp = duel`) | Smallest surface; every report converged on it |
| Starting a duel | `duel <name>` / `accept` / `decline` / `yield` / `duel off`, 30 s timeout | Text commands and toasts already exist; an in-world click risks accidental challenges |
| Making the partner attackable | During a duel only, the partner's body joins a hostile faction (Hostile_1: reads hostile to the player, ignored by enemies of that faction); flipped back afterwards | Engine report: smallest change; companions follow automatically, so they are benched for v1 |
| Who decides a hit | Attacker's engine rolls the swing; the defender's game applies the damage and rolls its own saves; the host orders results and caps damage and rate | Networking + engine agree. Open point: the partner's body does not carry the real character's defence, so either copy that stat onto it for the duel (preferred, needs a live check) or have the defender re-roll |
| Ending a round | Both duellists are pinned at 1 hp minimum; the round ends at a health threshold (25%), judged by the host; health and Force restored to the pre-duel snapshot | No real death, so no game-over and no wipe vote; reuses the existing min-1-hp pin |
| Pause | Shared pause with a budget (3 pauses of 30 s each per player); a menu counts as a pause; the menu protection window is off in a duel | Product report; KOTOR is built around pausing, an unlimited pause is an exploit |
| Fairness | Real characters; an automatic capped handicap when levels differ by more than 3, shown in the accept prompt | Product + arenas reports; reuses the enemy-scaling detour with a player-vs-player branch |
| Consequences | No XP, loot, penalties or save changes; autosave blocked during a duel; campaign sync channels that could leak (progression, loot, journal, doors, companions) paused | All four reports |
| Settings | `pvp = off` by default, host-authoritative behaviour flag; `pvp_pause`, `pvp_force_powers`, `pvp_handicap` follow later | Same negotiation as `corpse_loot`; a peer without PvP support refuses cleanly |
| Arenas | v2: warp both duellists to the Taris duel ring (`tar_m02ae`, room `m02ae_07a`, spawn pair `tar02_wppcarena` / `tar02_wpopparena`) and back. v3: a clean `k_coop_arena.mod` cloned from that area on the player's machine at install time | One single warp each way (rapid warps crash the engine); the cantina's 40 NPCs and story globals make the in-place ring a prototype, the cloned module the product |

## Order of work

1. **Duel MVP (about 2 weeks).** Behaviour flag and capability bit; challenge / accept / yield and
   the host's round state; the duel faction flip and the min-1-hp pin; forwarded damage with
   the enemy-damage scaling bypassed for player attackers; round end, restore, autosave block;
   plain-language toasts. Live checks first: does the target cursor turn red promptly after the
   faction flip, and can the duellist's defence be written onto the partner's body.
2. **Fidelity (about 1 week).** Force powers and stuns between players (the effect-sync channel,
   untested hostile), critical hits, grenades; the pause budget; the level handicap.
3. **Arena prototype.** `arena` command: one warp into the Taris duel ring, rounds, one warp back.
   Measure what the cantina's scripts do around it.
4. **Shipped arena module.** `ErfWriter.Build`, a clone generator from the player's own game
   files, installer support for `modules\` (and the Linux `install.sh` mirror), then team modes
   with companions and an in-session scoreboard.

## Risks, worst first

1. A duel defeat reaching the campaign death path (game over, wipe recovery).
2. Faction-flip side effects outside the duel (a stale hostile partner, enemies changing sides).
3. Hit fairness against the partner body's wrong defence; a modified client over-reporting hits
   (bounded by the existing plausibility caps).
4. Non-damage effects (stun, stasis, knockdown) between players.
5. Griefing and harassment on public servers: consent-only, `duel off`, no stakes.

## Phase 1 as built (2026-10-09)

Built to this plan, with these choices where the plan left room:

- **Setting.** `pvp = off | duel` (anything else reads as off), behaviour flag bit 22,
  host-authoritative (`corpse_loot`'s wiring: digest, compare, `SessionOverrides`, `AgentSettings`,
  `AgentComposition`). No `SessionCapabilities` bit and no protocol bump: an older build is found
  out by a handshake instead. Every duel-aware client says `PvpOp.Aware` every 2 s while the host
  allows duels; the host turns a challenge to a peer that has not said it away with "does not
  support duels yet".
- **Wire.** `PvpIntent` (111, client to host, repeated 2 s, one action per serial) and `PvpState`
  (112, host to all, the round as a level every 0.5 s while anything is going on, plus the last
  eight messages). Messages are codes (`PvpNoticeCode`) rendered by `PvpText`; each is addressed to
  one peer and shown once per serial. A client that hears nothing for 4 s reads the round as idle.
- **Round.** `PvpDuelRound` (pure; `PvpDuelRoundTests`): one round at a time; refusals for duels
  off, older build, `duel off`, busy, other area, below half health, repeat inside a minute, round
  already running; 30 s answer window, 5 s countdown, ends at 25%, yield, leave, down, or the pair
  no longer fits (conversation, minigame, solo segment, another area).
- **Local player.** `PvpDuelMachine` (pure; `PvpDuelMachineTests`) driven by `PvpDuelController`:
  on the countdown snapshot hp and Force and set the engine's min-1-hp flag (only claimed if the
  module had not already set it); on the end take the flag off, revive if down, write hp and Force
  back (6 tries, 5 frames apart). The block gained three agent-to-plugin levels at 0xCC80 (phase,
  opponent peer id, round id), block version 100, no growth.
- **Opponent's body.** `PhantomController.ApplyTargetable` puts the opponent's stand-in, and only
  that one, in Hostile_1 while the phase is live, and flips it back to Friendly_1 with
  `PacifyPhantom` after. It ignores the local_combat hold (it would keep the player from targeting
  the body) and the menu guard; a stealthed opponent stays hidden.
- **Damage.** The existing `PhantomHitHook` -> `PhantomDamage` -> `IncomingDamageController` path,
  unchanged, with two rules in the hook: only the local player's own character's hits count in a
  live duel (companions swing at any hostile body, so theirs are dropped, as are traps' and
  scripts'), and `enemy_damage` never scales a player's hit on a player.
- **No campaign consequences.** `DeathPump` does not count a duellist toward the wipe vote and a
  duellist cannot start one; the pin means nobody should be down at all. The menu cushion is off
  for duellists. Saves: the `save` verb is refused agent-side, and `OnSaveGameRequested` swallows
  every engine save (module-enter autosave included) while the pin is up.

What is left, by design or for a later phase:

- Campaign channels (progression, loot, journal, doors, companions) are NOT paused for the duel's
  duration. The 1 hp pin means no death, so no XP, loot or journal change arises from the fight,
  and the host cancels the duel on any area change, but a duellist can still open a chest or a
  door while the round runs (the menu cushion is off, so it costs them). The plan's "pause the
  channels that could leak" is deferred as larger than the MVP; phase 1 itself writes to none of
  them.
- Engine saves are only blocked when `save_flush` (default on) installed the save hook. With it
  off, a save during a duel bakes the pin into the save until the character is next loaded and
  released.
- An area change mid-duel: the host calls the duel off on its next tick, but the module-enter
  autosave on the arrival side is dropped by the same hook only while the pin is still up.
- The opponent's body carries the stand-in's defence, not the real character's (the plan's open
  point), and `PhantomDamage` carries no crit flag. Companions, Force-power saves, stun-type
  effects, the pause budget, the level handicap and arenas are phases 2 to 4.

## NEEDS-LIVE checklist (phase 1)

Two instances, a host and a joiner, same area, `pvp = duel` on the host. Each item names the
evidence to capture; the plugin log lines are `duel: ...` (`kotorcoop-plugin.log`).

1. **Setting.** With `pvp = off` on the host, `duel <name>` on either side prints "The host has not
   turned on duels. Ask them, or see the pvp setting." and no log line `duel: round ...` appears.
   With `pvp = duel` on the host and `off` on the joiner, the joiner adopts it (a `W_CFG pvp` line)
   and can challenge.
2. **Challenge and answer.** `duel <name>` on one side, the other sees the toast and chat line
   ("... challenges you to a duel. Type accept or decline. No answer in 30 seconds counts as no.")
   and can `accept` / `decline`; silence for 30 s tells the challenger "did not answer".
   Both directions (host challenges joiner, joiner challenges host).
3. **Countdown pin.** On `accept`, both log `duel: round N armed against peer M - pinned at 1 hp
   minimum, snapshot hp .. fp ..`. The numbers match the HUD. A save attempt (F4, the save panel,
   `save`) is refused and logs `duel: refused a save request`.
4. **Cursor turns red.** When the fight starts (log `duel: round N is live`), the opponent's body
   shows the red hostile target cursor and the attack option within a second or two of "Fight!".
   Record how many frames it lags the faction flip (`phantom ...: joined the hostile faction (duel)`).
   Before the fight starts (countdown) the cursor must NOT be red.
5. **Damage lands on the real character.** Attack the opponent's body with the player's own
   weapon: the opponent's health bar falls on THEIR screen and the number matches (within the
   defender's own resistances), and the attacker's view of the body shows no change beyond the
   floaty. Repeat with a Force power that does damage (Lightning).
6. **Companions are benched.** With a companion in the party, the companion may swing but its
   hits are dropped (`duel: dropped a hit on stand-in ... (only your own character fights)`) and
   the opponent's health does not move from them.
7. **`enemy_damage` does not touch it.** With `enemy_damage = 200` the hit on the opponent is the
   same size as with the default (no `scaling: enemy damage` line for it).
8. **1 hp floor holds.** Keep attacking a duellist well past the line, from both sides, with
   nobody yielding: nobody drops below 1 hp, nobody enters the temp-dead state, `LocalDowned`
   never goes to 1, and no `death:`/game-over line appears.
9. **The round ends at 25%.** The host ends the round when a duellist reaches 25% (not before),
   both are told who won ("... won the duel ..."), the opponent's body stops being hostile at
   once (cursor no longer red, nothing keeps attacking it: `PacifyPhantom` worked).
10. **Restore.** After the end, both players' hit points and Force points read exactly what the
    countdown log line recorded (HUD and `stats`); the pin is off (`god` status shows OFF unless
    the module pinned it); a player who was at 1 hp when it ended is up and standing.
11. **No game over.** Drive one duellist to the floor on purpose if the floor can be defeated
    (e.g. a script kill, or `down`): the duel ends as a loss, no game-over screen, no wipe vote,
    no `WipeRecovery`, and the player stands up again after.
12. **Yield and disconnect.** `yield` from each side ends it at once with the right lines.
    Killing the joiner's game mid-duel: the host ends it ("... left, so the duel is over.") and
    the host is not left pinned or hostile-flipped.
13. **Area change.** Walking one duellist through a door mid-duel calls it off on both sides
    ("... was called off. Nothing was lost.") and both are restored. Check the arrival side:
    `duel: refused a save request` for the module-enter autosave, and no pin left on the
    character in `saves\` (the Min1HP field of the pifo in a quicksave made afterwards is 0).
14. **Faction reverts.** After any end, the opponent's stand-in is back to Friendly_1: a friend
    sphere cast (Force Valor) still reaches it, and a nearby Hostile_1 enemy still ignores it but
    a Sith soldier of another faction does not start attacking it.
15. **Menu cushion.** Opening the inventory mid-duel does not make a duellist untouchable: the
    opponent can still hit the body while the menu is open.
16. **Rapid rounds.** Two duels back to back (with a minute between challenges): round ids 1 and
    2, a clean restore after each, no stale hostile body.

Items most likely to fail: 4 (the reaction byte may lag the faction flip), 5 (whether the
defender's engine applies the forwarded damage under the pin at all), 10 (the Force point write is
a plain field poke), and 13 (the autosave race).

### Live results (2026-10-09, Dantooine `danm13`, host Lanik 12 hp vs joiner Malak 120 hp, `pvp = duel` on the host only)

| Item | Result | Evidence |
| --- | --- | --- |
| 1 setting adopted | PASS | joiner: `adopted host's pvp (on; local setting was off)` |
| 2 challenge / accept | PASS | both sides: countdown, then `Fight!` |
| 3 countdown pin and snapshot | PASS | `round 1 armed ... pinned at 1 hp minimum, snapshot hp 12` / `hp 120` |
| 4 opponent attackable | PASS (cursor colour not captured) | a queued attack on the host's stand-in landed |
| 5 damage reaches the real character | PASS | host: `took 2 damage ... took 5 damage from a hit on our phantom`, hp fell |
| 8 1 hp floor | PASS | the round ended at `target 1/12`, no death path |
| 9 host ends the round | PASS | `Malak won the duel`; ending logged as `round 1: challenger 120/120, target 1/12` |
| 10 restore | PASS | hp back to 12 / 120 |
| 11 forced down | PASS | `down` on the host mid-duel: game over suppressed, revive, loss, restored to 12 |
| 12 yield | PASS | `You yielded to Malak`, restored |
| 13 area change | PASS | host walked into `danm14aa`: called off on both sides and restored before the load |
| 14 faction reverts | PASS | `joined the friendly faction (partner_buffs)` after every round |
| 16 back to back | PASS | rounds 1-4 in one session, clean each time |
| 6 companion hits, 7 enemy_damage bypass, 15 menu cushion | NOT RUN | no companion in the save; scaling and menu cases left for the next pass |

Fixed during the pass: the round now logs how it ended (health/max/down per side, a yield, or
called off), which explained a round that seemed to end above 25% (the host had dipped to 1 hp
between samples).


## In-game surface: the duel panel (2026-10-09, Built, NEEDS-LIVE)

Players start a duel without typing: F10 opens a list of the players in the area, Up/Down or 1-9
pick, Enter challenges; a challenge shows "F11 accept / F12 decline"; F12 yields during a fight.
Built as a keyboard panel on the queued-text overlay, not a clickable button. Why:

**(a) A clickable in-game button was not built.** The mod's "Co-op" button on the main menu is not
a button the mod creates. It is `BTN_WARP`, a control the retail main-menu constructor builds and
then deliberately hides one instruction later; the mod only sets the visible bit back, relabels it
and rebinds its click (`menu-button-runtime.md`; the "shelved" note in the history is that feature's
first attempt, which crashed because BTN_WARP is embedded in the menu object, not a pointer
off it, and was fixed the same day). Nothing like that exists on the in-game HUD: every HUD control
is a real one with a job (the pause hourglass already has its click detoured, `PauseButtonHook`).
A new HUD button would mean constructing and laying out `CSWGui*` controls at runtime, or shipping a
`.gui` override that replaces a retail HUD layout (a mod-compat hazard for any player with a HUD
mod), and the engine's panel constructors take `CExoArrayList` arguments whose ABI the repo has
never driven (`server-browser.md` crash-risk register). That is a poor trade for a feature that
must never crash a fight. The shelved-menu blocker (a wrong-address write) does not apply, but the
dormant-control trick does not exist in-game either.

**(b) Built: the keyboard panel.** It is the same technique the in-world server browser (F9) already
ships: `PostTextLine` rows re-posted every frame, key edges from `IsKeyDownHere`, nothing that calls
into the engine. `PvpMenuController` (SwapBuffers hook, in-world only) draws it; `PvpMenuRule` holds
every decision (mode, key mapping, selection, text) and is unit-tested (`PvpMenuRuleTests`).

**(c) A main-menu entry was not built.** Duels need two players in one area, so a main-menu entry
would have nothing to do; the Co-op button opens the server browser, and that stays as it is.

How it works:

| State (agent-written) | What is drawn | Keys that act |
| --- | --- | --- |
| Duels off on the host | nothing at all | none (every key ignored) |
| Duels on, nothing under way, someone in the area | one faint line "F10: challenge a player to a duel" | F10 opens the list |
| List open | "=== DUEL ===", one row per player in the area (busy ones marked), footer | Up/Down or 1-9 move the highlight; Enter challenges; Esc or F10 closes |
| Challenged | "Aria challenges you to a duel." and "F11 accept   F12 decline   No answer in 30 seconds counts as no." | F11 accept, F12 decline |
| Waiting on an answer | "You challenged Aria to a duel. Waiting for an answer." | none |
| Countdown or fight | "Duel with Aria.   F12 yield (stops it, nothing is lost)" | F12 yield |

- **Visibility.** The agent writes a flag word every pump (`SharedBlock.OffsetPvpUi` 0xCC8C, with the
  other player's peer id at 0xCC90): bit 0 "the host allows duels" (the host's `pvp`, adopted by a
  client like every behaviour flag), bit 1 "challenged", bit 2 "waiting". Zero (duels off, no
  session) hides the panel entirely. Block version 101; both fields are zeroed on disconnect.
- **No new intent field.** A key press publishes the same console line a player would type
  (`duel Aria`, `accept`, `decline`, `yield`) through the console mailbox, so the agent runs the
  code the typed command runs.
- **Never without a key.** A command goes out only on a key press edge (a key already held when the
  panel appears is not a press). Number keys only move the highlight; Enter is the only thing that
  challenges, and it does nothing with the list closed. A challenge is answered only by F11/F12.
  Silence is still a no, host-side.
- **Who is listed.** The players with a live stand-in in this world, which is exactly "same area"
  (the agent clears a slot when a peer is in another area). Levels are not shown: the remote slot does
  not carry them; the host's own refusal lines still cover "too hurt" and "busy".
- **Keyboard conflicts handled.** The panel is inert while the console, a conversation, a full-screen
  panel or a password prompt is up, and the list closes if one opens. While the list is open the
  Enter-to-chat prompt is held off so Enter confirms a pick and does not also open the console.

Known limits, honestly: the arrow keys also reach the game while the list is open (the character
can turn or walk), Esc also opens the game menu (which closes the list), and the key choice (F10 to
F12) is not configurable. F12 is Steam's default screenshot key on a Steam install.

### NEEDS-LIVE checklist (duel panel)

Two instances, same area, `pvp = duel` on the host. Capture the plugin log (`duel panel: ...` lines)
and a screenshot of each state.

1. **Hidden when off.** With `pvp = off` on the host, no hint line appears, F10/F11/F12 do nothing,
   and the log has no `duel panel: Idle`. Turn it on: the hint appears on the joiner too (a client
   adopts the host's value).
2. **Hint and list.** With the partner in the area the faint "F10: challenge a player..." line shows
   below the connection indicator and clears neither the minimap nor the chat overlay (800x600 and a
   wide resolution). F10 opens the list with the partner's name; with nobody else in the area the
   list says so. Leaving the area closes it.
3. **Pick and challenge.** Up/Down and 1-9 move the highlight; Enter sends `duel <name>` (log
   `duel panel: sent 'duel Aria'`); the challenger's screen shows the waiting line and the target
   gets the prompt. A name with a space and a name that is a prefix of another both pick the right
   player. Enter does NOT open the console chat prompt while the list is open or on the confirming
   press.
4. **Answer by key.** F11 starts the countdown; F12 declines ("said no. Nothing happened."). Doing
   nothing for 30 s clears the prompt and nothing is sent. F11/F12 pressed when nothing is pending do
   nothing. Both directions (host and joiner).
5. **Yield by key.** During the countdown and during the fight F12 ends the duel (`YouYielded` line,
   hit points restored); a single press is enough; no other key (Enter, Esc, F11, 1-9) yields.
6. **No stray fire.** Hold F12 through a challenge appearing: nothing is sent until a fresh press.
   Open the console (~) and type 1-9, F10, F11, F12: the panel does nothing. Open the inventory or a
   conversation with the list open: the list closes.
7. **Keyboard side effects.** Record what the arrow keys do to the character while the list is open
   (turn/walk), and whether F10, F11 or F12 trigger anything in the retail game (quicksave, screenshot,
   Steam overlay). If any does, the keys need moving.
8. **Block v101.** A plugin and agent from this build attach (no "plugin shared block not found");
   `0xCC8C` reads 1 with duels on and idle, 3 on the target while challenged, 5 on the challenger
   while waiting; all zero after `disconnect`.

## Phases 3 and 4 as built (2026-10-09)

The arena prototype and the shipped arena module, built as one piece (the prototype flow of phase 3 only
makes sense on top of the module of phase 4, so the in-place Taris ring was skipped). Nothing here has run
in the game.

- **The module.** `ArenaModule` (Memory) builds `modules\k_coop_arena.mod` from the player's OWN files, so the
  package carries no BioWare bytes: `module.ifo`, `m02ae.are` and the instance file out of `tar_m02ae.rim`, the path
  points out of `tar_m02ae_s.rim`, `m02ae.lyt`/`m02ae.vis` out of the BIFs (Override first, as the engine resolves
  them). Cut to the ring room `m02ae_07a`: one room in the layout and area, a visibility file saying it sees nothing
  else (which is what the cantina's says), the path points and waypoints inside the ring (x 184-216, y 84-118),
  nothing else in the instance file (no creatures, doors, triggers, placeables, sounds, stores or cameras; the ring's
  door is dropped, its blueprint lives in the cantina's script archive, so the doorway is a gap with no floor beyond
  it). Every module and area script is blank except the stock player-death trio. New names throughout
  (`k_coop_arena` for module, area, layout), so nothing of the cantina's is shadowed. Added waypoints:
  `k_coop_arena_a1/a2` (team A, east end, facing west, 213.6 and 209.6, 100.0), `k_coop_arena_b1/b2` (team B, west
  end, 186.6 and 190.5, 99.8-99.9) and `k_coop_arena_s` (spectators, 200.0, 88.4). The four fighter spawns are the
  cantina's own `tar02_wppcarena`, `tar02_wppcarena2`, `tar02_wpopparena`, `tar02_wpopparena2`, known to stand on the
  floor. The entry point is `a1`. `ErfWriter.Build` writes the archive with no dates and positional ids, so the same
  game files always give the same bytes. A game whose cantina is not as expected (no ring room, no
  `tar02_wppcarena`, trackcount or obstaclecount not 0) gets no arena and a reason, not a broken one.
- **Install.** `PackageInstaller` generates it after the Override assets (never failing the install), `--check`
  reports it (PASS when current, NOTE when absent or stale, never FAIL), uninstall removes that one file, the
  locked-file probe covers it. The agent also writes it on first start when it is missing (this is the Linux route:
  `install.sh` cannot run the generator natively, so it reports and removes the file, and says so). A file written
  while the game is running does not count as ready until the next start, because the engine read its modules folder
  at launch. The generated file is excluded from the module digest (presence is negotiated, a modded cantina already
  differs).
- **Setting.** `pvp = off | duel | arena` (arena implies duel); behaviour bit 23, key `pvp_arena`,
  host-authoritative, three readers agreeing (`CoopConfig`, `AgentSettings`/`AgentComposition`, the digest). A game
  says `PvpOp.AwareArena` instead of `Aware` when the setting is on and it had the arena file at start.
- **Flow.** `arena <name>` is `PvpOp.ChallengeArena`. Extra refusals: arena off, a player or the host without the
  file (named), the host in another area (one warp cannot take everyone). The round: Challenged -> (accept) Entering
  -> Countdown -> Live -> Leaving -> Idle. Entering: the host's `ArenaPump` asks for ONE `TryRequestTravel` into the
  arena and every other game follows the host's module as it always does; when both duellists and the host have been
  in the module for 3 s the countdown starts; 60 s limit. Each game moves its own character (the `Teleport` cheat) to
  its spawn on the countdown: challenger to team A, target to team B, anyone else to the spectator mark, landing
  checked against the position read back, 6 tries. The fight is the duel as built. Leaving (any end, including called
  off or a player leaving): 2 s later the host asks for ONE `TryRequestTravel` back to the module it stood in, and
  every game, once back, is put on the spot it stood on. Idle when the two duellists and the host are all out of the
  arena for 3 s, or after 60 s; a host still in the arena when the round let go is taken home once more (at most every
  30 s). A player whose state vanishes for under 20 s during the moves is loading, not leaving. No new plugin block
  fields: Entering and Leaving read as phase 0 to the plugin, so nobody is pinned or hostile on the way.
- **Saving.** Blocked for EVERY player from Entering to the end of Leaving (`PvpPumpRule.SaveAllowed`), by the
  dispatcher while standing in the arena module whatever the round says, and by the plugin's save hook while the
  engine's current module is the arena (this catches the module-enter autosave of the walk in, before the pin is up).
- **Not built.** 2v2 with companions (the spawn waypoints exist; companions stay benched), best of 3, a scoreboard,
  the throwaway-save flow B, teams chosen by command. Best of 3 and 2v2 are the next arena step.

## NEEDS-LIVE checklist (arena, phases 3 and 4)

Two instances on the rig, a host and a joiner, `pvp = arena` on the host only, both with `modules\k_coop_arena.mod`
present BEFORE the games start (run `install`, or start the agent once and relaunch). Same area for both. Log lines
are `arena: ...` (agent), `duel: ...` and `arena: refused a save request` (plugin).

1. **The file.** `install --check` PASSES with "arena fights available". List `modules\k_coop_arena.mod` with
   `tools/kotorfiles.py list`: six members (`module.ifo`, `k_coop_arena` .are .git .lyt .vis .pth). Two installs from
   the same game files give byte-identical files. Without the file on the joiner, `arena <joiner>` from the host
   prints "<joiner> does not have the arena installed yet..." and nothing moves.
2. **The module loads at all.** `warp k_coop_arena` on a throwaway instance (not during a round): the engine loads
   it without crashing, the ring is visible and walkable, the player stands at the east end facing west. Record what
   the doorway gap looks like and whether a player can walk out of the ring (they must not). If the engine refuses a
   module whose layout and area are renamed, set `ArenaModule.AreaResref` back to `m02ae` (and drop the renamed
   layout) and re-test.
3. **Refusals.** With `pvp = duel` on the host, `arena <name>` says the host has not turned on the arena. With the
   host standing in another area than the two players: "the host has to be in this area too".
4. **One warp in.** Challenge and `accept`. Log `arena: round N - will return to <module> at (x, y)` on both, then on
   the host exactly ONE `arena: Enter - asking the plugin for ONE module change to k_coop_arena (accepted)` and exactly
   one plugin `travel: moving to module 'k_coop_arena'`. The joiner follows by itself (no second request from its
   agent). No crash.
5. **Spawns.** Challenger stands on the east end and target on the west end (`where` on each; log `arena: Place` then a
   position within 3 of the spawn). A bystander (needs a third instance) stands on the spectator mark.
6. **Countdown starts only after arrival.** "Duel with X starts in 5" appears about 3 s after both are in the arena,
   not before; `duel: round N armed` appears after it, not on `accept`.
7. **The fight.** Phase 1 items 4, 5, 8 and 9, now inside the ring (cursor turns red, damage lands on the real
   character, the 1 hp floor holds, the host ends it at 25%).
8. **Saving blocked in the arena.** F4, the save panel and `save` are refused on both sides from the moment of
   `accept` ("You cannot save while the arena fight is on or in the arena"). Look for the plugin line `arena: refused a
   save request` at arrival, and, after the whole round, that a quicksave made back in the campaign loads into the
   campaign (not the arena) on both instances. The module-enter autosave of the walk in is the one most likely to slip
   through: check the autosave slot's module afterwards.
9. **One warp back.** On the end, 2 s after the result, ONE `arena: Return - asking the plugin for ONE module change to
   <origin>` on the host and one plugin `travel:` line; the joiner follows. Each player is put back on the spot they
   stood on (`arena: Restore`, position within 3 of it; the module's own entry point is not where they end up). Hit
   points and Force points are what the countdown snapshot recorded. No stale hostile body, no pin left.
10. **The round lets go.** The phase goes Idle about 3 s after both are home (log `duel: over - releasing the pin
    (round N: ...; everyone is back)`), and a new `duel` or `arena` can be started a minute later.
11. **Called off and failure paths.** `yield` during the walk in: both come back, nobody is left in the arena. Kill the
    joiner's game during the fight: the host wins by leave and is taken home. Kill it during the walk in: after 20 s
    the host is told the other left and goes home. Make the host's travel fail (rename the file on the host only, with
    the game running, then challenge from a game that still has it): nothing moves, and after 60 s the round is called
    off with "took too long".
12. **A host that is a bystander.** Two joiners fight with the host looking on (needs a third instance): the host's own
    game makes the two warps and stands on the spectator mark.
13. **Rapid repeats.** Two arena rounds a minute apart: clean each time, no crash on the second warp in (the engine is
    fragile around module changes, so look hard here; see transition-crashes.md).
14. **Saving afterwards.** After the round, `save` works again on both, and loading that save puts the player in the
    campaign module, not the arena.

Items most likely to fail: 2 (renamed layout), 8 (the walk-in autosave), 5 (the `Teleport` cheat inside a module the
agent knows nothing about), 9 (position restore after the module's entry spawn), 13 (rapid warps).
