# PvP: combined plan (2026-10-09)

Synthesis of four research passes, all read-only (code, docs, game files; nothing run in game):
[pvp-product.md](pvp-product.md) (rules and player experience),
[pvp-engine-feasibility.md](pvp-engine-feasibility.md) (what the engine allows),
[pvp-networking.md](pvp-networking.md) (authority, latency, wire),
[pvp-arenas.md](pvp-arenas.md) (where to fight). Where they disagree, this file says which way
and why. Phase 1 (the duel MVP) was built the same day and is unit- and loopback-tested but
never watched in the game: see "Phase 1 as built" and the NEEDS-LIVE checklist at the end.
Nothing later than phase 1 is built.

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

