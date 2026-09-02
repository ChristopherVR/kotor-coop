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
- ~~**The client-side dedup set (`_materialisedLootSeen`) is not reset on module change.**~~
  FIXED 2026-08-21 — see "Item 11(a) closed, 11(b) deferred" further down. An id space is
  per-module; a reused id after a transition would have been silently ignored.

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

### Item 11(a) closed, 11(b) deferred (2026-08-21) — the client's dedup set now resets on module change

Closes roadmap item 11's first half exactly as flagged in the "known gaps" list above: the
client-side dedup set (`CoopClient._materialisedLootSeen`) was never told a module had changed,
so an owner enemy id reused in the next module would have been silently swallowed as
"already seen" forever, dropping that enemy's real loot with no log line and no symptom other
than a missing item.

**What changed.** `ICoopSession` gained `ResetMaterialisedLootDedup()` — a no-op default,
implemented on `CoopClient` as `_materialisedLootSeen.Clear()` under the same `_gate` lock the
inbox queue already uses (this set was previously mutated unlocked from the receive loop; it is
now also cleared from the agent's thread via the session interface, so it needed the lock it
had been missing). `CoopAgent.PumpMaterialisedLoot` now takes the local module name, tracks the
last module it reset for (`_materialisedLootDedupModule`, mirroring `_deathCensusModule`'s
existing pattern on the owner-authority side), and calls the reset exactly once per genuine
module change — gated on `IsFallbackModuleName` so a transition-window fallback label
(`areaXXXXXXXX`) never fires a spurious reset. A no-op on the host, which is never the
receiving side of this channel.

Tests: `MaterialisedLootQueueTests.ModuleChangeTellsTheSessionToForgetItsDedupSet` (one reset on
arrival, none on repeat pumps in the same module, one more on a genuine change),
`.AFallbackModuleLabelDoesNotTriggerAReset`.

**Item 11(b) — resync for a peer that reconnects after an op retired — CLOSED (2026-08-21).**
`MaterialisedLootHistory` (`src/KotorCoop.Session/MaterialisedLootHistory.cs`) is the historical
store this entry originally said was missing: a bounded (256 entries), per-module dictionary
keyed by owner enemy id, exactly the `ContainerLootStore` pattern applied to this channel.
`CoopHost.PublishMaterialisedLoot` records into it alongside the ordinary
`MaterialisedLootSender.Enqueue` call; `CoopHost.PublishEnemies` clears it on a genuine module
change (owner enemy ids are per-module, so an entry from the last one names a different creature
now, or nothing); a new `CoopHost.ResyncMaterialisedLoot`, called from `HandleHello` on
(re-)admission exactly where `ResyncContainerLoot` already runs, re-`Enqueue`s every remembered
death under a fresh sequence. A re-offer to a peer that already applied the op is a no-op there
(the client's `_materialisedLootSeen` dedup-by-owner-id is not reset by a reconnect, only by a
module change — confirmed still true and now regression-tested), so the replay only ever adds
loot for a peer that was genuinely missing it. No protocol change: `MaterialisedLoot`/
`MaterialisedLootAck` (41/42) and the client-side handling are unchanged: the store, PublishEnemies
comparison, and HandleHello call are the whole diff.

Tests (`MaterialisedLootHistoryTests`, `MaterialisedLootResyncTests`): the history caps at 256
and evicts oldest; overwriting an already-known owner id neither grows the history nor evicts;
a genuine `PublishEnemies` module change clears it and a same-module re-publish does not; a
peer disconnected BEFORE an op retires (so it was never a recipient of the retirement itself —
the empty-recipients-hold rule means retirement needs at least one other peer still connected)
receives that death's loot through the resync once it reconnects; a peer that already applied an
op does not receive it a second time after its own reconnect (dedup survives); and a peer joining
fresh, after every op already retired against an earlier peer, receives the WHOLE remembered
history, with the earlier, already-caught-up peer seeing no duplicate as a side effect.

⚠ **NOT live-verified.** This whole entry — the history store, its wiring into
`PublishMaterialisedLoot`/`PublishEnemies`, and the `HandleHello` resync call — is reasoned from
the store and packet-handling code and exercised only by the loopback-socket unit tests listed
above, never against the running two-instance rig. In particular the exact scenario the tests
simulate (a real ambush death, one peer genuinely absent for the whole retirement window, a real
reconnect through `SessionManager`/the plugin rather than a raw `CoopClient`) has not been walked
through live. The previous version of this note (superseded by the paragraph above) is preserved
in git history for anyone who wants the original "deferred, not built" framing this replaced.

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

## 7. What a stand-in LOOKS like — the twin gap, and the party leak that fed it (2026-08-29)

Two independent defects, both closed this round. They compound: the first put the wrong
CREATURE on the census, the second made every stand-in wear the wrong BODY.

### 7.1 The census leak — a companion published as an enemy (MEASURED LIVE)

`KotorGameBridge.ReadEnemies` excluded a creature when it was the player or when
`CreatureRef.IsPartyMember` was true, and that flag is a read of the party-FOLLOW pointer,
`CSWSCreature+0x4C0`. **That pointer is follow-AI state, not a membership record.** The engine
attaches it some time after the body exists — which is exactly why `PartyFollowController` and
`FollowPhaseReleaseRule` exist at all.

Measured on the rig, `end_m01ab`, host instance, two independent runs:

```
13:20:19.485  roster[0] id 0x76 appearance 378 follow 0x0   *** roster member, follow NULL ***
   ... 15 consecutive samples, 5.5 seconds ...
13:20:24.958  roster[0] id 0x76 appearance 378 follow 0x0   *** roster member, follow NULL ***
13:20:25.360  roster[0] id 0x76 appearance 378 follow 0x18851130   ok
```

Trask — appearance **378**, `CSWPartyTable+0x0C` roster slot 0 — spent **5.5 seconds** looking,
to the old test, exactly like a hostile stranger. The window was opened here with
`removeparty 0` / `recruit 0` (the dev verbs), the same body detach/attach the engine performs
around a party rebuild; the first run measured 4.9 s, the second 5.5 s.

What that costs on the receiving side is the whole "Trask icon" report:

```
client: resolve 0x76  ->  'no target: 0x76 resolves to 0x0 here'
client: partyTable roster[0] = 0x74      (its OWN Trask, a different runtime id)
```

Party bodies are spawned by the party table at load, **not placed in the GIT**, so the two
engines number them independently — measured, `0x76` on the host against `0x74` on the client
for the same companion. A leaked companion id therefore resolves to nothing on the peer, sails
straight past `TryMaterialise`'s "does it already exist locally" sweep, and gets a stand-in built
for it, repainted to the row the census carried: **378, Unique_Trask, standing where the host's
Trask stands.** That is a Trask-shaped body appearing next to the party after a relocation, out
of the enemy channel, with no peer involved at all.

**The fix** is `PartyCensusRule` (`KotorCoop.Protocol`, shared by source into the plugin):
membership comes from the party TABLE's roster ids, `CSWPartyTable+0x0C`'s `int[9]`, which is
written when a body is created and is therefore correct in the window the follow pointer is not.
The follow-pointer test is KEPT as a widening belt — it is never wrong when true — and so is
`IsPlayer`; see the warning at the end of `name-plates.md` for why `!IsPartyMember` must never be
used to identify the player. Applied at all three census sites (`ReadEnemies`,
`ReadCreatureLiveness`, and the external-mode phantom-body claim in `TryResolve`).

Verified live, same window, same body: five `census 0x76` queries issued between 13:20:19.560
and 13:20:23.370 — every one of them inside the follow-null window above — all answered
**`not listed in this census`**. `verify.ps1` on a fresh demo afterwards: 40 pass / 9 fail,
inside the documented ~40-42 / 7-9 baseline, with `enemy presence mirrored on the client PASS
presence 25, census 25` — the same 25 ids as before the change, so nothing legitimate was lost.

The mirror refuses too (`PartyCensusRule.MayMaterialise`): never a body for one of our own roster
ids, and never one standing on our own companion wearing that companion's appearance.
⚠ Appearance alone is deliberately not enough — refusing every enemy that shares a companion's
row would make a real hostile invisible while it could still hurt us, which is the exact failure
this whole file exists to remove. The POSITION is what makes it safe: a leaked companion is
published at our own copy's feet, and a look-alike across the room is not.

⚠ **Known consequence, accepted.** The roster array keeps a slot's object id after
`RemovePartyMember` — measured, indefinitely, for a body the dev verb leaves standing — so a
dismissed-but-present companion stays out of the enemy census. It is never anyone's enemy, and
in real play the engine despawns that body anyway.

### 7.2 Stand-ins were never dressed on the half that renders

`TryMaterialise` wrote the appearance row once, at spawn, called the SERVER-side
`RebuildAppearance`, and **never called `Odyssey.RebuildClientAppearance` at all, at any point**.
The half that renders is the CLIENT TWIN, and `name-plates.md`'s live pass measured the twin
absent at spawn on **100%** of spawns (re-confirmed this round: every phantom spawn logged
`renders no twin yet`, the latch closing 30-380 ms later). So every stand-in rendered whatever
`k_coop_phantom` gave it — appearance 114 — rather than the creature it stands for.

Fixed with the identical per-body latch `PhantomController.ApplyAppearance` got one channel over:
`Appearances[slot]` keeps the row the owner published, `TwinDressed[slot]` is the latch, and
`Dress(slot)` re-asserts from the per-frame `Sweep` until `RebuildClientAppearance` returns true.
It **re-resolves the body by id each pass** (`Odyssey.ResolveServerObject`) rather than trusting
`Bodies[slot]`: this runs every frame until the latch closes, and a cached pointer to a freed
body is the shape of crash the project's per-frame-write rule exists for. The spawn line now
carries the twin's row alongside the server row, and a second line records the latch closing:

```
materialise: owner 0x3B -> local 0xB1 (appearance 355, renders no twin yet, faction 1)
materialise: local 0xB1 (owner 0x3B) now renders appearance 355
```

⚠ **Unit-covered and mechanism-verified, NOT symptom-verified.** The latch is the same call, the
same return value and the same discipline that was live-verified for phantoms on 2026-08-29, and
this round re-confirmed the twin gap live on every phantom spawn — but no stand-in was
materialised during the run to watch the new lines fire. Both instances loaded the same module,
so all 25 (then 10) census ids resolved on both sides and there was nothing to build. Forcing a
materialisation needs a creature that exists in exactly one engine; the natural source is an
encounter spawn, and the one dev verb that creates a runtime body (`recruit`) creates a PARTY
body, which §7.1 now excludes by construction. The next session that sees a real encounter spawn
gets the answer for free from the two log lines above.

## 8. Filtering a doomed give BEFORE it spends the round trip (2026-09-02)

### The noise measured live

A client rejoining after a wipe reload, 23:03:05: the host published a `MaterialisedLootPacket`
for a corpse's items the usual way (§6), and the client's `LootPump.PumpMaterialisedLoot` queued
one give per item unconditionally, same as every session before this one. But this client held
the REAL creature under that owner id — not a materialised stand-in — the overwhelmingly common
case §6 already names: an ordinary GIT-identical enemy that had already rolled its own equivalent
loot independently when it died locally. Six items went through
`CoopCommandDispatcher.DrainAutomaticLoot` and the single-slot giveloot cheat channel in one
second, each one:

- occupying the ONE channel a human's typed `giveloot` shares, for a request that could never
  succeed;
- costing two log lines (agent: `materialised loot: submitting '<resref>' for target 0x<id>
  through the giveloot channel`; plugin: `cheat: 0x<id> is not a materialised stand-in here -
  nothing to give (status -3)`) for a refusal the client could have known about without asking.

Nothing was WRONG — `CheatController.GiveLoot`'s refusal is exactly correct, and it is the
backstop this whole design still leans on (below). The bug was spending a round trip to learn
something the client's own plugin already knew.

### The question: does the agent already know which ids are materialised locally?

No — not before this pass. `MaterialisedEnemies.ToLocal`/`.OwnerIds` is plugin-side, static,
in-process state; nothing published it across the block. The other bounded id tables the agent
already reads (`SharedBlock.OffsetLocalCombatExcludedIds`, `SharedBlock.OffsetDamageDealtSlots`)
are a close structural match — a fixed-capacity array, written by the plugin every pass, read by
the agent through `SharedBlockView`/`KotorGameBridge` — but none of them carries owner ids the
way `MaterialisedEnemies.OwnerIds` already does. So option (b) from the task: publish one.

### The design chosen

**SharedBlock version 65** (2026-09-02, TotalSize grown 0x4B00 → 0x4C00):
`SharedBlock.OffsetMaterialisedOwnerIds` (0x4B00..0x4B80) is a fixed `EnemyCapacity`-wide (32)
array of owner ids, 0 marking an unused slot — the SAME shape
`SharedBlock.OffsetLocalCombatExcludedIds` already uses, chosen deliberately because
`MaterialisedEnemies.OwnerIds` already IS this table, unpacked, in place: publishing it is a
straight `ReadOnlySpan<uint>` copy (`MaterialisedEnemies.OwnerIdsSnapshot`), not a rebuild, and
needs no separate count field for the same reason the local-combat table does not. Written by
`EnemyPresenceController.PublishMaterialisedOwnerIds`, called alongside every
`MaterialisedEnemies.Sweep` (all three call sites in `EnemyPresenceController.OnFrame`) — Sweep
is what actually changes the table, so publishing at the same cadence keeps the two in lockstep
with no separate schedule to reason about, and no new PluginSignature byte pattern to keep in
sync (the growth sits well past the signature's first 16 bytes).

Both the block version scalar (`PluginHost.cs`'s `_block->Version`) and its byte-exact twin
inside `KotorGameBridge.PluginSignature` (byte 8: `40` → `41`) moved together, per the existing
"one number in two files" guard — `ItemHarnessTests.TheBlockVersionIsTheSameNumberInBothHalves`
and `TransitionMovieTests.TheBlockVersionMovedInBothHalves` both still pass, the latter now
pinned at 65.

**Agent side:** `IEnemyBridge.ReadMaterialisedOwnerIds()` (default `null`), implemented by
`KotorGameBridge.ReadMaterialisedOwnerIds` — reads the table, returns a `HashSet<uint>`, or
`null` when `_pluginBlock == 0`. Null is deliberately the ONLY case meaning "don't know": because
`PluginSignature` embeds the block version byte-exact in the scan pattern (see that field's own
remarks), a version-mismatched agent/plugin pair never finds the block at all rather than reading
it half-right, so there is no partial/stale-version case to hedge against separately.

**The filter itself: `KotorCoop.Session.MaterialisedLootFilterRule`**, a pure, stateless
`static bool ShouldQueue(uint ownerEnemyId, IReadOnlySet<uint>? materialisedOwnerIds)` —
`materialisedOwnerIds is null || materialisedOwnerIds.Contains(ownerEnemyId)`. Tri-state by
design, matching the task's own explicit requirement: a null set (unsure — no plugin attached)
always sends, trusting the plugin's own refusal as the backstop; a real set, however it reads —
including a genuinely empty one — is trusted by membership. `LootPump.PumpMaterialisedLoot` reads
the snapshot ONCE per pump (not once per packet, not once per item — a burst of several deaths in
one tick shares one plugin-block read, the same reasoning `ReadPossessedItems`'s own "one sweep
per tick" doc already gives) and applies the rule per PACKET, since every item in a
`MaterialisedLootPacket` shares one `OwnerEnemyId`. A filtered packet costs exactly ONE log line
naming the owner id and the item count, not one per item — the whole point was to stop the
per-item noise, and a per-item log would just move it from the plugin's log to the agent's.

**The dialogue-grant half of the same queue is never filtered.** `PumpMaterialisedLoot` enqueues
two different things through the same `_pendingLoot` queue (see that field's own doc): a
materialised-loot give (owner id, needs `MaterialisedEnemies.ToLocal` translation) and a
dialogue-grant give (`AgentWellKnownIds.LocalPlayerObjectId` sentinel, targets the local PC
directly, needs no translation at all). The filter is applied ONLY inside the first loop, over
`session.DrainMaterialisedLoot()`'s packets — the second loop, over
`session.DrainDialogueGrants()`, never calls `MaterialisedLootFilterRule` at all, by construction
(no code path connects them), so a dialogue grant is unaffected by whatever the materialised-id
table currently holds, including an empty one. `MaterialisedLootQueueTests
.ADialogueGrantIsNeverFilteredEvenAgainstAnEmptyMaterialisedTable` pins this directly.

**Host side: unchanged, by construction.** `ICoopSession.DrainMaterialisedLoot()` defaults to
`[]`, and `CoopHost` never overrides it — only `CoopClient` does (§6's own wire section) — so the
loop this filter sits inside runs zero times on a host regardless of what
`ReadMaterialisedOwnerIds()` would answer there. Nothing about this pass touches the publish side
in `CoopAgent.DetectMaterialisedDeaths`.

### On the "materialise a moment after the packet arrives" race

Filtering earlier (at enqueue time) than the plugin's own check (at cheat-submission time, one or
more single-slot-channel turns later) narrows — but does not add — a pre-existing race: an owner
id whose stand-in is still mid-spawn when its loot packet is drained here reads as unmapped now,
whereas today's un-filtered code could still succeed later if materialisation finished before the
queued cheat was actually submitted. Judged acceptable for three reasons: (1) the measured failure
mode this pass fixes — a client that never had, and never will have, a stand-in for that id
(§6's ordinary case, and literally the rejoin scenario measured above) — is the overwhelming
majority of refusals, not the rare in-flight-materialisation case; (2) `TryMaterialise` runs every
engine frame while the combat pump that drives `PumpMaterialisedLoot` is throttled to a slower
interval, so a stand-in already in flight when the corpse died (the ordinary case — the enemy
was visible and fought before it died, which is what let this client's own census see it) is
very likely to have finished mapping several frames before the death is even detected, captured,
transmitted and drained; and (3) the SAME race already existed at the plugin's own `ToLocal`
lookup before this pass — this only moves the decision point earlier by roughly one combat-pump
tick, it does not create a new failure mode. **NEEDS-LIVE**: no live capture yet confirms the
false-positive rate this trade costs is actually negligible in practice; the fix as measured only
covers the confirmed-forever-unmapped case above.

### Tests

`MaterialisedLootFilterRuleTests` (the pure tri-state contract: null unsure/always-sends, empty
set filters everything, a real set filters by membership) and five cases added to
`MaterialisedLootQueueTests` (no known table still queues; a known empty table queues nothing;
a mapped id is still queued alongside a filtered one in the same pump; one log line per filtered
owner regardless of item count; a dialogue grant is never filtered). `dotnet test
tests/KotorCoop.Tests --filter "FullyQualifiedName~Loot"` — 82 passed.
