# Player-count scaling (review blocker #9, roadmap #41)

Every fight was vanilla-balanced for two PCs. Four coop.ini keys now scale with the number of
connected players. **Status: built; health save/load idempotence remains open.** The safe default
for `enemy_health` is currently vanilla 100%; `auto` remains an explicit opt-in until persisted
scaled maxima can be identified without multiplying them after reload. This prevents another
automatic multiplication on future loads but does not restore saves that already contain a
scaled maximum.

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
shipped ini): `enemy_health = 100` (safe vanilla fallback while reload idempotence is open),
`enemy_damage = auto` (live-verified), `xp_rate = auto`, `store_stock = auto`. Set
`enemy_health = auto` explicitly to opt into the player-count health curve. `store_prices` is NOT built: the price lookup behind
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
new to this codebase. Health scaling also needs save/load persistence before the reload checklist
item can be closed.

## Save/load health compounding (confirmed defect; open)

The per-epoch latch prevents repeated writes while a world is running, but it is deliberately
cleared by `EnemyScalingController.Invalidate()` at every world epoch. The health write stores the
scaled value directly in the creature's `+0xE0` maximum field, and no vanilla/base maximum is
retained in the engine object. A save therefore contains the already-scaled maximum while the
next load starts with an empty latch.

This was reproduced in the restart/load probe with health scaling at 150%: a creature logged
`131/197 -> 197/296` after the new epoch, showing that the saved 197 maximum was multiplied a
second time. This is separate from the object-table scan performance fix; replacing the process
sweep does not make the stored value idempotent.

Inferring whether a creature was previously scaled from its current maximum or current/max ratio
is unsafe. For example, a vanilla creature with maximum 23 is indistinguishable from a creature
whose vanilla maximum was 15 and was scaled to 23 at 150%. Reusing a game global would corrupt
story state, and per-object local variables remain unverified and explicitly out of scope in
`docs/research/quest-sync.md` §8. No engine marker or new native address has been added.

The practical design is a mod-owned sidecar, still unimplemented:

1. After a save has landed, record an exact fingerprint of the stable `SAVEGAME.sav`, with the
   existing `SaveIdentity` as a secondary guard. `SaveIdentity` alone is intentionally stable for
   the same module/object layout and is not unique across revisions of a save slot.
2. Associate records with the saved module and object identity (object id plus tag/template), and
   retain the original maximum and the applied percentage. The existing `SaveFlushHook` capture
   and `ReissueServerSaveGame` path can identify a held save request, but a successful reissue
   only means the engine accepted it; the sidecar must wait for the archive write to settle before
   recording the fingerprint. Writes should use the existing temp-file/replace pattern.
3. Establish the exact loaded archive key before the first health scan. If the loaded save or the
   sidecar cannot be identified or parsed, fail closed for health scaling for that epoch rather
   than risk multiplying a persisted maximum. This needs a verified load-request source and a
   separate completion/baseline binding step.
4. For a matching record, seed the applied-object latch and preserve the saved ceiling. Apply the
   current percentage only to newly seen objects. Re-scaling an existing saved body when the
   player count changes needs a separate baseline/current-health design so it does not heal or
   over-damage the creature.

Until the sidecar, successful-load binding, and baseline apply behavior exist, reload idempotence
remains open and the health setting must retain its live-verification gate.

### Exact load-request provenance foundation (2026-09-23)

The plugin has a **read-only** load-request observer in shared-block version 74. It watches the
main-menu `CSWGuiSaveLoad::LoadPCAutoSave` call (`0x006CA250`) and the server load dispatcher at
`CServerExoAppInternal::LoadGame` (`0x004BA640`). The server routine receives the slot and
CExoString pointer in its first two stack arguments. Both hooks capture before the retail load
body runs. The string is bounded to 64 bytes, with the engine's length-includes-NUL terminator
removed from the published bytes.

The v74 mailbox occupies `0x4E50..0x4EA8`: an even/odd stable generation, slot, source, capture
status, `WorldLoadSerial` observed at request time, byte length, and bounded value bytes. The
memory bridge exposes a stable-generation read API. An odd/changed generation, unreadable or
malformed record, or unsupported plugin version fails closed. This records the exact archive
request and its relationship to the plugin's then-current world-load serial. It does **not** say
the request succeeded, identify the world after loading, persist scaling data, or change health
scaling. No SHA, successful-load acknowledgement, or HP write is inferred from this mailbox.

The first live probe (2026-09-23) exposed a distinct `LoadPCAutoSave` input path. The mailbox
reported generation 2, main-menu source, slot 0, and `InvalidSaveName`. This was the plugin's
synthetic entry: its flags at `+0x1C4` are `0x30`, its real-save fields (`+0x1C8/+0x1D8`) are not
populated, and its direct save-directory CExoString at `+0x210` was `000001 - AUTOSAVE`. This
record is an invalid capture, **not** evidence that slot 0 or an empty save name was loaded.
The executable's `ShowGame` at `0x6C89D0` tests bit 0 of `entry+0x1C4` and takes the real-save
display branch only when it is set; the plugin's direct-directory entry uses exactly flags
`0x30`. The observer now classifies those cases separately: a real row uses `+0x1C8/+0x1D8`,
the verified direct-directory path uses `+0x210` with an explicit no-slot sentinel, and other
flag patterns are marked unsupported. For the direct source, the public API exposes a directory
name and no save slot/name. This correction is statically verified; a new build still needs a
live read confirming `MainMenuDirectDirectory / CapturedDirectory / 000001 - AUTOSAVE`.

The remaining persistence work is still substantive: after the engine finishes writing a save,
fingerprint the stable `SAVEGAME.sav` bytes and atomically store per-creature original maximum,
applied percentage, and object identity under that fingerprint plus the exact slot/name. On a
later load, pair the request record with the subsequent world-load epoch and verify the loaded
archive fingerprint before seeding the health latch. A missing, stale, mismatched, or unreadable
sidecar must leave health scaling disabled for that load epoch. The apply path also needs a
reviewed baseline/current-health rule for percentage changes that preserves the saved health
ratio without healing or over-damaging. Until that binding and persistence behavior are tested,
`enemy_health` remains at the safe default of 100% and its reload checklist stays open.

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

Two players on the Endar Spire deck, with explicit `enemy_health = auto`: `scaling: enemy health 150%, enemy damage 115%`, then `enemy 0xE 'sithsoldier' hp 15/15 -> 23/23 (150%), read back 23/23` (0x10 the same, `end_gensith` 12 -> 18) on BOTH instances; both agents logged the resolved policy (`2 player(s): enemy health 150% (auto) ...`). Watch item: the host's agent briefly resolved `1 player(s)` about 5 s after the join (12:13:52) before returning to 2; an enemy first sighted in that window keeps 100% for the module. The shipped default is now 100% because the saved maximum is currently multiplied again after reload; the explicit auto path remains live-verified. Not exercised: the damage rewrite at the hit detour (needs a real hostile hit on a player; `hurt` is not one), XP delta scaling, store stock top-up, 3+ players.

## The player-count flap, closed (2026-09-19)

The live pass above saw the host's count read 2, then 1 for about five seconds, then 2 as the joiner arrived. Because health is scaled once per enemy on first sighting, an enemy first seen inside the one-player window would have kept 100% for the module. `PlayerCountSettleRule` (src/KotorCoop.Session) now sits between the agent's raw count and the curve: a rise is adopted at once, a fall only after it has held for 10 s (`FallSettleMs`). A real leave therefore relaxes the curve 10 s late; a transient dip costs nothing. Unit-tested against the measured flap (`PlayerCountSettleRuleTests`); not re-run live.

## The damage rewrite, verified (2026-09-19)

`PhantomHitHook.ScaleHostileDamage` now logs its first twelve rewrites (`scaling: enemy damage <before> -> <after> (<percent>%) on a hit to a player body (rewrite #N)`), because until then the only trace was a counter nothing read. Live, two players, `enemy_damage = auto`: `spawncreature n_sithsoldier001 2` beside the host, and the first hostile hits logged `enemy damage 4 -> 5 (115%) (rewrite #1)` and `1 -> 1 (115%) (rewrite #2)`. The rewrite fired on the HOST only (the client's log had none), as designed: the host owns the hostile. Consequences: `enemy_damage` now defaults `auto` (every reader agrees: `CoopConfig` record and parse, `AgentComposition`, `BehaviourDigest.Default`, `PlayerCountScalingSettings.Default`, the shipped template). Known limit: `ScaleDamage` works in whole points, so a 1-point hit is unchanged at 115% and small hits round; the curve matters for the 4-10 point hits that dominate the early game. Not exercised: 3+ players, the client being the one hit (a peer's stand-in on the host), XP delta and store stock.

### Staged direct-directory capture check (2026-09-23)

With `10e2194`, both engines loaded named Korriban checkpoints through the mod’s staged
autosave path. Each mailbox reported generation 2, source `MainMenuDirectDirectory`,
status `CapturedDirectory`, no save slot, and exact directory `000001 - AUTOSAVE`.
Subsequent mid-session loads advanced the generations and recorded their request-time
world-load serials (host generation 6 / serial 2; client generation 8 / serial 3).
This validates that replay path and its bounded native CExoString read. Ordinary retail
selected-row/server-request capture and archive-fingerprint/baseline persistence still
need their own validation; no HP persistence completion is claimed.
