# Player-count scaling (review blocker #9, roadmap #41)

Every fight was vanilla-balanced for two PCs. Five coop.ini keys now scale with the number of
connected players. **Status: built; health save/load idempotence is wired (block v76,
[enemy-health-persistence.md](enemy-health-persistence.md)) but not yet live-verified.** The safe default
for `enemy_health` is currently vanilla 100%; `auto` remains an explicit opt-in until persisted
scaled maxima can be identified without multiplying them after reload. This prevents another
automatic multiplication on future loads but does not restore saves that already contain a
scaled maximum.

## Keys and curve

`enemy_health`, `enemy_damage`, `xp_rate`, `store_stock`, `store_prices` = `auto` or a percent (lenient parse,
`%` accepted, clamp 50-400, junk falls back to the default). `auto` resolves from the live player
count (this instance + connected peers) in `PlayerCountScalingRule.Curve`:

| Players | enemy_health | enemy_damage | xp_rate | store_stock | store_prices |
| --- | --- | --- | --- | --- | --- |
| 1 | 100 | 100 | 100 | 100 | 100 |
| 2 | 150 | 115 | 100 | 150 | 100 |
| 3 | 200 | 125 | 100 | 200 | 100 |
| 4+ | 250 | 135 | 100 | 250 | 100 |

Defaults (three readers agree: `CoopConfig`, `AgentComposition ??`, `BehaviourDigest.Default`,
shipped ini): `enemy_health = 100` (safe vanilla fallback while reload idempotence is open),
`enemy_damage = auto` (live-verified), `xp_rate = auto`, `store_stock = auto`,
`store_prices = 100`. Set
`enemy_health = auto` explicitly to opt into the player-count health curve. `store_prices` is
100 by default, and `auto` currently resolves to the same neutral price until a balance curve
is chosen.

## Wire

`BehaviourDigest` scheme 3 (protocol version 63) carries all five SETTINGS as int32 fields,
including `store_prices`. They are host-authoritative keys (`W_CFG` warning + adopt,
`SessionOverrides.EnemyHealth` etc.). The resolved percent is not sent on the network: both
sides derive it from the adopted setting and shared player count. Shared-block version 75
(signature byte 0x4B) adds the resolved store price word at 0x4C40. The existing combat and
economy words remain at 0x4C38 and 0x4C3C; all three are cleared on disconnect.

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

The in-game status line for the economy word; a live A/B of every apply point, including prices.
The stored-max write (health) and the in-place effect-list write (damage) are the two writes
new to this codebase. Health scaling also needs save/load persistence before the reload checklist
item can be closed.

### Store price path (2026-09-23 static GOG trace)

The GOG executable and `tools/kotor1_0_3.db` identify the retail lookup methods as
`CSWGuiStore::GetItemBuyValue` (`0x006C0790`) and `GetItemSellValue` (`0x006C07F0`). Fresh
`tools/disasm.py` output shows both resolve the live store from `panel+0x2278` through
`CServerExoApp::GetStoreByGameObjectID` (`0x004AE7B0`), call `CSWSItem::GetCost`
(`0x00554000`), then apply that store's modifier and integer `/100` conversion:

| Method | Retail calculation from the traced instructions |
| --- | --- |
| `GetItemBuyValue` | `floor(item.GetCost() * (store.mark_up [+0x248] + store[+0x250]) / 100)` |
| `GetItemSellValue` | `floor(item.GetCost() * (store.mark_down [+0x244] + store[+0x24C]) / 100)` |

The formulas describe ordinary nonnegative values; the retail multiply retains only the
low 32 bits before unsigned division, so overflowing inputs do not follow unbounded arithmetic.
The database names `CSWSStore.mark_up` and `mark_down`; it does not name the additional terms at
`+0x250` and `+0x24C`. Their arithmetic role is verified, but their exact semantic source is
unknown, so do not rewrite or replace these per-store fields. `CSWSItem::GetCost` (`+0x260`
`add_cost`, base-item cost float at base-item `+0xC4`) is the retail base. Its body returns zero
for an item whose plot flag is set; otherwise it computes the integer item cost. The two store
lookups then return zero when either the store or item is unresolved.

The getters are the shared price boundary, not just a display helper. Static direct-call xrefs
show `GetItemBuyValue` at `0x006C0B00` (store-list row text), `0x006C0C68` (`BuyItem` click
handler), and `0x006C11B9`/`0x006C1205` (other store panel logic); `GetItemSellValue` is called
at `0x006C0B19` (sell-row text), `0x006C0E0C` (`SellItem` click handler), and `0x006C101C`
(other panel logic). The transaction handlers recalculate the value when clicked, then update
the panel's cached credits at `+0x2270`, call `CSWSCreature::SetGold` (`0x004EDD90`), and move
the item through `CSWSStore::SellItem` (`0x005C6F70`) for a buy or `CSWSStore::AcquireItem`
(`0x005C7610`) for a sell. The real `BuyItem` handler has no independent affordability check;
the surrounding panel-action path (`0x006C1130`) reads `GetItemBuyValue` against the cached
credits and also compares it against a persuasion-derived threshold before continuing. The mod's
`SellToStore` also calls `GetItemSellValue` directly, so it shares a getter-based price change.
These facts make a post-retail-price return adjustment at both getters a stronger candidate than
mutating store markup: row text, panel checks, retail click handlers, and the mod's sell path
would all observe the same adjusted integer, with the retail per-store formula left intact.
That interception also changes the price used by the panel's persuasion threshold, so that
interaction must be explicitly tested at high and low multipliers before adopting this hook
boundary as the final design.

The implementation adds `store_prices`, default 100; `auto` currently resolves to the neutral
100 until a balance curve is chosen. Fixed values are host-authoritative settings carried by
`BehaviourDigest` and `SessionOverrides`, then written as a resolved percent to SharedBlock
0x4C40. A verify-bytes entry hook runs after neither store mutation nor item-list patching: it
calls each original getter through a trampoline and scales that getter's returned integer once.
The exact eight-byte GOG entry prologue is `8B C1 8B 0D FC 39 7A 00` for both getters, with no
PC-relative instruction. A 100% value leaves the retail return unchanged. The rule uses a wide
multiply, floor division, keeps a positive price at least one credit, leaves zero and negative
values unchanged, and saturates at `Int32.MaxValue`. This post-getter scale also affects the
panel's persuasion threshold, which must be included in the eventual live A/B. If player count
changes while a store is open, the getter computes the current percent whenever the panel draws
or checks a transaction; its native list data is not rewritten. Do not write `mark_up`,
`mark_down`, `+0x24C`, or `+0x250` into a running store: it would mutate world state and could
compound or save differently. For post-return arithmetic use a wide intermediate, clamp to a
nonnegative `int`, preserve exact value at 100%, and specify rounding and low-price behavior
before coding; retail's own integer conversion has already happened before a proposed multiplier.

The static addresses and retail call sites are traced, and the scale/config/wire/hook bytes have
targeted tests. No live transaction yet proves the modified return is displayed and charged as
intended. Prices remain retail by default; use a non-100 setting only after a live buy/sell A/B
with a known base item and store verifies both transaction sides and the persuasion threshold.

## Save/load health compounding (confirmed defect; fix wired 2026-09-24, NEEDS-LIVE)

The fix is described in [enemy-health-persistence.md](enemy-health-persistence.md): a
per-world health gate in the plugin, an agent-side lineage, a settled-archive capture, and a
load binding that works on content. The history below is kept as it was written.

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

### Store price live A/B (2026-09-24)

Host `store_prices = 200`, joiner left at the default; the joiner adopted 200 through the
behaviour digest. `dan_droid` (`danm13`, markup 100, markdown 25): the host bought
`g_i_drdltplat001` (retail 75) for 150; the joiner bought it for 150 and sold it back for 36
(retail 18). The plugin logged each rewrite (`store prices: buy 75 -> 150 (200%)`,
`sell 18 -> 36 (200%)`), including the row-text values. After a joiner `disconnect` the same buy
cost the retail 75 and no rewrite was logged, confirming the session-scoped word resets. The
transactions used the `buy`/`sell` verbs (the retail click handlers); the persuasion-threshold
check and a mouse-driven purchase remain unexercised.

## NEEDS-LIVE checklist

1. `enemy_health = auto`, two players: `spawncreature n_sithsoldier001 1` on the host; log line
   `scaling: enemy 0x.. hp a/b -> c/d`, read back matches; `sethp.ps1` read shows the scaled hp.
2. Kill it from the joiner; the client copy must not snap back to vanilla max (mirror clamp).
3. Reload the module: the same enemies scale once, not twice. The full recipe (save, reload,
   percent change, dead enemy, fail closed) is at the end of enemy-health-persistence.md.
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

### Baseline sidecar foundation (2026-09-23)

`EnemyScalingBaselineDocument` and `EnemyScalingBaselineStore` now provide a bounded,
strictly validated sidecar tied to the exact archive SHA-256 and selected save key.
Records include module, object ID, tag, template, original maximum, and applied percentage.
Selection also checks that the archived maximum agrees with that baseline and percentage.
The store uses a flushed temporary file and atomic replacement, rejects changed archives,
and bounds sidecar reads. Direct-directory loads retain their exact directory name with
an explicit no-slot sentinel. Fifteen focused tests cover identity rejection, non-compounding
arithmetic, native arithmetic parity, and persistence validation.

This layer is not yet consumed by the save/load path or native scaling controller. Remaining
integration: retain baseline metadata in the controller; capture only after a completed save;
bind a successful load and its world epoch to the exact archive; preserve valid records for
visited modules; and define current-health handling when percentages change. The default
remains 100%, and scaled-save reload correctness is still open.

### Native baseline capture groundwork (2026-09-23)

The controller's existing fixed-capacity, module-epoch latch now retains the original
maximum HP and applied percentage with each scaled object ID. The actual scaling path
records this metadata, including when integer rounding leaves HP unchanged. Duplicate
observations preserve the first baseline; reaching capacity stops further writes, and
module invalidation clears the records.

This is not save/load persistence. The latch does not yet retain template/tag identity,
export a sidecar record, or import a verified saved baseline. Those steps require a
completed-save archive binding and a successful-load binding before the first health
scan. An import must preserve dead state and define current-HP handling when a percentage
changes; it must not resurrect an enemy. The default remains 100%.

Validation: the native x86 build passed with warnings as errors. The combined scaling,
host-world-readiness, and host-away suite passed 30 tests with warnings as errors.

### Exact baseline persistence wired (2026-09-24, shared block v76)

Steps 1-4 of the plan are now connected end to end. The full design, the static evidence and
the live recipe are in [enemy-health-persistence.md](enemy-health-persistence.md).

- **Gating.** The plugin gates health per world generation (Pending until the agent's verdict).
- **Latch.** Imported baselines are seeded so they are never re-multiplied, and they are
  reconciled from the original maximum when the percent changes. The ratio is kept, and a
  corpse is never resurrected.
- **Export.** The plugin exports its latch page by page, and it publishes every native save
  request.
- **Lineage.** The agent keeps a lineage (new game / exact load / transitions) with per-module
  coverage.
- **Capture.** After a save, the agent waits for the archive to settle, then joins the latch
  against the saved GIT `HitPoints`. `HitPoints` is the stored `+0xE0`: `SaveStats` writes it
  at `0x5B1FCD`, and `ReadStatsFromGff` reads it back at `0x5B1264`.
- **Load binding.** A load is bound by provenance, a later load edge (or a request made with
  no world standing), and the archive's SHA-256 and identity.
- **Latch release.** `Invalidate()` no longer releases the latch. "Transition flushed but not
  taken" used to clear it, then rescaled the same standing creatures, which was a second
  compounding path.

The `enemy_health` default stays at 100%. Nothing here has run on the rig.
