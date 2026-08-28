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
