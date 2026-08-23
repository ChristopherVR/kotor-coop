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

## Live pass (2026-08-22)

Run against the GOG pair (`tools/demo.ps1`, block v47 / protocol 33, both builds clean, baseline
`dotnet test` 1379/1379), `liveness_sync=1` on both installs, in END_M01AA (~20 unique creatures
total). `tools/live-liveness.ps1`'s own Read-Host prompts don't fit an unattended pass, so the
same three scenarios were driven directly through `blockpoke.ps1 console 'slay 0x<id>'` on the
host and log-watching on the client, per the script's own documented mechanism.

### Scenario A — out-of-census slay: owner-side scan confirmed, kill/destroy dispatch NOT isolated

The owner-side scan/derive worked correctly every time it was exercised — six separate slays
(`0x40`, `0x2C`, `0x33`, `0x36`, `0x10`, `0xE`), each correctly named in the host's agent log
within one scan cycle:

```
* liveness: 1 creature(s) newly gone this pass: 0x40
* liveness: 1 creature(s) newly gone this pass: 0x33
* liveness: 1 creature(s) newly gone this pass: 0x36
* liveness: 1 creature(s) newly gone this pass: 0x10
```

(One early burst — `25 creature(s) newly gone this pass` immediately after an unrelated module
transition/pull-back — was a transient artifact of that transition, not a liveness bug; the very
next two scans correctly reported exactly one id each. Not investigated further this pass; noted
as a rough edge in scan freshness immediately after a transition.)

**What could NOT be isolated**: END_M01AA has roughly 20 unique creatures total, which never
exceeds the ordinary 32-slot combat census cap. Every one of the six test kills was ALSO within
that census, so the PRE-EXISTING `CombatController` (Dead-flag mirroring) reached the mirroring
client first in every single trial — its own log line (`combat: killed 0x36 on the owner's
authority`) appeared before the liveness channel's derivation even completed. `LivenessRule
.Decide` therefore only ever saw "Dead, yes, yes → Nothing (already agrees)" for these six —
correct behaviour, but it means `LivenessReconcileController`'s own Kill/Destroy dispatch
(`combat.KillForLivenessReconcile`, `Odyssey.DestroyObjectById` reached specifically through THIS
channel) was never the first mover and remains **not independently exercised live**. A genuine
out-of-census test needs either a module with more than 32 concurrent creatures or a real script
`DestroyObject` — neither reachable from this save/module this pass. Left open, exactly as §7
item 1/6 anticipated ("or use a crowded module").

### Scenario A (continued) — the anomaly path: PASS, repeatedly

Because several test creatures were, by the time of the reconcile pass, already dead on the
client via the ordinary combat channel while the owner's OWN scan cycle still reported them alive
(the two channels racing independently, not a bug — see above), the ANOMALY path got exercised
far more than intended, which turned out to be useful:

```
liveness ANOMALY: owner says 0x40 is alive, this instance already has it dead - not resurrecting
(the fix belongs upstream, see docs/research/creature-liveness.md)
```

`status` on the client showed `liveness anomalies: N` climbing across the session (11, then 52)
and then holding steady in a quiet period with no further deaths — confirming both the counting
and the **never-resurrects guarantee**: no body was ever seen to un-die on the client across the
whole session. This is the one item from §7's checklist (item 4) this pass confirms cleanly.

### Scenario B — a script death: not independently exercised

No reachable script/cutscene `DestroyObject` trigger was found from this save's position within
the time available (END_M01AA's own "cut2" NPCs, which looked like candidates, turned out to be
tied to the module's own background scripted sequence and died on both instances independently of
this pass's own kills — see the note on cross-contamination below). Left open, as the design doc's
own script for this scenario already anticipated ("depends on which module/save is loaded").

### Scenario C — disconnect, kill, reconnect: mechanically clean, timing not cleanly isolated

Disconnect and reconnect both completed without error (`co-op: connecting...` → `connected...` in
one round trip, party roster resumed: `party roster: reconciling against a peer party of 0x001`),
and a creature slain while the client was disconnected (`0xE`) was confirmed converged (dead) on
the client after reconnect. The specific claim §7 item 3 asks for — a FORCED immediate broadcast
cycle rather than waiting the ordinary 2 s — could not be cleanly timed apart from the anomaly
noise described above (the reconnect's own resync also re-triggered anomaly reports for the
session's earlier test kills, whose owner-side "last known state" had not yet settled). Read as
"reconnect delivers a correct picture reasonably promptly," not as a precisely measured number.

### A testing-process finding, not a product defect

Re-running the SAME `demo.ps1` save slot across restarts within one long testing session lets a
prior session's kills bleed into the next session's baseline (KOTOR autosaves on every module
load, and `demo.ps1` always reloads the same slot) — several of this pass's "out-of-census" test
creatures were already dead from an EARLIER restart's own testing by the time a LATER restart's
scenario ran, which is what produced several of the anomaly readings above rather than fresh
kill/destroy dispatches. Confirmed by picking previously-untouched creatures (`0x36`, `0x10`) for
the cleanest trials. Worth knowing for the next person testing this: pick fresh ids, or expect the
save's own accumulated state to explain an unexpected "already dead."

### Verdict: `liveness_sync` stays OFF

Per this project's own bar (flip on if clean): the owner-side scan and the anomaly/never-
resurrect guarantee are clean and repeatedly confirmed. The channel's own distinguishing
mechanism — reconciling a creature the ordinary 32-slot census channel does NOT already cover —
was never actually exercised, because this module is too small to need it. That is a real,
specific gap, not a vague "needs more testing"; the fix is a bigger module or a script-driven
`DestroyObject`, both concrete next steps. Left OFF by default pending that.

## Live pass (2026-08-22): tat_m20aa measured directly — same gap, now with hard numbers

Task: re-run the three §7 scenarios specifically on Tatooine's Sand People territory
(`tat_m20aa`), on the strength of `README.md`'s own note that an EARLIER, unrelated pass saw
"the census ran at its full 32-creature cap" there. `liveness_sync=1` on both GOG installs,
`warp tat_m20aa` on both. This pass adds direct memory evidence the earlier passes did not have.

### The population, measured, not inferred

`tools/creatures.ps1` (a raw `CSWSCreature` vtable scan, independent of every layer this
codebase's own census/ghost-gate logic sits on top of) against the host: **53 raw hits, 32
UNIQUE object ids** — the excess 53-32=21 are stale/duplicate memory copies left over from the
module load, not real creatures (confirmed by cross-reference: the host's own agent log the same
moment reads `census: gate: array 0x1C7E6DA8 confirmed 32 of 53, dropped 21` — the SAME 32-of-53
split, independently reached by the ghost-gate's own engine-array confirmation, not just this
raw scan). Of the 32 unique ids, one is the local PC (`0x7FFFFFFF`), leaving **31 actual
NPCs/creatures** total in the loaded area — matching the `census` console command's own report
of `OWNER 29 id(s)` (29 hostiles once `end_trask` and the peer's `coop_phantom` are excluded, per
`ReadEnemies`'s own filters).

**31 hostiles is still under the 32-slot cap.** Repositioning the host (`tp 60 120`, well outside
the area the initial 31 were clustered in) and re-scanning produced the identical 32 unique ids —
this module's population is a fixed placement, not a proximity-triggered spawn table with
reinforcement waves; walking away does not grow it. `README.md`'s own "32-creature cap" line,
re-read against this evidence, is now understood differently: it recorded population reaching
exactly the cap (all 32 mirrored, nothing shed) in a PAST session's exploration/combat state, not
a claim that this module reliably exceeds 32 concurrently. In THIS save, at this point in the
module, it does not.

### Why this closes off both the distance test AND changes what "disconnect + reconnect" proves

With population ≤ cap, the ordinary 32-slot combat census (`ReadEnemies`, `CombatController`'s own
channel) never sheds anyone by distance regardless of where either player stands — so a `slay` on
any of the 31 is, by construction, never "out of census." Six more fresh kills were staged
anyway (`0x1B`, `0x1D` connected; `0x20`, `0x23` while the client was disconnected) specifically
to re-check whether the disconnect+reconnect path could isolate the channel a different way (the
reasoning that a DEAD creature might be excluded from a fresh `EnemySnapshotPacket`, starving
`CombatController` of anything to compare against on reconnect, independent of the 32-cap). It
does not: `ReadEnemies` publishes dead creatures too (current hit points ≤ 0 is a valid, INCLUDED
entry — this is how ordinary real-time death mirroring already worked before the liveness channel
existed), so a fresh post-reconnect `EnemySnapshotPacket` still carries every one of the ≤31
hostiles, dead or alive, and `CombatController` reaches the client with (or before) the liveness
packet every time, exactly as it did while connected. Confirmed live: `combat: killed 0x1D on the
owner's authority` (the CombatController mirror line) appears in the client's log for the
in-session kill; **zero** `liveness: 0x... is a corpse on the owner and was still alive here -
killing to match` or `liveness: 0x... no longer exists on the owner` lines appear anywhere in
either plugin log across the whole session (`grep -c "killing to match" == 0`) — the Kill/Destroy
dispatch this channel exists for remains completely unexercised, connected OR disconnected, in
this environment.

### What DID reconfirm cleanly, again

- **Anomaly path, repeatedly**: `0x1B` and `0x1D`, killed by the host while the client's own
  `local_combat` fight had (or was about to have) independently killed the same bodies for real,
  produced a burst of `liveness ANOMALY: owner says 0x... is alive, this instance already has it
  dead - not resurrecting` lines (dozens, at the controller's own throttled cadence) until the
  owner's next 2 s scan caught up — `status` climbed from a fresh count to 31 across the session,
  and no body was ever observed to un-die. Same guarantee, same clean result as the prior pass.
- **No false kill of a plot creature**: `end_trask` (`0xB6`, plot-flagged) still `resolve`s
  normally on the host after the whole session's kill/anomaly activity — never touched.
- **Transition-window suppression, clean THIS time**: the earlier END_M01AA pass saw one spurious
  "25 creature(s) newly gone this pass" burst immediately after an unrelated transition, noted as
  a transient artifact and not investigated further. The `tat_m20aa` warp in this pass produced
  no such burst — the host's agent log has no `liveness:` line at all in the several seconds
  around or after the warp settling, only the expected per-kill lines once real slays started
  minutes later. Consistent with the earlier burst being a one-off rather than systemic, though
  a sample of two transitions is not a proof either way.
- **Reconnect mechanics**: disconnect and reconnect both completed cleanly (`disconnected` ->
  `reconnecting...` -> `connected`), and the host's own owner-side scan correctly derived both
  `0x20` and `0x23` as `newly gone` WHILE the client was disconnected (`02:21:15`/`02:21:17`,
  client reconnected at `02:21:24`) — the scan itself does not need a connected peer to do its
  job, only the delivery does. What could not be shown, again, is the delivery reaching the
  client through the CHANNEL THIS FEATURE OWNS rather than through the ordinary census race it
  loses.

### Verdict: `liveness_sync` stays OFF — same call, better evidence for why

Not a new finding so much as the OLD finding pinned down with a number: this codebase's own
32-slot cap is not exceeded by any currently-reachable module's placed population, tat_m20aa
included, so `LivenessReconcileController`'s Kill/Destroy dispatch — the mechanism the whole
channel exists to ship — has now been tried against two different modules and genuinely cannot
be independently exercised by killing ordinary placed creatures in either. The two concrete next
steps from the prior pass stand, sharpened: (1) a module with a real proximity-triggered
reinforcement wave that pushes population past 32 (not just a large static placement — tat_m20aa
IS large, and still tops out at 31), or (2) a real scripted `DestroyObject` (which `ReadEnemies`
cannot represent at all — a destroyed object simply stops resolving, with no "Gone" concept on
that channel — making it the one case genuinely orthogonal to population size). Neither was
reachable this pass; no console verb exists to force either one synthetically (`slay` only ever
calls `Odyssey.KillCreature`, never a destroy). Left OFF by default.

## Live pass (2026-08-22): the `destroyobject` verb built, and the real blocker found underneath it

Task: build the smallest console verb that can force case (2) above — a real `DestroyObject`,
independent of population size — and use it to try, a third time, to isolate
`LivenessReconcileController`'s Destroy dispatch live. Built `destroyobject <id>`, following
`applyeffect`'s wiring exactly (`ConsoleCommand` → `CoopCommandDispatcher` → `CheatController` →
`Odyssey.DestroyObjectById`, the SAME engine call this channel's own Destroy action already
wraps) and `slay`'s authority rules exactly, via a new `CheatAuthority.ForDestroy` /
`DestroyVerdict` pair that mirrors `SlayVerdict`'s person/companion/plot checks but — deliberately
narrower than `slay` — refuses a mirroring instance outright (`RefuseNotOwner`) rather than
forwarding: there is no destroy-ledger to spend a forwarded destroy through, and carrying a
destroy TO a mirroring instance is this channel's own job, not a console verb's. `CheatKinds
.DestroyObject = 16` / `SharedBlock.CheatKindDestroyObject = 16` (next free number after
`MoviePlay = 15`); no shared-block version bump (reuses the existing `CheatTarget` field, same
discipline `ApplyEffect`/`FireScript`/`MoveNear` already established). See
`docs/research/console-cheats.md` §3 for the verb table entry and the authority rule.

### The verb itself: fully proven live, first try

Rig: `tools\demo.ps1` cold start, both GOG installs rebuilt and deployed
(`src\KotorCoop.Plugin\build.ps1 -Install` against both `-GameDir`s), block v48 / protocol 34
confirmed on both, `liveness_sync=1` on both, warped to `tar_m10aa`. Five separate live checks,
all clean on the first attempt:

```
HOST:   destroyobject 0x3B          -> "destroyed 0x3B"
HOST:   resolve 0x3B                -> "0x3B resolves to 0x0 here"           (confirms it is really gone)
HOST:   destroyobject 0x7FFFFFFF    -> "refused: 0x7FFFFFFF is your own character"
HOST:   destroyobject 0x11D         -> "refused: 0x11D is a party companion" (Trask, this session's id)
HOST:   destroyobject 0x130         -> "refused: 0x130 is a peer's phantom, not an enemy"
CLIENT: destroyobject 0x33          -> "refused: 0x33 is the owner's; destroyobject has no
                                         forward path - run it on the instance that owns this
                                         creature's fight"
```

Every refusal names the rule and the id, matching `CheatAuthority.Explain` verbatim — the same
"a guard that fails safe must say what it saw" bar every other staging verb in this codebase
holds itself to. The verb, its authority split, and its wiring are DONE; nothing about the
verb itself needs more live time.

### The reconcile dispatch: still not independently exercised — and now a *sharper* reason why

The `destroyobject 0x3B` above should have produced, within `LivenessScanIntervalMs` (2 s) on the
HOST's agent log, `liveness: 1 creature(s) newly gone this pass: 0x3B`, and shortly after that on
the CLIENT's plugin log, `liveness: 0x3B no longer exists on the owner - destroying to match` (or
equivalent). **Neither line appeared, across more than three minutes of observation** — a
materially longer wait than either of the two prior passes' own windows (§ above, both settled
within the ordinary 2 s scan cadence for every OTHER case they tried). This is not the same gap
those two passes recorded (population never exceeding the 32-slot cap); it is new, and it points
somewhere more specific.

**Isolated with a same-session control.** Immediately after the silent `destroyobject`, `slay
0x38` (an ordinary kill — `Odyssey.KillCreature`, HP → 0, corpse remains and still resolves) was
run against a different, equally fresh creature in the same census. The `census` verb's own
count dropped from 25 to 24 within the next 3-second check — the AGENT's external scan reacted to
the KILL correctly and promptly, the same freshness every other channel in this codebase has
always shown. A repeated `census` read taken **more than three minutes after** the
`destroyobject`, by contrast, still read **25** (not 24, as it would if 0x3B had genuinely dropped
out of the scan) even with 0x38 *also* now gone (which would have made it 23) — i.e. the census
arithmetic is consistent with 0x3B still being counted as present, the whole time. The engine's
own in-process view (the plugin's `resolve` verb, `CGameObjectArray::GetGameObject`-based) said
gone within the same second the destroy landed; the AGENT's external view (`EnumerateCreatures`'s
scan + `LiveObjectGate.Confirm`'s array-based re-check, `src/KotorCoop.Memory/KotorGameBridge.cs`
and `LiveObjectGate.cs`) did not update to agree with it inside the observed window, for THIS
specific object, while correctly and promptly updating for an ordinary kill on a sibling object
one command later.

**Reading of what this means, not yet independently confirmed further:** `Odyssey
.DestroyObjectById` is the NWScript `DestroyObject(object, fDelay=0, bNoFade=1,
fDelayUntilFade=0)` command — a real engine call, immediately effective for the PLUGIN's own
in-process resolution (`resolve` proves this) — but the object's underlying memory, or the
engine's own external-facing id→address table `EngineGlobals.TryResolveServerObject` walks, may
not be updated on the same timescale, or at all within a session, for a `DestroyObject`ed body the
way it is for one whose hit points merely dropped to zero. Both `ReadEnemies` and `ReadCreatureLiveness`
share the exact same `EnumerateCreatures()` → `Confirm()` pipeline (§1.1 above: "no new plugin-side
scan... reuses this directly"), so this is not a defect the new verb introduced or a gap specific
to the liveness channel — it is a property of the AGENT's own external creature enumeration that
neither `ReadEnemies` nor `ReadCreatureLiveness` had previously been tested against, because
nothing before `destroyobject` could put a `DestroyObject`ed body in front of either of them live.

This reframes the channel's own headline blocker. The two prior passes read the gap as "no
reachable module's population exceeds 32, so the shed-by-distance case never fires." This pass
finds a gap that would block the SAME dispatch even in a module that DID exceed 32, or even for a
population-independent script `DestroyObject` exactly like item 2 of §7's own checklist
anticipated — except the checklist expected the BLOCKER to be "no way to stage one," and it turns
out staging one (which this verb now does, cleanly) surfaces a SEPARATE, deeper blocker in the
scan that would have to be fixed first regardless of which staging path reaches it. Not chased
further this pass — diagnosing `EngineGlobals.TryResolveServerObject`/`LiveObjectGate.Confirm`'s
own behaviour against a `DestroyObject`ed id (rather than a freed/reused one) needs its own
focused session, and is out of scope for "build the verb and test the existing channel."

### The reconnect catch-up case

Not independently re-run this pass: since the root cause identified above sits in the OWNER's own
scan (the HOST never derives "gone" for a `DestroyObject`ed id within the observed window), the
CLIENT side of the reconnect case — whether a forced catch-up cycle delivers the picture promptly
once reconnected — cannot produce a different answer than "nothing to deliver," and re-running it
would not add information beyond what is already recorded above. Left open for whoever picks up
the scan-freshness question this pass surfaces.

### A related, same-session finding: party-presence positions can also go stale for minutes

Not this channel, but found while staging the roadmap-item-4 live check the same session (see
`docs/research/party-reconciliation.md`'s 2026-08-22 entry for the full writeup): the
`PartyPresenceSnapshot` a mirroring client's `SharedBlock` holds for a companion can freeze for
several minutes at a time during an otherwise-healthy, non-`[W_SAVE]` connected session, self-
resolving only via a full `disconnect`/`reconnect`. Recorded here too because it is the same
SHAPE of bug as this section's own finding — an external, agent-side read going stale relative to
the live game state while the in-process plugin view stays correct — even though the two are
different code paths (`ReadPartyMembers` vs. `EnumerateCreatures`/`Confirm`) and this document
does not claim they share a root cause.

### Verdict: `liveness_sync` stays OFF — a third pass, a different and more specific reason why

Per this project's own bar (flip on if clean): `destroyobject` itself, and the authority rules
gating it, are clean and live-verified without qualification — ship-ready as a diagnostic
instrument regardless of what happens to the flag below. `liveness_sync` stays OFF because the
dispatch it exists to run has now failed to fire live on its THIRD attempt, and for the first time
this pass narrows the reason to something concrete and independently checkable rather than "no
reachable module is big enough": the agent's own external scan appears not to notice a
`DestroyObject`ed body promptly, if at all, within a session, which would block the Destroy
dispatch regardless of population or module choice. The next concrete step is narrower than
either prior pass's own: instrument or directly test `LiveObjectGate.Confirm`
(`EngineGlobals.TryResolveServerObject`) against a `DestroyObject`ed id specifically, to confirm
or refute that reading before touching the reconcile controller itself.

## Round (2026-08-23) — why "under 32" already covers this case some other way, and a debug
## `--enemy-census-cap` built to make the KILL half of item 1 testable without new content

Task: understand precisely why every module small enough to have been tested in "already covers
this case some other way" while under the 32-slot census cap, and look for a way to make item 1
(a creature beyond the cap, shed by distance) testable without needing a module that legitimately
places 33+ creatures.

### Why "under the cap" already covers it

`KotorGameBridge.ReadEnemies` (the SAME scan `CombatController`'s ordinary real-time health/death
mirroring runs off every tick, distinct from this channel's own uncapped
`ReadCreatureLiveness`/`EnumerateCreatures` — see §1.1 above) truncates to `BlockEnemyCapacity`
(32) creatures, prioritising in-combat first, then nearest-to-a-player. **Every creature that
truncation KEEPS is already being health/death-mirrored by the ordinary channel, every tick,
independent of this document's own reconcile path** — that is what "covers this case some other
way" means concretely: with population ≤ cap, nothing is ever shed, so the ordinary channel alone
already produces exact agreement, and `LivenessReconcileController`'s own Kill/Destroy dispatch
never has a divergence to correct because one never opens in the first place. This is exactly what
the two `tat_m20aa`/`END_M01AA` passes above measured directly (31 and fewer hostiles respectively,
both under 32, both leaving the dispatch unexercised). It is **not** the same gap the third pass
(`destroyobject`) found — that one is population-independent (the agent's own scan not noticing a
`DestroyObject`ed body promptly) and would block the Destroy half of this channel even in a module
that DID exceed 32. This round's own fix (below) only targets the population-gated half — an
ordinary `slay` (`Odyssey.KillCreature`, HP -> 0) on a creature past the cap — which the third
pass's own control case already showed the agent's scan DOES notice promptly (`census` dropped
25 -> 24 within 3 seconds of a `slay`, unlike the `destroyobject` case).

### Made testable: `--enemy-census-cap`, a debug-only agent CLI option

Precedent checked first, per the task's own steer: `AgentOptions.Phantoms` (`-Phantoms N`,
`KotorGameBridge`'s existing `maxPhantoms` constructor parameter) is the established pattern in
this codebase for "an agent-side cap, normally a small built-in default, overridable per-session
for testing" — a CLI option threaded through `Program.CreateBridge` into the bridge's own
constructor, not a `coop.ini` key (that file is read by the PLUGIN process and the connect-flow
`CoopConfig`, neither of which owns `KotorGameBridge` or its census truncation, which lives
entirely in the out-of-process AGENT). `--enemy-census-cap` follows the identical shape:

- `AgentOptions.EnemyCensusCap` (int, default `0` = "use the built-in 32"), parsed by
  `--enemy-census-cap N`, clamped to `1..32` (never allowed to exceed the SharedBlock's actual
  32-slot wire capacity — this can only ever LOWER the truncation, never raise it).
- `KotorGameBridge`'s constructor takes an `enemyCensusCap` parameter (mirroring `maxPhantoms`'s
  own shape exactly) and stores it as `_enemyCensusCap`, falling back to `BlockEnemyCapacity` (32)
  when unset or out of range.
- `ReadEnemies` truncates to `_enemyCensusCap` instead of `BlockEnemyCapacity` directly.
  `TryWriteEnemies` (the actual wire write into the plugin's fixed-size SharedBlock region) is
  UNCHANGED — it still bounds itself by the true `BlockEnemyCapacity`, which is always safe
  since `_enemyCensusCap` can never exceed it.
- Zero behaviour change with the flag absent (`0`, the default): `_enemyCensusCap` falls back to
  the same 32 every existing session already runs with.

No `coop.ini` key, no `SharedBlock` layout change, no protocol version bump — this is purely an
agent-process launch parameter, exactly as narrow as `-Phantoms` is.

### Live-test plan

Goal: exercise `LivenessReconcileController`'s KILL dispatch (`liveness: 0x... is a corpse on the
owner and was still alive here - killing to match`) specifically, isolated from the separate
`DestroyObject`-scan-freshness blocker the third pass above found.

1. Launch the HOST agent with `--enemy-census-cap 5` (or similar — small enough that an ordinary
   populated area exceeds it, without needing a module anyone has confirmed has 33+ creatures).
   Leave the CLIENT agent's own cap at the default 32 (only the OWNER's publish needs shedding —
   the reconcile channel this tests is host -> client).
2. `liveness_sync=1` on both `coop.ini`s (still OFF by default; this is a deliberate opt-in test
   session). Warp both instances to any module with more than 5 creatures — `tat_m20aa` already
   has 31 confirmed live (see above), comfortably past a cap of 5.
3. Confirm the shed is real: `census` on the host should read at most 5 (the ordinary combat
   channel's own view), while `tools/creatures.ps1`/the liveness channel's own scan (uncapped, per
   §1.1) still sees the full population — the gap between the two IS the creature(s) now
   population-shed rather than distance-shed by a real player position, which is easier to
   reproduce on demand than physically separating two players far enough apart.
4. `slay <id>` on the HOST for a creature OUTSIDE the host's own now-5-wide `ReadEnemies` result
   (i.e., one that would have been included at cap 32 but is now shed) — confirmed via the
   `census`/`resolve` verbs before killing, the same discipline the third pass's own control case
   used.
5. Because that creature is no longer in the host's own `ReadEnemies` output, `CombatController`'s
   ordinary real-time channel never mirrors this kill to the client at all — the ONLY path left
   that can correct the client's now-stale "still alive" copy is this document's own liveness
   reconcile. Watch the CLIENT's plugin log for `liveness: 0x... is a corpse on the owner and was
   still alive here - killing to match` within `LivenessScanIntervalMs` (2 s) of the kill. This
   line, on THIS specific creature (never the "already dead here" anomaly path both prior live
   passes exercised repeatedly), is the dispatch this whole channel exists to prove.
6. If it fires: `liveness_sync`'s Kill half is confirmed live for the first time: the flag's
   remaining default-OFF reason narrows to the third pass's own `DestroyObject`-scan-freshness
   gap alone (the Destroy half), not "never independently exercised" in general.
7. If it does NOT fire within the window: this is now a genuinely NEW data point distinct from
   all three prior passes (population-gated case, real shed, ordinary `slay` — not
   `DestroyObject`, not an already-dead-here anomaly) and should be recorded here with the same
   rigor those three passes used (host log timing, client log timing, `census` before/after).

Not attempted this round — no live rig access (another agent owns the two-instance rig this
session); this is a design + implementation pass only, same as `destroyobject`'s own verb-building
round was before its own live pass.

### Test suite

`dotnet test tests/KotorCoop.Tests` (Release): 1638/1638 passed, unchanged baseline other than the
new build succeeding — this round's changes are additive (`AgentOptions.EnemyCensusCap`,
`KotorGameBridge`'s new constructor parameter and `_enemyCensusCap` field) and behaviour-neutral
with the flag unset, so no existing test's expectations moved.

## Live pass (2026-08-23): the cap makes it testable, and the Kill/Destroy dispatch is confirmed
live for the FIRST time — end to end, exactly as designed

Ran the live-test plan above almost exactly as written. Rig launched with the HOST agent given
`--enemy-census-cap 5` (via a new `tools/demo.ps1 -Agent1ExtraArgs @('--enemy-census-cap','5')`
passthrough, added this round — the script had no way to forward extra agent CLI args before;
additive, empty by default, zero effect on an ordinary `demo.ps1` invocation), CLIENT agent left at
the default 32. `liveness_sync=1` was already set on both installs from a prior session. Both
instances warped to `tat_m20aa` (host-only `warp`, client follows — the same travel-follow pattern
`party-reconciliation.md`'s 2026-08-22 entry already established as the way to avoid an
independent-warp `[W_SAVE]` mismatch), the same module the 2026-08-22 passes already measured at
31 confirmed hostiles.

### Step 3 confirmed: the shed is real

`census` on the host read **`OWNER 5 id(s)`** — exactly the `--enemy-census-cap 5` value, confirmed
working as designed (falls back to the built-in 32 when unset, per this round's own earlier
design — here it genuinely lowered the truncation). `tools/creatures.ps1` against the host (the
same uncapped raw vtable scan the 2026-08-22 passes used) still found the full ~31-hostile
population, confirming the gap between the two — `census`'s capped 5 vs. the raw scan's full
population — is real and is exactly the population-shed condition item 1 needs.

### Step 4-5: a genuinely out-of-census creature, killed, and the ONLY channel that could correct
the client is this one

Picked `0x9` (`tat20_09chief_01`, position (15.77, 63.74)) — by a wide margin the single FARTHEST
hostile from the player's position ((-27.38, 66.76), ~43 units away, versus the nearest hostiles
at 2-9 units) — as the creature certain to be outside the host's own 5-cap `ReadEnemies` result
(sorted nearest-first with none in combat yet). Confirmed alive and resolvable on the host first
(`resolve 0x9` → a real address), then `slay 0x9` on the host (`Odyssey.KillCreature`, HP → 0).

### Result: BOTH the Kill and Destroy dispatch fired correctly, converging the client to match the
host — the headline this whole document exists to prove, confirmed live for the first time

Host agent log:
```
[19:39:14] * liveness: 1 creature(s) newly gone this pass: 0x9
```

Client PLUGIN log, within the very next scan/reconcile cycle:
```
[19:39:13.610] liveness: 0x9 is a corpse on the owner and was still alive here - killing to match
[19:39:15.623] liveness: 0x9 no longer exists on the owner and still did here - destroying to match (local 0x9)
```

(The Kill line's timestamp preceding the host's own "newly gone" log line by ~0.4s is a real
observation, not a contradiction — the owner's OWN scan derives "Dead"/"Gone" from its comparison
against the LAST scan's state, and `slay` immediately makes the creature resolve as HP-zero/corpse
on the host well before that scan's own log line is written; the two systems' logging cadences are
independent, not causally ordered by these particular timestamps.) The client first converged to
**Dead** (matching the corpse state immediately after `slay`), then — once the owner's own
`ReadCreatureLiveness` scan derived the corpse as having fully disappeared from the loaded area on
a later cycle — converged again to **Gone** (destroyed). `resolve 0x9` on the CLIENT afterward
confirmed genuine, complete destruction: `no target: 0x9 resolves to 0x0 here` — not merely a log
line, the body is actually gone.

This is the FIRST time, across four prior live passes (2026-08-22 x3, this document's own record),
that `LivenessReconcileController`'s Kill/Destroy dispatch has fired due to a genuine, real
divergence the ordinary 32-slot combat census could not have caught on its own — every earlier
attempt was either confounded by the ordinary census reaching the client first (population ≤ 32,
so nothing was ever actually shed) or blocked by the separate `DestroyObject`-scan-freshness defect
the third 2026-08-22 pass found (population-independent, and not implicated here since this was an
ordinary `slay`, not a `destroyobject`). `--enemy-census-cap` closes exactly the gap it was built
for.

### Verdict: the reconciliation mechanism itself is proven sound end to end; `liveness_sync` stays
OFF pending a decision on the flag's own remaining open items

The channel's headline claim — a body alive on one screen and dead/gone on the other gets
corrected, converging the wrong side to the right one — is now directly, live-confirmed, not
merely designed and unit-tested. This is a genuinely positive result: three prior live passes
(2026-08-22) all found real things (the anomaly path, the never-resurrects guarantee, a real
scan-freshness defect in `EngineGlobals.TryResolveServerObject`/`LiveObjectGate.Confirm` for a
`DestroyObject`ed body specifically) but never the dispatch itself firing from a genuine
population-shed divergence; this pass closes that gap. `liveness_sync` is left OFF by default
regardless, per this project's own bar and for reasons independent of this pass's positive
result: the `DestroyObject`-scan-freshness defect the third 2026-08-22 pass found is still
unfixed (this pass's own kill went through the ordinary `slay`/HP-zero path, not `destroyobject`,
so it does not re-test or resolve that defect), and a flag this document itself has left off
across four live passes for four different reasons is not flipped on the strength of a fifth pass
closing only one of them. The concrete remaining blocker for a future round: fix or re-test
`LiveObjectGate.Confirm` against a genuinely `DestroyObject`ed id now that `--enemy-census-cap`
gives a cheap, repeatable way to stage the population-shed half of any future test.

### Test suite and build

`dotnet test tests/KotorCoop.Tests` (Release): 1638/1638 passed, unchanged baseline — this pass's
only production change is additive (`tools/demo.ps1`'s new `-Agent1ExtraArgs`/`-Agent2ExtraArgs`
passthrough params, empty-array default, zero effect on the ordinary invocation), no C# changes.
Both plugin builds clean from an earlier point in the same combined session. Rig eventually torn
down clean (`Get-Process swkotor,KotorCoop.Agent` empty), `auto_start_agent` restored to `1` in
both GOG installs.
