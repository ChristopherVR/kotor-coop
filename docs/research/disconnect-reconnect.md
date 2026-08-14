# Disconnect and reconnect (2026-08-14)

What actually happens when the link between two players fails, what was broken, what is
fixed, and how to reproduce any of it on demand — in a test or on the live rig — without
unplugging anything.

## 0. The distinction this whole document rests on

`docs/live-verification.md` records that "automatic reconnect works unattended": the host
**agent process** was killed and restarted, and the session healed with no action on the
client. That is true, and it is the *easy* failure. Killing a process closes its sockets. The
OS tells the other end. Both sides get a clean edge and a fresh session.

A dropped internet link gives no edge at all:

- the sockets stay open and healthy on both machines;
- packets simply stop arriving, in one or both directions;
- both ends keep transmitting into a hole and neither is told;
- the host's peer table expires the client after five seconds, while the client is still
  entirely convinced it is connected;
- then it all comes back, possibly from a different source port.

Every bug below lives in that second shape and **none of them in the first**, which is why
none of them had been seen. So the first thing this pass built was a way to produce the
second shape on demand.

## 1. The fault injector

`src/KotorCoop.Session/NetworkSim.cs`, mounted on `UdpChannel` (`channel.Sim`) and exposed on
`ICoopSession.Network`. It sits at the transport seam, so **every** channel goes through it —
the avatar stream, the reliable stores, the acks, the handshake, the digests — and arming it
partitions the whole peer rather than one feature.

- **Off by default and free when off.** The send and receive paths test one volatile bool and
  carry on. Nothing is allocated and no timer runs. A blackout is an absolute deadline against
  `SessionClock`, so it ends on its own and re-clears the bool the first time a packet is
  judged afterwards.
- **Faults:** loss percentage, added latency with jitter (which reorders packets, as a real
  link does), and a full blackout for a window — both directions or one.
- **Rules are swapped as one immutable record** under a lock, so a console thread changing
  them mid-flight can never be observed half-applied by a socket thread.

### Runtime verb

Registered in `ConsoleCommand.Parse`, `CoopCommandDispatcher.ApplyNetSim`, the plugin's
`FuncInterpHook.Verbs` (so the in-game `~` console routes it) and `ConsoleVerbCatalog` (so Tab
completes it). Applies to the **live session only**, so `disconnect` is always a way out of a
fault set by mistake.

```
coop netsim blackout 10          # cut everything for 10 s, both directions — the cable pull
coop netsim blackout 10 out      # upstream only — the half-open link (see §2.8)
coop netsim blackout 10 in       # downstream only
coop netsim loss 30              # 30% packet loss, both directions
coop netsim latency 120 40       # 120 ms of latency, +/- 40 ms of jitter
coop netsim status               # what is armed, and how many packets it has eaten
coop netsim off                  # clean link
```

The leading `coop` is optional (`netsim blackout 10` works), and `net`, `cut`, `drop`, `lag`
and `delay` are accepted aliases after it. A malformed line is refused with the correct shape
rather than half-applied — a typo in a command that deliberately breaks the network must never
be read as a different way of breaking it.

Tests drive the same object directly: `client.Network.StartBlackout(10_000)`.

## 2. The audit, and what it found

Read in full: `UdpChannel`, `CoopHost`, `CoopClient`, `SessionManager`, `CoopAgent`,
`SessionClock`, `SnapshotBuffer`, the four `Reliable*Sender`s, `EventFlagStore`,
`GroundItemStore`, `JournalStore`, `ContainerLootStore`, `InteractionLockStore`,
`EncounterTriggerStore`, `PeerDamageLedger`, `WorldTransfer`, `WorldAdoption`.

### Already correct — no change needed

Worth stating plainly, because it is most of the surface:

- **Peer expiry** on the host, and the sweep already released the dialogue token, the outbound
  phantom-damage rows, the encounter triggers and the interaction locks.
- **The client already noticed silence** and fell back to the handshake **with the same
  nonce**, and already dropped the conversation token on that path (a fix from an earlier
  live session).
- **Duplicate and reordered packets** are structurally safe on the world channels: flag and
  journal merges are a max, so replaying one is a no-op; op dedup by `(origin, sequence)`
  backs that up with a 4096-op history.
- **`PeerDamageLedger`'s three rules** already handled a peer's totals restarting (generation)
  and a reordered smaller total (ignore decreases).
- **The progression ledger's generation guard** already stopped a re-attached agent's totals
  being spent a second time.
- **`SIO_UDP_CONNRESET`** is handled — the single most common way a Windows UDP server dies.
- **The reliable senders did not grow without bound during a blackout**, though not by design:
  the client's tick returns early once it declares itself disconnected, so `CollectDue` stops
  being called at all. They did burn a fifth of their attempt budget in the five seconds
  before that.
- **World transfer / adoption is a startup path**, not a session one. A mid-session reconnect
  never re-stages a save, which is correct.

### 2.1 Peer identity did not survive an outage longer than the peer timeout — the big one

`HandleHello` matched a returning client by its nonce, but only against the **live** peer
table. A blackout longer than five seconds expires the entry, so the returning Hello matched
nothing and the same player was dealt a **brand new peer id**.

Peer id is the key of every cumulative channel in the session, so this is not cosmetic:

| Channel | What a new id did |
| --- | --- |
| `PeerDamageLedger` | `_seen[(peer, object)]` had no row, so `previous = 0` and the peer's **entire** running total was banked against the enemy again. Rules 2 and 3 cannot catch this: the total did not go backwards and the generation did not change — only the identity moved. |
| Incoming phantom damage | Rows are deliberately never removed (removing one walks a monotonic total backwards). A returning peer therefore **added a second running total on top of the first**, and the plugin spends the difference as real damage: a player takes every hit they have ever taken, again, in one tick. |
| Progression ledger | "First sight of this peer" adopts totals **without spending them**, so everything the peer earned or spent during the outage was silently dropped. |
| Every other client | The host's own buffer is cleaned at expiry, but a third player's copy was being refreshed by the relay right up to that moment, so it aged out a **further full timeout** later. A rejoin inside that window put two of the same player on screen. |
| `PeerSlotMap` (plugin) | A new id takes a new slot: the body, name, gear and damage total are rebuilt from scratch while the old slot lingers. |

**Fix.** `CoopHost` keeps a retired-identity table keyed by client nonce (`RetiredPeer`),
written on both timeout expiry and `Bye`. A Hello with a known nonce reclaims its id and its
name. Bounded at `MaxPeers * 8` departures, oldest dropped — and the failure mode of that bound
is "a very old identity is treated as a new player", which every cumulative channel handles
correctly (first sight adopts, never double-counts).

#### Why `Bye` retires too — the rule, since this was contested

The obvious objection is that a clean farewell should be final: the session announced "so-and-so
left", so a returning player is a new player. The premise does not survive contact with the
code.

- **What a departing player tears down is on *their* machine.** `SessionManager.Disconnect`
  disposes their session and calls `ClearCoopState`. On the host, nothing keyed on their peer id
  is discarded by the announcement — and two of the tables that matter are never discarded at
  all, deliberately: `_phantomDamageIn` (dropping a row walks a monotonic total backwards) and
  `CoopAgent`'s `PeerDamageLedger`, which is scoped to the **module** and knows nothing about
  sessions.
- **Their own counters survive as well.** The running total of damage they have dealt lives in
  the plugin's `DamageDealtTable`. `ClearCoopState` does not touch it — it clears the presence
  counts, the dialogue flag and the encounter request, nothing damage-related. The only thing
  that clears it is `PluginHost.InvalidateAll`, which fires on world unload, module change and a
  flushed transition. Never a disconnect. `Generation` is bumped in the same place, so it does
  not move either.

So a player who quits to the menu and rejoins **in the same module** reports the same cumulative
total under the same generation. Deal them a fresh id and the ledger finds no prior row, banks
all of it as new damage, and the enemy takes their entire historic contribution a second time —
with rule 3 unable to help, because the generation is exactly what did not change. The
third-party phantom-damage tables double the same way, and there the victim is a player's own
hit points.

**The invariant is therefore not "clean exit versus dropped link". It is: an identity must live
as long as the counters keyed on it, and those are scoped to the module, not to the
connection.** Where a departure genuinely does reset them — a module change, a restarted agent,
a new game — the returning player carries a **new nonce** (a fresh `CoopClient` mints one), so
nothing is reclaimed and every channel correctly treats them as first sight. "A farewell is
final" therefore remains true for every departure that actually resets anything; it is enforced
by the nonce, which knows, rather than by the Bye, which does not.

A corollary worth knowing: only a client object that *survived* can present a nonce that has
said Bye, and that is precisely the client whose counters survived with it — so this path is
nearly unreachable in production, and where it is reachable, reclaiming is the safe answer.
`ByeIdentityTests` constructs both halves.

### 2.2 Container-loot removals were destroyed the moment a client dropped

`ReliableLootSender.CollectDue` retired every pending op when the recipient list was empty —
and the recipient list goes empty exactly five seconds after a client's link fails. Every chest
the host emptied during the outage was retired **unsent**. The client came back, was never
told, and could take the same loot a second time: precisely the duplication the channel exists
to prevent.

Worse, there was no resync for this channel at all. It is the one world channel with **no
version tripwire** — a removal is announced once, reliably, and then never mentioned again — so
a peer that misses one misses it forever.

**Fix.** An empty recipient list now means "hold, there is nobody to tell yet", not "done".
On admitting a peer the host re-offers every removal it knows (`ResyncContainerLoot`) and
resets the backoff; recipients dedup on the `(container, item)` pair regardless of who
announced it, so re-offering costs the peers that already have them nothing.

### 2.3 The flag resync could walk a client's world backwards

The flag snapshot was applied with `ReplaceAll`. A resync fires precisely when the versions
disagree, which is precisely when the client is holding a change the host has not applied yet —
so the repair **erased the client's own advance**. Nothing recovered it: the bridge reports
only globals that *changed*, so a flag deleted from the store was never re-observed and never
re-announced. A door opened during an outage stayed shut for the other player permanently.

Two aggravating factors: the flag resync was the only one of the three **not** gated on the
sender being empty (items and journal both were), and the snapshot is capped at what fits one
datagram (~277 flags), so a large set had everything past the cap silently deleted.

**Fix.** `EventFlagStore.MergeSnapshot` — upward merge, never removing, the same shape the
journal has always used — plus the pending gate on the flag tripwire. The host's flag set only
ever grows, so nothing it omits is a deletion and nothing it sends can lower a local advance.

### 2.4 Reliable ops were abandoned after fifteen seconds

All three client senders gave up after 60 attempts at a flat 250 ms, with a comment saying "the
version-mismatch resync path will repair the state". It does not. The resync runs host→client;
the abandoned op is the client's own advance, which the host has never heard of. And because
the bridge only reports *changes*, nothing re-announces it. What the give-up was really
protecting against — unbounded memory, and four datagrams a second per op flooding into a
hole — is now handled properly.

**Fix.** Doubling backoff to a 4 s cap (`RetransmitBackoff`), a hard cap on the queue
(4096 ops), no give-up, and `ResetBackoff()` on reconnect so everything owed goes out at once
rather than trickling. A minute of silence costs ~600 attempts per op instead of ~6000, and the
op is still there to deliver.

### 2.5 Everything the player did while disconnected was dropped on the floor

`SetFlag`, `SetJournalEntry`, `DropItem`, `RemoveItem` and `PublishContainerLoot` were all
gated on `_localPeerId`, which is zeroed the instant the link-loss check fires. So for the
whole outage the agent kept pumping and every world change the player made was applied locally
and **never queued at all** — the reliable channel that exists to survive exactly this had
nothing in it to deliver.

**Fix.** A separate `_sessionPeerId` that is set on Welcome and never cleared. Changes are
queued under it while the link is down and sent when it returns.

### 2.6 Stale peers were never pruned while disconnected

`_buffer.PruneStale` sat at the bottom of the client's tick, past the `if (!IsConnected)` early
return. A client whose host vanished kept every peer it had ever seen frozen at their last
position, indefinitely — and the agent goes on writing those into the game, so the other player
stood there like a statue instead of disappearing. Pruning now also runs on the disconnected
path.

### 2.7 Clients aged departed peers out instead of believing the roster

The host's world snapshot is a complete level — everyone it knows about, every tick — so a peer
missing from it has gone. Clients were only aging them out on a timer, which is what created
the duplicate window in §2.1. `SnapshotBuffer.RetainOnly` now drops anyone absent from the
snapshot immediately.

Related: interpolating across a ten-second gap made a returning peer **slide** from where they
had been standing to where they are now, over one interpolation window. `SnapshotBuffer.Push`
now discards the pre-gap history when the gap exceeds a second, so a return is a teleport.

### 2.8 The half-open link left a permanent ghost

The nastiest one, and it needed the injector to find. If only the **upstream** direction fails
(NAT rebinding, a one-way firewall or ISP fault), the client keeps receiving the host's
broadcasts perfectly. Silence is its only disconnect signal, so it never fires. Meanwhile the
host hears nothing and expires the slot after five seconds.

The host then sends that client nothing at all — no snapshot, no roster, no token — because
every broadcast is addressed to current recipients. Except one: **it answered the ghost's
`Ping` with a `Pong`**, unconditionally, once a second, refreshing the ghost's own liveness
clock forever. The client sat there "connected as peer 2", streaming an avatar under an id the
host discards on arrival, rendering the world perfectly and invisible to everyone in it, with
nothing in its own state saying anything was wrong.

**Fix, three layers:**
1. The host only pongs a **known** peer — the same "only a known peer" rule every other
   peer-keyed handler already applies. Silence is the message.
2. The client no longer treats a `Pong` or an op ack as proof of membership: those are sent to
   anyone who asks. Only traffic the host sends to *members* refreshes the liveness clock.
3. Belt and braces: if the host's roster stops listing this client for longer than the peer
   timeout while its traffic keeps arriving, the client concludes it has been forgotten and
   re-handshakes.

### 2.9 A host with no avatar advertised itself as a living player

Raised by the concurrent network-topology review and taken here because it is the same
liveness question. `CoopHost.Tick` added the host's own snapshot entry unconditionally, and
`PlayerState.Empty` is all zeroes — which is finite, so it passes every plausibility check
downstream and reads as an unhurt, standing player at the origin. A host still at the main menu
therefore appeared to every client as an alive peer, and since a party wipe requires *every*
player to be down, `allDown` could never become true and the game-over suppression never
lifted.

**Fix.** The host advertises itself only once it has published an avatar. The symmetric case —
a *client* that has not loaded a world yet still sends state updates, because those are also
its keepalive — is handled at the consumer: `PumpDeath` treats a peer with no module as not a
participant in the fight (it cannot be down, and it cannot come and rescue anyone).

### 2.10 `OperationAborted` was not caught on the receive path

Also from the topology review. Disposing a socket with a receive in flight threw out of
`RunAsync`. Harmless under the normal cancel-then-dispose order, and immediately relevant once
tests start partitioning links and tearing sessions down aggressively. Now treated as the
shutdown it is.

## 3. The recovery sequence, exactly

Two players, defaults (30 Hz, `PeerTimeoutMs` 5000), the client's link cut in both directions
for ten seconds.

| t | Client | Host |
| --- | --- | --- |
| 0.0 s | link cut; keeps ticking, still believes it is connected | keeps broadcasting into the hole |
| 0–5 s | state updates, ops and pings all vanish; pending reliable ops retransmit into nothing and back off | hears nothing from the peer |
| 5.0 s | silence exceeds the timeout → `ForgetSession`: peer id cleared, conversation token dropped, `reconnecting`; starts sending Hello every 500 ms (also lost) | peer expires: **identity retired against its nonce**, removed from the table and the buffer; dialogue token released, phantom-damage rows, encounter triggers and locks forgotten; other clients drop it from their rosters within one snapshot |
| 5–10 s | world changes the player makes are still queued under `_sessionPeerId`; stale peers pruned; nothing rendered for the missing host | container-loot ops **hold** (no recipients); its own world changes bump the store versions |
| 10.0 s | blackout ends | |
| ~10.2 s | next Hello lands | nonce found in the retired table → **same peer id, same name**; container-loot removals re-offered and backoff reset |
| ~10.3 s | Welcome: peer id restored, all four senders' backoff reset, full resync armed | |
| ~10.4 s | pending flag/journal/item/loot ops all go out at once; host acks and merges them upward | |
| ~10.5 s | first world snapshot: peer buffer resumes (as a teleport, not a slide); once the senders are quiet, flag + item + journal snapshots are requested **unconditionally** | replies with all three |
| ~10.6 s | snapshots merged upward — nothing local is lowered or deleted | |
| steady | cumulative channels continue rather than restart: the damage ledger sees the same running total from the same peer id and banks nothing new; incoming phantom damage stays where it was | |

The forced resync on reconnect is deliberate and is not just belt and braces — see §4.1.

## 4. Failure modes that remain

### 4.1 The version tripwire is a counter, not a fingerprint

Each store's `Version` counts local changes. Two sets that diverged by the **same number** of
changes compare equal, and no resync is ever requested. Ordinarily the client's own ops bump
the host and break the tie, but an outage is exactly the window in which both ends change
independently, and a client change the host already holds at that value bumps nothing.

Mitigated, not eliminated: every (re)connection now demands all three snapshots regardless of
what the versions say, so any divergence created by an outage is repaired at the moment the
outage ends. A tie created *while connected* still persists until the next change on either
side moves a version. The real fix is a content digest in place of the counter — same field
width on the wire, so it costs no protocol change — and it is the obvious next step here.

### 4.2 The flag snapshot is capped at one datagram

~277 entries. A client behind by more than that receives the rest only as later changes arrive.
No longer destructive (the merge cannot delete), but still incomplete. The session store holds
only globals that changed *during the session*, so reaching the cap takes a long session.

### 4.3 `GroundItemStore.ApplyOp` is not monotonic

A `Drop` replayed after its `Remove` resurrects the item. It needs the op to fall out of the
4096-entry dedup history first, so it takes 4096 intervening ops from the same origin — but
unlike flags and journal, the merge itself offers no protection.

### 4.4 Identities age out after 64 departures

`MaxRetiredPeers` is `MaxPeers * 8`. A player whose identity has aged out that far comes back
as a new player: cumulative contributions from before are adopted, not spent. Loses a
contribution; never counts one twice.

### 4.5 A restarted agent is a genuinely new identity

`SessionManager.Reconnect` (and the `~ reconnect` verb, the Co-op button, a restarted agent
process) constructs a **new** `CoopClient` with a **new** nonce. That is a new player as far as
the host is concerned, and the cumulative channels treat it as one — first sight adopts totals
without spending them. Safe, and the right default, but it means the process-restart recovery
recorded in `live-verification.md` and the link-drop recovery described here are genuinely
different paths. Only the second keeps a peer's contributions.

This is not quite airtight, and it is the one place worth watching. A rebuilt *agent* mints a
new nonce, but the *plugin* keeps its `DamageDealtTable` — `InvalidateAll` fires on a module
change, not on an agent restart. So an agent restarted in place presents a new identity while
its game still holds the old running totals, and the host banks them again as first-sight
damage. The `PeerDamageLedger`'s generation rule cannot see it, for the same reason as above:
the generation is module-derived and did not change. The clean fix is for the ledger's
first-sight branch to adopt-without-spending the way the progression ledger already does, rather
than banking the opening total; it is a small change, it is not in this pass, and the exposure
today is bounded by how rarely an agent is restarted mid-module.

### 4.6 Latency simulation reorders packets

By design — that is what jitter does on a real link. Worth remembering when reading a
`netsim latency` run: any ordering anomaly is the fault being injected, not a new bug.

### 4.7 A snapshot that does not fit is not sent

A full lobby with long names can overrun `MaxPacketSize`; the host logs it once and sends no
snapshot that tick. Clients then receive no roster — but they still receive the dialogue,
roster and lock levels every tick, so the membership check in §2.8 does not false-positive.
The pre-existing overflow problem itself is untouched by this pass.

### 4.8 Loot is retransmit-only, so heavy loss has nothing to rescue a straggler

Observed at integration time, not during the pass that wrote this doc:
`HeavyPacketLossStillConverges` failed once in a full merged suite run (2 m 12 s wall clock)
and passed in the next (1 m 33 s) and in isolation (9 s). Every channel converged except
container loot — `clientLoot=0` with flags and journal both settled.

**The correlation is now measured, across five full-suite runs: it fails on the SLOW ones and
passes on the fast ones** — failures at 2 m 12 s and 2 m 16 s, passes at 1 m 33 s, 1 m 47 s
and 1 m 49 s, and it passes in 9 s alone. Suite wall-clock is the predictor, which is exactly
what the starvation explanation below requires: the 25 s budget is wall-clock but the attempts
inside it are ticks, and a loaded machine buys fewer of them. Treat a failure here as a signal
about machine load, not about the transport — but do NOT silence it by widening the budget,
because the underlying asymmetry it points at is real.

That asymmetry is not luck, it is the shape of the channel. Loot backs off 250 ms doubling to
a 4 s cap, which fits roughly nine or ten attempts into the test's 25 s budget; at 30 % loss in
both directions a send-plus-ack survives with p ≈ 0.49, so being unacked after nine attempts is
a ~0.3 % event — until the suite runs under enough parallel load to starve the 30 Hz tick,
which cuts the number of attempts that fit in the window and lifts that probability sharply.
**Flags and journal have a version tripwire and a divergence-repair path that catches a
straggler; loot has neither (§4.1 notes it is the one channel with no tripwire), so it is
retransmit-only and a run of bad luck has nothing behind it.**

This is a genuine, if modest, weakness rather than purely a test artifact: on a real link that
is bad for a sustained period, a loot removal is the op most likely to arrive late. It is not
*lost* — the sender keeps it until acked, and the (re)connect resync now covers the
outage-shaped case — so the world does not diverge permanently. Deliberately not "fixed" by
widening the test budget, which would only hide the property. The real fix is to give loot the
same content-fingerprint tripwire §4.1 proposes for the others, at which point a straggler is
repaired rather than merely retried.

## 5. Tests

`tests/KotorCoop.Tests/DisconnectRecoveryTests.cs` — real host, real client, real sockets, and
a real link failure produced by the injector.

| Test | Proves |
| --- | --- |
| `ATenSecondBlackoutHealsAndThePlayerComesBackAsTheSamePeer` | Ten seconds of nothing, both directions, twice the peer timeout. The client notices unaided, the host lets go, the client rejoins unaided **with the same peer id**, and exactly one phantom exists on each end afterwards. |
| `NoReliableWorldStateIsLostAcrossTheBlackout` | Flags, journal, a ground item and a container-loot removal, changed on both sides during the outage, are all present on both sides afterwards — and a flag both ends already agreed on has not regressed. |
| `TheWorldNeverWalksBackwardsThroughAResync` | Client advances a flag to 3 and a quest to 40 while the host holds 1 and 10. After the resync the client still holds 3 and 40, and the host has them too. |
| `ADamageLedgerDoesNotReBankAPeersTotalAfterAReconnect` | 40 damage before the outage is still 40 after it, through the real `PeerDamageLedger` fed from the real host. |
| `IncomingPhantomDamageIsNotDoubledByAReconnect` | The same arithmetic on the channel that hurts a player directly. |
| `AThirdPlayerNeverSeesTwoCopiesOfTheReconnectingPeer` | Three peers; the watcher's roster goes 2 → 1 → 2 and never 3, during or after. |
| `AOneWayOutageIsNoticedAndRepaired` | Upstream-only blackout: the host drops the peer, and the client works out from the roster and the silence that it has become a ghost, and re-handshakes to the same id. |
| `HeavyPacketLossStillConverges` | 30% loss on both ends: flags, journal and container loot converge both ways, the session never drops, and the injector confirms it really was eating packets. |
| `NetworkSimTests` | Disarmed until configured; total loss drops everything; a blackout ends on its own without a timer and re-clears the fast path; a directional blackout touches only its half; jitter stays in bounds. |
| `NetSimCommandTests` | The verb parses, and every malformed shape is refused with a reason. |

Two existing tests were **inverted on purpose**, because they asserted the bugs:
`RetransmitsStopAfterMaxAttempts` (now `AnUnackedOpSurvivesALongBlackoutAndBacksOff`) and
`AnOpWithNoRecipientsIsDroppedRatherThanQueuedForever` (now
`AnOpWithNoRecipientsWaitsForOneInsteadOfBeingDropped`).

These are real-time tests against real timeouts, so the suite went from ~13 s to ~90 s. That
is the price of testing a five-second expiry without faking the clock, and it buys tests that
exercise the shipping constants rather than a convenient set.

## 6. NEEDS-LIVE

Nothing here required the game, and nothing here was run against it. The following want a live
pair on the rig. Both instances need the rebuilt plugin (`build.ps1 -Install`) because the
console verb list changed.

1. **`netsim` reaches the co-op layer at all.** In-game `~`, type `netsim status`. Expect
   `netsim off (clean link)` on the overlay. If instead the engine says the command is unknown,
   `FuncInterpHook.Verbs` did not take. Also check Tab completion offers `netsim`.
2. **The ten-second cable pull.** With both players in a module and visible to each other, run
   `netsim blackout 10` on the CLIENT. Expect: the phantom stops moving; within ~5 s the client
   logs the disconnect and the host's roster loses the player; at ~10 s it rejoins by itself;
   `who` shows one entry per player and the peer keeps its name. The thing to watch for is
   **two bodies** — that is the bug this pass exists to close.
3. **Open a door during the blackout, on each side in turn.** It must be open for both
   afterwards. Then a quest advance the same way.
4. **Empty a chest on the HOST during a client blackout.** After the rejoin, that chest must be
   empty in the client's game too. This is the loot-duplication path and the one with no
   tripwire to repair it.
5. **The half-open link.** `netsim blackout 15 out` on the client. Expect the client to *keep
   rendering the world* for about five seconds, then notice, then rejoin. Before this pass it
   stayed "connected" and invisible forever, so a stuck-but-connected client is the regression
   signature.
6. **Reconnect mid-fight.** Damage an enemy on the client, blackout 10, rejoin, and watch the
   enemy's hit points on the HOST. They must not drop by the client's whole contribution a
   second time when it returns.
7. **Take a hit, then reconnect.** With the client's phantom having taken damage on the host,
   blackout and rejoin, and watch the client's own hit points. A sudden second helping of
   damage on return is the phantom-damage double-count.
8. **A lossy link is not a disconnect.** `netsim loss 30` for a couple of minutes of ordinary
   play: movement should be visibly rougher, the session should never drop, and doors and
   quests should still converge.
9. **Confirm the fault is really off afterwards.** `netsim off`, then `netsim status`. A
   blackout ends on its own, but leaving loss armed would make everything after it look broken.

## 7. Protocol version

**Unchanged, deliberately: still 23.** Nothing was added to, removed from or reinterpreted on
the wire. Every fix is in how the two ends *use* packets they already exchanged — which
identity a Hello reclaims, whether an unacked op is kept, whether a snapshot is merged or
overwritten, whether a stranger's ping is answered, and which packets prove membership. An old
peer and a new peer still understand each other's bytes exactly as before; the new peer is
simply the one that recovers.
