# Shared inventory v2: host-authoritative absolute state (design + build, 2026-08-29)

`party_pool_sync` and `store_sync` (mode 1, the bidirectional op-log design documented in
`docs/research/party-pool-sync.md` and `docs/research/store-sync.md`) are both disqualified from
shipping ON by default — not by one bug, but by **seven independently-found, independently-fixed
root causes** in the party pool alone, followed by a live rig round (party-pool-sync.md §27) that
still amplified three real deposits into 500+ phantom units with a cross-instance divergence on
top, using a build that had every one of those seven fixes. Each fix closed the specific mechanism
it targeted; none of them closed the *class* of bug, because the class was never one bug — it was
the shape of the design. This document describes why that shape is unsound in general, the
replacement design (mode 2), and what was built.

Tag legend, matching every other doc in this directory: **VERIFIED** = read out of the codebase or
asserted by a test. **NEEDS-LIVE** = reasoned but never watched happen on a running game.
**INFERRED** = a reasonable deduction, not directly observed. Nothing in this pass touched a live
process — this is a design-and-implementation pass, loopback-tested only.

---

## 1. Why the op-log design cannot be patched into safety

### 1.1 The seven root causes, in one place

All seven live in `docs/research/party-pool-sync.md`; restated here because the pattern across
them is the point, not any individual mechanism:

1. **Ordering bug** (§7): a reconcile apply landing on a receiving instance was, on the very tick
   it landed, misdetected as a *fresh* local action and republished — a straightforward
   detect-vs-apply ordering mistake.
2. **The depositing-side duplicate** (§8-§10): a `pooladd`-driven (cheat-channel) local change,
   unlike a GUI drag, lands asynchronously on a frame the pump's own tick does not control; a
   race between the pump's own step-1 scan and step-2's belief update let the same deposit be
   seen as new twice.
3. **Over-exclusion** (§11-§12, "root cause 3"): the original fix for #2 gave a detected local
   change a grace window that *excluded* the key from further detection while "settling" — a
   second genuine deposit inside that window was silently swallowed. Fixed by replacing exclusion
   with an **excess-beyond-wire** computation: `excess = movement not explained by wire already
   owing this key a change`, publish only the excess, never exclude a key from detection at all.
4. **Unbounded ping-pong** (§21-§22, "root cause 4"): the fix for #3 still compared a scan against
   a *frozen snapshot* of "what the wire wanted," captured once at the moment an apply was queued.
   The queued apply's own trip through the arbitrated cheat channel is not instantaneous; the wire
   can move again for the same key while it is in flight, and the frozen snapshot has no way to
   know that. Every round trip amplified — three deposits observed inflating through 1, 2, 4, 12,
   40, 180, 615, 1068, 906 units before settling. Fixed with a **hard loop breaker**: freeze a key
   that publishes more than 8 ops in 10s and force a resync, independent of whether the
   surrounding reasoning is airtight.
5. **An unguarded cross-thread queue** (§24, "root cause 5"): the apply queue between the pump
   thread and the cheat-dispatch thread was a bare `Queue<T>`, undefined behaviour under
   concurrent access by the BCL's own contract.
6. **A permanent-stall exact-match bug** (§24, "root cause 6"): a receiving instance's in-flight
   marker cleared only on an *exact* match against a frozen target: a second wire-side change
   arriving before the first apply landed meant the exact match could never fire again, stalling
   the key forever with no error.
7. **The single-slot cheat channel silently clobbering an in-flight request** (§26, "root cause
   7"): a human console verb typed while an automatic reconcile apply was still awaiting its
   answer overwrote the shared block's request fields before the plugin ever read them — the
   queued apply's own engine-side delta simply never ran.

Every fix above is individually sound and stayed in the codebase (mode 1 keeps them). But §27 ran
the exact recipe root cause 7's own fix was built to close — three rapid `pooladd`s on the host —
against a build carrying all seven fixes, and it **still** amplified past 500 units on each side
with a 15-unit cross-instance divergence, attributed to ambient in-module item-spawn scripts
colliding with the wire channel's own response to that noise. Nobody has re-pinned the exact eighth
mechanism. That is itself the argument for this document: a design where "detect a local change,
publish it, and reconcile toward what a MERGED cross-instance total says you should have" runs
symmetrically on every peer has a feedback path built into its shape, and closing one door onto
that path does not prove there is no other door.

### 1.2 The two structural properties that make it unsound

- **Both sides publish, and what they publish influences what the other side compares against.**
  A's publish changes the merged wire total; B's next detect compares its own engine against that
  now-different wire total, decides something changed, and may publish back. There is no
  topological reason this has to terminate quickly — every fix above narrowed *when* the cycle
  could start, not *whether* a cycle can exist.
- **Ambient content is genuinely, independently duplicated per instance.** `end_m01aa`'s own
  heartbeat/plot scripts deposit into the party pool identically on both instances
  (party-pool-sync.md §19-§20). A design that treats "the engine moved" as evidence of a fact to
  reconcile toward a shared total has no way to tell that apart from a real player action without
  a suppression heuristic — and §20's `IsAmbientPartyPoolNoise` heuristic (a 1-second
  creation-to-pool timing window, decided once per item id) is exactly the kind of guess that a
  colliding real player action can defeat, which is what §27 found.

### 1.3 What "host-authoritative absolute state" removes

If only ONE party ever **publishes state** (grows or shrinks the canonical total) and every other
party only ever **converges toward what it published**, the feedback path in 1.2 does not exist:
a receiver's own convergence can never itself become a new fact for the publisher to react to,
because the publisher never reads the receiver's engine at all. This is not a new idea in this
codebase — it is exactly `docs/research/progression-sync.md`'s gold ledger
(`PartyPump.PumpProgression`/`PumpProgressionHost`, VERIFIED, live-confirmed 2026-08-27) and
`docs/research/creature-liveness.md`'s full-census broadcast
(`CreatureLivenessPaging`/`CoopHost.Liveness.cs`, live-verified). Both already ship this shape for
their own channel; this document generalises it to the shared party pool and merchant stores.

---

## 2. Design

### 2.1 Roles

- **The host's engine is the single source of truth**, for the party pool and for every store the
  host (or any peer) has ever named. There is no merged cross-instance store to publish into or
  reconcile against — there is exactly one canonical dictionary, and it is a direct read of the
  host's own game.
- **The host publishes an absolute snapshot** — a full `(key -> quantity)` dictionary — as a LEVEL,
  the same shape `PartySnapshot`/`EnemySnapshot`/`CreatureLiveness` already use: recomputed and
  re-sent every cycle regardless of whether anything changed (so a dropped datagram self-heals on
  the next cycle, needing no ack, no retransmit-until-ack machinery at all), paged when the
  content exceeds one datagram (`CreatureLivenessPaging`'s own `Generation`/`Digest`/
  `PageIndex`/`PageCount` shape, reused unchanged in spirit).
- **A client never publishes state.** It publishes **intents**:
  "I deposited/withdrew/bought/sold `Key` × `n`", tagged with a client-side monotonic serial.
  A client's own local engine is never treated as a second copy of the truth — only as a source of
  candidate intents to report, and as a thing to be pulled into line with the host's snapshot.
- **The host applies an accepted intent to ITS OWN engine** via the exact same cheat/apply
  primitives mode 1 already uses (`Odyssey.AddItemToPartyPool`/`RemovePartyPoolStack`,
  `Odyssey.Store.cs`'s store-repository equivalents) — no new plugin code. The next snapshot cycle
  reflects the result automatically, because the snapshot is just a fresh read of the host's own
  engine.
- **A client's engine is reconciled toward the host's snapshot** by delta-apply — the SAME
  cheat-channel primitives, run locally, converging the client's own repository to match. This is
  a pure one-way pull: it never publishes anything, so it cannot feed the cycle in 1.2.

### 2.2 The in-flight adjustment — why a client's own deposit does not visibly roll back

The host's snapshot lags a client's own intent by however long the intent takes to reach the host,
get applied through the arbitrated single-slot cheat channel, and show up in the host's own next
scan — up to a few pump cycles. If the client reconciled straight against the raw snapshot during
that gap, it would see "my engine has +N of key K that the snapshot doesn't show yet" and issue a
Remove to converge — visibly deleting the item the player just placed, then re-adding it once the
snapshot caught up.

Each client keeps a small in-flight ledger, one integer per key: the sum of every intent it has
reported for that key whose effect has not yet been observed in the snapshot. The reconcile
target for a key is `snapshot[key] + inFlight[key]`, never the raw snapshot alone. The in-flight
entry for a key is retired — by however much, not necessarily all at once — the moment a NEW
snapshot's own value for that key has moved in the same direction since the previous snapshot; an
exact match is not required (this is root cause 6's own fix, reused: never gate a resolution
behind an exact match against a value that can itself still be moving). This is the identical
"excess/retire" arithmetic `PartyPoolPump`'s step 1 already uses (`ExcessBeyondWire`), just
re-pointed at snapshot-vs-previous-snapshot movement instead of engine-vs-wire movement — proven
machinery, reused, not reinvented.

### 2.3 Detecting a client's own local change — unavoidably a scan diff, same as mode 1

There is no GUI-drag hook in this codebase (mode 1 never had one either); the only observation
mechanism available is periodic bridge scanning (`IPartyPoolBridge.TryScanPartyPoolTotals`,
`IStoreBridge.TryScanOpenStore`), unchanged from mode 1. A client's pump compares a fresh engine
scan against its own last-scanned belief, key by key. Movement that is fully explained by a
reconcile-apply this pump itself just queued (and has not yet observed land) is not reported —
mirrors `_applyInFlight`'s exclusion in mode 1, cleared unconditionally on the next observed
movement for that key rather than requiring an exact match (root cause 6, reused). Anything left
over is a genuine new local delta and is reported as a fresh intent.

### 2.4 Ambient-script noise needs no suppression — and here is exactly why

Mode 1 built a heuristic (`IsAmbientPartyPoolNoise`) to try to keep ambient script noise off the
wire, because on that design a false negative could feed the amplification loop. Mode 2 has no
such loop for it to feed:

- **On the host**, ambient noise is simply part of the host's own engine state — the very next
  scan already includes it, no wire hop, no detection, no decision to make. It is not
  "suppressed"; it never needed handling at all.
- **On a client**, a step-1 detection cannot (and does not try to) tell an ambient deposit from a
  genuine player one — exactly like mode 1's own detector. But because a client's report is an
  intent applied ONCE (deduplicated by client-side serial, mirroring the existing
  `PartyPoolStore.ApplyOp`/`ContainerLootStore.ApplyOp` bounded-history dedup) rather than a fact
  that gets independently re-detected and re-published by a symmetric peer, the worst case is a
  single, bounded double-count — the SAME ambient event genuinely happened on both instances'
  engines independently, and both copies end up counted once each. That is a real but bounded,
  one-time economic quirk, not a runaway: nothing about applying it can trigger a second
  application, because nothing on the client's side ever watches its own reconcile-apply and
  reports it as a NEW local action (the in-flight/reconcile-apply exclusion in §2.3 exists
  specifically to prevent that). Any residual mismatch between a client's local pool and the
  host's eventual truth — from ambient noise, from a race, from a dropped packet, from anything —
  is simply overwritten by the next reconcile pass. No suppression heuristic is required for
  correctness; mode 1's own heuristic is left in place at the bridge layer (`KotorGameBridge.
  PartyPool.cs`'s `IsAmbientPartyPoolNoise`) as an orthogonal, unrelated optimisation that mode 2
  does not depend on.

### 2.5 Amplification is structurally impossible

Only the client publishes (a bounded, per-serial-deduplicated stream of intents, each applied
exactly once by the host); the host and every other party only ever converge toward a value the
client's own intents cannot influence except through the host's single, serialised apply. No
message a receiver processes can itself become a new fact a sender reacts to, because sending and
receiving are not symmetric roles any more — there is no cycle to amplify around. The two
remaining "went briefly wrong" cases this design still allows are both bounded and self-healing on
the very next cycle: (a) a client and host independently produce the same ambient content (§2.4),
and (b) the host's belief for a key it has neither scanned nor received an intent for (a store
nobody has told it about yet) reads as zero until it learns otherwise (§2.7).

### 2.6 Infinite-stock store items

Excluded from the snapshot entirely, unchanged from mode 1 (`docs/research/store-sync.md` §1.5/
§2.1): the engine's own `SellItem`/`AcquireItem` never move an Infinite-flagged entry's stock in
either direction, so there is never a delta to publish or reconcile for one.
`IStoreBridge.TryScanOpenStore` already filters these out before mode 1 or mode 2 ever sees them —
no new filtering code needed.

### 2.7 Stores: object id + module scoping, cold-start limitation carried forward honestly

A store is a real `CGameObject` (`docs/research/store-sync.md` §1.2), so `StoreStackKey` already
carries the store's object id; the host's canonical belief is a persistent
`Dictionary<StoreStackKey, int>`, accumulated across however many stores anyone has opened over
the session (mirroring mode 1's `StorePump._engineBelief` exactly), cleared on a module change
(object ids are only meaningful within the module that minted them — the same fix
`docs/research/store-sync.md`'s "Part 2" round already made for mode 1's own pump). Applying a
client-reported buy/sell needs no GUI open on the host (`GetStoreByGameObjectID` resolves by bare
id at any time, §2.4 of the store doc) — only READING a store's stock needs the GUI open, matching
mode 1 exactly.

**Honest limitation, carried forward rather than solved**: if the host has never itself opened a
store and receives a client's intent naming it, the host applies the raw delta against whatever it
currently believes that key is — zero, if this is the first time it has heard of the key — so a
buy against a store's real (nonzero) pristine stock can floor at zero on the host's belief until
the host itself opens that store for a genuine scan. This is the identical shape as mode 1's own
documented open risk (`docs/research/store-sync.md` §5 item 1: "once EITHER instance has opened it
once, it stays known... for the rest of the session"); mode 2 does not add a baseline-hint
mechanism to close it (mode 1's `PublishStoreOp`'s `localBaselineIfUnknown` parameter is
host-local bookkeeping, not a wire field, and porting it was judged not worth the added protocol
surface for a gap that self-heals the moment anyone opens the store).

### 2.8 Reuse — nothing new below the session layer

- **Bridges**: `IPartyPoolBridge.TryScanPartyPoolTotals`/`InvalidatePartyPoolScanCache`,
  `IStoreBridge.TryScanOpenStore` — unchanged, shared verbatim with mode 1.
- **Cheat/apply primitives**: `CoopAgent.PendingPartyPoolApply`/`PendingStoreApply`,
  `TryDequeuePendingPartyPoolApply`/`TryDequeuePendingStoreApply`,
  `CoopCommandDispatcher.DrainAutomaticPartyPool`/`DrainAutomaticStore` — unchanged. Mode 2's pumps
  enqueue into the exact same queues mode 1's pumps do; the dispatcher code needed zero changes.
  No new `SharedBlock`/`CheatKinds` entries, no plugin changes at all.
- **Scan cache invalidation**: `InvalidatePartyPoolScanCache` is still called the moment a mode-2
  pump queues a reconcile apply, for the identical reason mode 1 calls it — a second rapid local
  change must not be masked behind the bridge's throttle window.
- **Initial full snapshot on admission**: `RequestPartyPoolResync`/`RequestStoreResync`'s existing
  call sites (`HandleHello`'s freshly-admitted-peer branch) are reused to force the very next
  snapshot broadcast out immediately rather than waiting out the ordinary cycle interval — the
  same `_forceLivenessBroadcast` idiom `CreatureLiveness` already uses, applied here instead of
  mode 1's `ResyncPartyPool`/`ResyncStore` reliable re-offer (mode 2 has no reliable ops to
  re-offer; the very next snapshot cycle already is a full one).

### 2.9 Wire shape

No new packet SHAPES beyond one snapshot type per channel — the intent and its ack reuse the
EXISTING `PartyPoolOpPacket`/`PartyPoolAckPacket`/`StoreOpPacket`/`StoreAckPacket` records verbatim
(an intent is structurally identical to a mode-1 op: origin peer, sequence, Add/Remove, key,
count — only `Set` is never used, and only the wire ROUTING differs: client -> host only, never
broadcast/relayed), under new `PacketType` values so mode 1 and mode 2 traffic can never be
misread as each other. `ReliablePartyPoolSender`/`ReliableStoreSender` are reused unchanged as the
client's own intent retry queue (retransmit-until-ack, cap, backoff) — sent to the host only,
never broadcast.

New packet types (≥60 per this pass's own instructions; `ProtocolConstants.Version` is
deliberately NOT bumped here — `TODO(version)`, bumped on merge):

| Type | Value | Direction | Shape |
| --- | --- | --- | --- |
| `PartyPoolSnapshot` | 60 | host -> all | new `PartyPoolSnapshotPacket`, paged |
| `PartyPoolIntent` | 61 | client -> host | reuses `PartyPoolOpPacket` |
| `PartyPoolIntentAck` | 62 | host -> client | reuses `PartyPoolAckPacket` |
| `StoreSnapshot` | 63 | host -> all | new `StoreSnapshotPacket`, paged |
| `StoreIntent` | 64 | client -> host | reuses `StoreOpPacket` |
| `StoreIntentAck` | 65 | host -> client | reuses `StoreAckPacket` |

### 2.10 Config

`party_pool_sync`/`store_sync` become small integers rather than booleans: `0` = off (default,
unchanged), `1` = mode 1 (the existing bidirectional op-log, byte-for-byte unchanged, still off by
default), `2` = mode 2 (this document). Parsed with the same `int.TryParse` + `Math.Clamp(0, 2)`
idiom `tick_rate`/`interp_ms` already use in `CoopConfig.cs`, so `party_pool_sync = 1` (or any
existing coop.ini) keeps meaning exactly what it always meant.

---

## 3. What was built

**Protocol** (`KotorCoop.Protocol`): `PacketType.cs` gains the six values in §2.9's table.
`Packets/PartyPoolPackets.cs`/`Packets/StorePackets.cs` gain `PartyPoolSnapshotEntry`/
`PartyPoolSnapshotPacket` and `StoreSnapshotEntry`/`StoreSnapshotPacket` — paged, `Generation`+
`Digest`+`PageIndex`+`PageCount`, the exact shape `CreatureLivenessPacket` already uses.
`PacketCodec.PartyPool.cs`/`PacketCodec.Store.cs` gain the snapshot Write/Read pair plus
`WritePartyPoolIntent`/`ReadPartyPoolIntent`/`WritePartyPoolIntentAck`/`ReadPartyPoolIntentAck`
(and the Store equivalents) — same field layout as the existing Op/Ack codecs, different header
tag only. `ProtocolConstants.cs` gains `MaxPartyPoolSnapshotEntriesPerPage`/
`MaxStoreSnapshotEntriesPerPage` and a `TODO(version)` comment; `Version` itself untouched.

**Session** (`KotorCoop.Session`): `IntentTracker.cs` (generic over the stack-key type, shared by
both channels) — the in-flight ledger from §2.2, plus a bounded per-peer applied-serial dedup set
for the host side (§2.4). `PartyPoolSnapshotPaging.cs`/`StoreSnapshotPaging.cs` (mirrors
`CreatureLivenessPaging.cs`). `CoopHost.PartyPoolV2.cs`/`CoopClient.PartyPoolV2.cs`,
`CoopHost.StoreV2.cs`/`CoopClient.StoreV2.cs` — publish/broadcast/receive/apply/ack, wired into
each class's handler dictionary and `Tick`'s broadcast loop; `CoopHost.Connection.cs`'s
freshly-admitted-peer branch forces the next snapshot cycle immediately (§2.8). `IPartyPoolSession`/
`IStoreSession` gain the v2 members. `Pumps/PartyPoolPumpV2.cs`/`Pumps/StorePumpV2.cs` — host-side
(trivial: scan -> publish, apply drained intents) and client-side (detect -> intent, snapshot ->
reconcile) logic, entirely separate from the mode-1 pumps, which are untouched.
`AgentSettings.PartyPoolSyncMode`/`StoreSyncMode` (int, 0-2) replace the bool properties;
`CoopConfig.cs` parses the same way. `CoopAgent.cs` constructs both pump generations and routes
`TryDequeuePendingPartyPoolApply`/`TryDequeuePendingStoreApply` to whichever mode is active — the
dispatcher and every plugin-side type are untouched.

**Tests** (`KotorCoop.Tests`): `PartyPoolV2LoopbackTests.cs`/`StoreV2LoopbackTests.cs` — real host,
real client, real sockets, real `CoopAgent` pump loop on each side, modelling both pumps plus the
single-slot arbitrated cheat channel plus ambient-script noise on both sides plus blackout/
reconnect, matching this pass's own task brief:

- ×3 rapid deposits/sells on the client converge to exactly +3, no amplification, bounded op count.
- ×3 rapid deposits/sells on the host converge to exactly +3 (trivial for the host — no detect
  step at all — kept as a regression pin against ever reintroducing one).
- Ambient-script noise on BOTH sides simultaneously with genuine player actions interleaved:
  bounded, deterministic, non-amplifying totals (the exact §27 recipe, run against mode 2).
- Deposit then withdraw (party pool) / sell then buy-back then re-sell (store): converges to the
  correct net total on both sides.
- Deplete-to-zero: a key that nets to exactly zero drops off the snapshot and both sides agree.
- Blackout + reconnect mid-intent: the in-flight ledger and the reliable intent sender both
  recover without double-applying (the exact bug class `docs/research/store-sync.md`'s own
  "blackout+reconnect resync double-applies a pending sell" finding described for mode 1).

Codec round-trip/malformed-rejection cases for all six new wire shapes added to
`PacketCodecTests.cs`; a `PartyPoolSnapshotFitsInOneDatagram`-style budget test pins
`MaxPartyPoolSnapshotEntriesPerPage`/`MaxStoreSnapshotEntriesPerPage` against
`ProtocolConstants.MaxPacketSize`, mirroring `FullPeerSnapshotFitsInOneDatagram`.
`PacketHandlerRegistryTests.cs`'s handler-count pins updated for the six new registrations on each
side.

---

## 4. NEEDS-LIVE

Nothing in this pass touched a live process — bench/loopback only, matching the discipline every
prior no-rig round in this family has followed. `party_pool_sync`/`store_sync` stay OFF by default
(mode 0); mode 2 exists as an opt-in for whoever picks up the next live round. Before flipping mode
2 on by default, the next live pass should re-run this document's own §3 recipe list against a real
two-instance rig, watching specifically for: whether the in-flight/reconcile-apply exclusion
(§2.3/§2.2) genuinely prevents a visible rollback-then-reappear flicker on a real GUI-dragged
deposit (not just the `pooladd`/`scriptgive` dev-verb path this pass's loopback tests can drive);
whether the snapshot broadcast cadence feels responsive enough for a player watching the stash/store
panel live; and whether §2.7's cold-start store-baseline limitation is player-visible often enough
in practice to be worth a follow-up fix.

---

## 5. 2026-08-29, verification round — four real bugs found and fixed via loopback testing; all
## 2,506 tests pass, `party_pool_sync=2`/`store_sync=2` stay OFF by default, NEEDS-LIVE

This pass ran `SharedInventoryV2LoopbackTests.cs` (the actual shipped file name — §3's own
`PartyPoolV2LoopbackTests.cs`/`StoreV2LoopbackTests.cs` were the planning names, not what landed;
similarly §3's `IntentTracker.cs` and per-snapshot `Digest` field were both dropped during the
build — the shipped `PartyPoolSnapshotPacketV2`/`StoreSnapshotPacketV2` carry no digest at all,
deliberately: §2.7 already makes a stale/incomplete page reassembly harmless, since the very next
publish cycle is always a full, current resend regardless, so a cross-page content fingerprint
buys nothing a full resend does not already fix on its own) against the real build for the first
time and found four genuine, reproducible defects — none of them the amplification class §1
describes (nothing here ever exceeded a small, bounded overshoot, and every one converged on its
own once identified), but each a real correctness gap in the client-side reconcile logic
(`Pumps/PartyPoolPumpV2.cs`/`Pumps/StorePumpV2.cs`) that would have shipped silently wrong.

**Bug 1 — a test-only race, not a production defect.** The first loopback pass asserted
`StaysAsync` immediately after the WIRE-level snapshot (`TryGetPartyPoolSnapshotV2`) showed the
expected total, without waiting for the CLIENT's own engine to actually reconcile up to it first —
the two are not the same claim, and the engine's own catch-up (through the simulated cheat channel)
can lag the wire by tens to hundreds of milliseconds. Fixed in the tests themselves: every
`StaysAsync` is now preceded by a `WaitForAsync` on the engine-level value it is about to assert
stays put.

**Bug 2 — a real double-count: `target = snapshotValue + OutstandingNet(key)` is only ever valid
for DETECTION, never for what gets APPLIED.** Reproduced live in `ThreeRapidClientDepositsConvergeToExactlySumOnBothSides`:
a client's own +3 deposit, once mirrored back in the host's next snapshot (which already reflects
it), reconciled the client's own engine UP to +6 — the snapshot said 3, and the still-outstanding
+3 (kept alive by `AckGraceMs`'s own deliberate grace window, see §2.2) got added on top a SECOND
time. Root cause: a client's own outstanding intents are facts it ALREADY applied to its own
engine the moment it detected and published them — reconciling toward `snapshot + outstanding`
double-applies that same contribution once the snapshot independently catches up to it.
**Fix**: split the two uses apart explicitly. `detectTarget` (`snapshotValue + OutstandingNet`)
remains the only thing the DETECT step compares `actual` against, to avoid re-reporting an
already-in-flight deposit as a new one. Every RECONCILE-APPLY now targets a new
`ReconcileTarget(key, actual, snapshotValue)` instead: if the engine's own value already sits at
or past the raw snapshot in the outstanding intents' own direction, they are retired outright
(genuine agreement was just observed, not a merely-stale snapshot) and the target is `actual`
itself — nothing to apply; otherwise the target is the RAW snapshot value alone, never widened by
outstanding.

**Bug 3 — the same permanent-stall shape `party-pool-sync.md`'s own root cause 6 already named for
mode 1, reproduced independently in mode 2.** `_reconcileInFlight` used to clear ONLY on an exact
`actual == inFlightTarget` match. A scan-cache gap (the bridge's own throttled read) can let the
observable value skip straight PAST the target before the pump ever samples it landing exactly on
it — e.g. a queued apply lands at 1, then a genuinely new local action removes it again before the
next cache refresh, so the pump sees 0 and never 1 — leaving the key permanently excluded from
both detection and reconcile, forever, with nothing to trip it back out. **Fix**: track this
pump's own last-observed `actual` per key (`_lastActual`) and clear `_reconcileInFlight`
unconditionally the moment `actual` differs from what was last observed, not only on an exact
target match — root cause 6's identical argument reused verbatim (a queued apply's own delta
lands in one synchronous engine call, so there is no observable partial-landing state a
poll-based scan could ever catch mid-flight; ANY movement while in flight IS that apply landing,
by construction).

**Bug 4 — a narrower variant of bug 3 that `_lastActual` alone cannot see, closed with a
timeout self-heal instead.** `StoreHostSellMirrorsToClientAndClientBuyBackReachesTheHost` hung the
full 15 s timeout: a deposit immediately followed by a withdrawal, both landing entirely within
ONE scan-cache window, is invisible even to `_lastActual` — the cache never samples the
intermediate value, so from the pump's own perspective NOTHING ever moved at all, and neither the
exact-match nor the `actualMoved` clear condition ever fires. **Fix**: the same class of guard
`party-pool-sync.md` root cause 7 already used for its own single-slot cheat channel — a
`ReconcileInFlightTimeoutMs` (10 s, matching every other in-flight timeout in this family) drops a
stuck `_reconcileInFlight` entry unconditionally regardless of why it never resolved, letting the
very next tick's ordinary detect/reconcile logic re-evaluate the key fresh against whatever is
CURRENTLY true.

None of these four bugs is the amplification class §1 exists to eliminate — each was a bounded,
convergent (once retried) correctness gap in the client's own reconcile bookkeeping, not a
feedback loop between host and client. That distinction is the whole of §2's argument holding up:
even with four real defects in the client-side logic, nothing here ever grew without bound, and
every one converged to the exact correct total once identified and fixed — categorically different
from mode 1's seven-root-cause history, where the SAME class of local bookkeeping bug fed back
into an escalating cross-instance loop because both sides ran the detect/reconcile logic against a
jointly-mutated total. Here, only the client's own bookkeeping was ever wrong; the host was never
in question, because it never does anything more than read-and-publish.

**Result**: all 9 `SharedInventoryV2LoopbackTests`, re-run twice for flakiness, pass cleanly both
times. The full suite (`dotnet test tests/KotorCoop.Tests --no-build`) — 2,506 tests, including
every pre-existing mode-1 and unrelated suite — passes with zero failures. `dotnet build
KotorCoop.slnx -warnaserror` is clean (0 warnings, 0 errors). `party_pool_sync=2`/`store_sync=2`
stay OFF by default, exactly as designed — this pass only strengthens the bench-side confidence
NEEDS-LIVE already required; nothing here has run against a real game.

---

## 6. 2026-08-29, rig round — Live pass: `party_pool_sync=2` DISQUALIFIED by the exact same
## exponential-doubling runaway class as mode 1's §21/§27, reproduced twice, including with
## `dialogue_item_sync`/`script_item_sync` isolated; `store_sync=2` not reached this round

Task: the queue's item 1 — the exact §27 recipe (`docs/research/party-pool-sync.md` §27.1) re-run
against mode 2. Two-instance rig (`tools/demo.ps1`), both game directories freshly
`build.ps1 -Install -WarnAsError`'d (both deployed `dinput8.dll` 2,060,288 bytes / identical
timestamp; both deployed `KotorCoop.Session.dll` confirmed by byte-scan to contain
`PartyPoolPumpV2`/`StorePumpV2`/`PartyPoolSnapshotPacketV2`). `coop.ini` reset to
`profiles/coop.ini.example` on both, then `party_pool_sync=2`/`store_sync=2` set on both (the
queue's own instruction). Both instances loaded into `END_M01AA` (the default starting save, no
warp — confirmed via `who`: `Malak END_M01AA`).

### 6.1 First attempt: contaminated by `dialogue_item_sync`/`script_item_sync` (both ON by
### default) — a different channel, but a real amplification nonetheless

Three `pooladd g_i_medeqpmnt01` on the HOST, each confirmed individually
(`added 'g_i_medeqpmnt01' x1 to the party pool`). Within seconds, both agent logs showed an
unbroken cascade of `materialise: gave loot 'g_i_medeqpmnt01' ... for the local player (dialogue
grant)` lines — the `giveloot`/dialogue-grant channel (`CheatController.Items.cs`,
`DialoguePump`/`LootPump`), NOT `party_pool_sync` — with stack sizes 6, 120, 321, 201, 243, 216,
18, 369, 576, 9, 288, 1251, 90, 171... interleaved with `party pool: added`/`removed` lines of
similar magnitude. The cascade stopped on its own after ~2 minutes; both sides converged to the
SAME total (`items 99`: 82 of 82 on both) — unlike mode 1's own divergent failure, but still a
two-orders-of-magnitude overshoot. Root-caused to `dialogue_item_sync=1`/`script_item_sync=1`
(both ON by default in `profiles/coop.ini.example`) — `party-pool-sync.md` §11/§19 already
documents this exact cross-channel contamination trap and the established practice of isolating
both flags to `0` before a clean party-pool read; the queue instructions for this round did not
call for that isolation, so the first attempt inherited it. Recorded here so the next round does
not waste time rediscovering it, and because it is itself informative: mode 2 did not prevent
this contamination from reaching a ~180x overshoot either, even though the two sides agreed with
each other at the end.

### 6.2 Second attempt, `dialogue_item_sync=0`/`script_item_sync=0` isolated: mode 2's OWN
### client-side pump reproduces a genuine exponential-doubling runaway, unrelated to any other
### channel

Demo stopped and restarted clean: `coop.ini` reset again, `party_pool_sync=2`/`store_sync=2=2`
kept, `dialogue_item_sync=0`/`script_item_sync=0` added on both, logs cleared, fresh save load
(confirmed `who` → `Malak END_M01AA` again). The same three `pooladd g_i_medeqpmnt01` on the HOST,
each confirmed individually.

**What happened, read directly from the HOST's own agent log
(`%TEMP%\kotorcoop-demo\agent1.log`), tagged unambiguously `party pool v2:` (the mode-2 pump, not
mode 1, not the dialogue-grant channel)**: within ~10 seconds, `party pool v2: submitting
add/remove of Nx '<resref>' through the party-pool channel` lines began an unmistakable
power-of-two doubling ramp — **and it hit items nobody ever touched**: `g_w_blstrpstl001` and
`g_a_clothes01` (both presumably `end_m01aa`'s own opening-tutorial gear, never named in this
round's `pooladd`) rode the identical doubling sequence alongside the targeted
`g_i_medeqpmnt01`:

```
01:51:43  add 1x g_w_blstrpstl001 / add 1x g_a_clothes01
01:51:58  add 2x g_a_clothes01 / add 2x g_w_blstrpstl001      01:51:54/59  add 2x/8x g_i_medeqpmnt01
01:52:02  remove 4x g_a_clothes01 / remove 4x g_w_blstrpstl001            01:52:03  remove 40x g_i_medeqpmnt01
01:52:06  add 6x g_w_blstrpstl001 / add 6x g_a_clothes01                  01:52:07  add 112x g_i_medeqpmnt01
01:52:10  remove 10x / remove 10x                                        01:52:12  remove 315x
01:52:15  add 18x / add 18x
01:52:19  remove 34x / remove 34x
01:52:24  add 66x / add 66x
01:52:28  remove 130x / remove 130x
```

The cascade paused for ~2 minutes (last line 01:52:28, nothing until 01:54:24), giving the false
impression of settling — then resumed a SECOND doubling ramp unprompted (1, 2, 4, 8, 16, 32... on
`g_w_blstrrfl001`, `g_i_adrnaline003`, `g_i_medeqpmnt01`, `g_w_blstrpstl001`, `g_a_clothes01`
simultaneously) between 01:54:24 and 01:54:56, at which point observation stopped (not confirmed
whether it would have resumed a third time). **Final state** (`items 99`): host 66 items total,
client 72 — a real 6-item cross-instance divergence, the same qualitative failure §27 found for
mode 1 (host and client disagreeing with EACH OTHER, not just both wrong by the same amount).

**This is mode 2's own client-side pump (`Pumps/PartyPoolPumpV2.cs`), not a cross-channel
artifact**: the log tag `party pool v2:` is written only by the mode-2 code path
(`CoopAgent.cs`'s `EffectivePartyPoolSyncMode == 2` branch calling `_partyPoolPumpV2.Pump`), and
`dialogue_item_sync`/`script_item_sync` were confirmed OFF in both `coop.ini`s before this run.
This directly contradicts §1.2/§2.5's own "amplification is structurally impossible" argument —
the mechanism reasoned about there (both sides publish, and what one publishes influences what the
other compares against) was believed closed by construction because only the client publishes and
only the host applies. Live evidence says something in the client's own detect/reconcile loop (the
§5 "Bug 1-4" fixes did not, apparently, close everything) still re-derives a doubling delta each
cycle against `end_m01aa`'s own ambient noise, even without the op-log's two-way publish. Root
cause not pinned this round (out of budget for a three-item queue); the doubling-by-exactly-2x
shape on EVERY cycle, across UNRELATED keys simultaneously, is a strong, specific clue for whoever
picks this up next — it smells like a single shared multiplier or a target computed from a
quantity that itself already includes the previous cycle's delta, not per-key independent noise.

### 6.3 Verdict

`party_pool_sync` **stays OFF, not flipped to 2.** Disqualified on the same bar mode 1 failed at
§21/§25/§27: the recipe's own first clause (three rapid host deposits converging to exactly +3)
fails, twice, under two different conditions (contaminated and isolated), with the isolated
condition proving the defect lives in mode 2's own code, not a channel this document blamed for
mode 1's own §27 finding. No crash; both instances stayed responsive throughout
(`Get-Process ... Responding=True`). **Store mode 2 (queue item 2) was NOT reached with
`party_pool_sync` still enabled** — the single-slot arbitrated cheat channel mode 1's root cause 7
already documented as vulnerable to being clobbered by an unrelated concurrent verb made it unsafe
to run the store recipe's own console commands (`openstore`/`scriptgive`/`buy`/`sell`) while
mode 2's party-pool pump could still be mid-cascade; the rig was stopped and restarted with
`party_pool_sync=0`/`store_sync=2` to get a clean read for the store channel instead (see below).
**Concrete next step**: root-cause the doubling shape in `Pumps/PartyPoolPumpV2.cs` before any
further live attempt — in particular whether `OutstandingNet`/`ReconcileTarget` (§2.2/2.3, the
exact functions the §5 "Bug 2" fix touched) can itself be seeded from a value that already
reflects a still-in-flight reconcile, producing a target that is always ~2x the true delta.

### 6.4 Store mode 2, same rig round (`party_pool_sync=0`/`store_sync=2` isolated): TWO
### independent disqualifying defects — a client buy-back never mirrors (a real double-sell), and
### the host's own store pump wiped then restored nearly the entire pristine stock unprompted

Demo stopped and restarted a second time: `coop.ini` reset, `party_pool_sync=0` (back to
default — the mode-2 party-pool defect above made it unsafe to run alongside a store test, per
mode 1's own root-cause-7 single-slot-cheat-channel-clobber precedent), `store_sync=2`,
`dialogue_item_sync=0`/`script_item_sync=0` kept isolated on both, logs cleared, fresh instances.
ONE relocating `warp tar_m02aa` on the host (this session's only warp, matching
`transition-crashes.md`'s own verdict). The documented ambient modal trap (`docs/live-verification.
md`'s established `skipline`/`selectreply 0`/`partyselect ok` retry loop) cleared in 4 tries on
the host, 1 try on the client — both `openstore Larrim_Store` succeeded, no wipe observed between
open and the first deliberate action (clean, matching every prior mode-1 round's own finding for
this specific clause).

**Host sell → client mirror: clean.** `scriptgive g_w_ionblstr01` then `sell Larrim_Store
g_w_ionblstr01` on the host; host log `store: local add of 1x 'g_w_ionblstr01' detected in store
0x74, publishing`; client log, ~2s later, tagged `store v2: submitting add of 1x 'g_w_ionblstr01'
in store 0x74 through the store channel` / `inserted 'g_w_ionblstr01' x1 into store 0x74`. No
repeat, no amplification — unlike the party-pool channel above, this specific op did not loop.

**Client buy-back: never detected, reproducing a genuine double-sell.** `setgold 5000` then `buy
Larrim_Store g_w_ionblstr01` on the CLIENT succeeded (engine-confirmed: `buy 'g_w_ionblstr01' x1
from store Larrim_Store`, a REAL removal through `CSWGuiStore::BuyItem`). Watched the HOST's own
agent log for 2+ minutes afterward: **zero** `store v2:` lines referencing `g_w_ionblstr01` ever
arrived — the client's own detect step (§2.3, the same bridge-scan diff mode 1 always used) never
noticed its own successful buy as a local change to report. Consequence, proven directly: `setgold
5000` then `buy Larrim_Store g_w_ionblstr01` on the HOST **succeeded a second time**
(`buy 'g_w_ionblstr01' x1 from store Larrim_Store`) — the queue's own explicit check
("host re-buy refused") **fails**. `items 99` afterward showed the store's copy of the object
(`0x82`) at `stack 1` (dropped from 2 only by the HOST's own second buy, never by the client's
first one) and a fresh `0xB4 g_w_ionblstr01 yours stack 1` in the host player's own inventory —
two different players each hold a genuine copy of what should have been one sold unit. This is the
SAME symptom class `docs/research/store-sync.md`'s own "client buy-back gap" has documented across
multiple mode-1 rounds (most recently blamed on the `tar_m02aa` ambient-modal trap preventing the
client from even reaching `openstore`) — but this round's client reached `openstore` on the FIRST
retry and the buy itself engine-confirmed success, isolating the defect cleanly to the DETECT step
rather than to the modal trap this document's own prior entries kept blaming.

**NEW, more severe finding: the host's own store pump wiped then restored nearly the store's
entire pristine stock, unprompted, ~5 minutes after opening.** Starting at the same moment the
host's own re-buy and an `items 99` query were issued, the host's agent log shows an unbroken
18-entry cascade tagged `store v2: submitting remove of 1x '<resref>' in store 0x74 through the
store channel` / `removed '<resref>' x1 from store 0x74` — covering `g_w_vbroswrd01`,
`g_w_sonicgren01`, `g_w_poisngren01`, `g_i_frarmbnds01`, `g_a_class6001`, `g_i_belt006`,
`g_w_sonicpstl01`, `k_repair_part` x2, `k_computer_spike` x2, `g_a_class7001`, `g_w_rptnblstr01`,
`g_w_ionblstr01`, `g_a_class5001`, `g_i_cmbtshot001`, four `g_i_trapkit0xx` entries,
`g_w_stungren01` x2, `g_w_vbroshort01` — essentially the store's entire base inventory, not
anything this round's recipe ever touched. ~4-19 seconds later, an `add` cascade of nearly
identical shape restored every one of those same keys at the same quantities EXCEPT
`g_w_ionblstr01` (correctly not restored — that unit was legitimately consumed by the host's own
buy). Net effect after ~20 seconds: the store's total stock was correct again, but for that window
the HOST's own belief — which mode 2's design makes the sole canonical source every peer
converges toward (§2.1) — read as almost completely empty, and since the host broadcasts its
snapshot unconditionally every cycle (§2.1), any peer polling or rendering its own store panel
during that window would have shown the SAME false wipe. This is the identical shape
`docs/research/store-sync.md`'s very first live pass found and marked "OPEN, CRITICAL" (a virgin
store's local baseline diffed against an empty prior belief reads as "should be zero") — later
reported fixed at the READ side for mode 1, but reproducing here in mode 2's own, textually
separate pump (`Pumps/StorePumpV2.cs`) roughly five minutes after the store was first opened and
scanned, not on the very first scan — suggesting whatever triggered this round's wipe is a
DIFFERENT trigger than mode 1's original "first scan ever" case (a plausible candidate: the
host's own `items 99` full-inventory query and/or its own `buy` cheat forcing a fresh, larger-scope
bridge scan that this pump's own belief dictionary had never seen these keys through before,
mirroring mode 1's original bug's own mechanism one layer later).

**Infinite-stock buy: clean no-op**, re-confirmed even during/around the wipe window — `buy
Larrim_Store g_w_blstrpstl001` (Larrim's known infinite entry) succeeded with no `store v2:` line
for that resref ever appearing (infinite entries are filtered out of the snapshot entirely, §2.6).
Not investigated this round for lack of budget: an incidental, unrelated finding — `setgold`'s own
gold value went NEGATIVE on the host (`gold -140 -> 5000` read back from a subsequent `setgold`)
after a burst of `purse: peer 2 contributed +4294962076` / `+0 -3294967077` lines in the host's
agent log immediately after the client's own `setgold 5000` — a `progression_sync` gold-ledger
integer-overflow/wraparound, NOT a `store_sync` defect (the wire delta magnitude, ~2^32, is a
strong sign of a signed/unsigned mismatch somewhere in the gold delta encode/decode path), flagged
here only so the next round does not mistake it for a `store_sync` symptom. Blackout+reconnect was
NOT attempted this round — not meaningful to pursue against a channel that already fails its own
first two clauses this badly, matching `party-pool-sync.md` §27's own precedent for when to stop
early.

### 6.5 Verdict

`store_sync` **stays OFF, not flipped to 2.** Two independent, reproducible, disqualifying defects
found in a single round: a client buy-back that never reaches the host (a genuine double-sell,
directly proven), and a host-side full-stock wipe/restore cascade unprompted by any store action in
the recipe (the same critical shape mode 1's very first round found, reproducing in mode 2's
separate implementation). No crash; both instances stayed `Responding=True` throughout. **Concrete
next steps, ranked**: (a) root-cause the client buy-back detect gap in `Pumps/StorePumpV2.cs`'s
client-side branch — likely the same family of bug as the party-pool doubling above, since both are
detect-step defects in freshly-built mode-2 code the loopback tests (§5) did not catch; (b)
instrument `Pumps/StorePumpV2.cs`'s host-side scan to log WHY a key transitions from "unknown" to
"known" mid-session well after the store was first opened, to pin what specifically triggered the
wipe-then-restore cascade (a `buy` cheat, an `items 99` query, or simple time/cadence); (c) the
`progression_sync` gold-overflow finding is unrelated to this document's own scope but real and
reproducible — worth its own follow-up before the next round uses `setgold`/`grantgold` near a
`buy`.

Build/test evidence for this round: `dotnet build KotorCoop.slnx -warnaserror` and `src/
KotorCoop.Plugin/build.ps1 -Install -WarnAsError` (both game directories) clean before the rig was
brought up; both deployed `dinput8.dll` confirmed 2,060,288 bytes / identical timestamp, both
deployed `KotorCoop.Session.dll` confirmed by byte-scan to contain the mode-2 type names. Full
`dotnet test`/native-build baseline for this session is recorded separately in
`docs/live-verification.md`.

---

## 7. 2026-08-29, correctness pass — the doubling's real mechanism found and fixed (client-side
## outstanding-ledger retirement, not a host self-apply); host apply surface tightened to the
## letter of §2's design; two store bugs (virgin wipe, absent-store false-deposit) closed;
## `party_pool_sync`/`store_sync` stay OFF, both modes at 0

This pass set out to verify item-by-item that the HOST does exactly the two things §2 allows —
scan-and-publish, and apply an accepted client intent once each — and that the CLIENT's reconcile
logic cannot re-derive an intent from its own reconcile-caused engine movement. Auditing the host
branch found one real departure from the letter of §2.1 (below), but the actual mechanism behind
§6.2/§6.3's exponential doubling turned out to live entirely in the CLIENT's outstanding-intent
ledger, confirmed by writing exactly the host-only and repeated-ambient-churn harnesses item 4
asked for and watching one of them fail.

### 7.1 The real defect: `ReconcileTarget` retired the WHOLE outstanding ledger on the weakest
### possible evidence, instead of retiring only what confirmed snapshot movement actually covers

`Pumps/PartyPoolPumpV2.cs`'s `ReconcileTarget(key, actual, snapshotValue)` — introduced by §5's own
"Bug 2" fix to stop a reconcile-apply from double-counting an already-in-flight intent — decided
"has the snapshot caught up with what I still owe?" by comparing `actual` (this instance's OWN
current engine value, which reflects EVERY local change the instant it happens) against
`snapshotValue` (the host's last-broadcast belief, which necessarily lags). Whenever `actual` sat
on the correct side of `snapshotValue` in the outstanding ledger's own direction, it called
`ClearOutstanding(key)` — wiping the **entire** outstanding list for that key in one shot, from
both the `snapshotMoved` branch and the `_reconcileInFlight` landing branch.

This is sound for exactly one outstanding entry. It is wrong the moment a SECOND, genuinely
distinct, not-yet-host-applied intent for the same key is also outstanding — which is not a rare
edge case here, it is the ordinary shape of rapid or repeated local activity (several `pooladd`s in
a row; ambient script noise recurring every few seconds; several buy/sell clicks before the host's
single-slot cheat channel has drained the first one). Because `actual` reflects the client's own
engine immediately, it is very often already past `snapshotValue` in the right direction well
before the SNAPSHOT itself has caught up to more than the first of several outstanding intents —
the `explainedByOwnEngine` check has no way to tell "one intent's effect showed up" from "my own
engine has raced ahead on its own, with several separate intents still genuinely owed". The wipe
retired credit for intents the host had not applied yet at all. On the very next tick,
`detectTarget = snapshotValue + OutstandingNet(key)` was short by exactly that wiped amount, so the
SAME already-published movement was re-detected as "new", re-published under a fresh serial, and
(once the host got around to it) re-applied — the client's own bookkeeping manufacturing a second,
real request for something it had already asked for once. Reproduced directly by this pass's own
`RepeatedClientAmbientChurnConvergesExactlyWithBoundedHostApplies` test (ten rapid, distinct client
deposits 120&#160;ms apart): the client's own detect log showed nine published serials summing to
13 units of "new" delta for an engine that only ever moved by 10, and the host settled at 11 while
the client settled at 10 — a genuine cross-instance divergence of the exact qualitative shape
§6.2/§6.3 described (not merely both sides agreeing on an inflated number).

**Fix**: `ReconcileTarget` is now a pure decision function with no side effects. Retirement happens
in exactly one place, driven by the snapshot's own confirmed movement rather than a comparison
against this instance's `actual` — the literal reading of §2.2's own words, which this
implementation had never actually followed: *"retired — by however much, not necessarily all at
once — the moment a NEW snapshot's own value for that key has moved in the same direction... an
exact match is not required."* A new `RetireOutstandingBy(key, snapshotValue - priorSnapshotValue)`
runs once per tick, exactly when `snapshotMoved`, and retires outstanding entries oldest-first, up
to the confirmed amount, restricted to entries moving in the same direction — partially shrinking
an entry (`OutstandingIntent.Delta` is now mutable) rather than removing more than the evidence
supports. `AckGraceMs`'s blind timer (previously 2,500&#160;ms) is kept only as a last-resort
safety net for a genuinely stuck entry (raised to 15,000&#160;ms — the old bound assumed a single
in-flight apply and did not hold once several accepted intents had to drain one at a time through
the single-slot arbitrated cheat channel); the evidence-based path above is what normally retires
an entry, usually within one broadcast cycle. Applied identically to `Pumps/StorePumpV2.cs`.

### 7.2 Host apply-surface audit: the party pool matched §2.1 exactly; the store needed its
### write-through kept, for a reason the party pool does not share

Per the task's own checklist (intents re-applied while unacked; a reconcile-toward-snapshot branch
running on the host; a snapshot fed back to the host via the relay), none of those were found —
`PartyPoolPumpV2.PumpHost`/`StorePumpV2.PumpHost` never call anything but a scan-publish and a
drain-of-accepted-intents-into-one-apply-each; `session.IsEnemyAuthority` is two static `true`/
`false` overrides (`CoopHost.Enemies.cs`/`CoopClient.Enemies.cs`), never both branches at once.

One real, narrower departure from §2.1's "the host publishes... a direct read of the host's own
game": `CoopHost.HandlePartyPoolIntentV2`/`HandleStoreIntentV2` used to write an accepted intent's
GUESSED effect into the broadcast belief immediately, ahead of the real engine scan ever seeing it
land. For the party pool this write-through was removed outright — `PartyPoolPumpV2.PumpHost` scans
UNCONDITIONALLY every tick (no GUI-gating of any kind), so the real scan always catches up on its
own within one apply-landing cycle, and all party-pool tests (existing and new) pass with the
write-through gone. Attempting the identical removal for the store broke
`ClientBuyBackAppliesToTheHostExactlyOnceAndStaysThatWay` (a NEW test, added this pass): a store can
only be SCANNED while its GUI is open (§2.6) — applying never needed it, only reading does — so
with the store's GUI closed at the moment a buy-back intent lands (the ordinary, documented case),
the broadcast belief would never be refreshed by a real scan at all until the GUI reopens, leaving
`AckGraceMs`'s timer as the ONLY way outstanding could ever retire for that key — which, once it
fired, reproduced a real second apply on the host. The store's write-through was restored (§2.7's
own store-specific limitation already accepted a comparable gap; this is the same shape, now
understood precisely) — it never applies anything the store's own next real scan would not
independently confirm, it just keeps the evidence-based retirement path in §7.1 reachable even with
the GUI closed. The party pool's own write-through removal stands, since it was never needed there.

A cross-thread hazard was also tightened while auditing this code, independent of the above: both
`BroadcastPartyPoolV2` and `BroadcastStoreV2` used to hand `Paginate` a bare reference to the live
belief dictionary, captured under `_gate` but then enumerated OUTSIDE it while a concurrent intent
handler could still be mutating the very same `Dictionary` instance on the receive-loop's own task —
undefined behaviour under `Dictionary<TKey,TValue>`'s own contract, the identical "unguarded
cross-thread queue" class party-pool-sync.md's root cause 5 already named for mode 1. Both now clone
under the lock before paginating. Not confirmed as a contributor to §6's own doubling (§7.1 above is
a complete, deterministic explanation on its own, reproduced without any concurrency involved), but
a genuine, provable correctness gap worth closing regardless.

### 7.3 Store virgin wipe and absent-store false-deposit, both closed

**Virgin wipe (§6.4)**: `CoopHost.PublishStoreSnapshotV2`'s per-store merge treated a SINGLE scan's
absence of a previously-known key as proof it left the store, with no protection against
`KotorGameBridge.TryScanOpenStore` transiently reading back a reduced/zero count while the engine's
own item repository is mid-mutation (a stack merge/split, a fresh larger-scope re-read forced by an
unrelated cheat) — the exact live-observed shape (an 18-entry wipe, then an almost-identical
restore ~4-19&#160;s later). Fixed with a debounce: a key now missing from a fresh scan is not
removed on the spot — its first-missing moment is recorded, and it is only actually dropped once it
has read absent continuously for `StoreStaleRemovalConfirmMs` (2,000&#160;ms), i.e. confirmed by a
SECOND, independent fresh scan of that same store, not a repeat read of a cached one. A genuine
depletion stays absent across that whole window and is still removed, just up to ~2&#160;s later; a
one-off transient glitch self-heals on its very next real scan and never touches belief at all.

**Absent-store false-deposit (item 3)**: a store the host has never scanned contributes nothing to
`_storeBeliefV2`, so every one of its keys reads as snapshot-value 0 — indistinguishable, from the
client's own detect step, from "the host scanned it and it is genuinely empty". Left unguarded, a
client opening a real, pristine, never-shared store would read its own real stock as a giant "local
deposit" against that false zero baseline — not merely a bad wire report, a real risk of the host
later double-adding that store's own pristine content once it finally scans it for itself. Closed
with an explicit "known store" signal: the host now tracks every store id its own bridge has ever
scanned (`_storeKnownIdsV2`, populated unconditionally in `PublishStoreSnapshotV2`, independent of
whether that scan came back empty) and exposes it via `IStoreSession.IsStoreKnownV2` (a new default
interface member, false everywhere except `CoopHost`'s own real answer); the client independently
learns a store id is known the moment ANY entry naming it arrives in a snapshot
(`CoopClient.StoreV2.cs`'s own `_storeKnownIdsV2`, monotonic — "once known, stays known", mirroring
§2.7's own precedent) and exposes the identical query. `Pumps/StorePumpV2.cs`'s client branch now
skips a key ENTIRELY — no bookkeeping writes either — whenever its store is not yet known, so the
moment it becomes known the pump's ordinary `firstSight` seeding handles it correctly, exactly as if
that were the very first tick the key had ever existed.

### 7.4 Tests

`tests/KotorCoop.Tests/SharedInventoryV2LoopbackTests.cs` gained six new cases, exercising the
harness shapes item 4 asked for:

- `HostOnlyPartyPoolAmbientChurnNeverQueuesAHostSideEngineApply` /
  `HostOnlyStoreAmbientChurnNeverQueuesAHostSideEngineApply` — a host with no peer that ever
  publishes anything, its own engine bumped every tick for 40 rounds; `AgentLoopV2`'s new
  `PoolApplyDequeueCount`/`StoreApplyDequeueCount` counters (incremented the instant this side's own
  pump ever queues an engine apply, landed or not) must read exactly 0 throughout, while a bare
  observer `CoopClient` (connected, `RunAsync` only, no `AgentLoopV2` of its own) still sees the
  snapshot track every change — proving the publish half keeps working with nothing to gate it on.
- `RepeatedClientAmbientChurnConvergesExactlyWithBoundedHostApplies` — the test that caught §7.1:
  ten rapid, distinct client-side deposits must converge to exactly 10 on both sides and STAY there,
  with the host's own apply count bounded to a small multiple of 10, never anything exponential.
- `ClientBuyBackAppliesToTheHostExactlyOnceAndStaysThatWay` — the test that caught §7.2's store
  write-through gap: a client buy-back (host's own store GUI closed at the moment the intent lands,
  matching the pre-existing `StoreHostSellMirrorsToClientAndClientBuyBackReachesTheHost`'s own
  scenario) must apply to the host's engine exactly once (`AgentLoopV2.StoreApplyLandings`), held
  stable for 5 further seconds.
- `ClientStoreNeverOpenedByHostStaysUnknownAndUntouched` — the test for §7.3's second fix: a client
  with real, pristine stock in a store the host's `OpenStoreId` never touches for the whole test
  must never be reported as a deposit, never reconciled, and both sides' `IsStoreKnownV2` must stay
  false for that store id throughout.

`AgentLoopV2` gained `PoolApplyDequeueCount`/`StoreApplyDequeueCount` (incremented the instant this
side's own pump ever dequeues an apply, whether or not it later lands) and
`PoolApplyLandings`/`StoreApplyLandings` (incremented only once the simulated single-slot cheat
channel actually lands one), used by the new tests above and available for any future test in this
family.

### 7.5 Result

`dotnet build KotorCoop.slnx -warnaserror`: 0 warnings, 0 errors. The filtered suite
(`FullyQualifiedName~SharedInventoryV2|FullyQualifiedName~PartyPool|FullyQualifiedName~Store`, 274
tests) passed cleanly three consecutive runs in a row after the §7.1 fix landed (it reproduced the
defect reliably before the fix, and failed zero times in three runs after). The full suite
(`dotnet test tests/KotorCoop.Tests --no-build`, 2,511 tests) passed with zero failures on its
second run; its first run showed three failures
(`CoopCommandDispatcherTests.HostingWithBothRejoinGatesOffServesNothing`,
`CoopCommandDispatcherTests.HostingWithARejoinGateOnServesItsWorldOverWorldTransfer`,
`StoreLoopbackTests.ReconnectResyncLeavesUntransactedStoresAlone`) — all three unrelated to this
document's own code (rejoin-gate hosting selection; a mode-1 store reconnect-resync test), and all
three passed cleanly when re-run in isolation immediately afterward, matching this repository's
already-documented pattern of environment-flaky tests under full-suite parallel load (see the
`kotor-coop-live-session-checks` note and the recent `SaveDigestTests` "CI race" fix) rather than a
regression from this pass's changes.

`party_pool_sync`/`store_sync` **stay OFF (mode 0) by default**, both explicitly confirmed at 0 in
every test and left unchanged in `profiles/coop.ini.example` — this pass closes bench-side defects
found by construction and by targeted loopback testing; it does not itself constitute a live pass,
and the next live round should re-run §6's own exact recipe (three rapid host `pooladd`s;
`dialogue_item_sync`/`script_item_sync` isolated to 0; the store buy-back and virgin-store-open
checks) against a build containing this pass's fixes before either mode is considered for a default
flip.

**Files touched**: `src/KotorCoop.Session/Pumps/PartyPoolPumpV2.cs`,
`src/KotorCoop.Session/Pumps/StorePumpV2.cs`, `src/KotorCoop.Session/CoopHost.PartyPoolV2.cs`,
`src/KotorCoop.Session/CoopHost.StoreV2.cs`, `src/KotorCoop.Session/CoopClient.StoreV2.cs`,
`src/KotorCoop.Session/IStoreSession.cs`, `tests/KotorCoop.Tests/SharedInventoryV2LoopbackTests.cs`,
this document.

---

## 8. 2026-08-29, second rig round (against §7's fixes) — item 1: a NEW, distinct client-side
## defect (a reconcile-apply landing re-detected and re-published as a fresh intent), no
## exponential doubling this time; item 2: inconclusive on the client half (pre-existing
## `tar_m02aa` environment hazard blocked `openstore` for the whole session) but a genuine,
## unexplained host->client divergence confirmed at the engine level; both flags stay OFF

Task: re-run the queue's items 1/2 against `main` merged forward through §7's own fixes
(retire-by-confirmed-snapshot-movement, cloned-under-lock belief, store-removal debounce,
absent-store=unknown). Two-instance rig (`tools/demo.ps1`), both game directories freshly
`build.ps1 -Install -WarnAsError`'d (both deployed `dinput8.dll` 2,060,288 bytes / identical
timestamp `08/29/2026 04:07:14`; both `KotorCoop.Session.dll` confirmed by byte-scan to contain
`PartyPoolPumpV2`). `coop.ini` reset to `profiles/coop.ini.example` on both, then
`party_pool_sync=2`/`store_sync=2` plus `dialogue_item_sync=0`/`script_item_sync=0` (isolated from
the start this round, per §6.1's own lesson — no cross-channel contamination observed).

### 8.1 Item 1 — `party_pool_sync=2`: DISQUALIFIED again, but by a DIFFERENT mechanism than §6's
### doubling — a client reconcile-apply landing gets re-detected as a fresh local action

`END_M01AA`, no warp. Ambient noise on both instances settled cleanly first (a single bounded
deposit each for `g_i_medeqpmnt01`/`g_w_blstrpstl001`/`g_a_clothes01`, exactly matching §2.4's
predicted shape — no ramp, confirmed stable for 90s before testing). Three rapid HOST
`pooladd g_i_medeqpmnt01`s, each confirmed individually. **No exponential doubling was observed
this round** — every logged movement stayed single-digit, unlike §6.2's clean 1,2,4,8,18,34,66,130
ramp. But after all activity settled (60s+ of no new `party pool v2:` lines, re-confirmed after a
further 2 minutes idle with zero change), the two engines had permanently DIVERGED: summing every
`added '<key>' xN`/`removed '<key>' xN to/from the party pool` line for `g_i_medeqpmnt01` gives a
net of **+3 on the host, 0 on the client** — a real, unrecovered, 3-unit split that did not
self-heal across 4+ minutes of idle time, contradicting the "2 min idle -> stable" clause (it WAS
stable, just stable on the wrong number).

**Root cause, read directly from the client's own log.** The client's detect-log line
(`Pumps/PartyPoolPumpV2.cs` line 342, tagged `"party pool: local {kind} of {count}x '{key}'
detected, requesting (serial N)"` — no `v2` in this specific tag, unlike the apply-drain line) fired
TWICE for `g_i_medeqpmnt01` in this session, and in both cases it fired within 0-2 seconds of that
SAME key's own reconcile-apply landing (the immediately preceding `party pool v2: submitting
{kind} of {count}x .../ removed/added ...` pair):

```
[04:11:55] * party pool v2: submitting remove of 1x 'g_i_medeqpmnt01' through the party-pool channel
[04:11:55] * removed 'g_i_medeqpmnt01' x1 from the party pool
[04:11:57] * party pool: local remove of 1x 'g_i_medeqpmnt01' detected, requesting (serial 4)   <- re-detected its OWN reconcile landing
...
[04:14:00] * party pool v2: submitting remove of 4x 'g_i_medeqpmnt01' through the party-pool channel
[04:14:00] * removed 'g_i_medeqpmnt01' x4 from the party pool
[04:14:02] * party pool: local remove of 4x 'g_i_medeqpmnt01' detected, requesting (serial 8)   <- same shape again
```

Both false intents were accepted and applied by the HOST (`host`'s own log shows a matching
`party pool v2: submitting remove of Nx .../ removed Nx` at the same moment for each), so the
HOST's own canonical engine — the single source of truth §2.1 exists to protect — was reduced by
movements that were never real player or ambient actions, only the client catching up to
something the host already knew. This is the exact class §1.2/§2.5 claims mode 2 eliminates by
construction ("a receiver's own convergence can never itself become a new fact for the publisher
to react to, because the publisher never reads the receiver's engine at all") — except here it is
not the RECEIVER's convergence directly read by the host; it is the receiver's own convergence
being MIS-CLASSIFIED as a fresh local change by its own detect step and re-published as a genuine
intent, which the host then (correctly, per its own rules) accepts and applies. §7's fix closed
the specific doubling shape found in §6 (a whole-outstanding-ledger wipe on weak evidence); this
is a different leak in the same neighbourhood, not yet root-caused to a specific line — a
plausible next step is checking whether `_reconcileInFlight`'s clearing (the `actual ==
inFlightTarget || actualMoved` branch, `PartyPoolPumpV2.cs` around line 266) can clear on the SAME
tick the detect check below it (line 329, after the `continue` at line 291 skips it) still sees a
stale `_lastActual`/`detectTarget` pairing that has not yet absorbed the just-cleared reconcile,
one tick later. No crash; both instances stayed `Responding=True` throughout (re-confirmed after
the divergence, and again after a further 2-minute idle wait). `party_pool_sync` stays OFF.

### 8.2 Item 2 — `store_sync=2`: the client half could not be exercised (pre-existing `tar_m02aa`
### environment hazard, distinct from `tp`'s known z-height poison — this round never used `tp`),
### but the host->client engine divergence that resulted is itself worth recording

Rig restarted clean: `party_pool_sync=0` (unsafe to run alongside item 1's still-open defect, per
§6.3's own precedent), `store_sync=2`, `dialogue_item_sync=0`/`script_item_sync=0` kept isolated.
ONE relocating `warp tar_m02aa` (this session's only warp, host only — the client follows
automatically). Both instances landed in `tar_m02aa` cleanly (`who` confirmed both sides), no
crash, `status` showed zero liveness anomalies on both. The host's own store GUI cleared the
documented ambient-modal trap in 1 retry (`skipline`/`selectreply 0`) and stayed open for the rest
of the session. **The client's store GUI never opened at all, for the entire ~13-minute session**:
its very first `openstore Larrim_Store` refused with `"another modal screen is up"`; the documented
`skipline`/`selectreply 0`/`partyselect ok` retry loop was run 8 times over several minutes with no
change in the refusal reason. Unlike the module's own documented `duros`-conversation trap, this
was not blocked by an active conversation — `skipline` itself replied `"no target: no active
conversation here to skip a line in"` every time, and the client's own gait line showed `hp 12/12`,
`not in combat`, `"nobody here is fighting"` throughout, with `standup` replying `"you are already
up"` (the local PC itself was never down). The client's own log repeatedly printed `"your party is
down — a peer can still revive you"` (a companion, not the PC) starting seconds after the warp,
coinciding with a real fight the client's own gait briefly reported (`[fighting]`, per the host's
`who`) — plausibly a hostile encounter at the `tar_m02aa` warp-in point downing a companion. Moving
the host directly onto the client's position (`goto Malak`) to trigger the documented
proximity-based peer-revive, then waiting a further 2.5 minutes, produced no change. This is a
DIFFERENT trigger than `store-sync.md`'s own documented `tar_m02aa` hazard (that one required an
explicit `tp ... 0` z-height fall — this round never issued `tp` at all), so it is recorded here as
a distinct, not-yet-explained variant of the same module's general instability for the next round
to watch for.

**What could still be checked despite the client being locked out of its own store GUI.** Host
sell: `scriptgive g_w_ionblstr01` then `sell Larrim_Store g_w_ionblstr01` on the host succeeded at
the engine level (`items 99` on the host: object `0x82` `g_w_ionblstr01` in store `0x74` read stack
1 -> 2). The client's own `items 99`, taken ~5 minutes later, still read the SAME object id `0x82`
in the SAME store `0x74` at stack **1** — the sale never mirrored, a genuine, persistent
host-vs-client divergence for a single, simple, uncontested sell. Code inspection offers a partial
explanation, distinct from a proven root cause: `Pumps/StorePumpV2.cs`'s `PumpClient` returns
immediately (line ~118-125) whenever `bridge.TryScanOpenStore` fails to find ANY store open THIS
tick — before ever reaching the snapshot-vs-belief reconcile logic below. The file's own header
comment claims *"the reconcile-apply itself needs no GUI open... only fresh detection is
store-gated"*, but that is only true for a store the client has reconciled at least once before (so
a reconcile is already queued and continues draining independently via the dispatcher); for a store
never opened even once in a session, the client's PumpClient can never build the `keys` set for that
store's object id in the first place, so it can never begin converging toward the host's snapshot at
all — a narrower guarantee than the doc comment states, and one this round's environment hazard
exposed directly (the client legitimately could not open `Larrim_Store` even once, so its reconcile
for that store never started). Whether this fully explains the observed divergence, or whether a
genuine wire/reconcile defect also contributes, is NOT settled this round — worth re-testing with a
client that can actually reach `openstore`. The client buy-back and "host re-buy refused" checks
were **not reached** for the same reason. No crash; both instances stayed `Responding=True`
throughout. `store_sync` stays OFF.

### 8.3 Item 3 — flip defaults: NOT REACHED (same as the prior round — both flags still disqualified
### or inconclusive). `profiles/coop.ini.example`, `CoopConfig`/`AgentSettings` defaults,
`CONFIG.md`, and the README/roadmap/research-README rows are all unchanged.

### 8.4 Item 4 — baseline: **PASS.**

- `dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings/errors, against the
  freshly-fast-forward-merged `main` (`1e7abca`).
- `src/KotorCoop.Plugin/build.ps1 -Install -WarnAsError`: clean NativeAOT win-x86 build, both game
  directories freshly installed; both deployed `dinput8.dll` byte-identical (2,060,288 bytes, same
  timestamp); both `KotorCoop.Session.dll` confirmed by byte-scan to contain `PartyPoolPumpV2`. A
  second standalone `build.ps1 -WarnAsError` re-run at the end of the session (no source changes)
  was also clean.
- `dotnet test tests/KotorCoop.Tests --no-build` (rig down): **2511/2511 passed**, 0 failures, one
  run, no flake.
- `tools/verify.ps1`, fresh two-instance session with `coop.ini` reset to the plain
  `profiles/coop.ini.example` on both sides: **41 passed, 8 failed** — exactly the documented
  40-42/7-9 baseline range, the same specific 8 known failures (`phantom damageable, not a sink`;
  `the peer really loses those hit points`; `a client hit costs the enemy real hit points`; `a
  downed player is reported to the agent`; `the co-op revive stands the player back up`; `every
  engine call signature matches the binary` at 197/199; `a container opened on the client is held
  and shown open on the host`; `no per-frame spam`). No regression, no crash.

**Verdict**: `party_pool_sync`/`store_sync` **stay OFF (mode 0)** — item 1 is disqualified by a
new, distinct, reproducible defect (not the doubling §7 already fixed); item 2's client-dependent
checks are inconclusive (blocked by an environment hazard, not by a proven `store_sync` defect
alone) but the plain host-sell-never-mirrors result stands as real, live-observed evidence
regardless of that hazard's exact cause. No crash in either round. **Concrete next steps, ranked**:
(a) instrument `PartyPoolPumpV2.cs`'s `_reconcileInFlight` clear-and-continue path
(~line 266-291) to log `detectTarget`/`_lastActual` on the SAME tick a reconcile lands, to catch
the false re-detection in the act rather than inferring it from timestamps; (b) get a client
past `tar_m02aa`'s `openstore` block in a way that survives a full store recipe (a different warp
target module entirely may be simpler than continuing to fight this one's ambient hazards); (c)
re-test the host-sell-mirror specifically with a client whose store GUI IS open, to separate the
"never opened" explanation in §8.2 from a genuine wire defect.

**Files touched this round**: none — bench/rig-only verification pass, no source changes. This
document only.

---

## 9. 2026-08-29, client-echo redesign (item 1) + store GUI-gating split (item 2) — the
## `_reconcileInFlight`/absolute-target detect step replaced with a belief + expectedFromReconcile
## ledger; store reconcile now runs blind (no GUI needed, matching the apply primitive); a genuine
## test-timing race found and fixed along the way; both modes stay OFF, at 0

Task: (1) close §8.1's own client-echo defect (a client's reconcile-apply landing gets
re-detected as a fresh local intent) by tracking every reconcile this pump queues against its own
engine as an explicit "expected movement" ledger, consumed against future observed movement before
anything is ever reported as new; model the loopback harness's cheat channel as genuinely
asynchronous (ticks, not an instant) so the bug reproduces before the fix and vanishes after. (2)
split `StorePumpV2`'s client-side GUI gate: `Odyssey.Store.cs`'s `InsertItemIntoStore`/
`RemoveStoreStack` (driving the `StoreInsert`/`StoreRemove` cheats) resolve their target store
purely by object id (`GetStoreByGameObjectID`, VERIFIED, no GUI read anywhere in either function) —
reconcile must run for every known store regardless of GUI state; only DETECTING a local buy/sell
(the scan) needs the GUI open.

### 9.1 Item 1 — root cause and fix

§8.1's own read of the client's log pinned the symptom (a reconcile-apply's own landing
re-detected and re-published, permanently diverging the host) but not the exact mechanism. Re-
examining the pre-§9 design here: `_reconcileInFlight` excluded a key from BOTH detection and
reconcile while a single frozen target was outstanding, clearing on an exact match or "any
movement" (root cause 6's fix, reused); the DETECT check underneath compared `actual` against an
ABSOLUTE `detectTarget = snapshotValue + OutstandingNet(key)`, a completely different bookkeeping
dimension (client-published intents to the HOST) than what the reconcile itself had just landed.
The exclusion window closing correctly (the reconcile's own value landed) did not guarantee
`detectTarget` had ALSO caught up to reflect it that same tick — snapshot-driven outstanding
retirement (§7's `RetireOutstandingBy`) runs on its own schedule, keyed off the snapshot's own
confirmed movement, not off a reconcile landing. A residual gap between the two, on the very tick
the exclusion cleared, read as "actual disagrees with the (still stale) absolute target" — a
spurious local change, exactly §8.1's own symptom.

**Fix**: replace the single-value exclusion and the absolute-target detect comparison with two
independent mechanisms that no longer share any state:

- **`_belief`** (`Dictionary<Key, int>`): this pump's own last-observed local scan value per key,
  advancing to the fresh scan value EVERY tick, unconditionally, whether or not the movement since
  the last tick was reported as anything.
- **`_expectedFromReconcile`** (`Dictionary<Key, List<ExpectedReconcileMovement>>`): every
  reconcile-apply this pump has queued against its own engine for a key, not yet confirmed landed
  by an observed scan movement, with a `Delta` mutable so a movement that only PARTIALLY explains
  an entry shrinks it rather than only ever consuming it whole, and a 10&#160;second timeout
  (`ExpectedTimeoutMs`, matching every other in-flight timeout in this family) that drops a stuck
  entry unconditionally — a scan-cache gap swallowing the intermediate value entirely (the exact
  permanent-stall shape root cause 6 already named) is presumed resolved rather than left stuck
  forever.

Every tick, per key: `rawMovement = actual − belief`; `ConsumeExpected` nets it against
`_expectedFromReconcile`, consuming oldest-first, direction-gated (a movement in the OPPOSITE
direction of every pending entry — a genuine local action happening while an unrelated reconcile is
in flight for the same key — is left entirely unconsumed); only the unexplained remainder is EVER
reported as a fresh intent. `belief` advances to `actual` regardless. This key is NEVER skipped
wholesale any more (no `continue` while "in flight") — a reconcile and a genuine, independent local
action for the same key can now coexist and both resolve correctly, instead of one hiding behind
the other's frozen target.

The old `_outstanding` ledger (client intents published to the HOST, not yet echoed back) is kept,
unchanged in its own mechanics (§7's evidence-based `RetireOutstandingBy`), but is now consulted
ONLY by `ReconcileTarget`'s decision of whether reconciling toward the raw snapshot would clobber a
not-yet-echoed local deposit — it no longer feeds the detect step at all. This is the actual
structural fix: detection and "am I explaining my own writes" no longer share a bookkeeping
dimension with "has the host echoed my publish back yet".

Separately, and independent of detection, the reconcile decision itself is now also ledger-based
rather than a frozen single value: `target = ReconcileTarget(key, belief, snapshotValue)`;
`projected = belief + ExpectedFromReconcileNet(key)` (what this pump's own already-queued
reconciles are expected to bring the engine to, once they land); any gap between the two queues
one more reconcile for exactly the remainder, recomputed fresh every tick — never a value captured
once, so a target that moves again while an earlier reconcile is still draining through the
single-slot arbitrated cheat channel is caught the very next tick.

**A second, distinct defect found while building this** (not part of §8.1's own report): the
first version of this redesign kept the old design's "silently seed belief and defer to the second
tick" idiom for a key's very first observation, to avoid double-counting two instances
independently loading identical pristine content. A DELTA-based design cannot recover from that
seed the way the old ABSOLUTE-target design's one-tick defer could — once `belief` is silently set
to `actual` on first sight, nothing ever moves again to re-surface the standing value it absorbed,
so it is lost forever, not merely deferred. Found live via
`ThreeRapidClientDepositsConvergeToExactlySumOnBothSides` regressing (a genuine +3 local action
already sitting in the engine before the pump's very first observation was silently eaten, never
published). **Fix**: no special first-sight branch at all — `belief` simply defaults to 0 for a
never-seen key via `Dictionary.GetValueOrDefault`, and the SAME detect/reconcile logic runs
immediately. A standing surplus is now reported on the very first tick instead of deferred one
tick; the "two instances independently loaded the same pristine content" case this used to guard
is the same accepted, bounded, at-most-one-time over-count §2.4 already documents, and
`IdenticalAmbientNoiseOnBothSidesConvergesWithoutASecondRoundOfAmplification` already tolerates
either outcome (2 or 4) for exactly this reason.

### 9.2 Item 2 — root cause and fix

`Pumps/StorePumpV2.cs`'s `PumpClient` used to `return` immediately whenever
`bridge.TryScanOpenStore` failed — i.e. whenever the client's own store GUI was closed — skipping
RECONCILE entirely, not merely detection. Read directly out of `src/KotorCoop.Plugin/
Odyssey.Store.cs` this pass: `InsertItemIntoStore`/`RemoveStoreStack` (the primitives the
`StoreInsert`/`StoreRemove` cheats drive) resolve their target store through `ResolveStoreRepo` ->
`ResolveStoreBase` -> `GetStoreByGameObjectIDFn` (`CServerExoApp::GetStoreByGameObjectID
0x004AE7B0`, VERIFIED full disassembly, store-sync.md §1.2) — a bare object-id lookup through the
app's general per-instance object registry, with no GUI read anywhere in either function's body.
Only `TryScanOpenStore` (reading a store's stock) needs the GUI open. The pre-§9 early return
conflated the two, so a store the client had reconciled toward before but whose GUI happened to be
closed THIS tick could never receive a further reconcile at all — matching item 2's own
"host sells while the client's GUI is closed" requirement failing outright (§8.2's own live-rig
finding: "a genuine, persistent host-vs-client divergence for a single, simple, uncontested
sell").

**Fix**: split the gate. RECONCILE (the `target`/`projected`/`reconcileDelta` computation, §9.1's
own ledger-based mechanism, applied identically here) now runs every tick for every key this
client's session knows about (`ICoopSession.IsStoreKnownV2`), using `belief` — updated by a real
scan whenever the relevant store happens to be open THIS tick, otherwise carried forward from
whatever it last was (or seeded, see below) — regardless of whether that store's GUI is open.
DETECTION (the belief-delta scan diff that can report a fresh local buy/sell) still only ever runs
for whichever ONE store is open THIS tick — unchanged from v1's own limitation, since there is no
way to observe a change in a shop nobody has opened.

First-sight seeding is asymmetric, deliberately: if the key's own store happens to be open THIS
tick, `belief` seeds from the REAL scan value (respects genuine pristine stock, the same class of
guard §7.3's "virgin wipe" fix already established on the host side); otherwise there is no way to
know the real local baseline without a scan, so it seeds 0 — the identical "unknown baseline
treated as the safest available assumption" shape §2.7 already accepts for the host's own
cold-start apply surface, now symmetric on the client's reconcile side. This carries the same
honest, documented limitation §2.7 already names (a store's real pristine stock, never locally
scanned, can be under-credited until the client itself opens it) — not solved here, not newly
introduced either; item 2 only concerns the previously-total absence of any reconcile at all while
the GUI was closed.

### 9.3 A genuine test-timing race, found and fixed in the pre-existing buy-back tests

Item 1's harness change (§9.4) plus item 2's own fix combined to expose a real race in the two
PRE-EXISTING buy-back scenarios (`StoreHostSellMirrorsToClientAndClientBuyBackReachesTheHost`,
`ClientBuyBackAppliesToTheHostExactlyOnceAndStaysThatWay`, both authored before this pass): the
reconcile-up to the host's mirrored sell can now land entirely BLIND (the client's GUI was closed
for the whole time it was in flight, item 2's own point), and with the harness's cheat channel now
landing in 1-3 TICKS rather than a wall-clock 30-250&#160;ms, it can complete within a handful of
milliseconds of the test setting `OpenStoreId`. Both tests immediately call `WaitForAsync` on the
raw engine dictionary (bypassing the pump), then IMMEDIATELY perform the buy-back
(`TrueStore.Remove`) the instant that resolves. If the reconcile's own landing AND the test's own
immediate removal both complete before this pump's FIRST live scan of the now-open GUI ever runs
at all, the pump's `belief` — which only ever updates from a live scan — goes straight from "never
observed" to observing the ALREADY-removed state, with no intermediate "1" ever witnessed. This is
not a detection bug: from the pump's own vantage point nothing ever moved, because it genuinely
never had a chance to see the deposit before the withdrawal erased it — the identical
"deposit-then-withdraw entirely inside one never-yet-sampled window" shape §7's own Bug 4 already
documented for the scan-cache gap, now REACHABLE across a much wider window because item 2's own
fix removed the old "only while the GUI stays open" bound on how long a reconcile can sit
in-flight, unobserved, before the test's own next action.

**Fix**: a 300&#160;ms settle delay added to both tests between "confirm the engine reconciled"
and "perform the buy-back" — giving this pump's first live scan of the newly-opened GUI a genuine
chance to run before the item is yanked away again. This is a test-realism fix, not a production
code change: a human opening a store and then clicking sell takes far longer than one scan cycle:
the synchronous, back-to-back scripting these two tests used is what created a race no real
player's own timing could ever produce.

### 9.4 Harness change (item 1's own explicit ask)

`SharedInventoryV2LoopbackTests.AgentLoopV2`'s simulated single-slot cheat channel changed from a
wall-clock random delay (30-250&#160;ms) to tick-based (1-3 pump ticks, one per tick, still
single-slot — `_tick`/`_cheatLandsAtTick` replacing `_cheatLandsAtMs`). This guarantees several
detect passes genuinely run WHILE a reconcile is in flight, including the exact tick it lands,
regardless of wall-clock scheduling jitter — the reasoning being that the previous wall-clock delay,
combined with the fake bridge's own ~150&#160;ms scan-cache throttle, rarely interleaved a landing
tightly enough against the pump's own detect passes to exercise §8.1's exact race.

### 9.5 Tests

`tests/KotorCoop.Tests/SharedInventoryV2LoopbackTests.cs` gained two new cases and two settle-delay
fixes to pre-existing ones:

- **`ClientReconcileLandingIsNeverReDetectedAsASpuriousIntent`** (item 1's own repro/fix
  requirement) — a genuine client-side deposit (so the client's own outstanding ledger for the key
  is already in play, §8.1's own precondition), then several rounds of HOST-driven bumps fired
  back-to-back with no per-round wait; every movement AFTER the initial deposit is caused entirely
  by the client's own reconcile converging toward the host's snapshot, so at most one
  `"party pool: local ..."` line naming the key is ever legitimate. **Honestly**: re-run several
  times against the pre-§9 code (`_reconcileInFlight` + an absolute `snapshot + outstanding`
  detect target), it reproduced the class of defect — the client's own engine overshooting the
  expected total (one run: expected 12, landed at 14, a spurious extra report reaching the host) —
  on SOME runs and passed cleanly on others; it is a genuine timing race, not a deterministic
  repro, matching how §8.1 itself was only caught once in a full live-rig session, not on demand.
  Against the fixed (post-§9) code it has passed cleanly on every run tried. A first draft of this
  test that never touched the client's own outstanding ledger for the key at all passed even
  against the pre-§9 code on every run — that combination alone does not exercise the interaction
  §8.1's own mechanism depends on. A SEPARATE test-construction pitfall was also found and fixed
  while building this: firing the host-driven rounds before the host had even confirmed the
  client's own initial deposit raced this FAKE bridge's own absolute host-side writes against the
  host's real ADD-semantics accept-intent apply, double-counting by test construction alone (
  reproduced identically against the FIXED pump code too, which is what exposed it as a test bug
  rather than a production one) — fixed by waiting for that confirmation before the rapid rounds
  start, while still not waiting any longer than that.
- **`StoreHostSellMirrorsToClientEvenWithTheClientsGuiClosed`** (item 2's own repro/fix
  requirement) — host sells while the client's store GUI is NEVER opened for the whole test (
  `ClientAgent.Bridge.OpenStoreId` stays 0 throughout); asserts the client's own engine still
  gains the item and holds it. Fails against the pre-§9 early-return gate (no reconcile ever runs
  with the GUI closed); passes after the split.
- `StoreHostSellMirrorsToClientAndClientBuyBackReachesTheHost` /
  `ClientBuyBackAppliesToTheHostExactlyOnceAndStaysThatWay`: 300&#160;ms settle delay added
  before the buy-back step (§9.3).

### 9.6 Result

`dotnet build KotorCoop.slnx -warnaserror`: 0 warnings, 0 errors. The filtered suite
(`FullyQualifiedName~SharedInventoryV2|FullyQualifiedName~PartyPool|FullyQualifiedName~Store`, 276
tests): **all pass**, 0 failures, one run (5m35s — this family is socket/timing-heavy and runs with
assembly-wide test parallelism disabled, so its wall-clock cost is real, not a flake signal). The
full suite (`dotnet test tests/KotorCoop.Tests --no-build`): **2,513 tests, all pass, 0 failures**,
one run (8m41s). `party_pool_sync`/`store_sync` **stay OFF (mode 0) by default**, both explicitly
confirmed at 0 throughout — this pass is bench/loopback-only, no live rig; the next live round
should re-run §8's own recipe against a build containing this pass's fixes before either mode is
considered for a default flip.

**Files touched**: `src/KotorCoop.Session/Pumps/PartyPoolPumpV2.cs`,
`src/KotorCoop.Session/Pumps/StorePumpV2.cs`,
`tests/KotorCoop.Tests/SharedInventoryV2LoopbackTests.cs`, this document.

---

## 10. 2026-08-29, third rig round (against §9's fixes) — item 1: the exponential-doubling
## runaway class reproduces AGAIN, a spurious client-side "local add of 6x" landing on the
## host and a 4-minute non-self-healing divergence; item 2: a NEW, severe, unbounded
## re-insertion loop found on the client's store reconcile (every known key re-applied every
## ~10s, 432 total inserts for 18 keys over ~13 minutes, host clean at 0); item 3 not
## reached; item 4: full baseline PASS, plus a 9th, newly-reproducible `verify.ps1` failure

Task: re-run the queue's items 1/2 against `main` merged forward through §9's own fixes
(the belief/`_expectedFromReconcile` ledger replacing `_reconcileInFlight`, the store GUI-gate
split). Two-instance rig (`tools/demo.ps1`), both game directories freshly
`build.ps1 -Install -WarnAsError`'d (both deployed `dinput8.dll` 2,060,288 bytes / identical
timestamp `08/29/2026 06:43:18`; both `KotorCoop.Session.dll` confirmed by byte-scan to contain
`PartyPoolPumpV2`). `coop.ini` reset to `profiles/coop.ini.example` on both, then
`party_pool_sync=2`/`store_sync=2` plus `dialogue_item_sync=0`/`script_item_sync=0` isolated
from the start (per §6.1/§8's own lesson).

### 10.1 Item 1 — `party_pool_sync=2`: DISQUALIFIED again — the exponential-doubling class
### from §6 reproduces, not §8's narrower re-detection leak

`END_M01AA`, no warp. Ambient noise on both instances settled cleanly (each side independently
deposited `g_i_medeqpmnt01`×2, `g_i_adrnaline003`×1, `g_w_blstrrfl001`×1, `g_a_clothes01`×1,
`g_w_blstrpstl001`×1 — matching §2.4's predicted shape exactly, no ramp), confirmed stable for
90s before testing; host and client agreed at net +2 for `g_i_medeqpmnt01` going in. Three
rapid HOST `pooladd g_i_medeqpmnt01`s, each confirmed individually (serials 2/3/4, each
answering `added 'g_i_medeqpmnt01' x1 to the party pool`).

**Read directly from the host's own agent log**: the three legitimate `+1`s landed
(06:48:15, :20, :23), but sandwiched between the first and second was an entirely unprompted
`party pool v2: submitting add of 6x 'g_i_medeqpmnt01' through the party-pool channel` /
`added 'g_i_medeqpmnt01' x6 to the party pool` at 06:48:19 — six units nobody asked for. Host
net: 2 (ambient) + 1 + 6 + 1 + 1 = **11**, against an expected 2 + 3 = 5.

**The client's own log names the source precisely.** At 06:48:17 the client legitimately
reconciled `+3` toward the host's growing snapshot (`submitting add of 3x .../ added x3`) —
correct so far. In the SAME second it also attempted `submitting remove of 4x .../ the engine
refused it: 'g_i_medeqpmnt01' remove failed: no matching stack found` — a spurious REMOVE
request the engine itself caught and refused (harmless, but proof the client's own
detect/reconcile bookkeeping was already computing a nonsense target). One second later
(06:48:18): `party pool: local add of 6x 'g_i_medeqpmnt01' detected, requesting (serial 6)` —
a fabricated "local" deposit nobody made, published to the host and applied there (the exact
`+6` seen on the host's own log one second later). At 06:48:35 the client issued a partial
self-correction (`submitting remove of 3x .../ removed x3`), landing the client's own engine
back near 2, but the HOST had already applied the bogus `+6` and never received any correction
for it — the host and client's own engines were left permanently split.

**No further movement for 4+ minutes idle** (last activity 06:48:35, confirmed quiet through
06:52:36, four separate 20s polls) — the divergence is stable, not self-healing, matching
§6.2/§6.3's own verdict shape exactly ("stable, just stable on the wrong number"). No crash;
both instances stayed `Responding=True` throughout. This is the SAME exponential-doubling
runaway class §6 first found and §7/§9 each believed they had closed — §9's own
belief/`_expectedFromReconcile` redesign closed §8.1's narrower re-detection leak (confirmed
by its own passing loopback test), but this round shows a wider defect in the same
neighbourhood survives: a fabricated multi-unit "local add" appearing on the client with no
corresponding real engine movement to explain it. Root cause not pinned this round (out of
budget); the shape — a spurious detection landing exactly between two real, individually-
confirmed pooladds — is a strong clue that `_belief`/`rawMovement` computation is itself
racing against this session's own rapid successive `pooladd` calls in a way none of §9's own
loopback tests (which use a simulated, tick-based cheat channel, not this rig's real
single-slot arbitrated channel under real back-to-back console commands) reproduce.
`party_pool_sync` **stays OFF.**

### 10.2 Item 2 — `store_sync=2`: DISQUALIFIED — a NEW, severe, unbounded client-side
### re-insertion loop, independent of the client's own `openstore` block

Rig restarted clean: `party_pool_sync=0` (unsafe to run alongside item 1's still-open defect,
per §6.3's own precedent), `store_sync=2`, `dialogue_item_sync=0`/`script_item_sync=0` kept
isolated. ONE relocating `warp tar_m02aa` (this session's only warp, host only). Both landed
cleanly (`module tar_m02aa` on both agent gaits), no crash. The host's ambient `duros`
conversation trap and a brief incidental combat both cleared within two `skipline`/
`selectreply 0` rounds; `openstore Larrim_Store` then succeeded cleanly on the host.

**The client's own `openstore` never succeeded, for the ~13-minute session.** Every attempt
(9 retries across skipline/selectreply cycles, window activation via `movewin.ps1 -Restore`,
`topup`/`standup` on both players to rule out a downed-party modal) refused identically:
`openstore: 'Larrim_Store' refused - another modal screen is up (galaxy map / party select /
...)` (`CGuiInGame`+0x30 != 0, read directly from `Odyssey.Store.cs`'s `OpenStore`). Ruled out
directly: the party-select screen was swept away at 06:55:10, over two minutes before the
first `openstore` attempt (`party-select: client copy swept away`); no conversation was active
(`dialogue: skipline about to poke - no conversation here` on every attempt); both players sat
at `hp 12/12`, not down (`topup` answered `12 -> 12`, `standup` answered `you are already up`).
This is the same qualitative `tar_m02aa`-on-the-client hazard §8.2 already documented (a
persistent, unexplained GUI-busy flag blocking `openstore` specifically on the CLIENT side of
this module), reproduced a third time under different specific micro-conditions (no `tp`, no
active fight at the moment of refusal) — still not root-caused, still recorded rather than
chased further this round.

**A separate, NEW, and far more serious defect was found regardless of the `openstore` block:
the client's own store reconcile — which §9 item 2 deliberately made run "blind" (no GUI
needed) — re-applies the ENTIRE known store snapshot to its own engine roughly every 10
seconds, forever, with no sign of ever stopping.** Grepping the client's plugin log for a
single representative key (`g_i_belt006`) found **22 separate `store: inserted 'g_i_belt006'
... into store 0x74` lines**, one approximately every 10.1-10.9 seconds, from 06:57:13 to
07:00:51 (the observation window). Every one of the store's 18 distinct known resrefs shows
the IDENTICAL pattern — **24 insert events each, 432 total client-side insert events**,
confirmed by `grep -oE "store: inserted '[a-z_0-9]+'" | sort | uniq -c`. **The host's own
plugin log shows ZERO `store: inserted` lines for the entire session** — the host only ever
scans and publishes an idempotent snapshot, exactly per §2.1's design; this is unambiguously a
CLIENT-side reconcile defect, not a host re-publish or a wire-level duplication. This
contradicts §2.5's own "amplification is structurally impossible" argument in the same way
§6.2 did for the party pool: the client's reconcile step is never recognising "I already
applied this key's full amount" and keeps re-detecting the identical snapshot as an unexplained
gap, cycle after cycle, unboundedly — the store-channel sibling of item 1's own defect. No
crash; both instances stayed `Responding=True` throughout the entire 432-insert cascade.
`store_sync` **stays OFF.**

Not reached this round for lack of a working client store GUI and out of an abundance of
caution once the re-insertion loop was found: the host-sell-with-client-GUI-closed check, the
client buy-back check, and blackout+reconnect.

### 10.3 Item 3 — flip defaults: NOT REACHED. Both items 1 and 2 are freshly disqualified,
### by defects distinct from every prior round's own root cause (§9's fixes did not hold under
### this round's real single-slot-arbitrated-channel/real-GUI conditions, which no loopback
### test in this family currently reproduces). `profiles/coop.ini.example`, `CoopConfig`/
### `AgentSettings` defaults, `CONFIG.md`, and the README/roadmap/research-README rows are all
### unchanged — `party_pool_sync`/`store_sync` stay at their code default of 0 everywhere.

### 10.4 Item 4 — baseline: **PASS, with one new, reproducible `verify.ps1` finding flagged
### for follow-up.**

- `dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings/errors, against the
  freshly-fast-forward-merged `main` (`1002474`).
- `src/KotorCoop.Plugin/build.ps1 -Install -WarnAsError`: clean NativeAOT win-x86 build, both
  game directories freshly installed; both deployed `dinput8.dll` byte-identical (2,060,288
  bytes, same timestamp); both `KotorCoop.Session.dll` confirmed by byte-scan to contain
  `PartyPoolPumpV2`. A second standalone `build.ps1 -WarnAsError` re-run later in the session
  (no source changes) was also clean.
- `dotnet test tests/KotorCoop.Tests --no-build` (rig down): **2,512/2,513 passed** on the
  first run, one failure
  (`StorePendingApplyQueueConcurrencyTests.ThePendingApplyQueueSurvivesConcurrentEnqueueAndDequeueWithoutLosingOrDuplicatingEntries`,
  expected 0 got 136) — re-run alone immediately after, **passed cleanly (1/1)**, matching this
  repository's own documented environment-flaky-under-full-suite-parallel-load pattern (no
  source changes were made this session, so this is not attributable to anything in this
  round's own work).
- `tools/verify.ps1`, run TWICE against two INDEPENDENT freshly-restarted two-instance
  sessions (`coop.ini` reset to plain `profiles/coop.ini.example` on both sides each time):
  **both scored 40 passed, 9 failed, with the IDENTICAL 9 failures both times** — the 8
  previously-documented baseline failures (`phantom damageable, not a sink`; `the peer really
  loses those hit points`; `a client hit costs the enemy real hit points`; `a downed player is
  reported to the agent`; `the co-op revive stands the player back up`; `every engine call
  signature matches the binary` at 197/199; `a container opened on the client is held and shown
  open on the host`; `no per-frame spam`) **plus a 9th, newly-reproducible failure not in the
  prior documented set: `an item picked up here vanishes on the peer`**
  (`pickups issued 0 -> 1, client slot 0 state 1, host slot still state 1` on one run;
  `pickups issued 1 -> 2, client slot 1 state 1, host slot still state 1` on the other) — the
  client's own pickup is registered but the host's copy of the item slot never follows,
  reproduced identically across two separate fresh-rig sessions, so not a one-off flake. Total
  count (40/9) is still inside the documented 40-42/7-9 baseline range, but the SPECIFIC ninth
  failure is new and worth a dedicated look before the next round treats it as
  "known/expected." **A methodology note, not a product finding**: a THIRD `verify.ps1` run
  fired back-to-back against the SAME session without a rig restart scored worse (38/11, with
  different specific checks flipping pass/fail, e.g. `a container opened on the client is held
  and shown open on the host` passed on the un-restarted run but failed on both fresh runs) —
  `verify.ps1`'s own checks are not fully idempotent across repeated runs against one
  accumulating session; always restart the rig between runs when comparing against the
  documented baseline, not just between differently-configured rounds.

**Verdict**: `party_pool_sync`/`store_sync` **stay OFF (mode 0)**, unchanged from every prior
round. Both items are freshly disqualified by defects distinct in specific shape from every
earlier round's own root cause, in code that has already been through two dedicated bench-only
correctness passes (§7, §9) between live rounds — strong evidence that this family's own
loopback test suite, however thorough (286 tests across the two files), is not yet
reproducing the exact interleavings a real rig produces: real back-to-back console commands
arbitrated through the real single-slot cheat channel (item 1), and a real client GUI genuinely
never opening across a whole session (item 2). No crash in either round. **Concrete next steps,
ranked**: (a) build a rig-only diagnostic that logs `_belief`/`rawMovement`/
`_expectedFromReconcile` state on every tick around a real rapid `pooladd` burst, since §10.1's
spurious "local add of 6x" has no loopback repro yet to instrument instead; (b) instrument
`Pumps/StorePumpV2.cs`'s client-side reconcile to log why a key it already applied is
re-detected as still needing application on the very next cycle — the store re-insertion loop
in §10.2 is the single clearest, most reproducible repro this family has produced yet (24 clean
repetitions per key, 10-second period, 100% of keys) and should be the next round's first
target; (c) the client's persistent `tar_m02aa` `openstore` block (three rounds now, three
different specific micro-conditions, never once succeeding) may deserve its own dedicated
investigation independent of `store_sync` — every store-channel live round to date has been
partly or fully blocked by it.

**Files touched this round**: none — bench/rig-only verification pass, no source changes. This
document only.
