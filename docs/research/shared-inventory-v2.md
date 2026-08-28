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
`party_pool_sync=0`/`store_sync=2` to get a clean read for the store channel instead (see the
session's own separate live-verification entry for that result). **Concrete next step**: root-cause
the doubling shape in `Pumps/PartyPoolPumpV2.cs` before any further live attempt — in particular
whether `OutstandingNet`/`ReconcileTarget` (§2.2/2.3, the exact functions the §5 "Bug 2" fix
touched) can itself be seeded from a value that already reflects a still-in-flight reconcile,
producing a target that is always ~2x the true delta.
