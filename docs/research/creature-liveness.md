# Creature liveness reconciliation — the same NPC, alive on one screen and not the other (design 2026-08-21)

USER-REPORTED CONCERN: "we don't want instances of NPCs alive in one host and not another."
Today creature death is host-authoritative for the 32-slot combat census (`CombatController`
mirrors owner health and runs `Odyssey.KillCreature` — see
[enemy-presence-sync.md](enemy-presence-sync.md), [peer-damage-ledger.md](peer-damage-ledger.md),
[enemy-materialisation.md](enemy-materialisation.md)), but several paths can leave a body alive
on one side and dead or gone on the other:

1. A creature beyond the 32-slot census cap (distance shedding — the census sorts in-combat
   creatures first, then nearest-first to a player, and truncates).
2. A creature killed by SCRIPT on the owner (`DestroyObject`, a cutscene death, `SetPlotFlag`
   then a kill) — none of that runs through `CombatController`, which only ever reacts to the
   census's `Dead` flag on a creature the owner is still publishing.
3. A creature killed while the other player was in a different module or disconnected — the
   census and its damage ledger are both module-gated and peer-gated; nothing catches up a
   returning peer's OWN world state, only the shared stores.
4. A creature killed on the CLIENT side locally under `local_combat` (round 52/53,
   [shared-state-audit.md](shared-state-audit.md) §2) before the host's engine confirmed —
   the client's own engine can now genuinely kill a creature it is fighting for real, and
   nothing tells the host, whose copy stays alive and keeps publishing `Alive`.
5. Plot/story actors, which replicate PRESENCE but are combat-exempt (`CombatController` never
   touches their health) — a script `DestroyObject` on one is invisible to every existing
   channel.
6. Spawned-by-script non-encounter NPCs that exist on one side only.

Tag key: **VERIFIED** = read from the instruction stream or measured live in an earlier pass
this design reuses · **DESIGN** = a choice made here · **NEEDS-LIVE** = must be confirmed on
the running game. Nothing in this document's own mechanism has been run live yet — see the
NEEDS-LIVE checklist and the live-test script at the bottom.

---

## 1. The model

**The enemy owner publishes what its engine sees for the WHOLE area, not just the fought-over
32.** A mirroring peer compares that against its own engine and corrects a body that has
fallen out of step — kills a corpse still standing here, destroys a body the owner has fully
removed, or (if the owner is the one that is wrong — this instance killed something locally
that the owner never heard about) logs it and moves on. **Never resurrects.** The channel is
diagnostic and corrective in exactly one direction: towards agreement that a body should NOT
be standing. If the owner is alive and this instance is dead, that is either a genuine local
kill the ledger has not caught up with yet, or an actual bug — either way, the fix belongs
upstream (the damage ledger, `local_combat`'s own hand-back), not a forced revival here.

### 1.1 Why the scan does not need the plugin at all — a finding, not an assumption

The obvious design reads the plugin's SharedBlock census (32-cap) for the source data, since
that is what the plugin already enumerates for combat mirroring. That capacity is real for the
32-slot region, but it is not actually the limit on what the AGENT can see: `KotorGameBridge`
(the external, out-of-process half) already does its own FULL vtable scan of the game's
private committed memory — `EnumerateCreatures()`, the same method `ReadEnemies` builds its
capped, distance-sorted 32 from — and that scan is confirmed against the engine's own object
array (the ghost-gate fix in [enemy-presence-sync.md](enemy-presence-sync.md)'s "Root cause
2026-08-15" section) BEFORE anything downstream ever sees it. So the agent already has an
UNCAPPED, ghost-filtered view of every creature in the loaded area, independent of the
plugin's block. `KotorGameBridge.ReadCreatureLiveness()` reuses this directly: same
enumeration, same exclusions (player, party, phantom-tagged bodies), just without the
32-entry truncation and distance sort `ReadEnemies` applies for the combat channel. No new
plugin-side scan, no SharedBlock growth on the SCAN half at all.

The capacity limit that IS real is the hand-off from a mirroring peer's AGENT to that peer's
own PLUGIN — the reconcile decision (translate a materialised stand-in's id, resolve the local
body, compare, kill/destroy) has to run in-process, because `MaterialisedEnemies.ToLocal` and
the kill/destroy engine calls are plugin-only. That hand-off DOES need a SharedBlock region,
capped like every other agent→plugin array in this codebase. See §3.

## 2. The wire: `CreatureLivenessPacket`, `PacketType.CreatureLiveness = 47`

Host → clients only, protocol version **30**. Carries `Module`, a `Digest` (order-independent
fingerprint over the WHOLE cycle's entries — `KotorCoop.Session.EntryDigest.CreatureLiveness`,
the same style every other world store already uses), a `Generation` (bumped once per actual
broadcast cycle, not per publish — a receiver uses `(Module, Generation)` together to tell a
NEW cycle's pages from an OLD cycle's, and replaces its accumulated set rather than merging two
different snapshots), and up to `ProtocolConstants.MaxCreatureLivenessEntries` (96)
`CreatureLivenessEntry { ObjectId, State }` per packet, where `State` is `Alive` (0), `Dead` (1,
a corpse, still resolvable) or `Gone` (2, `DestroyObject`'d — nothing left to mirror).

**A full area can exceed 96 entries in one cycle.** `CreatureLivenessPaging.Paginate`
(`KotorCoop.Session`) splits it across however many pages are needed, `PageIndex`/`PageCount`
say where each sits, and every page of one cycle carries the SAME digest (computed once, over
the whole set, before slicing) — a receiver that has gathered every page can confirm its
accumulated view is complete.

**No ack, no retransmit-until-acked sender.** This is a LEVEL like `EnemySnapshotPacket` and
`PartySnapshotPacket`: a lost page costs one cycle of staleness on that page's ids, and the next
cycle (2 seconds later, see §4) resends the whole picture. Unlike those two, it is NOT resent
every network tick — a cycle can already be several datagrams, and multiplying that by the tick
rate would buy nothing a channel meant to converge on a scale of seconds needs.

**Join/reconnect.** `CoopHost.HandleHello`'s `freshlyAdmitted` branch — the same hook
`ResyncContainerLoot`/`ResyncMaterialisedLoot`/the various `ResetBackoff()` calls already use —
sets a flag that forces the very NEXT broadcast cycle out immediately rather than waiting out
the 2-second interval. Since every cycle already IS a full snapshot (this channel has no delta
concept at all), that is the entire "catch a returning peer up" mechanism: nothing else to
resync, no history store, no separate resync packet type.

## 3. The plugin hand-off: SharedBlock version 45, region `0x3C00..0x3F08`, `TotalSize` grown to `0x4000`

Agent → plugin only, and only ever written on a MIRRORING peer — the enemy owner never has
anything to reconcile against itself, so `LivenessCount` reads 0 there forever and
`LivenessReconcileController.OnFrame` does nothing on every pass.

```
0x3C00  u32   LivenessCount               how many of the 96 slots below are valid this pump
0x3C04  ...   LivenessSlots[96]           id u32 @0 (written last), state u32 @4
                                           (0 alive / 1 dead / 2 gone — matches
                                           Protocol.CreatureLivenessState BY VALUE; the plugin
                                           takes no assembly dependency on Protocol, the same
                                           "shared by value" rule EnemyFlagDead already follows)
0x3F04  u32   LivenessAnomalyCount        plugin -> agent, cumulative, never reset by a module
                                           change; see §5
```

Grown from the old `TotalSize` (`0x3C00`) rather than squeezed into the peer-effect region's
leftover tail (0x3B88..0x3C00 is only 0x78 bytes, nowhere near a 96-entry array) — the same
"start at the old boundary" choice versions 42 and 44 both made. `_block->Version = 45` /
`KotorGameBridge.PluginSignature`'s version byte both bumped together, enforced by
`ItemHarnessTests.TheBlockVersionIsTheSameNumberInBothHalves`.

### 3.1 Why 96 in the block does not have to equal 96 on the wire, and the rotation this implies

The wire's 96-per-packet cap is a datagram-size constraint; the block's 96-slot cap is a
completely separate, much smaller allocation (768 bytes) inside an already-large struct. They
happen to be the same NUMBER here, which is a convenience, not a coupling — a mirroring peer's
AGENT accumulates however many entries the current cycle's pages add up to (which CAN exceed
96 for a busy module: retained "gone" entries plus a full area census), and only THEN decides
what fits in the block's fixed 96 slots for this pump.

`LivenessBlockPage.Select` (`KotorCoop.Session`, pure and unit-tested — see §6) makes that
decision: dead/gone entries — the ones that actually need ACTING on — always go first and are
never rotated away, because a truncation dropping them defeats the whole channel's purpose.
Alive entries exist only to catch the rarer "owner alive, local already dead" anomaly (§5), so
when there is no room for all of them the remaining capacity rotates through them across pumps
(a cursor carried in `CoopAgent`) rather than always covering the same prefix and starving the
rest forever. This is the "design the plugin-side enumeration to rotate... and say so" case —
except the rotation lives on the AGENT'S hand-off to the plugin, not on the scan, because
(per §1.1) the scan itself was never actually capacity-limited.

## 4. `CoopAgent`: the owner-side scan and the mirror-side apply

**Owner side** (`CoopAgent.PublishCreatureLiveness`, called from `PumpCreatureLiveness` at most
once every 2 seconds — `LivenessScanIntervalMs`, matching the design's own "once per N seconds
(2 s) per module" cadence): calls `bridge.ReadCreatureLiveness()` (the full, uncapped,
ghost-confirmed area scan — §1.1), and derives **Gone** by comparison against a per-module
`Dictionary<uint, CreatureLivenessState>` of what the last scan saw: an id remembered from an
earlier pass that does not appear in THIS pass at all is genuinely destroyed — unlike the
32-slot combat census (capped, distance-shed), this scan is never capped or distance-shed, so
"disappeared from the full list" really does mean gone, not merely far from a player. A
detected "gone" id is retained and re-reported for `LivenessGoneRetentionMs` (15 s, several
broadcast cycles of headroom against a lost page) before being dropped from both the report and
the tracking table, bounding memory over a long session.

Deliberately **NOT** gated on `IsInDialogue`/`IsInMinigame`, unlike the 32-slot combat census
(`PumpEnemies`'s owner branch holds publication during a local conversation because the
dialogue system disarms every hostile and a paused census would drive a pacified fight onto a
peer). This channel drives nothing and disarms nothing — it only reports — and a script running
during a conversation is EXACTLY the case item 2 above names as a gap; skipping the scan there
would skip the one case most worth catching.

**Mirror side** (`PumpCreatureLiveness`'s non-owner branch): takes whatever
`session.GetCreatureLiveness(module)` has accumulated for the local module (empty when apart —
the same module gate `GetEnemies` already enforces, and the reason no separate
"skip during a transition" code is needed: `SharedBlockView.ClearAgentWorldInputs()` already
zeroes `LivenessCount` at every epoch invalidation, the same as `EnemyPresenceCount` and
`PartyPresenceCount`, following transition-window-races.md's rule 2 — agent-written block
inputs are cleared BY THE PLUGIN at the epoch edge, because the agent cannot do it at exactly
the moment that matters), orders it with `LivenessBlockPage.Select`, and writes the page into
the block. Also pulls `bridge.ReadLivenessAnomalyCount()` and pushes it into
`session.ReportLivenessAnomalyCount` every pump, so `status` can show it (§5).

## 5. `LivenessRule` — the reconcile decision table, and the plugin controller that drives it

A pure, dependency-free table (`src/KotorCoop.Plugin/LivenessRule.cs`, compiled directly into
the test assembly like `MirrorHealthRule`/`ControlReturnRule` — see that csproj's own comment
on why):

| Owner says | Local resolves? | Local dead? | Local plot? | Action |
| --- | --- | --- | --- | --- |
| Alive | yes | no | — | Nothing |
| Alive | yes | **yes** | — | **Anomaly** |
| Alive | **no** | — | — | **Anomaly** |
| Dead | **no** | — | — | Nothing (already gone here too) |
| Dead | yes | yes | — | Nothing (already agrees) |
| Dead | yes | **no** | **no** | **Kill** |
| Dead | yes | no | **yes** | Nothing (un-killable by module rule) |
| Gone | **no** | — | — | Nothing (already gone here too) |
| Gone | yes | either | either | **Destroy** |

Un-killable is not the same claim as un-destroyable: a plot creature's health is never anyone's
to spend (`CombatController` already enforces that for the ordinary census path), but if the
module itself removed the actor (a script's own `DestroyObject`) that is the module's business
either way, so `Gone` still applies even to a plot id.

`LivenessReconcileController` (new file, throttled like `CombatController` — a kill or destroy
is a VM dispatch, and this channel is meant to be EVENTUALLY consistent) walks the block's
entries each pass, translates through `MaterialisedEnemies.ToLocal`/`ToOwner` (the same
collision guard every other consumer of that map applies — an unmapped owner id that
numerically equals one of our own stand-ins' local ids must never be resolved as GIT-identical),
resolves the effective local id, reads `Odyssey.IsCreatureDead`/`Odyssey.IsPlot`, and feeds
`LivenessRule.Decide`. **Kill** calls a one-line public entry added to `CombatController`
(`KillForLivenessReconcile`, the smallest edit that could expose the existing `KillOnce` path —
id-space translation, unpin-before-kill, once-per-module dedup, so a liveness-triggered kill
runs the exact same corpse/loot/XP/OnDeath pipeline as any other mirrored death). **Destroy**
calls the already-general `Odyssey.DestroyObjectById` directly — no new engine-call wrapper
needed, it already exists and is already used by `MaterialisedEnemies`. **Anomaly** bumps
`SharedBlock.OffsetLivenessAnomalyCount` and logs once per entry (capped at 20 lines); it is
the one action that makes NO engine call at all — resurrecting a body this instance genuinely
killed (or has simply lost) is explicitly out of scope, on purpose (see the model in §1).

Rate-limited to `MaxActionsPerPass = 4` kill/destroy dispatches per throttled pass (every 40
frames, matching `CombatController`'s own cadence) — a fresh join or a module can hand this
controller dozens of genuine mismatches in one pump (a returning client's first liveness packet
is a full snapshot), and dispatching them all in one frame is the VM-storm every other
spawn/destroy path in this codebase already rate-limits against.

Gate: **`liveness_sync=0` in coop.ini, the DEFAULT**, until this channel has been live-verified
— matching `effect_sync`/`map_reveal_sync`'s precedent for a new, unverified plugin-side
mechanism. Read only inside the plugin (`PluginConfig.ReadBool`); the AGENT'S scan/publish/
receive/write pipeline runs unconditionally, the same "the agent always does its part, the
receiving plugin's own coop.ini decides whether it acts" split `enemy_drive`/`enemy_materialise`
already use.

## 6. Tests

- `PacketCodecTests`: `CreatureLivenessPacket` round-trips, fits one datagram at 96 entries,
  and rejects a corrupt oversized entry count.
- `CreatureLivenessPagingTests`: an empty cycle still produces one (superseding) packet; a
  small cycle is one page; a cycle over 96 entries splits into the right page count with the
  SAME digest on every page; the digest is order-independent and changes when a state changes.
- `LivenessBlockPageTests`: everything fits when there is room (unordered — actionable entries
  still sort first even then); dead/gone always lead and are never dropped ahead of alive
  entries; the alive tail rotates across calls rather than always covering the same prefix, and
  a few rotations eventually cover the whole set; an actionable-only overflow truncates rather
  than crashing; zero capacity is handled.
- `LivenessRuleTests`: every row of the decision table in §5, individually.
- `CreatureLivenessPumpTests`: the agent-level owner/mirror split — the owner scans and
  publishes the full area (not just the census), a skewed census label suppresses the publish
  (the same transition-window defect class `ASkewedCensusLabelSuppressesTheOwnersEnemyPublish`
  guards for the enemy channel), an id that disappears from the full scan is derived as Gone,
  the mirror orders dead/gone first when handing the plugin its page, the mirror reports the
  plugin's anomaly count into the session, the mirror writes nothing while apart, and a
  fallback module name suppresses both directions.
- `SessionIntegrationTests` (real host + client over loopback UDP, no fakes):
  `ADeadIdOnTheHostConvergesToDeadOnTheClientExactlyOnce` — a dead id published on the host
  converges to a single Dead entry in the client's accumulated view (kept to exactly one entry
  across repeated cycles, which is the state that would drive exactly one plugin-side kill
  dispatch — the plugin's own dispatch cannot run outside the game, so this is the correct
  scope for a loopback test) — and
  `LivenessIsWithheldWhenQueriedUnderADifferentModuleFromTheOnesPublished`, the
  transition-window skip: proves the packet genuinely reaches the client (so an empty result
  cannot be mistaken for "never arrived") and that querying under the client's own, different
  module correctly withholds it — cross-world values are poison
  ([transition-window-races.md](transition-window-races.md)).

## 7. NEEDS-LIVE checklist

Nothing below has run against the live game. The build compiles (`dotnet build KotorCoop.slnx`
and `pwsh src/KotorCoop.Plugin/build.ps1`) and the full suite passes; that is all.

1. **The headline case**: stage a kill OUT OF CENSUS RANGE. On the host, separate the two
   players far enough that a third creature falls off the 32-slot combat census (or use a
   crowded module), then `slay` it. Expect, within ~2-4 seconds on the mirroring client:
   `liveness: N creature(s) newly gone this pass: 0x...` or a Dead entry (depending on whether
   `slay` destroys or merely kills — see `docs/research/console-cheats.md`) on the host's agent
   log, and `liveness: 0x... is a corpse on the owner and was still alive here - killing to
   match` (or the destroying line) on the mirroring client's PLUGIN log
   (`kotorcoop-plugin.log`), followed by the body actually vanishing/falling on that client's
   screen.
2. **A script death**: trigger a scripted kill or `DestroyObject` (a cutscene, a trap, a
   `k_*` script) on a creature the 32-slot census would normally mirror anyway, and confirm the
   liveness channel's line appears even though `CombatController`'s own Dead-flag path may (or
   may not) also have caught it — the point is that this channel is not gated on dialogue, so a
   cutscene kill converges even while the mirroring peer is spectating that same conversation.
3. **Disconnect then reconnect**: with a creature killed while a peer is disconnected (or in a
   different module), reconnect that peer and confirm the FORCED next broadcast cycle
   (`_forceLivenessBroadcast`, HandleHello's `freshlyAdmitted` hook) delivers the full picture
   within one cycle rather than waiting the ordinary 2 seconds — watch for the peer's agent log
   picking up the mismatch promptly after rejoining rather than lagging behind the ordinary
   32-slot census's own resync.
4. **The anomaly path, and that it never resurrects**: force a local kill on a mirroring
   client (`local_combat`'s own real fight, or a client-side `slay` on a mirrored copy) faster
   than the owner's damage ledger can confirm it, and confirm `status` on that client shows
   `liveness anomalies: N` climbing rather than the body coming back — and that the OWNER'S
   copy is unaffected (this channel only ever acts on the RECEIVING side).
5. **Rate limiting under a real ambush wave**: several creatures dying together (an existing
   ambush test save) should not visibly stall the mirroring client's frame rate — confirm the
   `MaxActionsPerPass` throttle spreads the dispatches across a few passes rather than causing
   a burst of VM calls in one frame.
6. **Pagination for real**: a module with more than 96 concurrently-tracked creatures (alive +
   recently-gone) is needed to actually exercise a multi-page cycle live; the base game may not
   reach this in practice with the 15-second retention window, in which case this item stays
   open until a module is found that does, or is accepted as untested-at-scale.
7. **Bandwidth/CPU cost**: confirm the 2-second scan cadence does not visibly compete with the
   existing 150ms combat-cadence reads on a large module — `EnumerateCreatures()`'s own 15s
   address cache and 250ms liveness-confirm cache should make this cheap, but that is inferred
   from the existing enemy-census cost profile, not measured for this specific call pattern.

## Live-test script

`tools/live-liveness.ps1` drives the three staged scenarios above end to end against a running
`tools/demo.ps1` pair (both instances with `liveness_sync=1` in `coop.ini`): out-of-census
`slay`, a scripted/`DestroyObject` kill, and a disconnect-then-reconnect. It tails both agent
logs and both plugin logs, greps for the `liveness:`/`liveness ANOMALY:` lines this design's
controllers emit, and reports PASS/FAIL per scenario based on whether the expected line
appeared on the expected side within a generous timeout. See the script's own header comment
for exact usage and what each PASS line proves.

## Files

- `src/KotorCoop.Protocol/Packets.cs` — `CreatureLivenessState`, `CreatureLivenessEntry`,
  `CreatureLivenessPacket`.
- `src/KotorCoop.Protocol/PacketType.cs` — `CreatureLiveness = 47`.
- `src/KotorCoop.Protocol/ProtocolConstants.cs` — `Version = 30`,
  `MaxCreatureLivenessEntries = 96`.
- `src/KotorCoop.Protocol/PacketCodec.cs` — the wire encode/decode.
- `src/KotorCoop.Session/EntryDigest.cs` — `CreatureLiveness(objectId, state)`.
- `src/KotorCoop.Session/CreatureLivenessPaging.cs` — pagination + digest, pure/testable.
- `src/KotorCoop.Session/LivenessBlockPage.cs` — the block hand-off's priority/rotation
  selection, pure/testable.
- `src/KotorCoop.Session/ICoopSession.cs`, `CoopHost.cs`, `CoopClient.cs` — the publish/receive
  channel, the 2-second broadcast throttle, the freshly-admitted forced cycle, and the
  anomaly-count plumbing into `SessionStats`.
- `src/KotorCoop.Session/CoopAgent.cs` — `PumpCreatureLiveness`, the owner-side scan/derive/
  publish and the mirror-side receive/order/write.
- `src/KotorCoop.Memory/IGameBridge.cs`, `KotorGameBridge.cs` — `ReadCreatureLiveness`,
  `TryWriteCreatureLiveness`, `ReadLivenessAnomalyCount`; `PluginSignature` bumped to 45.
- `src/KotorCoop.Plugin/LivenessRule.cs` — the pure decision table.
- `src/KotorCoop.Plugin/LivenessReconcileController.cs` — the plugin-side controller.
- `src/KotorCoop.Plugin/CombatController.cs` — `KillForLivenessReconcile`, the one-line public
  entry.
- `src/KotorCoop.Plugin/SharedBlock.cs`, `SharedBlockView.cs` — region `0x3C00..0x3F08`,
  `TotalSize` grown to `0x4000`, block version 45.
- `src/KotorCoop.Plugin/PluginHost.cs` — wiring, `_block->Version = 45`.
- `src/KotorCoop.Agent/CoopCommandDispatcher.cs` — `status` shows `liveness anomalies: N`.
- `profiles/coop.ini.example` — `liveness_sync=0`.
- `tools/live-liveness.ps1` — the live-test script.
