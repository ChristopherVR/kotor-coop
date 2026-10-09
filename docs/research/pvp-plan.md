# PvP: combined plan (2026-10-09)

Synthesis of four research passes, all read-only (code, docs, game files; nothing run in game):
[pvp-product.md](pvp-product.md) (rules and player experience),
[pvp-engine-feasibility.md](pvp-engine-feasibility.md) (what the engine allows),
[pvp-networking.md](pvp-networking.md) (authority, latency, wire),
[pvp-arenas.md](pvp-arenas.md) (where to fight). Where they disagree, this file says which way
and why. Nothing here is built.

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
