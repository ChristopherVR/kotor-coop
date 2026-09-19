# Player-count scaling (review blocker #9, roadmap #41)

Every fight was vanilla-balanced for two PCs. Four coop.ini keys now scale with the number of
connected players. **Status: built, NEEDS-LIVE.** Nothing here has run in the game.

## Keys and curve

`enemy_health`, `enemy_damage`, `xp_rate`, `store_stock` = `auto` or a percent (lenient parse,
`%` accepted, clamp 50-400, junk falls back to the default). `auto` resolves from the live player
count (this instance + connected peers) in `PlayerCountScalingRule.Curve`:

| Players | enemy_health | enemy_damage | xp_rate | store_stock |
| --- | --- | --- | --- | --- |
| 1 | 100 | 100 | 100 | 100 |
| 2 | 150 | 115 | 100 | 150 |
| 3 | 200 | 125 | 100 | 200 |
| 4+ | 250 | 135 | 100 | 250 |

Defaults (three readers agree: `CoopConfig`, `AgentComposition ??`, `BehaviourDigest.Default`,
shipped ini): `enemy_health = 100`, `enemy_damage = 100` (**OFF until live-verified**, because
they are the two new engine writes), `xp_rate = auto`, `store_stock = auto`. Set them to `auto`
once the checklist passes. `store_prices` is NOT built: the price lookup behind
`CSWGuiStore::BuyItem` was never traced and needs RE.

## Wire

`BehaviourDigest` scheme 1 -> 2 carries the four SETTINGS (int32 each, 0 = auto), compared as
host-authoritative keys (`W_CFG` warning + adopt, `SessionOverrides.EnemyHealth` etc.). The
percent is never sent: both sides resolve it from the same setting and the same player count.
**Protocol version 52 -> 53.** Shared-block version 67 -> 68 (agent signature byte 0x43 -> 0x44):
`OffsetScalingCombat` 0x4C20 (health low 16, damage high 16) and `OffsetScalingEconomy` 0x4C24,
no `TotalSize` growth, cleared on disconnect.

## Apply points

- **Health** (`EnemyScalingController`, plugin, every instance): once per object id per module
  epoch (`ScaledObjectLatch`), hostile (engine reputation == 2), non-plot, non-PC, non-party,
  non-phantom, living creatures get stored max (+0xE0) and current scaled, max first then
  `Odyssey.SetCurrentHitPoints` (the engine's own `SetMaxHitPoints` order). Runs before
  `CombatController`, so `MirrorHealthRule`'s max clamp sees the scaled ceiling. A percent that
  rises mid-module applies to enemies first seen afterwards.
- **Damage** (`PhantomHitHook.ScaleHostileDamage`): at the `OnApplyDamage` detour, a hit on the
  local PC / local party roster / a watched phantom / a released companion whose creator is not
  one of those is rewritten in place (all per-type slots) before the ledgers read it, so a
  phantom's forwarded total already carries the multiplier and the owner applies it once.
  Skipped under `SuppressDealt` (the mod's own applies) and for creator 0 (mines, scripts).
- **XP** (`PartyPump.PumpProgressionHost`, `XpRateRule`): the host scales the DELTA its own
  engine granted and adds the bonus before the max-merge. Double-count hazard: our own write
  reads back as a rise, so `Rebaseline` follows every write. Rounding remainder carries. Below
  100 cannot work with a client connected (its vanilla total wins the max); logged once.
- **Store** (`StorePumpV2.TopUpStockOnce`): on the host's first scan of a store, finite stacks
  are topped up via the existing `EnqueueApply(Add)` path. Once per store per playthrough via
  `StoreStockScaleLatch` (`saves\kotorcoop-storescale.txt`, reset on entering `end_m01aa`); no
  writable marker = no top-up. Empty scans are not marked.

## Unfinished / not done

`store_prices`; the in-game status line for the economy word; a live A/B of every apply point.
The stored-max write (health) and the in-place effect-list write (damage) are the two writes
new to this codebase.

## NEEDS-LIVE checklist

1. `enemy_health = auto`, two players: `spawncreature n_sithsoldier001 1` on the host; log line
   `scaling: enemy 0x.. hp a/b -> c/d`, read back matches; `sethp.ps1` read shows the scaled hp.
2. Kill it from the joiner; the client copy must not snap back to vanilla max (mirror clamp).
3. Reload the module: the same enemies scale once, not twice.
4. `enemy_damage = auto`: `hurt` is local-only and does not exercise the hook; take a real hit
   from a spawned hostile and compare the `combat ground-truth` line's total to vanilla (x1.15).
5. A hit on the partner's phantom: the partner's real body loses the scaled amount exactly once.
6. `xp_rate = 200` on the host: kill something; host XP rises by 2x the vanilla grant, no
   compounding on the next ticks, the client converges to the host's total.
7. `openstore <tag>` on the host: `store_stock:` line, stock rises, the client sees it after
   opening; reopen and reload the save: no second top-up.
8. Alone (1 player): every value reads 100 and nothing is written.

## Live verification (2026-09-19)

Two players on the Endar Spire deck, `enemy_health = auto`: `scaling: enemy health 150%, enemy damage 115%`, then `enemy 0xE 'sithsoldier' hp 15/15 -> 23/23 (150%), read back 23/23` (0x10 the same, `end_gensith` 12 -> 18) on BOTH instances; both agents logged the resolved policy (`2 player(s): enemy health 150% (auto) ...`). Watch item: the host's agent briefly resolved `1 player(s)` about 5 s after the join (12:13:52) before returning to 2; an enemy first sighted in that window keeps 100% for the module. `enemy_health` now defaults `auto`. Not exercised: the damage rewrite at the hit detour (needs a real hostile hit on a player; `hurt` is not one), XP delta scaling, store stock top-up, 3+ players.

## The player-count flap, closed (2026-09-19)

The live pass above saw the host's count read 2, then 1 for about five seconds, then 2 as the joiner arrived. Because health is scaled once per enemy on first sighting, an enemy first seen inside the one-player window would have kept 100% for the module. `PlayerCountSettleRule` (src/KotorCoop.Session) now sits between the agent's raw count and the curve: a rise is adopted at once, a fall only after it has held for 10 s (`FallSettleMs`). A real leave therefore relaxes the curve 10 s late; a transient dip costs nothing. Unit-tested against the measured flap (`PlayerCountSettleRuleTests`); not re-run live.
