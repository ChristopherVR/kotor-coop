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

## Round (2026-08-24) — a genuine disassembly pass at the `DestroyObject`-scan-freshness
## blocker: the two lookups are STILL byte-identical, a real async mechanism is found underneath
## `DestroyObject`, and the prior round's own evidence for "still stale after 3 minutes" turns
## out not to rule out the one confound that matters — no code fix shipped

Task (assigned, no live rig access this round): find and fix why the agent's external scan
(`EnumerateCreatures()` → `LiveObjectGate.Confirm()`, `EngineGlobals.TryResolveServerObject`)
does not notice a `DestroyObject`ed body, per the previous round's own closing paragraph naming
this "the concrete remaining blocker for a future round." Read `LiveObjectGate.Confirm`,
`EngineGlobals.TryResolveServerObject`, and `Odyssey.ResolveServerObject` in full, then used
`tools/disasm.py`/`tools/xrefs.py` against the GOG `swkotor.exe` and `tools/kotor1_0_3.db`'s
symbol table (a SQLite `functions`/`classes`/`offsets` schema this round discovered gives real
class/method names for addresses, not previously exploited by name in this document) to trace
what `DestroyObject` actually does to the object array, compared to an ordinary kill.

### What was RE-CONFIRMED, not assumed: the two lookups are still byte-identical

The 2026-08-15 investigation (`enemy-presence-sync.md` §"Root cause 2026-08-15") already proved
`EngineGlobals.TryResolveServerObject`'s C# transcription of `CGameObjectArray::GetGameObject`
(0x004D8230) matches the real engine call for a LIVE object. This round re-disassembled the
whole chain fresh, now with names, to check whether anything differs for a DESTROYED one:

- `CServerExoApp::GetGameObject` (0x004AE750) is a two-instruction thunk
  (`mov ecx,[ecx+4]; jmp 0x4B1700`) into `CServerExoAppInternal::GetGameObject` (0x004B1700),
  which reads `[ecx+0x1005C]` (the object array — the exact field
  `EngineGlobals.TryGetServerObjectArray` reads) and calls `CGameObjectArray::GetGameObject`
  (0x004D8230) directly, comparing its return byte against `[0x74666C]` (a read-only `.rdata`
  zero, the not-found sentinel).
- `CGameObjectArray::GetGameObject` (0x004D8230) is, byte for byte, the bucket-and-chain walk
  `EngineGlobals.TryLookupObject` already transcribes: `bucket = ((id>>31)&1)<<12 | (id&0xFFF)`,
  walk `tree[bucket]`'s linked list comparing `entry->id` (offset 0) against `id&0x7FFFFFFF`,
  return `entry->object` (offset 4) on a hit. **There is no additional flag, generation counter,
  or liveness check anywhere in this function** — it is a plain hash-table lookup and nothing
  else. This directly rules out the design brief's own leading hypothesis ("a generation
  counter, a validity flag... the in-process call has access to a more authoritative live
  check the external walk does not") for THIS specific function: there is no such extra check
  for the in-process caller to have that the external one lacks. Both sides read the identical
  algorithm over the identical physical memory, with no cross-process caching in the way either
  (`ProcessMemory.TryRead*` are plain `ReadProcessMemory` calls every time — no memoisation
  layer exists between `EngineGlobals.TryResolveServerObject` and the OS syscall, confirmed by
  reading `src/KotorCoop.Memory/ProcessMemory.cs` in full).

So the object-array LOOKUP is not, and was never, the place the two sides could disagree. If
they disagree live, the object array's CONTENT must actually differ at read time, or something
outside this lookup (the agent's own scan/cache pipeline) must be at fault — not the algorithm.

### NEW this round: `DestroyObject` does not synchronously remove anything from the array

Disassembling the NWScript `DestroyObject` command handler itself (0x0052FF20, reached via
`Odyssey.DestroyObjectById`'s `ExpectedDestroyObject` dispatch) and its callees, matched against
`tools/kotor1_0_3.db`'s symbol table, in full:

1. It resolves the target via `GetGameObjectFn` (0x4AE750 — the SAME generic getter this whole
   document is about) purely to decide whether to set two CLIENT-side cosmetic flags:
   `CSWSObject::SetDeleteNoFade` (0x4CE920) and `CSWSObject::SetFadeOutTime` (0x4CE8A0). Both
   were disassembled in full: they call `CServerExoApp::GetPlayerCreatureId` +
   `CServerExoApp::GetClientObjectByObjectId` to find a CLIENT-side render/party node matching
   the object's own id and set a flag on IT (falling back to a flag field at `+0x204` on the
   SERVER object itself if no client node matches). **Neither touches the object's identity
   fields at `+0x04`/`+0x14`** — the two fields `Odyssey.ResolveServerObject`'s "layout check"
   probes — which rules out this round's own leading alternative theory (a stale-but-still-
   linked array entry whose *content* has been invalidated, caught by the in-process layout
   check the external walk lacks): if the array still held the id at this point, both sides
   would still agree it resolves, layout check included, since nothing here corrupts `+0x04`.
2. Unconditionally, it then computes a delay value from `fDelayUntilFade`/`fDelay` and calls
   `CServerExoApp::GetServerAIMaster` (0x4AED80) followed by `CServerAIMaster::AddEventDeltaTime`
   (0x4B08D0) — **the object is not deleted here. A delayed event is scheduled into the AI
   Master's own event queue** (`AddEventDeltaTime` → `CWorldTimer::GetWorldTime` +
   `CWorldTimer::AddWorldTimes` → `CServerAIMaster::AddEventAbsoluteTime_2`, a classic
   now-plus-delta scheduler). With `fDelay=0`/`fDelayUntilFade=0` (what `Odyssey.DestroyObjectById`
   passes for an instant remote-pickup-style destroy), this should fire on the very next AI
   Master tick, not synchronously inside the `DestroyObject` call itself.
3. The actual array removal — `CGameObjectArray::Delete` (0x4D81A0, xref-traced and disassembled
   in full: it properly unlinks the bucket-list entry, either rewriting the bucket head or the
   previous entry's `next`, then frees the entry struct via `0x6FA390`) — is only ever reached
   through `CGameObjectArray::Delete_2` (0x4D8290), whose 8 call sites (`tools/xrefs.py 0x4D8290`)
   resolve, via the symbol table, to real object destructors: `CSWSObject::Destructor` (0x4D02A0),
   `CSWSModule::Destructor`, `CSWSArea::Destructor`, `CSWCVisualEffect::Destructor`,
   `CSWCProjectile::Destructor`, plus `CSWSModule::EventHandler`/`CSWCArea::UnloadArea`/
   `CSWCAreaOfEffectObject::AIUpdate`. **The array entry is removed exactly when, and only when,
   the C++ object's own destructor runs** — which for a creature happens when the scheduled AI
   Master event actually fires and frees it, not at the moment `DestroyObject` is called.

This is a genuinely new finding for this document: `DestroyObject` is a two-phase, partly
asynchronous operation (immediate client-side cosmetic flag, then a scheduled server-side
delete), not the single synchronous engine call the design brief and prior rounds implicitly
assumed. It does not, by itself, explain a THREE-MINUTE external staleness — an AI Master event
queue serviced every server tick should fire a zero-delay event within at most a frame or two,
which is consistent with the third 2026-08-22 pass's own observation that `resolve 0x3B` showed
`0x0` "within the same second" the destroy landed — but it does mean the removal is provably
NOT instantaneous-by-construction, and a live session that wants to settle this needs to account
for that scheduling step rather than treat `destroyobject` as atomic.

### The layout-check theory, specifically ruled out

`Odyssey.ResolveServerObject`'s in-process "the engine found it, we threw it away" branch
(added 2026-08-15, `enemy-presence-sync.md` round four) was this round's other leading
hypothesis for the asymmetry — the plugin re-validates `+0x04`/`+0x14` after a hash hit; the
external `EngineGlobals.TryResolveServerObject` does not. Given the established fact already on
record in this document (`LiveObjectGate`'s own doc comment: "`operator delete` does not clear
the vtable word or the object id that follows it"), this theory is now provably moot for THIS
specific symptom: if the array still held the entry (delete not yet run), `+0x04` would still
read the correct id — nothing between `DestroyObject` being called and the object's eventual
destructor running writes to it (confirmed above: the only two things that run synchronously,
`SetDeleteNoFade`/`SetFadeOutTime`, do not touch it) — so the layout check would PASS, not fail,
for exactly as long as the array entry survives. And once the destructor does run,
`CGameObjectArray::Delete` removes the entry outright, so the raw hash lookup itself returns 0
and the layout check is never reached. There is no window in which the hash lookup succeeds but
the layout check would reject it. `Odyssey.ResolveServerObject`'s `resolved to 0x0` result,
observed live, must therefore mean the raw array lookup itself returned 0 (the array genuinely
lost the entry) — not a layout mismatch on a still-linked stale entry.

### The real gap: the prior round's own evidence does not rule out its module's population dynamics

Given the array lookup is proven identical and the removal, once it runs, is a clean unlink —
there is no code-level mechanism by which `EngineGlobals.TryResolveServerObject`, polled
repeatedly (every ≤250ms via `LiveObjectGate`'s `LiveConfirmMs`, confirmed by re-reading
`KotorGameBridge.EnumerateCreatures`'s cache-invalidation logic line by line — `_liveCache` is
unconditionally recomputed at least every 250ms with a FRESH `TryGetServerObjectArray` call, no
staler layer sits in front of it) should keep returning a STALE, non-zero answer for a body
whose array entry is truly gone. Re-reading the third 2026-08-22 pass's own transcript with this
in mind surfaces a methodology gap it did not close: its claim that "0x3B is still counted" after
three minutes was inferred from ARITHMETIC ON THE AGGREGATE `census` COUNT (`25` before, `24`
after an unrelated control kill, `25` again three minutes later, "not 23 as it would be if both
had genuinely dropped") — **it never ran `census 0x3B`, the existing per-id diagnostic
(`CoopCommandDispatcher.DescribeId`, which calls `KotorGameBridge.TryResolveObjectHere` — the
EXACT function under scrutiny — directly and reports `array 0x... | array NOT HELD` for that one
id, unambiguously)**. The aggregate count can climb back to 25 for a reason that has nothing to
do with 0x3B: `tar_m10aa` (the module that pass used) was never characterised for population
dynamics the way `tat_m20aa` was in the pass immediately before it (§"the population, measured,
not inferred" above, which found `tat_m20aa`'s 31 hostiles to be a fixed placement, not a
proximity-triggered spawn table) — an ambient encounter, a scripted reinforcement, or simply a
creature crossing back into scan range during three minutes of real time would independently
explain the count returning to 25 with 0x3B genuinely gone the whole time. The prior round's own
conclusion is plausible and may well be correct, but as written it cannot distinguish "0x3B is
still resolvable" from "something else in this uncharacterised module re-populated the count" —
and this round's disassembly gives no reason to expect the array lookup itself to lie.

### Verdict: no code change shipped — the honest-limitations bar, not a guess dressed as a fix

Per the task's own standard: a fix was not shipped, because no concrete defect was found in
`LiveObjectGate.Confirm`, `EngineGlobals.TryResolveServerObject`, or `KotorGameBridge`'s caching
that would produce the reported symptom, and the one candidate difference between the two
lookups (the in-process layout re-check) was disassembly-ruled-out above as incapable of causing
it. Shipping a change here (e.g. porting the layout check to the external side anyway "just in
case") would be exactly the "guess dressed as a change" the 2026-08-15 investigation warned
against, applied to a theory this round's own evidence already excludes. `dotnet test` was not
re-run for a code change because none was made; the worktree carries no production diff.

**What a live session needs to check next, precisely, to settle this instead of re-guessing:**

1. Repeat the `destroyobject <id>` staging from the third 2026-08-22 pass, but on a module whose
   population dynamics are known static first (`tat_m20aa`, already measured at a fixed 31 — see
   above — rather than the uncharacterised `tar_m10aa`), to remove the respawn/reinforcement
   confound entirely.
2. Immediately, and then repeatedly across at least a 3-5 minute window (every 15-30 s is
   plenty — `_creatureCache`'s own 15 s scan TTL is the loosest link in the chain), run BOTH
   `census 0x<id>` on the AGENT console (the per-id diagnostic, unambiguous "array 0x... /
   array NOT HELD") and `resolve 0x<id>` on the PLUGIN console (in-process, same question) side
   by side, logging both replies with timestamps. This is the direct analogue of
   `Odyssey.DescribeObjectResolution` (built 2026-08-15 specifically to ask "both questions in
   one frame, on the game thread" instead of comparing measurements taken seconds and processes
   apart) — applied here across TWO processes and a multi-minute window, which nothing has done
   yet for this specific defect.
3. If `census 0x<id>` and `resolve 0x<id>` ever DISAGREE (one says gone, the other still
   resolves) at the SAME sampled instant, that is the first genuinely reproducible instance of
   the defect this document has recorded with an unambiguous per-id signal — record which side
   lags and for how long, and whether it EVER catches up within the session (self-healing on a
   later scan) or stays wrong until a module change/reconnect.
4. If they never disagree once the aggregate-count confound is removed, the defect this
   document has been chasing across three live passes may not exist as stated — the 2026-08-22
   finding would then need to be re-attributed to `tar_m10aa`'s own uncharacterised population
   dynamics, and `LivenessReconcileController`'s Destroy dispatch would be back to merely
   "not yet independently exercised" (the §"Why 'under the cap' already covers it" gap this
   document already understands) rather than blocked by a confirmed scan-freshness bug.
5. Either outcome is useful and should be recorded here with the same rigor as every prior
   pass — this is the smallest live experiment that can finally tell apart "real defect" from
   "unexamined module population dynamics," which is what every attempt so far has actually been
   unable to separate.

### Files (unchanged this round)

No source files were modified. This round's only artifact is this document entry, informed by
fresh disassembly of `swkotor.exe` (`CServerExoApp::GetGameObject` 0x4AE750,
`CServerExoAppInternal::GetGameObject` 0x4B1700, `CGameObjectArray::GetGameObject` 0x4D8230,
`CGameObjectArray::Delete`/`Delete_2` 0x4D81A0/0x4D8290, the `DestroyObject` NWScript command
handler 0x52FF20-0x530169, `CSWSObject::SetDeleteNoFade`/`SetFadeOutTime` 0x4CE920/0x4CE8A0,
`CServerAIMaster::AddEventDeltaTime` 0x4B08D0) cross-referenced against `tools/kotor1_0_3.db`'s
`functions`/`classes`/`offsets` symbol tables via `tools/disasm.py` and `tools/xrefs.py`.

## Round (2026-08-24), continued — a THIRD theory for the scan-freshness gap:
## `CServerAIMaster::UpdateState`'s event-queue drain is real-time-budgeted, which could make
## the reported staleness a load-correlated property of the base engine, not a coop-plugin bug —
## no fix shipped, a paired-sampling probe script built instead

Task (assigned, no live rig access this round, continuing directly from the disassembly pass
above): the two leading theories for the `DestroyObject`-scan-freshness gap (a stale array
entry, a missing generation check) are both disassembly-ruled-out per this document's own
immediately preceding entry. Look specifically for a THIRD mechanism — a timing/ordering issue
between when an object is destroyed and when the next scan runs — that would not show up as
either of those two, using the same NCS/x86 disassembly approach plus `tools/kotor1_0_3.db`'s
symbol table.

### Where the prior round left off, and where this one starts

The prior entry established that `DestroyObject` does not delete anything synchronously — it
schedules a delayed event via `CServerAIMaster::AddEventDeltaTime` (0x4B08D0), and the actual
array unlink (`CGameObjectArray::Delete`/`Delete_2`, 0x4D81A0/0x4D8290) only runs when that
event's target object's own C++ destructor eventually runs. It read `AddEventDeltaTime` itself
in full but explicitly did NOT read what pops and dispatches events out of the AI Master's queue
once they are due, writing: "this should fire on the very next AI Master tick, not
synchronously... which is consistent with the third 2026-08-22 pass's own observation... but it
does mean the removal is provably NOT instantaneous-by-construction." This round reads that
missing half: `CServerAIMaster::UpdateState` (0x4B0B70, `tools/kotor1_0_3.db`'s
`functions` table — the AI Master's own per-tick pump, symbol-confirmed, not inferred from a
generic vtable slot), disassembled in full (`python tools/disasm.py 0x4B0B70 1600` against the
GOG `swkotor.exe`).

### What `UpdateState` actually does, established from the disassembly and the symbol table

Every callee below is a *named* hit in `tools/kotor1_0_3.db`, not a guess from calling
convention alone:

- It reads the current time twice by two different means: `CWorldTimer::GetWorldTime`
  (0x4ADE40, the game-simulation clock — the same call `AddEventDeltaTime` uses to compute a
  scheduled delta) for the object-state work, and `CExoTimers::GetHighResolutionTimer`
  (0x5E9260, a real wall-clock/QPC-style timer) captured before and after a chunk of work, for a
  budget check — these are two DIFFERENT clocks serving two different purposes in the same
  function, and conflating them was the risk in reading this function too quickly.
- **Object-state scan** (0x4B0C20-0x4B0D0B): walks an object list (`ebp = edi+0x40`), resolves
  each id via the same `CServerExoApp::GetGameObject` (0x4AE750) this whole document is about,
  checks an HP/priority byte and a state field at `[object+0xA8C]` against several small
  constants (1, 0x3D, 4, 5, 6), and for one matching case calls `CSWSCreature
  ::WalkUpdateLocation_QuickWalk` (0x51D9C0, symbol-confirmed) and conditionally writes
  `[object+0xA8C] = 1`.
- **Event-queue drain** (0x4B0D11-0x4B0E36): while the AI Master's own event queue
  (`edi+0x54`) is non-empty AND its head event's scheduled time (checked via `CWorldTimer
  ::CompareWorldTimes`, 0x4ADFA0, symbol-confirmed — the SAME comparator `AddEventDeltaTime`'s
  insert uses to keep the queue sorted) is not still in the future, `CExoLinkedListInternal
  ::RemoveHead` (0x5E9C80, symbol-confirmed) pops it, resolves its target object id via
  `GetGameObject` again, and either dispatches it (object still resolves — vtable calls through
  `[vtable+0x78]`/`[vtable+0x28]`/`[vtable+0x20]`, matching handler categories inferred from the
  earlier `SetDeleteNoFade`/`SetFadeOutTime` context) or, if the object no longer resolves,
  calls `CServerAIMaster::DeleteEventData` (0x4B0AB0, symbol-confirmed) to discard it. **This is
  the loop whose dispatch, for a `DestroyObject`-scheduled event, is what actually lets the
  target's destructor run and `CGameObjectArray::Delete` unlink it** — the mechanism the prior
  round found but did not trace forward to its consumer.
- **A real-time budget gates the whole thing.** After the drain loop, the code takes a 64-bit
  difference of two `GetHighResolutionTimer` samples and compares it against a limit computed
  earlier in the function (`0x4B0E4A`-`0x4B0E5C`, `0x4B0FDD`-`0x4B0FE3`): if the elapsed WALL-
  CLOCK time for this call is still under budget, it jumps BACK to the start of the object-state
  scan (`0x4B0C20`) and repeats — object-state scan, then event-queue drain, then the budget
  check again — advancing its own internal notion of "now" (re-read via `GetWorldTime`) each
  pass, until either the queue empties or the budget runs out. The exact numeric limit and its
  unit are NOT fully pinned down this round — the two operands feeding the comparison are built
  through a 64-bit multiply/divide pair (`0x6FC710`/`0x6FB030`, both unnamed in the symbol
  table, confirmed by disassembly to be generic 64-bit `mul`/`div` helpers, not a "clamp"
  function as an earlier draft reading of this same code mis-typed them) scaling a default of
  `0x2710` (10000, in whatever the resulting unit is) — but the STRUCTURE (a `GetHighResolutionTimer`
  before/after pair, subtracted and compared against a stored limit, gating a repeat of exactly
  the two loops above) is unambiguous from the disassembly regardless of the exact number.
- **Separately, and unconditionally** (0x4B103B-0x4B117F, after the budgeted while-loop exits):
  a five-bucket "AI level" scan (`CSWSObject::IsDirtyFlagSet`/`ClearDirtyFlag`/`GetClientObject`,
  0x4CC220/0x4CC250/0x4CC2B0, all symbol-confirmed) walks every object registered via
  `CServerAIMaster::AddObject`/`RemoveObject` (0x4B0850/0x4AF3D0, both disassembled this round:
  `AddObject(level 0..4, object)` files an object into one of five buckets by `object+0x78`,
  and every creature placed in a module registers into exactly one bucket — this scan's own
  cost is therefore proportional to the module's total creature count, not just its hostiles).

### The theory this supports, and why it is genuinely a THIRD mechanism

`DestroyObject`'s actual array-unlink depends on the event-queue drain loop above running and
reaching that specific event before the object's target is asked about again. That loop is
gated by a real, wall-clock time BUDGET shared with an object-state scan whose cost scales with
however many objects are in the AI Master's tracked list, and the function's outer scan (the
unconditional five-bucket dirty/walk-update pass) is a further, unconditional chunk of
per-call work whose cost scales with the module's total registered creature count. **Under a
module with enough registered objects, the per-call budget could plausibly be exhausted by the
object-state scan before the event-queue drain gets a turn (or gets far enough into a deep
queue) — deferring the actual delete, and therefore the array unlink both `LiveObjectGate
.Confirm` and the plugin's own `Odyssey.ResolveServerObject` are asking about, for as many
`UpdateState` calls as it takes for the budget to catch up.** This is not "a stale array entry"
(the entry is genuinely still linked, correctly, because the destructor genuinely has not run
yet) and it is not "a missing generation check" (there is nothing to check — both sides read
the same still-valid entry and correctly agree it resolves). It is a THIRD shape entirely: **the
underlying event that would make the two sides' answer change has not fired yet, on either
side, for a reason that is a genuine, load-correlated property of the base engine's own AI
scheduler** — which would mean the `DestroyObject`-scan-freshness "issue" this document has
chased across four live passes might not be a scan-freshness bug in this codebase at all, in the
same sense round 72 found the corpse-vanish mystery to be vanilla engine/content behaviour, not
a coop-plugin bug.

### Why no fix was shipped

This theory does not point at a defect in `KotorGameBridge`, `LiveObjectGate`, or
`Odyssey.ResolveServerObject` — all three already read the array correctly and promptly relative
to whatever the array actually contains (the 2026-08-24 disassembly round already established
this for the lookup algorithm itself, and this round adds no reason to doubt it). There is also
no safe, verifiable fix available on this codebase's own side of the boundary: forcing the
array-unlink ourselves (writing `CGameObjectArray::Delete` behaviour by hand, or nudging the AI
Master's own event queue) would be exactly the kind of direct engine-internals write this
project's own rules caution against shipping without a live-verified offset, and there is no
live rig this round to verify one. Hooking the `DestroyObject` NWScript action dispatch itself
so THIS codebase's own liveness scan could learn "a destroy was requested" independently of the
engine's own deferred completion would be a real, bigger architecture change (a new detour point,
not an existing one) that also cannot be shipped un-verified. Per this project's own bar and the
task's own instruction not to force a speculative fix: none was written. `dotnet test` was not
re-run for a source change because none was made to `src/`.

### What was built instead: a paired-sampling probe that can test this theory (and re-run the
### prior round's own recommended recipe) without a human watching a clock

The 2026-08-24 disassembly round's own closing section proposed the right experiment (paired
`census 0x<id>`/`resolve 0x<id>` sampling, repeated over a multi-minute window, on a
population-characterised module) but did not build it — that round shipped no code. This round
builds it: `tools/liveness-freshness-probe.ps1`, a new script that automates exactly that
recipe and adds the piece this round's own theory needs to be tested: a `-Label` so the SAME
recipe can be re-run in modules of different characterised population and the two runs'
convergence times compared.

- Fires (or accepts an already-staged) `destroyobject <id>`/`slay <id>` on one instance, then on
  a fixed cadence issues BOTH `census <id>` (the agent's own external-scan diagnostic,
  `CoopCommandDispatcher.DescribeId` → `KotorGameBridge.TryResolveObjectHere`) and `resolve
  <id>` (the plugin's in-process diagnostic, `CheatController.Resolve` →
  `Odyssey.ResolveServerObject`) for the SAME id, logging both with timestamps to a CSV, and
  flags the instant (if ever) the two disagree — the exact per-id signal the prior round's own
  methodology critique said was missing (that round's evidence was aggregate `census`-count
  arithmetic, never a direct `census 0x3B` call).
- **A real subtlety found and worked around while building this**: `census <id>`'s own console
  REPLY is not useful for this — `CoopCommandDispatcher.DescribeId` issues the array/scan line
  via `Reply()` and then IMMEDIATELY issues a second `Reply()` ("listed hp .../not listed in
  this census") that overwrites it in the single-slot shared-block reply field, per `Reply()`'s
  own doc comment ("only the last line survives in the block, and all of them survive in the
  log"). The script reads the array/scan line back from the AGENT's own log file instead
  (tracking a byte offset so consecutive samples cannot cross-read each other's answers);
  `resolve <id>`'s console reply has no such problem (`CheatController.Resolve` calls `Answer()`
  exactly once) and is read directly, matching how every prior live pass in this document
  actually used it.
- Also samples the OWNER's plain `census` (no id) each round for population context, so a CSV
  from a session records the module's own creature count alongside every disagree/agree sample.

Not run live this round (no rig access) — this is tooling built for the NEXT live round, not a
result. `dotnet test` was not re-run for this either (a `.ps1` addition under `tools/`, no
`src/` changes); the script's own syntax was checked with PowerShell's parser
(`[System.Management.Automation.Language.Parser]::ParseFile`, zero errors).

### What the next live round should actually do

1. Run `tools/liveness-freshness-probe.ps1` in `tat_m20aa` (already population-characterised at
   a fixed 31 hostiles — see the 2026-08-22 entry above) with a `destroyobject`-staged id,
   sampling for at least 3-5 minutes at a 15s cadence, `-Label tat_m20aa-31pop`.
2. Run the SAME script, SAME staging (`destroyobject`), in a module confirmed to have a much
   SMALLER total registered-object count (not just fewer hostiles — the object-state scan and
   the five-bucket dirty scan this round found both appear to cost proportional to the AI
   Master's total tracked object count, not just the census's hostile-only view; a corridor or
   small early-game area with a handful of NPCs is the right shape) — `-Label <module>-lowpop`.
3. Compare the two CSVs' `firstAgentGone`/`firstPluginGone` summary lines. If the high-population
   run converges MUCH later than the low-population run (and especially if the low-population
   run converges within one or two sampling intervals), that is real, load-correlated evidence
   FOR this round's theory — the "issue" is base-engine AI-scheduler latency, not a coop-plugin
   scan bug, and this document's remaining open item becomes "document the expected latency and
   size `LivenessScanIntervalMs`/user expectations around it," not "find and fix a bug."
4. If both runs converge on essentially the same short timescale regardless of population, this
   round's own theory is not the (or not the whole) explanation, and the disagreement-flag
   output (if it ever fires) is the next concrete lead — record either outcome here with the
   same rigor every prior pass in this document has used.
5. Independent of the above: if the probe EVER reports a `DISAGREE` sample (census says gone,
   resolve still resolves, or vice versa, at the same sampled instant), that is the first
   directly reproducible instance of an actual scan-freshness DEFECT this document has recorded
   with an unambiguous per-id signal, separate from the load-latency question — chase that first
   if it appears, since it would mean something IS wrong in the lookup/cache pipeline after all,
   which this round's disassembly did not find but also cannot fully rule out without live data.

### Files

- `tools/liveness-freshness-probe.ps1` — new, the paired-sampling probe described above. No
  other files touched; no `src/` changes.

## Live-verified (2026-08-24) — the probe run for the first time: convergence was IMMEDIATE in
both a high- and a moderate-population module, at this document's own 10s sampling grid; no
support found for the load-correlated theory at these population levels, and no disagreement ever
observed

Rig access this round. Ran `tools/liveness-freshness-probe.ps1` exactly as the prior round's own
"what the next live round should do" section specified, with one scope reduction for time
(`-DurationSec 90`/`60` and `-IntervalSec 10` rather than the recommended 3-5 minutes at 15s — a
shorter first pass, not the full recipe) and a substituted "low-population" module (`END_M01AA`,
25 registered hostiles per its own `census`, not dramatically lower than `tat_m20aa`'s 28-29 —
the prior round's own suggestion of "a corridor or small early-game area with a handful of NPCs"
was not chased further this pass; `END_M01AA`'s own population turned out closer to `tat_m20aa`'s
than expected).

**`tat_m20aa`** (`-Label tat_m20aa-29pop`, `destroyobject 0x36` staged, population 28-29 across
the window per the OWNER `census` sampled each round): both `census 0x36` (agent) and
`resolve 0x36` (plugin) already read "gone" on the VERY FIRST sample, taken immediately after
staging — `first AGENT reported gone at: 0s`, `first PLUGIN reported gone at: 0s`, zero
disagreements across 9 samples spanning 80s.

**`END_M01AA`** (`-Label end_m01aa-25pop`, `destroyobject 0x10` staged, population 24-25): same
result — both sides read "gone" at `t=0s`, zero disagreements across 6 samples spanning 50s.

### What this does and does not settle

At the population levels this pass could reach (24-29 registered objects, both well under
whatever `CServerAIMaster::UpdateState`'s own real-time budget would need to be exhausted by the
object-state scan before the event-queue drain gets a turn), the theorized load-correlated delay
did not manifest at all — convergence was immediate in BOTH modules, not merely faster in the
lower-population one. This is consistent with two different readings, and this pass cannot
distinguish them: (a) the theory is correct but 24-29 objects is nowhere near enough load to
exhaust a ~10,000-unit real-time budget per `UpdateState` call, so a genuinely busy module (many
times this population, or one with a much deeper AI Master event queue for other reasons) would
still show it, or (b) the event-queue drain in practice always gets a turn well within this
script's own 10s sampling grid regardless of population, at least in ordinary play, and the
staleness this whole document has chased across five live passes now has a different, still
unfound cause. Neither disagreement (the sharper, DEFECT-shaped signal item 5 of the prior round's
own plan called out) was ever observed either, in either module — no evidence this pass found of
an actual scan-freshness bug in the lookup/cache pipeline itself.

**Not settled, and worth naming precisely for whoever picks this up next**: a genuinely high-load
module (a large encounter, many dozens of live creatures with active AI, not just registered
corpses/hostiles sitting idle) was not tried — 28-29 is what this session's rig had readily
available on Tatooine, not a stress case chosen to test the budget theory's own upper bound. The
10s sampling grid also cannot distinguish "instant" from "fast enough to land inside the same
10-second window" — a sub-10s delay that this document's own prior passes would have called
"stale" is invisible to this measurement. A tighter interval (the script supports `-IntervalSec`
down to whatever cadence the console channel can sustain) against a genuinely large encounter is
the concrete next step, not a re-run of this same recipe.

### Verdict

The third theory (real-time-budget-gated event drain, load-correlated staleness) is **not
supported by this pass's own data**, but also not ruled out — the population range tested was too
narrow and too low to meaningfully stress the mechanism the disassembly identified. No fix
shipped (none was warranted either way). `dotnet test`: no production code changed this pass (a
live-verification run using the already-built tool only).

## Live-verified (2026-08-24, follow-up round): a systematic module survey finds no reachable
## population meaningfully above ~33, and a tight-interval (3s) probe run at this round's own
## ceiling still finds immediate convergence — the third theory is now weakly disfavoured, and the
## honest conclusion is that this game's engine may simply not host "many dozens" of creatures in
## one loaded area at all

Task (rig access this round, following the prior round's own "what the next live round should do":
find a genuinely large encounter — many dozens of live, actively-fighting creatures — and re-run
the probe there). Read this document's own history in full first, plus
`docs/research/party-reconciliation.md`'s round 89/90 for the second half of this round's work (see
below).

### Surveying for a "many dozens" module: none found

No console verb in this codebase spawns arbitrary creatures (`Odyssey.SpawnCreature` exists but is
only ever called internally, from `MaterialisedEnemies`/`PhantomController` for peer/enemy
stand-ins — there is no generic `spawncreature <template>` cheat), so the only way to find a
higher-population encounter is to survey the game's own placed modules directly. Warped the host
through twelve modules (`sta_m45aa` Star Forge factory, `unk_m44aa` the Rakatan Temple exterior
Elder-Rakata-vs-Sith battle, `manm26aa` Manaan Sith base, `lev_m40aa` the Leviathan, `korr_m38aa`
and `korr_m34aa` Korriban tombs/shyrack cave, `kas_m24aa` and `kas_m23aa` Kashyyyk Shadowlands,
`tar_m02aa` Taris, `danm14aa` Dantooine) and read the `census` (hostile, capped at the ordinary
32-slot combat truncation — `ReadEnemies`'s own cap, so it can never itself report above 32
regardless of true population) and the agent's own `census: gate: array 0x... confirmed X of Y,
dropped Z` line (`Odyssey`/`KotorGameBridge`'s ghost-filtered full-area scan, the SAME uncapped,
non-hostile-inclusive count the 2026-08-22 passes used to measure `tat_m20aa` at 31 — this is the
number that actually matters for `CServerAIMaster::UpdateState`'s own per-call cost, which the
prior round's disassembly found scales with TOTAL registered objects, not just hostiles):

| Module | `census` (capped, hostiles only) | ghost-confirmed total (uncapped) |
| --- | --- | --- |
| `tar_m02aa` | 14 | — |
| `korr_m34aa` | 4 | — |
| `kas_m23aa` | 15 | — |
| `lev_m40aa` | 23 | — |
| `korr_m38aa` | 23 | — |
| `sta_m45aa` | 25 | — |
| `manm26aa` | 29 | — |
| `kas_m24aa` | 29 | — |
| `danm14aa` | 29 | 29 of 46 |
| `unk_m44aa` | 31 (settled, repeated) | 31-33 of 42-50 (17 stale/dropped, constant) |
| `tat_m20aa` (2026-08-22, for comparison) | 29 | 31-32 of 53 |
| `END_M01AA` (2026-08-24 prior round, for comparison) | — | 24-25 |

**No module surveyed — across two separate live rounds now, fourteen modules total — reaches
meaningfully above ~33.** `unk_m44aa` (the Rakatan Temple's Elder-vs-Sith battle: dark Jedi guards,
Sith patrols, combat droids, shield droids, automated turrets, all placed in one loaded area) ties
`tat_m20aa` as the largest found, confirmed by a direct `tools/creatures.ps1` raw scan filtered to
this module's own coordinate plane (z≈0.44, to exclude stale duplicate entries left over from the
module-hopping survey itself — the same "raw hits include stale memory copies" caveat the
2026-08-22 passes already documented): 33 real entries at that plane, including several
non-hostile registered objects (`unk44_turret`/`turret2` — stationary, activate-on-trigger; the
player; `end_trask`) that `census`'s hostile-only view does not count at all but that DO count
toward `CServerAIMaster`'s own per-call object-state scan, per the prior round's own disassembly.

This is itself the honest headline finding of the survey half: **fourteen modules across two
rounds, spanning early-, mid-, and late-game content on five different planets plus two space
stations, all cap out in the same ~14-33 range**, with the top two (`tat_m20aa`, `unk_m44aa`) both
landing within 1-2 creatures of each other despite being entirely unrelated encounters (a Sand
People fixed placement vs. a scripted multi-faction battle). This is consistent with the base
game's own per-module creature budget being a soft ceiling somewhere in the low 30s across the
whole title, not a property of any one encounter's design — "many dozens" (40+) may simply not
exist as a reachable state in unmodified KOTOR1 without either a spawn mechanism this codebase does
not have, or a scripted wave/reinforcement encounter this survey did not happen to land inside at
the right script beat (not ruled out — none of the twelve new modules were played through their own
combat triggers, only walked into and immediately censused).

### The probe, re-run at this round's own ceiling with a 3-second grid instead of 10

Staged `destroyobject 0x58` (a `darkjedim`, confirmed alive via the prior scan) on `unk_m44aa`
(population 29 by the time of this run — two creatures, `0x56` and `0x58`, had already been
destroyed earlier in this same round's survey work, down from the settled 31), then ran
`tools/liveness-freshness-probe.ps1 -Label unk_m44aa-33pop-tight -DurationSec 150 -IntervalSec 3`
— a materially tighter sampling grid than the prior round's 10s, specifically to close the gap that
round's own verdict named: "the 10s sampling grid also cannot distinguish 'instant' from 'fast
enough to land inside the same 10-second window.'"

Result: **immediate convergence again**. Both `census 0x58` (agent) and `resolve 0x58` (plugin)
already read "gone" on the very first sample (`t=0s`), held gone for the entire 150s/50-sample
window at 3s resolution, zero disagreements. `dotnet test` not applicable (a live-verification run,
no production changes from this half — see the next section for this round's one code change).

### What this settles, and what it still cannot

At the largest population this round could find or re-confirm (~29-33, the same ballpark the prior
round already tested, now sampled 3-4x tighter), convergence is not merely "within the same 10s
bucket" — it is instant to the resolution this script can measure at all. Combined with the prior
round's own two data points (24-25 and 28-29), this is now **three independent modules, all in the
same population band, all converging immediately at both a 10s and a 3s grid, zero disagreements
ever observed**. The theory this document has been chasing (`CServerAIMaster::UpdateState`'s own
wall-clock budget starving the event-queue drain under load) requires a population this survey did
not find reachable in this game to actually test at the scale the disassembly's own reasoning
needs — reading (a)/(b) from the prior round's verdict again: this round's evidence now leans
somewhat more toward (b) (the drain gets a turn well within measurement resolution regardless of
population, at least up to ~33) simply because a THIRD independent module at a similar population
band produced the identical result, but it remains genuinely unable to distinguish that from "(a)
is still correct and ~33 objects is nowhere near enough load," because no module exceeding ~33 was
found to test against. Recorded honestly rather than rounded up to a conclusion the data does not
support: **the third theory is now weakly disfavoured by the accumulated weight of three
consistent, tight-grid, zero-disagreement live passes, but not disproven**, and the concrete
blocker for actually settling it has shifted from "find a bigger module" (this round's own survey
suggests one may not exist in this content) to "build a way to synthesize a bigger population" — a
generic `spawncreature` console verb (following `Odyssey.SpawnCreature`'s existing, already-proven
call path — see `MaterialisedEnemies.cs`/`PhantomController.cs` for two live-verified callers) is
the concrete next step for whoever wants to actually stress-test this theory rather than keep
surveying content for a module that this round's evidence suggests probably does not exist.

### Files

No production code changed for this half (a live-verification survey plus a re-run of the
already-built probe). `tools/liveness-freshness-unk_m44aa-33pop-tight-20260824-200732.csv` is this
round's own CSV (untracked, like the two prior rounds' CSVs already in this checkout).

## 2026-09-03 — ship-readiness review: what still keeps `liveness_sync` OFF

Task: settle precisely why `liveness_sync` ships OFF given a full re-read of this document
(every round above), `docs/live-verification.md`, `docs/research/transition-crashes.md` and
`docs/research/party-reconciliation.md`'s own mentions, and the code
(`src/KotorCoop.Plugin/LivenessRule.cs`, `LivenessReconcileController.cs`,
`src/KotorCoop.Plugin/CombatController.cs`, `PluginHost.SessionLifecycle.cs`), then close what
can be closed without the live rig (no rig access this round) and state precisely what cannot.

### Blocker 1 — a real, fixable gap found in this pass: the Destroy dispatch had no per-object
latch — **CLOSED this pass**

Re-reading `LivenessReconcileController.Apply` against `CombatController`'s own `KillOnce`
(which `LivenessAction.Kill` already reuses verbatim, per §5 above) found an asymmetry: the
**Kill** path dedups per owner id (`CombatController`'s `_killed` array — a kill is executed
once and never re-dispatched), but the **Destroy** path called `Odyssey.DestroyObjectById` again
on *every* throttled pass (`IntervalFrames = 40`, ~0.67 s) for as long as the owner kept
re-reporting `Gone` for that id — which, per §4's own `LivenessGoneRetentionMs` (15 s), is up to
~22 redundant calls for a single genuine divergence.

This matters because of what the 2026-08-24 disassembly round (above) already established:
`Odyssey.DestroyObjectById` (the NWScript `DestroyObject` command) is **not synchronous** — it
schedules a delayed removal via `CServerAIMaster::AddEventDeltaTime` rather than unlinking the
object array entry immediately, so a body can keep resolving here for a pass or several after the
first request. Calling it again on the SAME id while that first request is still pending does not
make the removal happen any sooner; disassembly of the command handler (§ "Round (2026-08-24)"
above) shows no dedup or coalescing on the engine's own side, so each redundant call schedules
*another* delayed event on `CServerAIMaster`'s own queue. That queue is exactly the resource the
2026-08-24 "THIRD theory" round spent two full rounds investigating for a real-time-budget-under-
load staleness effect (see Blocker 2 below) — a channel that keeps re-arming its own dependency
under load, every ~0.67 s, for up to 15 s per divergence, was never ruled out as a contributor to
that theory's own load, and is a bug regardless of whether that theory turns out to be real.

**Fixed**: `LivenessRule.Decide` (`src/KotorCoop.Plugin/LivenessRule.cs`) gained a
`destroyAlreadyRequested` parameter (default `false`, so every existing call site and test is
unaffected) — when `hostState == Gone`, `localResolved` is true, and this is true, `Decide`
returns `Nothing` instead of `Destroy`. `LivenessReconcileController`
(`src/KotorCoop.Plugin/LivenessReconcileController.cs`) gained a `_destroyRequested` latch — a
fixed `uint[SharedBlock.LivenessCapacity]` array, the same shape as `CombatController`'s own
`_killed` — set only on a *successful* `DestroyObjectById` call (a transient refusal, e.g.
failure 4 while the VM is mid-call, is NOT remembered, so it still retries next pass — the same
failure-retries symmetry `KillOnce` already has), and consulted before `Decide` is called. A new
`Invalidate()` clears the latch and is now wired into `PluginHost.SessionLifecycle.cs`'s
per-module reset list (`_combat?.Invalidate(); _liveness?.Invalidate();`) — **this was previously
missing entirely**: `LivenessReconcileController` was not in the module-transition reset list at
all, so without adding both the latch AND its invalidation together, a remembered destroy request
could otherwise survive a module change and wrongly suppress a legitimate destroy for an unrelated
creature that happens to reuse the same id number in the new module (the exact hazard
`CombatController.Invalidate`'s own doc comment already names for `_killed`).

Five new pure-rule tests (`tests/KotorCoop.Tests/LivenessRuleTests.cs`) cover the new parameter
directly: no-prior-request still destroys; already-requested suppresses it for both the
still-alive and still-a-corpse Gone sub-cases and for a plot id; already-unresolved is `Nothing`
regardless of the flag. `LivenessReconcileController` and `CombatController` themselves are not
independently unit-tested (they call `Odyssey`'s static P/Invoke surface directly and have no
existing test harness in this codebase — the tested boundary for both is their own pure `*Rule`
class, matching this document's own "same split `ControlReturnRule`/`MirrorHealthRule` already
use" note in §5), so this pass's own test coverage is exactly as deep as `LivenessRule`'s own
prior coverage was.

**This is a real fix, not a guess dressed as one** — it is directly inferable from the 2026-08-24
disassembly's own finding (a real engine call this codebase controls, re-dispatched in a loop, is
categorically different from the "no code fix available" verdict that round reached for the
external-scan question itself). It closes an asymmetry the design brief's own model (§5's decision
table) never actually specified either way — nothing in the original design says the Destroy
action should be retried every pass, and the Kill action right next to it already wasn't. It has
**not** been live-verified: this pass had no rig access, and a timing change to an already-built
dispatch path is exactly the kind of change this project's own rules want live-checked before
trusting, even though the failure mode it fixes (redundant calls, not a wrong call) is low-risk.
See the checklist below.

### Blocker 2 — the `DestroyObject` scan-freshness question: **NOT closed, genuine NEEDS-LIVE /
NEEDS-RE, cannot be resolved from this pass**

The third 2026-08-22 pass's original three-minute-staleness observation was, on the balance of
everything recorded above, most likely a methodology artifact (aggregate `census`-count arithmetic
rather than a direct per-id `census 0x<id>` read — see the 2026-08-24 disassembly round's own
"the real gap" section) rather than a reproducible defect: the object-array lookup algorithm
itself was disassembly-confirmed byte-identical between the plugin's in-process path and the
agent's external path (`CGameObjectArray::GetGameObject` 0x4D8230, no generation counter, no extra
validity flag), and the layout-check theory was disassembly-ruled-out (nothing between a
`DestroyObject` call and the object's own destructor running touches the array entry's identity
fields). Three follow-up live passes, each with direct per-id `census 0x<id>`/`resolve 0x<id>`
paired sampling (the exact diagnostic the earlier pass's own critique said was missing), found
**zero disagreements** across three different modules (24-25, 28-29, and 29-33 registered
objects), at both a 10 s and a tightened 3 s sampling grid — convergence was immediate (`t=0s`) in
every trial.

What remains open is precisely the "THIRD theory" the 2026-08-24 rounds themselves could not rule
out: `CServerAIMaster::UpdateState` (0x4B0B70) gates its own event-queue drain (the loop that
actually lets a `DestroyObject`ed body's destructor run and the array entry unlink) behind a
real-time wall-clock budget shared with a per-call object-state scan and an unconditional
five-bucket "AI level" scan, both of whose cost scales with the module's *total* registered object
count, not just its hostile count. **What is specifically unknown, and cannot be resolved without
either a bigger live rig test or more disassembly**:

- The exact numeric value and unit of that budget. The disassembly identified the structure (two
  `CExoTimers::GetHighResolutionTimer` samples, subtracted and compared) and a default scaling
  input of `0x2710` (10000), but the two 64-bit multiply/divide helpers that turn it into the
  actual compared limit (`0x6FC710`/`0x6FB030`) are unnamed in `tools/kotor1_0_3.db`'s symbol
  table and were not further disassembled — so it is not known whether that budget is ever
  actually reachable by any population this codebase's own creature scan could produce.
- Whether unmodified KOTOR1 content ever hosts a population large enough to test it. A systematic
  survey of fourteen modules across five planets and two space stations (both 2026-08-24 rounds
  combined) found none above ~33 registered objects — every module tested, regardless of era or
  encounter design, capped in the same ~14-33 range. This is itself the honest finding: "many
  dozens" (40+) concurrent creatures may not be a reachable state in this game's own placed
  content at all, which means the live rig alone — without a way to spawn creatures synthetically
  — may never be able to settle this theory definitively, only continue to fail to falsify it.

This is a genuine RE gap, named precisely rather than guessed around: the unnamed function
`0x6FC710`/`0x6FB030` pair's exact semantics, and whether `CServerAIMaster::UpdateState`'s budget
is reachable in vanilla content, are both unknown and cannot be verified without disassembling
those two helpers against `swkotor.exe` (no live rig needed for that half) and/or building a way
to synthesize a much larger population and re-running `tools/liveness-freshness-probe.ps1` against
it (rig needed for that half — no `spawncreature`-style console verb exists yet; the 2026-08-24
round's own closing note already identifies this as the concrete next step for whoever wants to
keep chasing it). Neither was attempted this pass — both are out of scope for "close what is
closeable without the rig," since the population-side half specifically NEEDS the rig and the
disassembly-side half is exploratory RE with no actionable code change identified to make from it.

### Blocker 3 — this project's own stated bar, not a technical blocker

The 2026-08-23 verdict already named this explicitly: *"a flag this document itself has left off
across four live passes for four different reasons is not flipped on the strength of a fifth pass
closing only one of them."* That is a process bar, not a code gap — it matches the precedent this
document itself cites for `effect_sync`/`map_reveal_sync`: a new, previously-unenabled plugin-side
mechanism gets one clean, dedicated live pass with nothing new left to explain before its default
flips, even after every individual technical question closes. Blocker 1's fix is new since the
last live round and has never been observed live at all; per this same bar, it needs its own live
confirmation before contributing to that clean pass, not just a passing unit-test suite.

### Verdict: still OFF, unchanged this pass — one technical blocker closed, one explicitly left
open with precise reasoning, the flag itself untouched

`liveness_sync` remains `0` in `profiles/coop.ini.example` and every existing default. Nothing in
this pass makes the case to flip it — quite the opposite: fixing a real dispatch bug means the
NEXT live pass is measuring genuinely different behaviour than every prior pass measured, which is
itself a reason to re-verify rather than to trust the accumulated history of clean passes at face
value. The two headline questions from every round above are unchanged: the reconcile mechanism's
own Kill/Destroy dispatch is live-proven end to end (2026-08-23, `--enemy-census-cap`), and the
`DestroyObject`-scan-freshness theory is weakly disfavoured by three clean live probes but not
disproven, for a reason (this game's own content ceiling) this pass cannot lift without either a
bigger disassembly session or a new spawn-testing tool. See below for exactly what the next live
session needs to observe to close the loop this pass opened.

### The live checklist to flip `liveness_sync` — console verbs from `docs/CONSOLE.md`

All of this needs the two-instance rig (`liveness_sync=1` on both `coop.ini`s for the session,
per every prior round's own convention — still `0` by default). Console verbs used below:
`status`, `census [id]`, `resolve <id>`, `slay [tag|id]`, `destroyobject <id>` (see
`docs/CONSOLE.md` §"The `slay`/`destroyobject` authority split" for exactly who may run which).

1. **Re-verify Blocker 1's own fix does what it claims, and does not regress anything.** Stage a
   `destroyobject <id>` on the HOST for a creature outside the mirroring client's own current
   view (an ordinary connected pair is enough — `destroyobject` is population-independent, per
   the third 2026-08-22 pass). On the CLIENT's plugin log, confirm **exactly one**
   `liveness: 0x... no longer exists on the owner and still did here - destroying to match` line
   for that id across the whole `LivenessGoneRetentionMs` (15 s) window the owner keeps
   re-reporting it — not the up-to-~22-line storm the un-patched code could have produced. If the
   removal is not instant, watching `resolve <id>` on the client tick from resolving to `0x0`
   confirms the SINGLE dispatch was still enough; a body that never disappears despite one clean
   dispatch is a different, new finding to record, not evidence the latch itself is wrong.
2. **Re-run the out-of-census Kill scenario** (2026-08-23's own recipe: HOST launched with
   `--enemy-census-cap 5` or similar, both instances warped to a populated module such as
   `tat_m20aa`, `slay` a creature outside the host's own shrunk `ReadEnemies` result) to reconfirm
   the Kill half is unaffected by this pass's changes (it should be — the Kill path was not
   touched) and still produces the single `liveness: 0x... is a corpse on the owner and was still
   alive here - killing to match` line it did before.
3. **Re-run `tools/liveness-freshness-probe.ps1`** once more (any of the three previously-tested
   modules is fine — `tat_m20aa`, `unk_m44aa`, `END_M01AA`) to confirm the latch does not change
   the *positive* result the three prior rounds already found: convergence should still read
   `t=0s` on both `census`/`resolve` sides, zero `DISAGREE` samples.
4. **New check specific to this pass's own `Invalidate()` wiring**: stage a `destroyobject <id>`
   on the host, then — before its removal would ordinarily complete — warp both instances to a
   DIFFERENT module where the same numeric id is reused by an unrelated creature (or, if no such
   collision is reachable on demand, at minimum confirm via the agent/plugin logs that
   `LivenessReconcileController.Invalidate()` runs at the transition, the same way
   `CombatController.Invalidate()` already logs at `epoch: ... clearing phantom, item, combat and
   door state`). Confirm a genuinely NEW `Gone` report for the reused id in the new module is not
   silently suppressed by a stale latch entry from the old module.
5. **Reconfirm the never-resurrect guarantee and the plot exemption still hold**, since both are
   cheap smoke tests and every prior round has run them: force a local kill on the mirroring
   client faster than the owner's ledger catches up, confirm `status` shows `liveness anomalies: N`
   climbing and no body un-dies; confirm a known plot id (e.g. `end_trask`) still `resolve`s
   normally after a session of liveness activity.
6. **Optional, not a hard gate**: if a way to synthesize a much larger creature population becomes
   available before this checklist is run (a `spawncreature`-style debug verb, following
   `Odyssey.SpawnCreature`'s existing call path per `MaterialisedEnemies.cs`/
   `PhantomController.cs`), re-run `tools/liveness-freshness-probe.ps1` against it specifically to
   finally stress-test Blocker 2's own load theory at a population this game's own content cannot
   reach. Not required to flip the flag — three clean rounds at the reachable ceiling is already
   the strongest evidence this document has — but it is the one experiment that could turn
   Blocker 2 from "weakly disfavoured" into "closed" outright.
7. `dotnet test tests/KotorCoop.Tests` full green (both before staging the rig session, as a
   baseline, and unchanged after — no production code should need touching to run this checklist).

If items 1-5 all come back clean, the honest position is: every technical blocker this document
has ever named is either closed or has three-plus clean live rounds behind it, and the one
remaining open theory (Blocker 2) is bounded by this game's own content, not by anything this
codebase controls. That is the point at which flipping `liveness_sync`'s own default stops being
premature — but that call belongs to whoever runs this checklist, against this project's own
"flip on if clean" bar, not to this document written without rig access.

### Files touched this pass

- `src/KotorCoop.Plugin/LivenessRule.cs` — `destroyAlreadyRequested` parameter on `Decide`
  (default `false`), with an updated doc comment.
- `src/KotorCoop.Plugin/LivenessReconcileController.cs` — `_destroyRequested` latch, `Invalidate()`,
  wired into the `Apply`/`Destroy` path.
- `src/KotorCoop.Plugin/PluginHost.SessionLifecycle.cs` — `_liveness?.Invalidate()` added to the
  per-module `InvalidateAll` reset list, next to `_combat?.Invalidate()`.
- `tests/KotorCoop.Tests/LivenessRuleTests.cs` — five new cases for the latch parameter.
- `docs/limitations.md`, `docs/research/creature-liveness.md` (this section) — status text refreshed.

Build: `src/KotorCoop.Plugin/build.ps1 -WarnAsError` clean. `dotnet test tests/KotorCoop.Tests -c
Debug --filter "FullyQualifiedName~Liveness"`: 52/52 passed. Full suite run alongside this pass —
see the commit for the result.

## 2026-09-03 — `spawncreature` debug verb shipped: checklist item 6 is now runnable

The `spawncreature <template> [count] [x y [z]]` (alias `spawn`) console verb this document's own
checklist item 6 asked for now exists, following `Odyssey.SpawnCreature`'s existing call path
exactly as proposed above — the same CreateObject route `MaterialisedEnemies.cs`/
`PhantomController.cs` already use for their own stand-ins, just reachable from a console line.
See `docs/CONSOLE.md`'s own row and `SpawnCreatureVerbRule`/`CheatController.SpawnCreature.cs` for
the implementation; `SpawnCreatureVerbRule` and `SpawnCreatureGridRule` are pure and unit-tested
(`tests/KotorCoop.Tests/SpawnCreatureVerbRuleTests.cs`, `SpawnCreatureGridRuleTests.cs`).

It spawns up to 32 ordinary hostile creatures from a .utc template resref, spread in a small grid
next to the local player by default (or anchored on an explicit position), with faction and every
other property left entirely to the template — nothing about the spawned population is
special-cased. **Local only, always**: on a CLIENT the spawned bodies are ordinary local objects
this instance's own engine created, not registered with any peer-sync channel, so a peer never
learns they exist — the intended use is on the HOST, where a spawned creature is population like
any other the host's own module already owns.

**Status: NEEDS-LIVE at the time of writing; verified live 2026-09-03 — see this document's own "2026-09-03, later" addendum below for the confirming facts.** This pass (as originally written) did not run the rig — no live confirmation that a 40+ creature
`spawncreature` population actually stresses `tools/liveness-freshness-probe.ps1` any differently
from the ~33-creature natural ceiling the 2026-08-24 rounds already surveyed cleanly. Checklist
item 6 (§7, "The live checklist to flip `liveness_sync`") is now genuinely runnable rather than
blocked on "no way to synthesize a bigger population" — whoever next has rig access can finally
run it as originally proposed.

Also usable for staging a fight anywhere a Vulkar or similar is needed for a combat round (e.g.
`docs/research/peer-damage-ledger.md` §9.5) without depending on what a given module happens to
contain.

### Files touched this pass

- `src/KotorCoop.Session/SpawnCreatureVerbRule.cs` — pure parse of the console grammar
  (template/count/position).
- `src/KotorCoop.Session/ConsoleCommand.cs`, `ConsoleCommand.Debug.cs` — the `SpawnCreature` verb
  kind, its `VerbSpecs` row, and `ParseSpawnCreature`.
- `src/KotorCoop.Agent/CoopCommandDispatcher.cs` — forwards the parsed request as a
  `CheatKinds.SpawnCreature` cheat.
- `src/KotorCoop.Memory/IGameBridge.cs` — `CheatKinds.SpawnCreature = 68`.
- `src/KotorCoop.Plugin/SharedBlock.cs` — `CheatKindSpawnCreature = 68`.
- `src/KotorCoop.Plugin/CheatController.cs`, `CheatController.SpawnCreature.cs` — the engine-side
  handler: reads the template/count/position off the cheat channel, spawns through
  `Odyssey.SpawnCreature` in a loop, reports `spawned N/count <template>: <ids>` (full ledger to
  the plugin log, a bounded summary on the 64-byte answer channel).
- `src/KotorCoop.Plugin/SpawnCreatureGridRule.cs` — the pure per-creature spread arithmetic.
- `tests/KotorCoop.Tests/SpawnCreatureVerbRuleTests.cs`,
  `tests/KotorCoop.Tests/SpawnCreatureGridRuleTests.cs` — 27 + 6 new unit tests, all green.
- `docs/CONSOLE.md` — new `spawncreature` row.

Build: `src/KotorCoop.Plugin/build.ps1 -WarnAsError` clean. `dotnet test tests/KotorCoop.Tests -c
Debug --filter "FullyQualifiedName~SpawnCreature"`: 27/27 passed. No rig access this pass —
everything above the plugin/test build is NEEDS-LIVE.

## 2026-09-03, later — `spawncreature` live-verified: checklist item 6 confirmed runnable, and a real refusal case found

`spawncreature g_vulkar001 3` — the example resref this document's own row and `docs/CONSOLE.md`'s
row used — refused live with failure 9 (template did not resolve): the resref has to be a real
creature template actually present in the loaded module or override, not any plausible-looking
`.utc` name. `spawncreature tar10_vulkpoo002 3` succeeded: `spawned 3/3 tar10_vulkpoo002: 0x13F
0x142 0x145`, then 20 more the same way, then (the final round, see "Item 6" in the checklist
addendum below) `spawned 20/20 tar10_vulkpoo002: ...`. Host log form for a larger batch:
`spawncreature: 12/12 'tar10_vulkpoo002' spawned near the player - ids: 0x140 0x143 0x146 0x149
0x14C 0x14F 0x152 0x155 0x158 0x15B 0x15E 0x161`.

Spawned creatures reach the CLIENT as materialised stand-ins exactly like any other host-owned
enemy — `materialise: owner 0x182 -> local 0x139 (appearance 264, renders no twin yet, faction
1)` — loot flows off them (`gave loot 'g_w_vbroshort01' to stand-in 0x139`), and they DO attack
the host PC for real (`combat ground-truth: local PC took 4 damage, attacker=0x140 (unrecognized
attacker id (not driven, not a phantom, not our own apply))`).

Also see `docs/CONSOLE.md`'s `spawncreature` row for the failure-9 caveat now noted there.

## 2026-09-03, 00:30–04:15 — the live checklist run: items 1, 2, 3, 4, 6 confirmed; `liveness_sync` flipped ON by default (`2bbf8de`)

Rig: `pwsh tools/demo.ps1 -Save 1`, host `C:\GOG Games\Star Wars KOTOR (GOG)`, client
`D:\Games\swkotor-gog-p2`, block version 66, `liveness_sync=1` on both halves for the session (the
template default has SINCE flipped to `1` as well — `2bbf8de`, "the 2026-09-03 live checklist ...
cleared the bar the template set for it" — see `profiles/coop.ini.example`, `README.md`,
`docs/CONFIG.md`). Item numbers below match "The live checklist to flip `liveness_sync`" above.

### Item 1 — destroy-to-match

Host `destroyobject 0x1F` → client, exactly one line: `liveness: 0x1F no longer exists on the
owner and still did here - destroying to match (local 0x1F)`; client `resolve 0x1F` read `0x0`
afterwards.

### Item 2 — out-of-census kill

Host agent launched with `--enemy-census-cap 5` — worth recording for anyone repeating this: `&
tools/demo.ps1 -Save 1 -Agent1ExtraArgs @('--enemy-census-cap','5')` run in-process from
PowerShell; calling `pwsh tools/demo.ps1` with the array arg breaks binding (`Could not find a
part of the path '...\5\coop.ini'`). Host `slay 0x3B` → client, one line: `liveness: 0x3B is a
corpse on the owner and was still alive here - killing to match`.

### Item 3 — freshness probe

`tools/liveness-freshness-probe.ps1 -Stage destroyobject` on `0x1C`, `tar_m10aa` with a
26-creature population: both sides gone at t=0, zero disagreements over the run.

### Item 4 — epoch

Warp `tar_m10aa` → `tar_m03aa` → `epoch: world unloading; clearing phantom, item, combat and
door state`. Object ids ARE reused across module loads in one process — `0x2` was a 22/22 Vulkar
on `tar_m10aa`, then a 20/20 plot creature on `tar_m03aa` — so `Invalidate` genuinely matters
here. Plot creatures (`0xF`, `0x1A`, `0x2` on the new module) resolve on the client and are
untouched.

### Item 6 — large population (49 creatures after spawning)

Host `census: gate: array ... confirmed 52 of 70` and `OWNER 32 id(s)` — the enemy census is
capped at 32 slots (`SharedBlock`; the liveness region's own capacity is 96). This is not a bare
truncation: `KotorGameBridge.Enemies.cs` sorts candidates fighting-first, then by squared distance
to the NEAREST player (any peer in the module, not just the publisher), and only then takes the
first 32 — so what fell off the list at 52 candidates was the far, idle creatures; every creature
actually fighting or standing near either player was published. `liveness_sync` is exactly what
covers a death among the shed ones. The round that reached this population ended in the
floaty-text crash documented elsewhere in this project (now fixed — see `effect-sync.md` §11.5,
`transition-crashes.md` 2026-09-03).

### Item 5 — untouched this round

Not exercised this pass.

### Log budget fix (99c6ffe, `LivenessReconcileController.cs`)

The shared 20-line log cap had been spent entirely on the anomaly line (counter reaching 148)
before the first genuine destroy ran, making the destroy/kill lines above invisible until the
fix — one budget per outcome (kills, destroys, anomalies, 20 lines each) now. The anomaly counter
itself keeps climbing regardless (60 in one client run; a line roughly every 130 ms for a creature
the client already reads dead before the owner's own snapshot agrees) — harmless, and never
resurrects anything.

**Verdict.** Every item this rig could reach came back clean; `liveness_sync` shipped ON by
default the same day (`2bbf8de`) — see `docs/limitations.md`'s combat table.
