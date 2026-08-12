# Enemy materialisation — the mirror builds bodies it does not have

Closes the gap `client-encounters.md` §8 documented: `EnemyPresenceController` and
`CombatController` used to give up when `ResolveServerObject(ownerId)` returned 0, so an
enemy the owner's engine CREATED after load — an encounter spawn, a scripted wave — was
invisible on the mirror while still able to hurt its player through the phantom.

Tag key: **VERIFIED** = measured on the running game or read out of the binary/data ·
**DESIGN** = a choice made here.

## 1. Why the blueprint name cannot travel — VERIFIED

- No resref survives on a live `CSWSCreature`: the spawn path's `LoadFromTemplate`
  consumes the CResRef and stores nothing (symbol DB: no template field on the class;
  placeables keep theirs at +0x378, creatures do not).
- The tag is NOT a blueprint proxy: checked offline against every `.ute` in the retail
  install, **53 of 156 encounter CreatureList entries have a tag that differs from the
  resref** — including every tar_m03 Black Vulkar. The item channel's tag-as-blueprint
  precedent does not transfer.
- §8's `(encounterId, creatureListIndex)` key would additionally need a
  `SpawnCreatureFromSpawnList 0x00591CA0` hook for attribution, and covers only
  encounter spawns — not script spawns.

## 2. What travels instead — DESIGN, protocol v18

The census (`EnemyState`) carries each creature's **appearance.2da row** and **repute.2da
faction id** (+4 bytes → 40 B/enemy, 32 enemies = 1293 B, fits one datagram,
test-asserted). That is the whole recipe, because everything else already flows:

- spawn `k_coop_phantom` (shipped in override; `n_commm` fallback) at the published
  position — the phantom spawner's own machinery;
- repaint: appearance row into `CSWSCreature+0xA60` + `RebuildAppearance` (the
  phantom-equip rule applies: never write the row without the rebuild);
- faction: the owner's id verbatim through the engine's own membership call
  (`SetCreatureFaction`) — hostile reads hostile, a neutral bystander stays neutral;
- `MarkAsMirroredEnemy`: plot 0, min-1-hp, corpse unselectable (its loot exists only in
  the owner's world), attack bit cleared — `MarkAsRemotePlayer` minus the forced-Neutral.

Position, gait, swings, health pin and death then arrive through the existing channels.
Kill switch `enemy_materialise=0`.

## 3. The id map — `MaterialisedEnemies` (plugin, static)

- ownerId → localId consulted by `EnemyPresenceController.DriveOne` and both
  `CombatController` resolve sites before `ResolveServerObject`.
- localId → ownerId applied in `PhantomHitHook` when recording damage dealt, so the
  ledger stays in the owner's id space end to end.
- ⚠ An UNMAPPED owner id that numerically equals one of our stand-ins' local ids is
  never resolved as GIT-identical (it would drive the stand-in to two masters) — the
  phantom-id collision guard, one table over.
- Lifecycle: spawn rate-limited (30 frames, 5-failure latch); body destroyed after 5 s
  absent from the census; flushed in both pre-store windows (a leftover becomes a
  permanent NPC in the save, same as phantoms); forgotten without destroy on module
  change; a mapping and its body are released TOGETHER (dropping only the mapping leaked
  one body per spawn cooldown — measured, 13 stray Sith soldiers in one session).

## 4. Traps found live — VERIFIED, each cost a real failure

1. **`CGameObject+0x08` is not a plain type enum.** The old resolver required
   `== 5`; a live census creature read `0x40005` and was silently dropped — and the
   materialiser answered "unresolvable" by spawning a duplicate every half-second.
   Identity is the vtable (`0x007470C8`), the test every other resolver already used.
2. **Prove absence before building.** `TryMaterialise` runs the authoritative
   creature-table sweep (`CreatureFinder.FindByObjectId`) before spawning; "the cheap
   resolver said 0" has already been wrong once. Sweeps that prove presence are cached
   (`ProvenPresent`) so a persistent false alarm does not scan memory forever.
3. **Birth grace.** A stand-in's fresh id can fail its first resolve (cause not yet
   pinned down; measured at 21:44, owner 0x3B, five bodies burned in two seconds).
   A mapped body may not be condemned as unresolvable in its first second, and the
   condemn path logs the resolver's raw answer.
4. **`CreatureFinder` must not fault.** The sweep read regions directly after
   `VirtualQuery`; during a module load a region vanished mid-scan and the AV became a
   NativeAOT `FailFast` — the whole game died. All reads go through
   `ReadProcessMemory`-on-self now (an unreadable page is a skipped region), and the tag
   compare validates the string header before following it.

## 5. Live verification

- Scripted lifecycle test (inject a presence slot whose id exists nowhere + appearance
  28 + faction 1 into a client's block): stand-in spawned and mapped, repainted, faction
  joined, standing exactly at the published spot, destroyed by the sweep 5 s after the
  census dropped it — all five stages PASS.
- Real-fight session (32-enemy census, six owner-authority deaths): stand-ins built with
  varied appearance/faction rows, health-pinned and driven; the churn defect found there
  is trap 3 above.
- Known limitation: loot on a stand-in's corpse does not sync (its container id exists
  only locally). A real ambush end-to-end (tar_m03aa) remains the outstanding live test —
  no save reaches it yet.
