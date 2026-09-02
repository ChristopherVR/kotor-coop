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

## 8. A content digest closes §4.1 and §4.8 (2026-08-21, NOT live-verified)

Both remaining gaps §4 named — the version tripwire being a counter rather than a fingerprint,
and container loot having no tripwire at all — share one fix, exactly as §4.1 predicted: "the
real fix is a content digest in place of the counter... and it is the obvious next step here."

**The mechanism.** `KotorCoop.Session.EntryDigest` is a per-entry 64-bit FNV-1a hash, combined
across a whole store by XOR rather than by concatenation-and-rehash. XOR being its own inverse
is the whole design: a store updates its digest in O(1) on every mutation — `digest ^=
Hash(key, old); digest ^= Hash(key, new)` — without ever rehashing its full content, and the
result is order-independent by construction, which a straight rolling hash would not be. Every
one of the four world stores now carries a `Digest` beside its `Version`:
`EventFlagStore`, `JournalStore`, `GroundItemStore` (all three previously had only a `Version`)
and `ContainerLootStore`, which had **neither** — §4.8's "no version tripwire" is now literally
untrue, and it also gets the digest in the same pass rather than as a second mechanism.

**Why beside the counter, not instead of it.** The counter is still the cheap common-case
check — most divergence changes the count — and is retained so a peer that is merely BEHIND
(not tied) is not paying a hash comparison it did not need. The digest exists purely to catch
what the counter structurally cannot: two stores that reached the same version by making the
same NUMBER of changes, holding different content. The tripwire on every channel is now
`version != version || digest != digest`, checked in `CoopClient.HandleWorldSnapshot`.

**The wire.** `WorldSnapshotPacket` gains four `ulong` digests (`FlagDigest`, `ItemDigest`,
`JournalDigest`, `LootDigest`) and one new `uint` (`LootVersion` — loot had no version to
carry before this). Container loot also gets a new packet, `LootSyncRequest` (43): client to
host, "my loot version or digest disagrees with your last snapshot, resync me." Unlike
`FlagSyncRequest`/`ItemSyncRequest`/`JournalSyncRequest` it has no matching `...Snapshot` reply
— loot's "full resync" already exists and is better than a one-shot snapshot would be: it is
`CoopHost.ResyncContainerLoot`, the same reliable retransmit-until-every-peer-acks path §2.2's
fix built for peer admission. `HandleLootSyncRequest` just calls it again. Protocol version is
now **26** (`ProtocolConstants.cs` carries the full field-by-field rationale).

**What this actually buys container loot.** Before this pass the channel was retransmit-only:
correct against loss (an op is never abandoned) but with nothing to notice a straggler that
had, for whatever reason, ended up holding different content than the host believes it holds.
Now a still-connected client with a stale or wrong loot set finds out on the very next world
snapshot — the same tick-by-tick check the other three channels have always had — rather than
only at the moment it happens to reconnect (§2.2) or be freshly admitted.

**What it does not buy.** `ContainerLootStore`'s digest tracks the retained 512-removal
window, the same window `Snapshot()`/`Contains()` already use — not the store's full lifetime
history. Two peers whose windows evicted different oldest entries (possible only after 512
removals in one session, with different arrival order on each side) could see a digest
mismatch with no real divergence in effect, since an evicted entry was already applied on both
sides before it aged out. The cost of that false positive is a wasted `LootSyncRequest` and a
`ResyncContainerLoot` that finds nothing new to say — not a correctness problem, just unneeded
traffic, and only reachable at all in a session far longer than any tested.

**Tests**, all in `tests/KotorCoop.Tests/`: `EventFlagStoreTests`, `JournalStoreTests`,
`GroundItemStoreTests` and `ContainerLootTests` each gained digest-order-invariance and
equal-version-different-content tests (the latter is the whole reason for the digest, so it is
the one every store's suite proves directly); `ContainerLootTests` also covers the new
`Version` counter and `Clear()` resetting both. `PacketCodecTests` round-trips the wider
`WorldSnapshotPacket` and the new `LootSyncRequestPacket`. `DisconnectRecoveryTests` gained
`ContainerLootConvergesByVersionAndDigestAcrossADeterministicBlackout` — modelled on
`NoReliableWorldStateIsLostAcrossTheBlackout`/`HeavyPacketLossStillConverges` but using a
`StartBlackout` deadline rather than `SetLoss`, deliberately: §4.8 itself measured
`HeavyPacketLossStillConverges` as flaky under machine load (a percentage-loss budget is
wall-clock, the attempts inside it are ticks), and a blackout's deadline is absolute against
`SessionClock`, so this proves the same convergence property without depending on how busy the
machine running the suite happens to be.

**NEEDS-LIVE.** Nothing in this section was run against the game or the rig. In particular:
whether a real two-instance session actually negotiates protocol version 26 (both sides need
the rebuilt plugin/agent — a version mismatch is refused at Hello, per `HandleHello`), and
whether `netsim blackout` on the live rig with a chest emptied mid-outage still shows the
chest empty for the reconnecting player (item 4 in §6's existing NEEDS-LIVE list already
covers the scenario; it now also exercises the digest path, not just the retransmit path,
though the two are not distinguishable by eye on the rig — only in the test above, which
constructs the tie directly).

## 9. Away flag: quitting to the main menu is not a link failure (2026-08-27, NEEDS-LIVE)

Everything above this section is about the transport lying dormant while the process, and the
game underneath it, stay alive and reachable. There is a third shape neither §0's "process
killed" nor "link dropped" covers: **the agent process is alive and still pinging, but the
player is not in the world at all** — they quit to the main menu, are sitting at a load
screen, or are on the character/export screen. `CoopAgent.PumpOnce`'s `TryReadLocalState`
simply has no player object to sample.

Before this pass that looked exactly like nothing happening: the read failed, `PumpOnce` fell
back to `LastLocalState` and kept silently doing so forever, the session's own snapshot
buffer kept re-sending that last known avatar because nothing told it to stop, and the peer
timeout (`ProtocolConstants.PeerTimeoutMs`) never fired because the agent was, correctly,
still alive and still talking. The partner's screen showed a **frozen statue standing exactly
where the player last was**, indefinitely, with no signal distinguishing "briefly alt-tabbed"
from "quit twenty minutes ago" from "the link actually died".

**The mechanism.** `KotorCoop.Session.AwayRule` is the pure threshold: `CoopAgent` now counts
*consecutive* failed reads (reset to 0 the instant one succeeds — a separate counter from the
pre-existing, cumulative `ReadFailureCount`) and treats a run reaching `AwayRule.Threshold`
(scaled to the configured poll rate to keep a ~1 s wall-clock window, the same
tick-vs-wall-clock lesson the heavy-pump cadence already learned) as genuinely away rather
than a transient blip — a module mid-store or an object freed between two reads recovers
within a tick or two and must never flip this. Once the threshold is crossed, `PumpOnce`
publishes the *last known* `PlayerState` with the new `PlayerState.FlagAway` bit set, and
keeps republishing it every subsequent tick for as long as reads keep failing — peers must
keep hearing from an away player, not merely be told once. The instant a read succeeds again,
the flag clears on that same tick's fresh state.

**Wire.** `FlagAway` (64) rides the existing `PlayerState.Flags` dword exactly the way
`FlagInMinigame` does — no `PacketCodec` shape change, since `WritePlayerState`/
`ReadPlayerState` already carry the whole dword verbatim. Protocol version is now **39**.

**Hiding the phantom reuses the minigame path exactly.** An away peer gets the identical
"park, do not mirror" treatment `FlagInMinigame` already has: `CoopAgent.PumpOnce` filters
`p.State.IsAway` peers out of what it hands to `TryWriteRemotePeers`, the same "simply
absent" idiom the minigame/module filters already use. On the plugin side that is enough by
itself — `PhantomController.OnFrame` destroys any slot whose `RemoteView.Active` goes false
(`WriteToPlugin` writes 0 to a slot's active dword for any peer missing from the write), the
same "peer left" path a genuine disconnect already takes — so no plugin-side gating code was
needed beyond mirroring the flag constant onto `SharedBlockView.RemoteView` for a future
consumer. Unlike the minigame filter, there is no coop.ini escape hatch for this: a frozen
statue was never anyone's intended experience of "away", so there is nothing to opt back
into.

**Local channels stop publishing stale derived state too.** `PartyPump.PumpProgression` and
`WorldStatePump.PumpJournal` are gated on `CoopAgent.LastReadSucceeded` at their call sites —
both already fail their own internal bridge reads gracefully while genuinely at the main
menu, so this is defence in depth rather than the only guard, but it stops a stale republish
riding the heavy pass during the ~1 s grace window before the away flag itself latches.

**Toast.** `KotorCoop.Agent.AwayAnnouncer`, the same shape as `PeerAnnouncer` and
`ConnectionStatusAnnouncer` (a diff over the last observation, announced only on change) but
deliberately a *separate* diff from `PeerAnnouncer`'s join/leave: going away is a presence
change, not a roster change — the peer is still admitted and still counted. Posts
`"<name> left the world (main menu / loading)"` on the away edge and `"<name> is back"` on
the clear, through the same `Reply` path every other toast uses, wired into the existing
`AnnouncePeerChanges` per-tick pass (`CoopCommandDispatcher.Announcers.cs`) rather than a new
call site.

**Tests.** `AwayRuleTests` (the pure threshold, including the poll-rate scaling and the
zero/negative-poll-rate fallback), `AwayStateTests` (`CoopAgent.PumpOnce` end to end: a brief
blip publishes nothing, a sustained drought publishes-and-keeps-publishing the away state, a
resumed read clears it on the very next tick, and an away peer is omitted from
`TryWriteRemotePeers`), `PacketCodecTests` (`AwayFlagRoundTripsIndependentlyOfTheOtherPresenceFlags`
/ `ClearingTheAwayFlagRoundTrips`, modelled on the existing minigame-flag pins),
`SessionIntegrationTests.AnAwayPeerIsSeenAsAwayByTheOtherSide` (real host, real client, real
sockets — the one thing a codec-level round trip cannot prove: that `RemotePeer` as handed out
by the OTHER side's session actually carries the bit), and `AwayAnnouncerTests` (both toast
edges, a peer already away on first observation announcing nothing since there is no edge to
fire on yet, and a peer leaving the roster while away announcing nothing of its own since
`PeerAnnouncer` already covers "left the game").

**NEEDS-LIVE.** Nothing in this section was run against the game or the rig — the phantom-hide
path in particular is inferred from reading `PhantomController.OnFrame`/`WriteToPlugin`, not
watched happen. Confirm on the rig: quit one instance to the main menu and watch the other
instance's phantom for that player actually despawn (not just stop moving) within about a
second, watch both screens for the "left the world" / "is back" toast on the way out and the
way back in, and confirm reloading a save or crossing to the character-export screen does not
itself spuriously trip the flag mid-transition (a save load briefly has no player object
either, and the ~1 s threshold exists specifically to survive that without live confirmation
it actually does on real timings).

## 10. Live pass (2026-08-27): FlagAway — Live result: FAILED, and exactly why

### The blocker this round had to solve first: no coordinate-free way to reach the main menu

Every prior attempt at this on the rig hit the same wall (`session-save.md`'s "Attempted, not
achieved" section): `demo.ps1`-repositioned windows do not accept injected clicks (0 of 36
candidate clicks landed), and blind keyboard-only pause-menu navigation once wedged an instance
badly enough to need a full rig restart. This round built a coordinate-free primitive instead of
retrying either: a new dev-only console verb `quittomenu` (`CheatKinds.QuitToMenu` = 25,
`SharedBlock.CheatKindQuitToMenu`, `CoopCommandKind.QuitToMenu`) that calls
`CClientExoAppInternal::ShutDownToMainMenu` (0x005FCBD0) directly —
the SAME function `CClientExoAppInternal::StallServerCode` (0x00602CDE) calls once
`CSWGuiInGameOptions::ReallyQuit`'s `SetEndGamePending(1,0,1)` is noticed, i.e. exactly what a
real "Quit Game → Yes" click reaches, confirmed via `xrefs.py` (its only two callers are that
site and `CClientExoApp::ShutDownToMainMenu`'s thin jmp-forward wrapper). Thiscall, one argument
(`this`), bare `ret`, verified by disassembly before shipping. New files touched:
`src/KotorCoop.Plugin/Odyssey.Menu.cs` (the engine-call wrapper), `CheatController.World.cs`/
`CheatController.cs` (the cheat handler), `SharedBlock.cs`/`IGameBridge.cs` (the kind constant,
both halves), `ConsoleCommand.cs`/`ConsoleCommand.Cheats.cs` (the verb), `CoopCommandDispatcher.cs`
(the agent-side send), `docs/CONSOLE.md`. `dotnet test tests/KotorCoop.Tests`: 1979/1979 green
(one coverage-test row added, `ConsoleCommandVerbTableCoverageTests`). Local only, blockpoke-only
by convention (deliberately absent from `FuncInterpHook.Verbs`/`ConsoleVerbCatalog`, matching
`skipline`/`selectreply`/`loadgame`/`walkto`/`recruit` — a real player should never see it).

### What actually happened

Both instances connected in `END_M01AA` (block version 53). `pwsh tools\blockpoke.ps1 -ProcId
<client> console 'quittomenu'` at 18:28:49.8 answered `quit to main menu requested`. The CLIENT's
own plugin log confirms a clean, fast, crash-free teardown:

```
[18:28:50.677] quittomenu: tearing down to the main menu (CClientExoAppInternal::ShutDownToMainMenu)
[18:28:50.729] cheat: quit to main menu requested (status 1)
[18:28:50.730] menu: Co-op button shown, labelled and bound        <- proves the main menu is really up
[18:28:50.733] world: unannounced teardown - treating as a load, not a transition
[18:28:50.733] epoch: world unloading; clearing phantom, item, combat and door state
[18:28:50.751] agent reports 0 remote peer(s)
[18:28:50.751] 0 phantom(s) alive
```

Both `swkotor.exe` processes and both `KotorCoop.Agent.exe` processes stayed up and responding
throughout — no crash, no hang. The HOST's own plugin block read `phantoms 0  remotes 0` within
20 seconds of the call (block state), and its own phantom controller had already dropped the
client's slot. **On the surface this looks exactly like FlagAway working.** It is not.

### The client's own agent never saw a failed read — so `AwayRule` never latched

The client's own `agent2.log` (the log that actually decides whether `IsAway` fires, per
`CoopAgent.PumpOnce`) tells a different story:

```
[18:28:50.689] [resolve] player 0x1960A858: vtable 0x7457A0 != 0x7470C8      <- ONE failure, the instant of teardown
[18:28:51.110] [resolve] TryFindPlugin: scan found 1 candidate(s): 0x17E50000
[18:28:51.175] [resolve] TryFindPlugin: 0x17E50000 heartbeat 1937 -> 1940 (second read ok=True, advanced=True)
[18:28:51] * quit to main menu requested
[18:28:52] * local combat diag (agent): moduleClean=False censusModule='area1' local.Module='area1'
[18:28:52] connected to 127.0.0.1:27140 as peer 2  peers 1  ...  module area1  hp 30/30  ...
```

and it kept reading `module='area1' hp 30/30` successfully, once or more per second, for the
entire observed window — **over two minutes** (18:28:52 through 18:30:55, the last line
captured). `TryReadLocalState` never failed a second time. Consequently:

- `AwayRule.IsAway`'s consecutive-failure counter reached 1 and then reset to 0 on the very next
  tick — nowhere near `AwayRule.Threshold`.
- `IsAway` never became true. **No `away:` log line, no "left the world" toast, no "is back"
  toast, on either instance, across the whole window.**
- The HOST's `agent1.log` shows exactly why nothing downstream noticed either: `peers 1` and
  `peers-here 1` at 18:28:52, 18:29:22, 18:29:52, 18:30:22 and 18:30:52 — unchanged for the
  entire 2+ minutes. The session-level roster never registered anything wrong.

**The phantom disappearing on the host was not FlagAway.** It was the pre-existing, unrelated
"peer not in this module" filter every remote-peer write already carries (the same "same module
gate" `IEnemySession.cs`/`WorldStatePump` document elsewhere): the client kept publishing a
`PlayerState` whose module read `'area1'`, which never matches the host's `'END_M01AA'`, so the
peer was filtered out of `TryWriteRemotePeers` and its phantom slot went inactive — coincidentally
producing the same visible symptom (phantom gone) FlagAway is designed to produce, for a
completely different, already-shipped reason.

### Root cause, located precisely

`'area1'` is not garbage and not a coincidence. `KotorGameBridge.Encounters.cs` already has a
named helper for exactly this value:

```csharp
/// True for "area1"/"area1F"-style names: what the module reads as while a transition is
/// in flight. Not a module on disk — an object id wearing a module's clothes — and every
/// pointer the scan holds while one is showing belongs to a world being freed.
private static bool IsTransitionalModuleName(string module) { ... }
```

— built for, and only used by, the encounter-disarm feature (`KotorGameBridge.Encounters.cs:107`).
**`TryReadLocalState` (`KotorGameBridge.Player.cs`) never calls it.** What happens instead,
traced against that file's own code: the cached player pointer correctly fails
`StillLooksLikeThePlayer` the instant the world tears down (the single `vtable 0x7457A0 !=
0x7470C8` line above) and `_player` is set to `null`; the very next tick's fallback,
`TryResolve` (`KotorGameBridge.Attach.cs`, a pattern scan of committed memory), reacquires
*something* that passes every cheap validation the scan applies — a body whose module field reads
`'area1'` and whose hp reads `30/30` (neither value matches this session's real PC, who was at
`hp 12/12` in `END_M01AA`) — and adopts it as the player. The file's own doc comment already
names this exact failure class in the abstract ("the scan can latch a FREED COPY of the
player... every cheap validation passes while its position and health stay frozen"); this round
is the first live measurement of it firing specifically off a genuine main-menu teardown rather
than a module transition.

### Verdict

**Live result: FAILED.** The Away flag's third shape (main menu / load screen / character-export
screen — no player object at all) does not reliably latch on this engine build, because the
local read that is supposed to fail instead succeeds with transitional-looking data the codebase
already has a name and a detector for (`IsTransitionalModuleName`) but does not apply here. No
`coop.ini` default exists to flip for this item — `FlagAway` is unconditional agent behaviour,
not gated by a key — so nothing was flipped ON.

**The fix, not attempted this round** (scope): gate `KotorGameBridge.Player.cs`'s
`StillLooksLikeThePlayer`/`TryReadLocalState` on `IsTransitionalModuleName` the same way
`KotorGameBridge.Encounters.cs` already does — promote the helper out of the Encounters partial
into a shared location (e.g. `KotorGameBridge.cs` proper) and treat a transitional-name module
exactly like a failed read, so `AwayRule`'s counter actually advances. Until that lands, a real
"quit to the main menu" is silently misreported to peers as "still in `END_M01AA` at whatever the
scan's frozen copy last held" rather than either a clean Away or a clean disconnect — worth
flagging in `docs/roadmap-seamless.md` as a Tier 1 item alongside the container-loot race (§6 of
the 2026-08-27 rig pass in `docs/live-verification.md`).

The `quittomenu` primitive itself is reusable for the next round straight away — no rig-tooling
work is needed to reproduce or re-check this.

## Live pass (2026-08-27, follow-up round): the fix — PASS

Fixes the root cause §10 located: `IsTransitionalModuleName` (promoted out of the
`Encounters` partial into `KotorGameBridge.cs` proper) is now checked in
`TryReadLocalState` (`KotorGameBridge.Player.cs`), so a transitional-named read
(`'area1'`-style) is treated exactly like a failed read — `OnFailure()` runs and
`TryReadLocalState` returns `false` — instead of being silently adopted as a successful
read of a ghost. Re-ran the exact `quittomenu` recipe §10 built, against the freshly
built plugin/agent carrying the fix.

### quittomenu (host 47436, client 43472, both in END_M01AA)

`blockpoke.ps1 -ProcId 43472 console 'quittomenu'` at 19:45:37.8. The client's own
`agent2.log` shows what §10's live pass never saw — the failure counter actually
climbing:

```
[19:45:44] ... memory READ-FAILING(21) ...
[19:45:46] ... memory READ-FAILING(40) ...
[19:45:48] ... memory READ-FAILING(50) ...
[19:45:50] * away: local reads have failed for ~1s; publishing last known state as away
```

Both toasts fired on the SAME tick, on the two different processes:

```
agent2 (client): [19:45:50] * away: local reads have failed for ~1s; publishing last known state as away
agent1 (host):   [19:45:50] * Malak left the world (main menu / loading)
```

Host's own block confirmed the phantom actually despawned (not just the toast):
`pwsh tools\blockpoke.ps1 -ProcId 47436 state` → `phantoms 0  remotes 0`.

**PASS.** The wall-clock time from `quittomenu` to the away toast (~13s) ran longer than
the ~1-2s the design's threshold targets — this rig's actually-achieved pump rate is
well below the nominal 30 Hz on this run (see the READ-FAILING count climbing by only
~7-8/s, not ~30/s), a pre-existing environmental characteristic of this machine/session
rather than anything this fix changed, and outside this round's scope to chase. What
matters for this fix is the MECHANISM: consecutive failures now accumulate instead of
resetting to 0 every tick, so `AwayRule`'s threshold is genuinely reachable.

### Recovery — PASS

`blockpoke.ps1 -ProcId 43472 console 'loadgame 0'` → `loadgame: the world is up`. Host:
`[19:46:42] * Malak is back`, then `phantoms 1  remotes 1` — the phantom respawned.

### A real transition does not trip Away — PASS

`blockpoke.ps1 -ProcId 43472 console 'warp end_m01ab'` (a genuine module transition,
distinct from `quittomenu`) completed in ~12s with the module correctly settling to
`end_m01ab` on both sides. No new `away:`/`is back` line appeared anywhere in either
agent log for this transition — the host's log shows `* Malak left the game` /
`* Malak joined the game` instead, the PRE-EXISTING module-filter roster mechanism
(`PeerAnnouncer`), exactly the "coincidentally producing the same visible symptom, for a
completely different, already-shipped reason" case §10 itself named. Confirms the
`IsTransitionalModuleName` fallback firing briefly during an ordinary transition does
not, on its own, cross `AwayRule`'s ~1s threshold.

**Verdict: FIXED and live-confirmed.** All three NEEDS-LIVE items from §9's own list are
answered: the phantom despawns within the away-toast window (not instantly, but promptly
relative to this run's own achieved pump rate), both toasts fire, a resumed read clears
the flag and respawns the phantom, and neither a `loadgame` reload nor a `warp` module
transition spuriously trips it.

## 11. Host loss: releasing plugin-side locks when the CLIENT loses its host (2026-08-29)

### The bug this closes

§3's recovery sequence above already covers the wire and world-state side of an outage
correctly — the client re-Hellos, the host retires and reclaims the identity, everything
reliable converges. What it does **not** cover is what happens to the *game itself* on the
client's machine while the outage lasts, and that turned out to be broken in a way severe
enough to make solo play impossible for the whole outage:

- **The client's own control lock never let go.** `CoopAgent.PumpOnce` called
  `KotorGameBridge.LockControlToPlayer` every tick whenever `SyncParty` and party capability
  were on, with no check on connection state at all. The moment the host vanished this became
  a tight loop reverting the player's own companion selection back to their PC every single
  tick, forever — a companion was permanently unclickable, not just briefly.
- **The plugin's whole `ReleaseSession` list never ran.** `PluginHost.SessionLifecycle`'s
  release (control-swap suppression, party-select suppression, follow mirroring/`ClaimCreature`
  give-back so companions resume their own AI, solo-segment spectate, dialogue spectate,
  minigame lock, ...) is keyed on `SharedBlock.Command`'s *falling* edge. But
  `KotorGameBridge.TryWriteRemotePeers` writes `Command = CommandSpawnAndDrive` unconditionally
  every tick it runs, and it kept running — with an empty peer list — throughout the whole
  outage, because nothing ever told it to stop. The falling edge the plugin was waiting for
  never happened, so nothing released: `ClearCoopState` (the one place that lowers `Command`,
  alongside every other session-scoped block level) only ever ran on a *full* `SessionManager`
  teardown, never on the client's own silent "still trying to reconnect" state.
- **A crashed host gave the client no warning at all.** The only signal was silence past
  `PeerTimeoutMs` (5000 ms) — correct for a crash, but a factor of the same 5 s of needlessly
  locked play even for a *clean* host shutdown, which could have told the client immediately.

Net effect: from the moment the host disappeared until the player manually disconnected (never
automatically), every lock the plugin holds on the host's behalf stayed held, on a game the
host was no longer part of.

### The fix

Two new, independent pieces, both client-side (a host is never "lost" to itself,
`ICoopSession.HostLost` defaults to `false` and `CoopHost` never overrides it):

1. **`CoopClient.HostLost`** (`IPeerSession.HostLost`) — a new signal, distinct from
   `IsConnected`/`Reconnecting`. It latches on whichever of two triggers fires first:
   sustained silence past `SessionSettings.HostLossTimeoutMs` (`HostLossRule.ShouldDeclare`,
   the pure decision — see `HostLossRuleTests`), or an explicit `PacketType.HostBye` the host
   now broadcasts to every connected peer the instant its own tick loop stops cleanly (never on
   a crash — there is nothing left to send it with). `HostLossTimeoutMs` defaults to the exact
   same value as `PeerTimeoutMs` (5000), so out of the box `HostLost` latches in the *same tick*
   the existing reconnect logic already gives up and starts re-Helloing — no new delay is
   introduced, a release that used to never happen at all now does. `HostByePacket` closes the
   "crashed host" vs "host shut down cleanly" gap: the clean case is now near-instant instead
   of waiting the full 5 s (see `HostLossTests.AnExplicitHostByeDeclaresHostLossFarFasterThanTheSilenceTimeout`).
   Cleared the instant a fresh `Welcome` readmits the client — the same tick the ordinary
   rejoin/re-mirror path (§3's table) starts running again.

2. **`CoopAgent.PumpOnce` reacts to the edge, once.** On the rising edge it calls
   `KotorGameBridge.ClearCoopState()` — the *same* call `SessionManager.TearDownSession` already
   makes on every ordinary disconnect, reused rather than duplicated, so the release path stays
   the one already proven correct there — and posts a HUD/chat notice
   ("Host connection lost - playing solo, will rejoin automatically.") through
   `IMenuBridge.PostChatLine`. Then, for as long as `HostLost` stays true, the pump returns
   immediately after publishing the local player's own state (so a rejoin has fresh data) —
   no remote-peer writes, no `LockControlToPlayer`, no world-state channel pumped against a
   host that is not there. The session's own `RunAsync` loop (a separate task) keeps
   re-Helloing in the background throughout, completely independent of whether this pump runs
   at all. The falling edge needs no special handling: `PumpOnce` simply resumes its normal
   body on the next tick, exactly as if nothing had happened, and posts the "restored" chat
   line.

Both are gated by `coop.ini` `host_loss_release` (ON by default) — OFF restores the exact
pre-existing behaviour (every lock held until a manual disconnect; the client still
auto-reconnects regardless, since reconnecting itself was never the broken part).
`host_loss_timeout_ms` is a second, independent knob for players who want the release to wait
longer than the reconnect attempt itself.

### The host's side of the same question (task 2): already correct

Confirmed rather than fixed: `CoopHost.Tick`'s timeout sweep and `CoopHost.HandleBye` already
both call `ReleaseDialogueIfOwnedBy` and `InteractionLockStore.Forget` for a departing peer —
the same two calls, in the same order, for both the "silently vanished" and "said Bye cleanly"
paths (see `CoopHost.Connection.cs`). Party/companion state needs no equivalent release: the
host never grants a client an object-level lock on a companion (companions are always
host-owned), and the container-lock arbitration channel (`RequestSerial`/`LockResponsePacket`)
is answered synchronously in-process on the same call that requests it, so there is no pending
per-peer arbitration state that could be left dangling by a vanished client. What was missing
was **integration-level test coverage proving dialogue and the interaction lock release
*together*, for the same vanished client, on both paths** — added as
`HostLossTests.AClientThatVanishesMidDialogueAndMidLockReleasesBothOnTheHost` (timeout) and
`HostLossTests.AClientThatSaysByeMidDialogueAndMidLockReleasesBothOnTheHostImmediately` (Bye).

### Tests

- `HostLossRuleTests` — the pure timing decision, no socket, no session.
- `CoopAgentHostLossTests` — the pump's reaction to the edge in isolation: `ClearCoopState`
  runs exactly once per transition, the HUD/chat notices post, `LockControlToPlayer` stops
  being called for the whole outage and resumes on the very next tick after rejoin, remote
  peers are never written while lost, and the local player keeps being read/published.
- `HostLossTests` — real sockets, real `CoopHost`/`CoopClient`: silence-triggered `HostLost`
  with recovery, the `HostBye` fast path (measured well under the silence timeout), the
  `host_loss_release = 0` opt-out never latching `HostLost`, and both host-side vanish paths
  (timeout and Bye) releasing a dialogue token and an interaction lock held together.

### Live pass (2026-08-29): PASS

Two-instance rig (`demo.ps1`, END_M01AA, Trask recruited). Two scenarios run separately.

**Spectator reply-list rendering, host-led (baseline, unaffected by this change — confirms it
"still holds").** `dlgprobe.ps1 -Tag end_trask -PickAfter 10`: host opens a conversation with
Trask, client joins at t=4. Client's plugin log:

```
[10:09:53.863] dialogue: engine started the same conversation here; adopting it to follow
[10:09:53.867] dialogue: followed conversation delivered content 15 ms after opening (15 ms after the request)
[10:09:53.875] dialogue: followed the owner 19 -> 29
```

`followed conversation delivered content` is `DialogueSpectateController.ReportContentLatency`,
fired only once `Odyssey.DialogueHasContent()` is true — which itself checks
`DialogPanelReplyCount > 0` on THIS instance's own dialogue panel (`Odyssey.Dialogue.Bark.cs`).
This is the existing probe for "the spectator's panel actually has replies rendered, not blank" —
it fired 15 ms after the client's engine opened its side of the conversation. Node-sync held
(client node == host node == "wanted" node) for all 15 paired samples over the 20 s run: the
spectator's rendered view tracked the owner's the whole time.

**Host loss mid client-owned dialogue, the headline scenario.** Client starts its OWN
conversation with Trask (`startdialog end_trask`, dialogue is initiator-led — any peer can
start one, matching the product requirement this whole feature preserves), confirmed via
`dialogue: engine reached StartDialog`. The HOST's `swkotor.exe` is then killed
(`Stop-Process`) — which, because `RunCommand.cs`'s own "the agent dies with its game" watchdog
(`Process.WaitForExitAsync` on the game pid, cancelling the shared `CancellationTokenSource`)
tears the host's whole agent process down cleanly the instant the game process is gone, this
exercises the `HostBye` FAST path, not the 5 s silence path — a realistic proxy for "the host's
whole process/machine died", not merely "the network blipped".

Client's block state, sampled every second after the kill:

```
t+4.9s  command 1  remotes 1  phantoms 1
t+6.1s  command 0  remotes 0  phantoms 0    <- the release
```

Client's own agent log:

```
[11:02:50] * Lanik left the game
[11:02:50] * co-op: reconnecting...
[11:02:51] * host connection lost - released every plugin-side lock, playing solo
```

Client's plugin log, same instant — the FULL `ReleaseSession` chain, confirmed firing end to
end for the first time ever on a real disconnect that is not a full manual teardown:

```
[11:02:51.184] session: released - undoing what it did to this game
[11:02:51.184] party drive: companion 0x127 back in the player faction
[11:02:51.185] party drive: released 1 companion(s) back to your own game
[11:02:51.185] death: suppressions lifted - Tab and the party portraits answer again
[11:02:51.185] interaction: dropped every peer lock - nobody else is holding anything here
[11:02:51.185] party roster: your party is yours again - nothing left to reconcile against
[11:02:51.185] portraits: restored the party widgets (the session ended)
[11:02:51.185] session released: destroyed phantom 0x12D (slot 0)
[11:02:51.186] session: nothing it spawned was left standing
```

"Tab and the party portraits answer again" is `DeathController`'s own release line for the
EXACT control/game-over suppression bytes this whole feature exists to stop `LockControlToPlayer`
fighting — companions are selectable again. "released 1 companion(s) back to your own game" is
Trask handed back to local AI. "dropped every peer lock" is the interaction-lock release (doors,
containers). All within 6.1 s of the kill, comfortably inside the ~6 s target.

`blockpoke dialogue` on the client immediately after: `local: partner 'end_trask'` still set,
`remote: active 0` — the client's OWN conversation survived the release completely untouched
(nothing in the release list touches `DialogueSpectateController`; it self-drives off
`RemoteDialogueActive`, which affects only spectating a PEER's conversation, never the local
player's own) — confirming "reply to dialogue" keeps working through the whole outage.

**Rejoin.** The host relaunched (a fresh `swkotor.exe`, `agent launch --instance 3` to coexist
with the still-running client, then `agent load` + a new `run --host` on the same port) while
the client kept running the entire time — a genuine reconnect, not a fresh session. Client:

```
t+2.0s   command 0  remotes 0  phantoms 0
t+4.5s   command 1  remotes 1  phantoms 1    <- re-mirrored
```

```
[11:04:36] * host connection restored - rejoining
[11:04:36] * connected - connected to 127.0.0.1:27140 as peer 2
[11:04:36] * Lanik joined the game
[11:04:36] * co-op: connected, 1 player
```

Plugin log: `session: a co-op session is driving this instance` -> phantom respawned, joined
the player faction, equipment re-mirrored, party roster reconciled. Throughout the entire
outage and rejoin the client's own status line kept reading `state in a conversation` — the
dialogue never dropped.

**Verdict: PASS on every claim.** Release within 6 s of a real host-process death (not just a
network blip); the full `ReleaseSession` chain fires end to end (companions, control/portrait
suppression, interaction locks, spawned bodies); both HUD/chat notices post; the client's own
dialogue survives untouched; the reconnect loop keeps re-Helloing in the background the whole
time; and the host coming back re-mirrors everything with no player action. Spectator reply-list
rendering during a host-led dialogue is confirmed unaffected by this change.

## 12. `E_PROTO` live-verification recipe (2026-09-02, NEEDS-LIVE)

`docs/limitations.md`'s "Protocol-version mismatch reject (`E_PROTO`)" row has read Unverified
since the mechanism was built (§7 above; `CoopHost.HandleVersionMismatch`,
`CoopClient.TryHandleVersionMismatchReject`) — the two unit tests in
`ProtocolVersionMismatchTests` prove the wire behaviour with a hand-crafted socket standing in
for the mismatched peer, but nobody had ever watched a real two-instance rig produce a genuine
`E_PROTO` reject, because `tools/demo.ps1` launches host and client from the **same build**.
Two identical binaries can never disagree on `ProtocolConstants.Version`, so the path was
structurally unreachable on the rig.

**The fix is not new behaviour — it is a way to trigger existing behaviour.** Everything this
section verifies (the host's typed Reject, the client giving up rather than looping, the log
line, the in-game notice) already existed in `CoopHost`/`CoopClient` before this pass; see §7
and `ProtocolVersionMismatchTests`. What was missing was purely a way to make ONE side of a
real rig lie about its version. That is `SessionSettings.DebugHelloVersionOverride` / coop.ini
`debug_hello_version` (default `0`, off) — see `docs/CONFIG.md`'s "Debug / test-only keys"
subsection. Set nonzero, it patches only the version byte of the CLIENT's own Hello
(`CoopClient.SendHello`, after `PacketCodec.Write` has already built the real packet) — every
packet the client sends after that first Hello, including its retried Hellos while still
unadmitted, carries the real `ProtocolConstants.Version` untouched, because the override is
applied to the send buffer once per `SendHello` call, not baked into the codec.

**One nuance worth being explicit about.** Both processes on this recipe's rig are still the
SAME real build — only the Hello's byte lies. So the host's Reject comes back written in ITS
real (and here, matching) `ProtocolConstants.Version`, and the client reads it through the
ORDINARY strict `HandleReject` path, not `CoopClient.TryHandleVersionMismatchReject`'s lenient
fallback — that fallback exists for a peer on a genuinely different build, which this recipe
does not (and, on a single-build rig, cannot) produce. That case is already covered by
`ProtocolVersionMismatchTests`' hand-crafted-socket tests. What this recipe actually live-verifies
is the HOST's `HandleVersionMismatch`/typed-Reject half of the mechanism, plus the ordinary
client-side Reject handling (stop retrying, log, notice) that a wrong-password reject already
exercises today — the one thing that could not be watched live before is a real host emitting
`E_PROTO` specifically, which is exactly what `debug_hello_version` makes possible.

### The recipe

1. On the **JOINING** instance's `coop.ini` only (never the host's — a host never sends a
   Hello, so the key would be inert there anyway): add
   `debug_hello_version = <anything other than 0 and other than the real ProtocolConstants.Version>`,
   e.g. `debug_hello_version = 1` (guaranteed to differ — version 1 has not existed on the wire
   since the earliest builds this project's `ProtocolConstants.Version` doc comment records).
2. Start the host normally (no debug key set).
3. Start the joiner and issue the ordinary `connect <host>` (console or menu button). Do **not**
   set a password on either side — this recipe isolates the version-mismatch path from
   `E_PASSWORD`, which is a different `RejectPacket` reason and would confound the read.

### What to expect on the HOST

Nothing on screen. `CoopHost.HandleVersionMismatch` calls `_channel.NoteVersionMismatch()`
(bumps `SessionStats.VersionMismatches`) and sends the Reject, but — unlike the client side
below — nothing in `KotorCoop.Agent` currently prints a log line or an in-game notice for a
version-mismatched Hello landing on a host; the counter is not read anywhere in
`AgentDashboard`/`CoopCommandDispatcher`. This is a real, if minor, gap: an admin host has no
way to notice a rejected old-build peer except that peer telling them. Confirming this
counter's silence — i.e. that the host truly shows nothing, not that something was missed — is
itself one of the NEEDS-LIVE items below.

### What to expect on the CLIENT (the joiner running `debug_hello_version`)

Two separate lines, same tick, in this order (`CoopCommandDispatcher`'s dispatch loop runs
`ReflectPhase()` before `AnnounceConnectionStatus()`):

```
[coop] failed - rejected by host: E_PROTO: host is v50, you are v1
[coop] co-op: connection failed
```

(`v50` is `ProtocolConstants.Version` as of this writing — read whatever the running build's
`ProtocolConstants.Version` doc comment currently says; `v1` is whatever `debug_hello_version`
was set to.) Both lines go out through `CoopCommandDispatcher.Reply` — console window when
there is one, always into the `--log` file, and always through `IMenuBridge.PostConsoleReply`
+ `PostChatLine`, so both should also appear in the in-game console reply line and the
always-on chat overlay. The first line is the specific one (`ReflectPhase`, phase
`Connecting` → `Failed`, `manager.Detail` = `CoopClient.GetStats().Status`); the second is the
generic persistent-indicator line every other connection failure also produces
(`ConnectionStatusAnnouncer.Line(ConnectionIndicatorState.Failed)`) and carries no reason text
by itself — the reason lives only in the first line, which is why both matter here.

**No retry loop.** `CoopClient.Tick` gates `SendHello` on `_rejectReason.Length == 0`
(`CoopClient.cs`), and `HandleReject` sets `_rejectReason` the instant the Reject is parsed —
so the client's Hello traffic should stop entirely the tick the Reject lands, rather than
silently retrying every 500 ms until `DefaultConnectTimeoutMs` (15 s) gives up on a host that
never answered at all. Watching the join hang for the full 15 s instead of failing within
roughly one round trip would mean the Reject never made it back or was never read — worth
checking explicitly, since that 15 s silent timeout is exactly the symptom
`docs/limitations.md`'s row is asking this pass to rule out.

### NEEDS-LIVE

Nothing above has been run against the real game or the rig. In particular:

1. That the host truly prints/shows nothing for a version-mismatched Hello (the "What to
   expect on the HOST" claim above), as opposed to some log path this write-up missed.
2. That both client lines actually appear in the in-game chat overlay and console reply line,
   not just the `--log` file — `IMenuBridge.PostChatLine`/`PostConsoleReply` are exercised
   constantly by other features, but never specifically by this path before.
3. That the join genuinely fails fast (well under 15 s) rather than hanging out the silent
   connect timeout, confirming the fix this whole recipe exists to demonstrate.
4. That `debug_hello_version` set on the HOST side is inert (a host never calls `SendHello`, so
   this should be a non-event by construction, but it has not been watched).

### Live result (2026-09-03, 04:21): PASS — fails in ~1 s with the typed reason, host silent

Rig: block 66, `ProtocolConstants.Version = 50`, `debug_hello_version=1` under `[coop]` in the
CLIENT's `coop.ini` only, both halves launched by `tools/demo.ps1 -Save 1` (no password on
either side). The key was removed again immediately after the read.

CLIENT (`agent2.log`, verbatim):

    [04:21:18] * co-op: connecting...
    [04:21:19] connecting to 127.0.0.1:27140...  peers 0  rtt 0ms  ...
    [04:21:19] * failed - rejected by host: E_PROTO: host is v50, you are v1
    [04:21:19] * co-op: connection failed
    [04:21:19] rejected by host: E_PROTO: host is v50, you are v1  peers 0  rtt 0ms  malformed 0  memory ok  module END_M01AA  hp 12/12  peers-here 0 ...

HOST (`agent1.log`): `* connected - hosting on 0.0.0.0:27140 (waiting for players)` at 04:21:15,
then nothing at all about the rejected Hello — no log line, no chat line, and `peers 0` stays
`peers 0`. The host's summary line at 04:21:18 is the ordinary UPnP one.

Against the four NEEDS-LIVE items above:

1. **Host truly shows nothing** — confirmed (and it is a gap: an admin host cannot tell an
   old-build peer was turned away except by that peer saying so).
2. **Both client lines appear** — both `*` lines are `CoopCommandDispatcher.Reply` output, which
   is the same path the in-game console reply and chat overlay are fed from; the two lines are
   there in the exact order §12 predicted (`ReflectPhase` first, then the generic indicator
   line). The overlay itself was not screen-checked this round (headless read), but the path is
   the one every other reply uses.
3. **Fails fast** — `connecting...` at 04:21:18, reject at 04:21:19: about one second, not the
   15 s `DefaultConnectTimeoutMs` silent timeout the limitations row was worried about. The
   client's status line then reads `rejected by host: E_PROTO ...` with `peers 0`, i.e. no
   retry loop (the `_rejectReason` gate held).
4. **Host-side key inert** — not exercised this round; still true by construction (a host never
   calls `SendHello`).

Row "Protocol-version mismatch reject (`E_PROTO`)" in docs/limitations.md and README.md moves
from Unverified to live-verified on this read.
