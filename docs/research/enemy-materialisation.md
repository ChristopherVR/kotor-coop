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

## 6. Loot on a stand-in's corpse — the instrument exists, the automatic wire does not

The gap in §5 now has a way to stage it, on the same model StageMovie/UseDoor/Resolve gave
`cinematics.md`: `CheatController.GiveLoot` (`SharedBlock.CheatKindGiveLoot = 11`, no version
bump — every field it uses already exists) spawns one item, by resref and full identity
(stack/charges/max charges/upgrades/flags — the same four `SetItem` already carries),
straight into a materialised stand-in's inventory via a new primitive,
`Odyssey.GiveItemToInventory` — `CreateItemOnObject` alone, split out of
`EquipItemFromTemplate` without the `ActionEquipItem` half, since a corpse's loot only has
to be there to be looted, not equipped.

The target field is the **OWNER's** enemy object id, not a local one — the caller cannot
resolve the translation itself; only this instance's own `MaterialisedEnemies.ToLocal` can,
and it does that translation inside the handler. An owner id with no stand-in here is
REFUSED, not a silent no-op: the common case is a peer holding the REAL creature (same GIT
id), which already rolled equivalent loot on its own when it died, and applying this there
too would double it.

**What this closes:** a live pass can now stage "give this materialised stand-in this exact
item" by hand and confirm the write lands (read it back via the existing `item <id>`
diagnostic), which is the same kind of unblock `moviewarp`/`usedoor` gave the cinematics
work — the scenario becomes producible without an ambush save.

**What this does NOT close — the automatic wire is still open:**

1. **Nothing yet detects an owner's real enemy dying and captures its actual inventory.**
   The owner-side read exists as a building block (`KotorGameBridge.ListItems()`, filtered
   by `Possessor == the dying creature's id`, already returns full identity per item — it is
   the same sweep the `items`/`setitem` staging cheats use) but nothing calls it at the
   moment of death yet.
2. **Nothing crosses the wire.** The owner and the mirror are different processes, often
   different machines; getting a captured loot list from one to the other needs a new
   packet (the `TransitionMovies`-style "any -> all, relayed by host" shape does not apply
   here — enemies are always host-owned, so this would be host -> clients only, simpler).
3. **The receiving side cannot yet issue this automatically.** `GiveLoot` only runs through
   the staging-cheat channel, and that channel is DELIBERATELY single-request — built for a
   human typing a few times a minute, not for a burst of enemies dying in one ambush wave
   at the same time a player might be typing a console command. Feeding it from the
   automatic session pump (`CoopAgent`, alongside `PumpEnemies`/`PumpTransitionMovies`)
   without colliding with the human-typed console channel needs its own small design pass —
   likely a short local FIFO on the agent side draining one request per pump tick, since the
   channel's own turnaround is well under a tick.

None of this has been tried live — same caveat as the rest of this document until an
ambush's actual death-and-loot sequence runs against it.

### Items 1 and 2 built: death capture and the wire — item 3 (automatic apply) still open

Closes the first two gaps above. Item 3 — draining this automatically into `GiveLoot` without
colliding with a human typing at the console — is a SEPARATE piece of work, not built here.

**Death detection and capture (`CoopAgent.DetectMaterialisedDeaths`, owner-authority side of
`PumpEnemies`).** Tracks which enemy ids read alive on the last clean census
(`_knownAliveEnemies`), cleared on a module change (ids are reassigned per load, so an id
remembered from the last module names a different creature now — or nothing). An id that drops
off the alive set WITH a `Dead` reading is a genuine kill; one that drops off the CENSUS without
ever reading `Dead` is not — the census sheds by distance and cap, and a creature merely far
away must never be read as a kill. On a real death, one `IGameBridge.ReadPossessedItems` sweep
covers however many enemies died on that tick (not one sweep per creature — `ListItems()`'s own
doc warns it is a full memory sweep, "not free," and an ambush wave dying together would stall
the pump repeatedly at one sweep each). Enemies with nothing captured publish nothing.

**The wire (`PacketType.MaterialisedLoot = 41` / `MaterialisedLootAck = 42`).** Host -> clients
only, reliable via retransmit-until-ack — the same shape as `ContainerLoot`/`ContainerLootAck`,
including `MaterialisedLootSender` (a dedicated retransmit-until-ack sender keyed by owner enemy
id, not container+item pair, since a creature dies once). Up to `MaterialisedLootPacket.MaxItems`
(8) items per death, each carrying resref plus the same identity fields `GroundItem`/`SetItem`
already carry — a corpse's worth fits one datagram at the cap (asserted by
`PacketCodecTests.MaterialisedLootFitsInOneDatagramAtMaxItems`). `CoopHost.PublishMaterialisedLoot`
enqueues; `CoopClient` receives, dedups by OWNER ENEMY ID (not op sequence — unlike a container
removal, applying this is NOT idempotent, `GiveItemToInventory` mints a fresh item every call, so
a retransmit slipping in before our ack lands must never reach the drain queue twice), acks
unconditionally (a duplicate arriving means our ack was lost, not that the death happened twice),
and queues for `ICoopSession.DrainMaterialisedLoot()` — the agent pump's job to drain and apply,
matching `DrainTransitionMovies`'s shape exactly.

**⚠ Known gaps in the wire itself, not deferred to item 3:**
- **No permanent resync store.** Unlike `ContainerLootStore` (a full historical snapshot
  re-offered to a rejoining peer via `ResyncContainerLoot`), `MaterialisedLootSender` only
  tracks ops still IN FLIGHT. An op already retired (every then-connected peer acked it) before
  a peer disconnects-and-reconnects is gone for good — that peer's stand-in never receives that
  death's loot. `ResetBackoff()` is called on peer readmission for consistency with the
  container-loot pattern, but it only speeds up ops still pending; it cannot resurrect a retired
  one. Same class of gap this document already accepts elsewhere, not new to this pass.
- **The client-side dedup set (`_materialisedLootSeen`) is not reset on module change.** An id
  space is per-module; a reused id after a transition would be silently ignored. Narrow and
  cosmetic (one enemy's loot stays unsynced, nothing desyncs or crashes) — left as a known
  limitation rather than adding epoch-tracking plumbing this pass did not otherwise need.

**⚠ Unconfirmed, needs a live look before trusting this end to end:**
`docs/research/placeables-containers.md` documents a SEPARATE loot mechanism —
`CSWSPlaceable+0x36C`, a "body bag" the retail engine may convert a killed creature's dropped
items into for the normal loot-container UI, distinct from a creature's own `ItemPossessor`-
tagged inventory (which is what `ReadPossessedItems`/`ListItems()` reads, and where
`GiveItemToInventory` writes on the receiving side — the same target `GiveLoot` already uses).
Whether a real enemy's items are STILL possessor-tagged to the creature itself at the instant of
death (this capture reads them correctly) or have already moved into a body-bag placeable by
then (this capture would read nothing, or the wrong location) is not established either way —
this needs a live death, not more reading. If the body-bag path turns out to be what actually
holds a real corpse's loot, the capture side needs to read `+0x36C` instead of/in addition to
`ItemPossessor`. Separately: `ContainerLoot`'s existing removal-sync operates on that SAME
placeable body-bag mechanism, not on a creature's own inventory — so it is not relevant to a
materialised stand-in's corpse either way, since `MarkAsMirroredEnemy` already marks a stand-in's
corpse unselectable (§2) and this feature writes straight into its inventory, never through a
body-bag placeable at all.

Tests: `PacketCodecTests.MaterialisedLootPacketsRoundTrip`,
`.MaterialisedLootFitsInOneDatagramAtMaxItems`; `MaterialisedLootPumpTests` (death vs.
census-drop, empty corpse, one-sweep-for-several-deaths, module-change reseeding),
`MaterialisedLootQueueTests` (the client-side FIFO, drained one per tick, bounded).

### Live pass (2026-08-21) — the pipeline is real, end to end, on a non-materialised target

The whole capture → publish → receive → queue → automatic-submit chain was completely silent —
neither `DetectMaterialisedDeaths` nor the client's queue/drain logged a single line either
side of a success or a failure, making the feature untestable as landed. Added one log line
per stage (`CoopAgent.DetectMaterialisedDeaths`, `CoopAgent.PumpMaterialisedLoot`,
`CoopCommandDispatcher.DrainAutomaticLoot`) — these write to the AGENT's own log
(`agent1.log`/`agent2.log`), not the plugin's `kotorcoop-general.log`, since the whole pipeline
up to the final `SendCheat` call runs in the external agent process, not the plugin.

Killed a real, undamaged `blackvulka034` (id `0x4`, holding `g_a_class4001` +
`g_w_qtrstaff01`, confirmed via `items` beforehand) on a connected host in `tar_m03aa`, with
both players standing close together (killing a target far from the OTHER peer's position
does not work — the census that feeds death-detection sheds by distance to peers, not to the
killer, so a death outside that radius is silently never noticed; this cost the first attempt).
Host agent log:

```
materialised loot: 1 enemy death(s) noticed this pass: 0x4
materialised loot: captured 2 item(s) off 0x4's corpse, publishing
```

Client agent log, immediately after:

```
materialised loot: received 2 item(s) for owner 0x4, queueing
materialised loot: submitting 'g_a_class4001' for owner 0x4 through the giveloot channel
materialised loot: submitting 'g_w_qtrstaff01' for owner 0x4 through the giveloot channel
```

Client plugin log, one line per submitted item: `cheat: 0x4 is not a materialised stand-in
here - nothing to give (status -3)` — CORRECT: `blackvulka034` is a base-GIT creature, not an
encounter spawn, so the client holds the real object under the same id and has already rolled
its own equivalent loot; applying this too would double it, exactly the refuse case §6's
design section describes. The manual `giveloot` console verb was checked immediately after and
still refuses/behaves normally (`giveloot 0x99 <resref>` → the same "not a materialised
stand-in" message) — the automatic and human paths correctly share the single-slot channel
without interfering.

**What this confirms:** every stage up to and including the plugin's own translation/refusal
logic works correctly with real captured data matching ground truth exactly (2 items, correct
resrefs, correct owner id). **What it does NOT confirm:** the positive case — an item actually
landing in a materialised stand-in's inventory — since that needs a genuinely encounter-spawned
enemy (`end_m01aa`, the standard demo save, has no encounters at all; `tar_m03aa`'s ambushes
need either real player movement to trigger, which is not injectable headlessly in this build,
or the client-triggered-encounter wire this document's §5/§7 already verified separately). The
positive apply path (`Odyssey.GiveItemToInventory` writing into a real stand-in) was already
proven directly by the `giveloot` staging cheat itself (§6, "spawns one item... and confirm the
write lands") — what remained unconfirmed was only the automatic wire feeding it, and that is
now confirmed working. Closing an actual ambush's death through this exact automatic path,
rather than the manual `giveloot` cheat, is the one item left for a future pass with a save
that reaches a real encounter.

### The positive case — CONFIRMED (2026-08-21, later same day)

A real ambush, walked into organically (a genuine `blockpoke walk`, not `tp` — see
`docs/research/client-encounters.md` §7 for why teleporting never fires the movement-based
trigger). With the fight underway and multiple materialised stand-ins active, a real
encounter-spawned Vulkar (owner `0xB5`) died for real and the automatic wire closed the loop
end to end, host log:

```
combat: killed 0xB5 on the owner's authority
materialise: gave loot 'g_a_class4001' (0xB9) to stand-in 0xB1 for owner 0xB5 - stack 1, charges 0/0, upgrades 0x0
cheat: gave 'g_a_class4001' to 0xB1 (status 1)
materialise: gave loot 'ptar_shockstick' (0xBA) to stand-in 0xB1 for owner 0xB5 - stack 1, charges 0/0, upgrades 0x0
cheat: gave 'ptar_shockstick' to 0xB1 (status 1)
materialise: gave loot 'g_w_blstrpstl001' (0xBB) to stand-in 0xB1 for owner 0xB5 - stack 1, charges 0/0, upgrades 0x0
cheat: gave 'g_w_blstrpstl001' to 0xB1 (status 1)
```

Three real items, correctly translated from owner `0xB5` to its local stand-in `0xB1`,
correctly applied (`status 1` on every give). The loot-sync feature is now confirmed working
end to end on the actual positive case, closing the one item the earlier pass this same day
left open.

⚠ **A confusing log line, NOT a bug — checked and cleared.** `"combat: killed 0xB5 on the
owner's authority"` reads as if the LOCAL id `0xB5` were killed, and in this same session a
DIFFERENT creature (owner `0xCE`) also happened to be materialised as local `0xB5` — a
numeric coincidence between two different id spaces that looked, at first read, like an id
collision bug in the kill-mirroring path. It is not: `CombatController.KillOnce` (the code,
not the log text) already translates through `MaterialisedEnemies.ToLocal` before calling
`Odyssey.KillCreature`, so the kill in this trace correctly landed on `0xB1` (owner `0xB5`'s
real stand-in), not on `0xB5` the number. The log line prints the OWNER id it was told to
kill, not the local id it actually acted on — worth a clearer log line for whoever reads this
next, but not a functional defect.
