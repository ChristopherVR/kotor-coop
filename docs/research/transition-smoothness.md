# Announced transitions: closing the gap between the host's door and the follower's warp (2026-08-21)

The user-visible ask: *"loading into another warp should feel smoother for other players."*
This is the timeline of why it does not today, the design that closes the gap, what shipped,
what is still a guess, and the exact live-test script that settles the guesses.

Read alongside [transition-window-races.md](transition-window-races.md) (the four safe
patterns this design does not violate), [cinematics.md](cinematics.md) (the movie-parity
channel this one rides beside — §9 in particular is the closest precedent for everything
here), and [ingame-gui-transition.md](ingame-gui-transition.md) (the engine's own load-screen
bring-up, which none of this needs to reproduce for a live transition — see §5).

Tag key, same as the docs above: **VERIFIED** = read from the binary or measured live ·
**INFERRED** = deduced from the surrounding, verified facts, not itself read off the binary or
measured · **NEEDS-LIVE** = the one thing that settles an INFERRED claim.

---

## 1. The timeline today, in numbers

Traced through the code named in each step; every millisecond figure below is either a named
constant in this codebase (VERIFIED — it is the number that ships) or a measurement already on
record in `cinematics.md` (VERIFIED-measured, cited).

| # | Step | Where | Cost |
| --- | --- | --- | --- |
| 1 | Host walks into a linked door. The engine's own movement update posts the transition event. | `CSWSDoor::EventHandler 0x0058C185` (engine-native; see `cinematics.md` §8.3) | 0 |
| 2 | Phase 1: the engine starts a 0.5 s fade and sets `trans_pending`. | `CGuiInGame::StartGlobalFade` + `CSWSArea::SetTransitionPending(1, id)` | **~0.5 s** |
| 3 | Phase 2: the actual move-to-module calls fire; the module unloads, the new one loads. No movie queued (`end_m01aa`→`end_m01ab`, this doc's own live-test pair): a bare load. | `CServerExoAppInternal::StartNewModule 0x004BA920` | **5.1–5.3 s** (VERIFIED-measured, `cinematics.md` §7.6, §8.1) |
| 3′ | …OR, if a script queued a cinematic (not this pair, but the canonical Endar-Spire-escape case): the main thread parks inside `WaitForMovie` for the length of the film. | same call, `PlayLoadModuleMovies` | **up to 25 s** for a single movie (`01c` measured 15,531 ms on the co-op-warp path, ~25 s unskipped — `cinematics.md` §7.6) |
| 4 | Host's OWN module name is unreadable ("areaXX" fallback) for the ENTIRE duration of 2–3(′). It cannot publish a real module until the load completes — the host's `PlayerState.Module` is frozen at whatever it last showed. | `CoopAgent.PumpOnce`, `IGameBridge.TryReadCurrentModule` | the whole of 2–3(′) |
| 5 | Once the load completes, the host's agent republishes the new module on its next tick. | `CoopAgent.PumpOnce` (30 Hz default) | **≤33 ms** |
| 6 | The follower's agent sees `host.State.Module` differ from its own, on its own next tick. | `CoopAgent.PumpTravel` | **≤33 ms** |
| 6′ | …UNLESS the follower itself transitioned recently (the exact shape of "a client walked through its own door first" — see §4): held for the settle window. | `AgentSettings.ModuleSettleMs` | **5,000 ms**, from whenever the follower's own last transition landed |
| 7 | The follower issues its own warp; the plugin flushes spawned bodies (0–3 frames, typically) then calls `Odyssey.TravelToModule`. | `TravelController.Execute` | **~0–50 ms** + |
| 8 | The follower's own load. Same cost as step 3/3′ — a bare load if no movie rides with it (it does, via the pre-existing `TransitionMovies` channel — §9 of `cinematics.md`), so this is the SAME number again. | | **5.1–5.3 s**, or up to 25 s with the movie |
| 9 | Follower arrives. | | |

**The avoidable latency is steps 2 through 6/6′ — everything before the follower's OWN load
even starts.** Today that is **5.6–5.8 s at minimum** (a bare door, no settle penalty) and **up
to several tens of seconds** whenever a cinematic is involved, because step 3′'s main-thread
stall is exactly the window the host cannot publish anything at all. None of that time buys the
follower anything: it is spent standing still, watching a frozen phantom, for no reason except
that the only signal the follower had — the host's own published module — was not writable
during the wait.

**Announced transitions cut steps 2–6/6′ to essentially step 2's own 0.5 s fade window plus one
agent tick (≤33 ms).** The plugin observes the SAME `trans_pending` rising edge the pre-store
flush already keys on (step 2, not step 3) — i.e. the announce goes out while the fade is still
running, before the load and before any movie has begun to play, regardless of how long either
one turns out to take. The follower's warp can then start while the host's own load (or movie)
is still in progress: **both loading screens overlap instead of running back to back.**

## 2. The jarring steps, and how each is addressed

| Jarring step | Cause | Addressed by |
| --- | --- | --- |
| The follower sees the host's phantom stand frozen for seconds (or tens of seconds) | The host stops publishing position the instant its main thread blocks (step 3/3′) — this is unavoidable, the phantom genuinely has nothing newer to show | Not eliminated (cannot be — see §3.3, "why not mirror the skip" reasoning in `cinematics.md` applies here too), but SHORTENED: the follower is no longer just standing still watching it — its own warp is already under way |
| The follower "stands around" doing nothing during the wait | Steps 4–6/6′ above: the follower's own PumpTravel had nothing to act on until the host's module changed | The announce gives PumpTravel something to act on ~5+ seconds earlier — §3 |
| The follower's loading screen appears with no warning | Nothing tells the player a warp is imminent | An on-screen line the instant the fast path fires (§3, task 2a) and a best-effort fade-out (§3, task 2b — **INFERRED**, off by default) |
| A client whose own door leads elsewhere completes its OWN transition and is then yanked back | The client's engine-native door crossing runs to completion before the follow logic ever sees a mismatch | The client-first-door refusal (§4) |

## 3. The design: `TransitionAnnounce`

Same shape as the pre-existing `TransitionMovies` channel (`cinematics.md` §9), and for the
same reasons — read that section first if this one is unclear on why "unreliable, no ack,
event-shaped bounds" is the right call here too.

### 3.1 The wire

`PacketType.TransitionAnnounce = 49`, `ProtocolConstants.Version = 33`
(`src/KotorCoop.Protocol/PacketType.cs`, `ProtocolConstants.cs`). Packet 47 and 48 are
deliberately skipped — reserved on paper by other agents working this tree concurrently, the
same courtesy `TransitionMovies` already extends to 39. Versions 30–32 are reserved the same
way.

```
TransitionAnnouncePacket(uint PeerId, string Module, string Waypoint, uint Serial)
```

Unlike `TransitionMoviePacket` (whose `Module` is what the announcer is LEAVING — the
follower already knows where to go from the mover's published module once it updates), this
one's `Module` is the DESTINATION: the entire point is to let the follower go there before the
mover's own published module has any chance to change. `Waypoint` is the entry point the
engine's own move-to-module fields carry — a real door's `linked_to`, or whatever the
plugin-initiated warp path was given.

Relayed by the host exactly like `TransitionMovies`; sent 5 ticks in a row (`AnnounceRepeats`)
as the sender's own redundancy against a dropped datagram, de-duplicated by serial on the
receiving end. **VERIFIED** (this is the wire code, not a claim about the engine).

### 3.2 The plugin side — VERIFIED signature, INFERRED effect for one piece

`PluginHost.OnFrame`'s pre-store flush already watches `Odyssey.IsAreaTransitionPending()`
(the `trans_pending` byte at area+0x2C4 — VERIFIED, this is the existing flush trigger). This
pass adds one call on the RISING edge, before the flush loop begins:

```csharp
if (!_transitionPending)
{
    _transitionPending = true;
    ...
    if (_view is { } announceView)
    {
        TransitionAnnounceWatch.OnTransitionBegan(announceView);
    }
}
```

`TransitionAnnounceWatch` (`src/KotorCoop.Plugin/TransitionAnnounceWatch.cs`) resolves the
NEAREST LINKED door to the local player — `CSWSArea::GetNextTransitionPendingID` was
considered and rejected: it hands back an opaque, single-byte REQUEST id fed into
`SetTransitionPending(1, id)`, not the door's own object id (**VERIFIED**, read off
`cinematics.md` §8.2's own disassembly), so there is no cheaper way to name which door just
fired. The sweep (`Odyssey.CollectDoors`) is documented "a debug verb only, never per frame" —
true, and irrelevant here: this runs exactly once per transition, on the edge, the same cost
class the `usedoor` cheat already pays on every human-driven run.

Once resolved, it publishes `(module, waypoint)` via a new `SharedBlockView.PublishTransitionAnnounce`,
bumping a serial in the free gap the cinematic channel's own version-44 comment already
identified as too small for an 8-entry ring (`0x38E4..0x3928`, 0x44 of the 0x11C bytes free —
**block version 47**; 45 and 46 are reserved the same way the protocol/packet numbers are).
`KotorGameBridge.TryReadTransitionAnnounce` is the agent-side read, the same LEVEL-plus-serial
shape `TryReadTransitionMovies` already uses.

**The plugin-initiated warp path is covered too**, cheaply: `TravelController.Execute` already
has the module and waypoint decoded before it calls `Odyssey.TravelToModule`, so it publishes
the same announce right there — no door sweep needed. This covers a host being pulled along by
a peer, or the `moviewarp` debug verb, though the canonical case (a human walking through a
real door) goes through the `trans_pending` edge above, because a plugin-initiated warp never
raises `trans_pending` at all (**VERIFIED**, `cinematics.md` §8.2: "a co-op warp gives it none,
and needs none, because the plugin flushed before it raised anything").

### 3.3 The agent side

`CoopAgent.PumpTransitionAnnounce` (mirrors `PumpTransitionMovies` exactly): reads the local
announce, relays it; drains peers' announces, and — for the HOST's announce only (a client is
never followed) — stores it as `_pendingAnnounce`, replacing any previous one wholesale (unlike
the movie accumulation, there is only ever one live destination, so nothing needs merging).

`CoopAgent.PumpTravel` checks `_pendingAnnounce` BEFORE the settle debounce and BEFORE the
"does the host's published module already differ from ours" check — both exist to protect
against acting on stale information, and an announce is neither. The pure decision
(`TransitionAnnounceRule.Decide`, unit-tested) is three lines:

```
no pending announce           -> Fallback (the pre-existing path, unchanged)
announce names our own module -> Fallback (nothing to do; consumed anyway)
otherwise                     -> Announced (warp now)
```

The stunt-module gate, the fallback-area-name guard and the retry debounce all still apply to
whatever module the fast path is about to warp to — an announced destination is earlier
information, never a different set of rules. Movies accumulated so far (`_pendingFollowMovies`)
ride the fast-path warp exactly like they ride the slow one.

Gated on `coop.ini transition_announce = 1` (default OFF). When off, the plugin still writes
the shared-block fields (cheap, and needed for a live capture even with the gate off) but
`PumpTransitionAnnounce` never reads or relays them, so a disabled feature puts nothing new on
the wire at all — the before/after comparison in §6 depends on that.

### 3.4 Task 2a — the on-screen line

The moment the fast path fires, it posts through `IGameBridge.PostChatLine` (the existing
always-on chat overlay path — no new UI):

```
following <host name> to <pretty module name>
```

`CoopAgent` is deliberately game-agnostic and has no file reader of its own; the pretty name
comes from an injected `AgentSettings.ModuleDisplayName` delegate, wired in
`KotorCoop.Agent.Program` to `KotorCoop.Memory.GameFiles.ModuleNames.TryRead` (which reads the
destination module's `.are`/`.mod` and `dialog.tlk` — the exact mechanism the server browser
already uses for the same purpose). A failed lookup falls back to the raw resref, same as every
other `ModuleNames.TryRead` caller.

**VERIFIED**: the call itself, the wiring, the fallback. **NEEDS-LIVE**: whether the chat
overlay renders it readably at the exact moment a fade (§3.5) is also starting.

### 3.5 Task 2b — freezing input / fading to black — INFERRED, off by default

`ingame-gui-transition.md` documents the fade-IN the engine's own `MainLoop` runs on world
reveal: `CGuiInGame::StartGlobalFade 0x0062ABF0`, **VERIFIED** `__thiscall`, `ret 0x10`, four
arguments (`direction, wait, length, colour*`), forwarded to `CSWGuiFade::SetFade 0x006244E0`
(`cinematics.md` §1.6). That is the fade call this task asked to find, and it is the same one a
door's own phase-1 fade already uses. What is **NOT** verified anywhere in this codebase's
research is the numeric DIRECTION that means "fade OUT" rather than "fade IN" — no disassembly
excerpt on record shows the literal arguments MainLoop's own call site pushes. `EngineHandles.cs`
calls it with a best-guess `FadeDirectionOut = 0` and says so in the comment next to it.

Getting the direction wrong cannot crash — it is a plain float/int write inside `SetFade`, no
pointer dereference on the direction value — and the effect is self-timed (the engine's own
`wait`/`length` floats bound it, so there is nothing to leak if the caller never runs again).
That safety margin is why this shipped at all rather than being deferred outright; it ships
gated OFF by default (`coop.ini transition_announce_fade`) pending a live pass, matching the
project's own convention for exactly this shape of risk (`effect_sync`, `map_reveal_sync`).

**Input freeze (`DisableInput`) was deliberately NOT implemented.** Unlike the fade, disabling
input has no self-timing — it needs an explicit `EnableInput` to undo, and this pass cannot
verify live that the undo always runs (an aborted travel request, a lost announce mid-flight).
A stuck-disabled input is a soft-lock, a strictly worse failure mode than a fade that runs the
wrong direction. This is exactly the shape of thing `cinematics.md` §3.2.5 already declines to
build in one pass ("not in scope, and say so") — the fade is the safe half; the input lock is
the documented next step, and it would need the same live-verified direction constant as a
prerequisite before it is even worth attempting.

## 4. Task 3 — the client-first-door case

**The bug**: today, if the CLIENT's own copy of the game walks through a transition door
before the host does, the client's transition runs to completion — full load, full HUD
bring-up — and is then yanked straight back out by the pre-existing follow logic the moment it
notices a mismatch (`PumpTravel`'s slow path, unchanged by this pass). Two full loads for
nothing.

### 4.1 The refusal

Same hook point as the announce (§3.2): the `trans_pending` rising edge. `TransitionAnnounceWatch`
checks `SharedBlockView.ClientDoorRefusalArmed` — an agent-written LEVEL, because the plugin has
no notion of "host" or "client" on its own — and if armed, refuses instead of announcing:

* posts `the host must lead the way - door 0x<id> leads to <module>` to the LOCAL chat overlay
  directly (`SharedBlockView.PostLocalChatLine`) — no agent round trip fits inside a 0.5 s fade
  window, so the plugin writes its own block field rather than waiting for one;
* attempts `EngineHandles.TryClearTransitionPending` — the SAME `SetTransitionPending(area, 0, 0)`
  call the door's own phase 2 makes on ordinary completion (**VERIFIED** signature and address,
  `0x00506B30`), called early instead. **INFERRED, NEEDS-LIVE** whether phase 2 re-checks the
  flag before running or is already unconditionally scheduled the moment phase 1 starts the
  fade — nothing in the disassembly read for this pass settles it either way. Safe regardless:
  this writes the identical state the engine itself writes on every normal transition, never a
  new poke, so the worst case is "did nothing", not "corrupted something".

### 4.2 The arm decision

`ClientDoorRefusalRule.ShouldArm` (unit-tested, pure): `isClient && followHostModule &&
client_door_refusal-ini-flag && !hostRecentlyTransitioning`. The last term is the accidental-
success guard: if the host has announced (or queued movies for) a transition of its own within
the last 45 s, the arm disarms — a client crossing the SAME door the host just led through
(`cinematics.md` §2.4's "both engines fire independently" case) must not be cancelled. Written
every agent tick, unconditionally, ahead of every early return in `PumpTravel` — a stale
"armed" left behind by an early return would outlive whatever justified it.

Gated `coop.ini client_door_refusal = 1`, default OFF — the cancellation itself is unverified
(§4.1), so this ships opt-in exactly like the fade.

### 4.3 The alternative not built

The task explicitly offers a second option: let the CLIENT lead, host follows. **Not built this
pass**, and here is what it would need: `PumpTravel` is currently asymmetric by construction
("the host never follows anyone" is baked into the guard clause, `session.IsEnemyAuthority`
short-circuits the whole method) — a lead-swap needs a session-level "who is leading right now"
flag both instances read, `TransitionAnnounceWatch` firing on EITHER role rather than being
gated to "client refuses, host announces", and — the genuinely new part — the HOST's own
`PumpTravel` would need to run the exact same follow logic it is currently exempt from,
including the pre-store flush ordering PluginHost already guarantees for a client. Nothing
about the wire or the pure rules above forecloses this; it is a second `PumpTravel` caller
shape, not a second protocol.

## 5. Task 4 — arrival polish: checked, not changed

**Finding: the two things task 4 asks about are already fast, and nothing this pass added
slows them down.**

* **The follower's phantom of the host** is written on the real-time path (`TryWriteRemotePeers`,
  every `PumpOnce` — 30 Hz default), not behind `HeavyPumpIntervalMs` or `CombatPumpIntervalMs`.
  It reappears within one agent tick (≤33 ms) of the host publishing a position in the new
  module — which is exactly what the announce fast path was built to bring forward, not
  something separate that needed its own fix.
* **The census** (enemy presence) is gated by `CombatPumpIntervalMs`, **150 ms** — already
  imperceptible, and unrelated to `ModuleSettleMs` (5,000 ms), which only ever gated the FOLLOW
  DECISION, never anything about what happens after arrival.
* **The follower's HUD bring-up** (`ingame-gui-transition.md`'s whole subject) is entirely
  engine-native for a LIVE in-game transition — `MainLoop`'s own in-world block runs the
  instant its own three gates are true (a client module, a client player creature, the display
  flag), with no plugin/agent involvement. That document's own defect (the main menu never
  leaving the panel list) is specific to the LOAD-VIA-MENU-CLICKS replay `MenuLoadController`
  performs at initial connect — a different code path from a warp or a door, which use the
  retail transition machinery unmodified. **VERIFIED by reading**, not measured live this pass
  (no game was run), but there is no code in this repository that could delay it: the follower's
  transition, once started, is the SAME `Odyssey.TravelToModule` → engine load path every
  transition in this mod already uses.

**Deliberately NOT tightened**: `HeavyPumpIntervalMs` (1,000 ms — gates doors/items/journal
republish) and `CombatPumpIntervalMs` (150 ms). Both were considered. Forcing an immediate pump
on module arrival (resetting `_lastHeavyPumpMs`/`_lastCombatPumpMs` when the module changes)
would speed up doors/items/journal reappearing by up to a second — but doing so races the
engine's own world construction in exactly the window `transition-window-races.md` spent four
defects proving is poison ("a module transition destroys every object and reassigns every id,
but nothing pauses the machinery"), and there is no way to verify a change to that timing is
safe without running the game, which this pass cannot do. The existing interval is the correct,
conservative choice until a live pass says otherwise.

## 6. The live-test script

**Target**: `end_m01aa` → `end_m01ab`, via `end_door10` — the same pair `cinematics.md` §8
already uses, chosen because it fires no movie at all, which makes it the cleanest measurement
of the announce's OWN effect (§1's "no movie" row) rather than a movie's.

`tools/transition-announce-measure.ps1` does the arithmetic for §6.1/§6.2 below: point it at
both instances' `kotorcoop-plugin.log` after a run and it prints Interval A and B straight from
the logged timestamps, so "measure ms from host door-use to follower warp start and to
follower arrival" is one command rather than a stopwatch:

```powershell
pwsh tools\transition-announce-measure.ps1 `
    -HostLog 'C:\GOG Games\Star Wars KOTOR (GOG)\kotorcoop-plugin.log' `
    -FollowerLog 'D:\Games\swkotor-gog-p2\kotorcoop-plugin.log' `
    -DestinationModule end_m01ab
```

### 6.0 Before anything

```powershell
pwsh src\KotorCoop.Plugin\build.ps1 -Install -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'
pwsh src\KotorCoop.Plugin\build.ps1 -Install -GameDir 'D:\Games\swkotor-gog-p2'
```

`demo.ps1` does not deploy the plugin (`kotor-coop-demo-does-not-deploy-plugin.md`). Confirm
the new build shipped before trusting anything below:

```powershell
Select-String -Path 'C:\GOG Games\Star Wars KOTOR (GOG)\dinput8.dll' -Pattern 'TransitionAnnounceWatch' -Encoding Byte -List
```

### 6.1 The "before" run — `transition_announce` left at its default (off)

1. Bring the pair up with `tools\demo.ps1` (save slot 1 — not `-Save 2`, see
   `kotor-coop-save-slot-off-by-one.md`). Confirm both logs show a connected session and each
   side reports the other's phantom.
2. Position both players together in `end_m01aa`, near `end_door10` (47.4172, 120.126).
3. Move ONLY the host through the door (walk it in, or `usedoor end_door10` via
   `blockpoke.ps1 console` — see `cinematics.md` §8.3 for why a walk-through is required, not
   a `tp`).
4. On the HOST log, record the timestamp of `area transition pending:` (step 2 in §1's table —
   this is the fade beginning) and the timestamp of the eventual `travel:`/module-load-complete
   line.
5. On the FOLLOWER log, record the timestamp of its OWN `travel: moving to module 'end_m01ab'`
   line — this is when the slow path finally fired.
6. **Measured interval A**: follower's `travel:` timestamp minus the host's `area transition
   pending:` timestamp. Expect **~5.6–5.8 s or more** — this is §1's avoidable latency, before
   the fix.
7. Confirm no `cinematic:`-prefixed or `announce:`-prefixed line appears anywhere in either log
   — the gate is off, so the channel must be silent. Its absence IS the control.

### 6.2 The "after" run — `transition_announce = 1`

Edit `coop.ini` on BOTH installs (`transition_announce = 1`), or pass the equivalent through
whatever launches the agent process, then repeat §6.1's steps 1–3.

Expect, on the HOST log, in order:

```
area transition pending: flushing spawned objects before the module is stored
announce: door 0x<id> transition beginning -> 'end_m01ab' at waypoint '<waypoint>'
```

and, in the HOST agent console/log:

```
announcing transition to 'end_m01ab' at waypoint '<waypoint>'
```

On the FOLLOWER agent console/log, within ~1 tick of the host's announce line:

```
host announced transition to 'end_m01ab' at waypoint '<waypoint>'
following the host to 'end_m01ab' IMMEDIATELY (announced transition serial 1, not waiting for the module-change settle)
```

and, in the follower's plugin log, its OWN `travel: moving to module 'end_m01ab'` line —
**measure the SAME interval A again**: this time it should be **on the order of the door's own
0.5 s fade plus one agent tick (≤~600 ms total)**, not seconds.

**Pass/fail**:

| Claim | Pass | Fail |
| --- | --- | --- |
| The announce fires before the load | `announce:` line timestamp is within the fade window (before the host's own module changes) | `announce:` line missing, or later than the host's own module-changed line |
| The follower acts on it | follower's `travel:` line lands within ~1 s of the host's `announce:` line | follower's `travel:` line only appears after the host's real module update (interval A unchanged from §6.1) |
| Both loading screens overlap | both instances' plugin logs show a stall (`the main thread was gone for N ms`) with overlapping wall-clock ranges | the follower's stall starts only after the host's has already ended |
| Nothing regressed | both arrive in `end_m01ab`, phantoms re-spawn on both sides, no `FIRST-CHANCE access violation` in either log | either side stuck, missing phantoms, or a crash |
| Interval A dropped | Interval A in §6.2 is at least an order of magnitude smaller than §6.1's | no measurable improvement |

### 6.3 The client-first-door case

1. Set `client_door_refusal = 1` on the CLIENT's `coop.ini` (leave the host's alone — the arm
   is per-instance and only ever meaningful on a client).
2. With both players in `end_m01aa`, the client walks into `end_door10` FIRST, before the
   host moves at all.
3. Expect, on the CLIENT plugin log:

```
area transition pending: flushing spawned objects before the module is stored
door-refusal: the host must lead the way - door 0x<id> leads to end_m01ab
door-refusal: cleared trans_pending (INFERRED whether this stops the load - NEEDS-LIVE)
```

4. **The decisive check**: does the client's own transition actually complete anyway (`where`
   on the client's console reports `end_m01ab`), or does it stay in `end_m01aa`? Both outcomes
   are informative — a completed transition means `SetTransitionPending(0,0)` did not stop
   phase 2 and the refusal is cosmetic-only (the on-screen line still fired, but the yank-back
   this feature exists to prevent still happens once); a transition that does NOT complete is
   the feature working as designed. Either way, **this is the one live measurement this whole
   pass most needs** — it is the only INFERRED claim with a real behavioural consequence.
5. Negative control: repeat with `client_door_refusal = 0` (or unset) and confirm the client's
   transition completes and is yanked back exactly as it does today — the pre-existing,
   unfixed behaviour this feature is measured against.

## 7. What shipped, in one place

* `src/KotorCoop.Protocol/PacketType.cs`, `ProtocolConstants.cs`, `Packets.cs`,
  `PacketCodec.cs` — `TransitionAnnounce = 49`, protocol version 33,
  `TransitionAnnouncePacket`, its codec.
* `src/KotorCoop.Session/ICoopSession.cs`, `CoopHost.cs`, `CoopClient.cs` — the relay, mirroring
  `TransitionMovies` exactly (unreliable, 5-tick repeat, host relays to everyone but the
  sender).
* `src/KotorCoop.Session/TransitionAnnounceRule.cs`, `ClientDoorRefusalRule.cs` — the two pure
  decisions, unit-tested with no engine, session or clock dependency.
* `src/KotorCoop.Session/CoopAgent.cs` — `PumpTransitionAnnounce`, the fast path in
  `PumpTravel`, the door-refusal arm write, `AgentSettings.TransitionAnnounceEnabled` /
  `ClientDoorRefusalEnabled` / `ModuleDisplayName`.
* `src/KotorCoop.Session/CoopConfig.cs` — `transition_announce`, `client_door_refusal` coop.ini
  keys.
* `src/KotorCoop.Memory/IGameBridge.cs`, `KotorGameBridge.cs` — `TryReadTransitionAnnounce`,
  `TryArmClientDoorRefusal`, the new `Block*` offsets mirroring the plugin's shared block.
* `src/KotorCoop.Agent/Program.cs` — wires `ModuleDisplayName` to `ModuleNames.TryRead`.
* `src/KotorCoop.Plugin/SharedBlock.cs` — the new region (`0x38E4..0x3928`), block **version
  47** (45 and 46 reserved).
* `src/KotorCoop.Plugin/SharedBlockView.cs` — `PublishTransitionAnnounce`,
  `ClientDoorRefusalArmed`, `PostLocalChatLine`.
* `src/KotorCoop.Plugin/EngineHandles.cs` (new) — the duplicated pointer chain and the two new
  native calls (`SetTransitionPending`, `StartGlobalFade`), kept deliberately separate from
  `Odyssey.cs` (see the file's own doc for why).
* `src/KotorCoop.Plugin/TransitionAnnounceWatch.cs` (new) — the door sweep, the announce
  publish, the refusal.
* `src/KotorCoop.Plugin/PluginHost.cs`, `TravelController.cs` — the two hook points (the
  `trans_pending` rising edge; the plugin-initiated warp) and the best-effort fade trigger.
* Tests: `tests/KotorCoop.Tests/TransitionAnnounceTests.cs` (wire round-trip, the pure rules),
  `tests/KotorCoop.Tests/TravelPumpTests.cs` (extended — the fast path, the refusal arm,
  integration with the pre-existing follow logic), `TransitionMovieTests.cs` (block-version
  assertion moved to 47).

**Deliberately not shipped**: input freeze (§3.5); the lead-swap alternative (§4.3); tightened
heavy/combat pump intervals (§5). Each has its own reasoning above, and each is a smaller,
separable next step rather than a gap in this one.

## 8. Live pass (2026-08-21)

Run against the GOG pair (`tools/demo.ps1`, block v47 / protocol 33, both builds clean, baseline
`dotnet test` 1379/1379). `end_m01aa` → `end_m01ab` via `end_door10`, exactly as §6 prescribes.

### 8.1 Baseline — `transition_announce = 0`

```
[23:58:24.896] area transition pending: flushing spawned objects before the module is stored
[23:58:26.544] travel: moving to module 'end_m01ab' on the session's authority   (follower)
```

**Interval A: 1648 ms.** Lower than §1's predicted 5.6–5.8 s floor — this pair's module cache
was already warm from earlier passes on this rig, so the host's own bare load ran well under
the documented 5.1–5.3 s figure this run. The absolute number moves with disk/OS cache state;
the point of this measurement is the BEFORE/AFTER ratio, not the absolute floor.

⚠ **§6.1 step 7 is wrong as written.** The plugin's own `announce:` log line
(`TransitionAnnounceWatch.OnTransitionBegan`, called unconditionally on every `trans_pending`
rising edge per §3.2) appeared in the host's plugin log on this baseline run — gate OFF,
`announce: door 0x4A transition beginning -> 'end_m01ab' at waypoint 'from_01a'` printed anyway.
This matches the design text exactly ("the plugin still writes the shared-block fields... but
`PumpTransitionAnnounce` never reads or relays them") — the PLUGIN log line was never gated,
only the AGENT's read/relay is. §6.1's own pass/fail line ("no `announce:`-prefixed line
appears... its absence IS the control") describes a control that does not exist; the real
control is the AGENT-side `announcing transition to '...'` console line, which correctly did
NOT appear in either agent log on this baseline run. §6.1's wording is left as originally
written rather than silently rewritten, since the mismeasurement itself is useful evidence of
what the channel actually does; this section is the correction.

### 8.2 Announced, no movie — `transition_announce = 1`

```
[00:02:57.521] area transition pending: flushing spawned objects before the module is stored
[00:02:57.661] announce: door 0x4A transition beginning -> 'end_m01ab' at waypoint 'from_01a'
[00:02:57.697] travel: fading to black for the incoming transition (INFERRED direction - NEEDS-LIVE)   (follower)
[00:02:57.702] travel: moving to module 'end_m01ab' on the session's authority                          (follower)
```

Agent consoles, in order:
```
[00:02:57] * announcing transition to 'end_m01ab' at waypoint 'from_01a'                 (host)
[00:02:57] * host announced transition to 'end_m01ab' at waypoint 'from_01a'             (follower)
[00:02:57] * following the host to 'end_m01ab' IMMEDIATELY (announced transition serial 1,
            not waiting for the module-change settle)                                     (follower)
```

**Interval A: 181 ms** (57.702 − 57.521), against the SAME host/follower pair and door,
same-session cache state as §8.1 — **~9× smaller**, on the order of the fade window plus one
agent tick, exactly as predicted. Both arrived in `end_m01ab`, phantom re-spawned on both sides,
no `FIRST-CHANCE` access violation in either log (the one `0x406D1388` line per launch is the
benign `SetThreadName` exception every session shows, unrelated to this feature). Run twice
(this run, and the movie-carrying run below); both pass.

### 8.3 Announced, with a movie — `transition_announce = 1`, `moviewarp queue 01c`

Synthesized via the same mechanism `cinematics.md` §8.4 uses (no live Endar-Spire stunt trigger
was reachable from the demo save's position without a long scripted sequence; `moviewarp queue`
exercises the identical `TransitionMovies` channel a real stunt-module escape would), specifically
to answer the task's "confirm the movie flush still works with the early warp":

```
[00:05:08.777] area transition pending: flushing spawned objects before the module is stored
[00:05:08.918] announce: door 0x4A transition beginning -> 'end_m01ab' at waypoint 'from_01a'
[00:05:11.835] cinematic: staged 1 of 1 movie(s) from the session onto this transition: 01c   (follower)
[00:05:11.836] travel: moving to module 'end_m01ab' on the session's authority                 (follower)
...
[00:05:26.092] cinematic: the main thread was gone for 16047 ms (host)
[00:05:28.656] cinematic: the main thread was gone for 16015 ms (follower)
```

**Pass.** The follower's `cinematic: staged...` line landed BEFORE its `travel:` line (the
accumulation rule in `cinematics.md` §9.4 riding the fast path exactly as designed), both
stalls overlap in wall-clock range (16.0–16.1 s, essentially simultaneous — the early warp let
the follower's own movie start almost immediately after the host's, rather than 1.6+ s later),
both arrived in `end_m01ab` with phantoms re-spawned, no crash. The announce path does not
break the movie-flush channel.

### 8.4 `transition_announce_fade = 1`

Fired on every transition above (`travel: fading to black for the incoming transition`), on
both the follower's fast-path warp and a host self-warp (the plugin-initiated path also calls
the same `TravelController` fade trigger). **No stuck-black, no hang**: every transition it
rode completed normally, the game kept responding to console commands seconds later, across
three separate transitions in this session. That rules out the worst failure mode named in the
task (a soft-lock). **The one thing it could not verify: fade DIRECTION.** No screenshot
capture was available in this environment to confirm the screen actually went to black rather
than staying lit or flashing white; the `FadeDirectionOut = 0` guess (§3.5) remains unconfirmed
either way. Given the specific uncertainty this flag exists to resolve was not resolved, it
stays OFF by default — "didn't misbehave" is not the same as "confirmed correct," and the task's
own bar for flipping a default is proof, not absence of a symptom.

### 8.5 Client-first-door (`client_door_refusal`) — BLOCKED, not a feature defect

**The decisive check (§6.3) could not be run.** The CLIENT install's own `usedoor` attempt on
`end_door10` reported:

```
door: the engine says the party is NOT together, and a door transition is gated on that
(0x0058C228) - gathering it first
door: gathered 0 party member(s) and the engine STILL says the party is not together - this
use will run k_trg_transfail and move nobody
```

`GatherParty`'s own member count (`CSWPartyTable`, server-side) read **0** — correct, since
`PartyRosterController.RemovePartyMemberBySlot` removes the mirrored companion locally on a
non-owning instance by design (companions are host-authoritative). But
`CSWParty::IsPartyTogether` (client-side, `CClientExoApp::GetSWParty`) still returned false with
nothing to gather, meaning **`CSWSDoor::EventHandler`'s party gate refuses every real door
transition on a client instance, before `trans_pending` ever rises and before
`TransitionAnnounceWatch` gets a chance to run at all** — independent of this pass's own code,
and independent of how the door is approached (raw walk hits the identical gate, per
`cinematics.md` §8.3a; a `usedoor`-style gather cannot fix it because there is nothing IN the
local party to gather). This is a **NEW finding**, not previously documented: the client-side
`CSWParty` struct apparently retains stale "together" state after `RemovePartyMemberBySlot`
clears the server-side `CSWPartyTable` count, and the two disagree. It blocks any future
client-side door test, not just this one.

Two workarounds were considered and rejected: swapping which physical install plays host/client
(the defect follows the CLIENT ROLE, not the install — confirmed by reasoning, not worth a
second restart to also confirm empirically); and hand-writing `trans_pending` directly to
exercise `TransitionAnnounceWatch` without a real door (would test the refusal logic in
isolation but not the actual engine interaction the decisive check exists to answer, and adding
a new debug poke for it was judged out of scope for a verification pass).

**`client_door_refusal` stays OFF by default.** Not because it misbehaved — it was never
reached. The pure decision (`ClientDoorRefusalRule.ShouldArm`) remains unit-tested only; the
one thing §4.1 flagged NEEDS-LIVE (whether `TryClearTransitionPending` actually stops phase 2)
is still NEEDS-LIVE. Filed as a follow-up: the party-gate defect above needs its own
investigation (and likely its own fix — extending `RemovePartyMemberBySlot` to also clear
whatever `CSWParty`-side state `IsPartyTogether` reads) before this feature's own decisive
check can ever run live.

### 8.6 Trigger relay (`trigger_relay = 1`) — end_m01ab

No deliberate walk was needed for the positive path: `end_trig_carth02` (id `0x26`, a STORY
trigger, `k_pend_carth_t02`) sits almost exactly on the `from_01a` waypoint every transition in
§8.2/§8.3 arrives at, so the client's real presence there on arrival was enough to exercise the
whole channel naturally.

```
[00:03:14.142] triggers: standing in 1 generic trigger(s), first 0x26 - asking the enemy         (client)
                owner to run its OnEnter too
[00:03:14.412] triggers: fired OnEnter of 0x26 for peer 2 (phantom 0x73)                          (host)
```

**Fired-set dedup: PASS.** The client re-reported "standing in" the same trigger on THREE more
separate arrivals in this module across the session (00:05:28, 00:11:01, 00:16:25 — each a
fresh module load after leaving and returning), and the host's fire count never moved:
`pwsh tools\blockpoke.ps1 -ProcId <host> triggers` read `fired 1 refused 0` at the end of the
session, matching the single `fired OnEnter of 0x26` log line. The store's per-module fired-set
persists across repeat VISITS to the same module name within one session, not just within a
single continuous stay — a stronger guarantee than the doc's own §7 item 6 asked for.

**Negative control: PASS.** The client was placed at world coordinates (78, 69) — confirmed
geometrically inside `k_stlthawrd`'s trigger polygon by hand (all four edge cross-products
positive against the polygon read from `m01ab.git`'s `TriggerList`) — and
`pwsh tools\blockpoke.ps1 -ProcId <client> triggers` read `inside 0`. Reading
`TriggerRelayController.HandleWatch`'s own order confirms why: it checks `IsRelayEligible`, THEN
geometric containment, THEN the allowlist pattern, and only publishes ids that pass all three —
`k_trg_stealth` fails the pattern check, so it is filtered out of the "inside" set before ever
being geometrically reported, exactly as designed. Standing inside it produced no watch report,
no relay request, no fire.

**Host double-fire (§3's named gap): observed, harmless in this module.** The host's own real
PC transitioned into `end_m01ab` at that same `from_01a` waypoint at least twice for real in
this session (§8.2's and §8.3's own arrivals) — a STORY trigger positioned on the arrival point
is exactly the shape of thing a vanilla player's own engine-managed module entry would cross
natively, so the host's engine almost certainly ran `k_pend_carth_t02` on its own account, in
addition to the ONE time this channel fired it via the client's relayed request. This channel
has no way to observe the host's own native fire directly (§3's own limitation, confirmed
rather than newly discovered), so this is inferred from the trigger's position, not read off a
log line naming it. **No visible consequence was found**: no duplicate dialogue window, no
extra spawn, no error, across the whole session in either log. Consistent with `k_pend_carth_t02`
being one of the "many `k_p*_en` scripts [that] destroy themselves or check a global on the way
in, which absorbs a duplicate call for free" (§3). Per the task's own conditional — implement the
native-fire tracking only if a double-fire is shown to be harmful here — **no tracking was
added**: nothing observed in this module needed it, and the `+0x29C`/hook approach named in the
task remains a documented, un-built option for a module where it does matter.

### 8.7 Tooling notes found along the way

* `tools/transition-announce-measure.ps1`'s `-Since` filter is a bare `TimeSpan` comparison with
  no date component — it cannot exclude an earlier run's lines once local midnight has passed
  between the two runs being compared (confirmed live: the baseline ran at 23:58, the announce
  run at 00:02, and `-Since 00:02:00` still matched the earlier 23:58 line because `23:58:24 >
  00:02:00` numerically). Not fixed this pass; measure across a midnight boundary by line-number
  offset instead, as this pass did.
* The same script's Interval B pattern (`epoch: module changed`) is real text, but not the ONLY
  epoch reason on an arrival — the bare door-crossing runs in §8.1/§8.2 logged
  `epoch: world unloading` instead, so Interval B was only measurable on the movie-carrying run
  (§8.3). Not a bug, just a narrower match than the script's own comment implies.
* `blockpoke.ps1 walk`'s queued `ActionMoveToLocation` move remains as unreliable for the real
  player character as `TestDriveController.HandleWalk`'s own doc comment already says it might
  be — it worked once from a fresh `tp`'d position, then repeatedly did nothing from further
  `tp`'d positions in the same session, with no error reported either time. `tp` itself was
  reliable throughout. Prefer `tp` immediately adjacent to (or just outside) a target polygon
  and accept that a `walk` request may need several retries or may not land at all; `usedoor`'s
  own internal walk (retried up to 120 frames) remains the reliable mechanism for doors
  specifically, with no generic equivalent exposed for arbitrary polygons.

### 8.8 Defaults changed by this pass

| Flag | Before | After | Why |
| --- | --- | --- | --- |
| `transition_announce` | OFF | **ON** | §8.2/§8.3: ~9× latency drop, no regression, movie-carrying case also clean |
| `trigger_relay` | OFF | **ON** | §8.6: correct fire-once, correct dedup across repeat visits, correct negative-control exclusion, no crash |
| `client_door_refusal` | OFF | OFF (unchanged) | §8.5: decisive check blocked by an unrelated pre-existing party-gate defect, never reached |
| `transition_announce_fade` | OFF | OFF (unchanged) | §8.4: no soft-lock observed, but the one uncertain claim (fade direction) could not be visually confirmed |

## 9. Live pass (2026-08-22) — the client door-gate root cause, and the decisive check

§8.5's blocker is fixed. Root-cause analysis and the fix itself are documented in
`party-reconciliation.md`'s dated 2026-08-22 entry (`CSWParty::IsPartyTogether`'s own disassembly,
why it diverges from `CSWPartyTable`, and `party_together_fix`) — this section is the live
door-crossing evidence that fix unblocks, run against the GOG pair per §6.0, block v47 / protocol
33, both builds clean, baseline `dotnet test` 1379/1379.

### 9.1 `party_together_fix` confirmed live, before any door was touched

On the very first frame `PartyRosterController` ran after loading into END_M01AA — on BOTH the
host and the client, with no manual reconciliation triggered yet:

```
party roster: local party is now 0x001 (recruited 0x001)
party together: CSWParty's own member count read 2 against CSWPartyTable's 1 - clamping the
client-side struct down to match (this is the field CSWParty::IsPartyTogether actually checks,
and the one a real door transition gates on)
```

Confirms the divergence is real and present from the first frame, on both roles, not something
introduced only by a client-specific remove. The fix re-fired identically on every later module
load and roster reconcile pass across the whole session (multiple door crossings, multiple
reconnects), always shrinking the count, never growing it — no case observed where it clamped
toward a value that was later wrong.

### 9.2 `client_door_refusal=0` — the client crosses a real door first, cleanly

With `party_together_fix=1` and `client_door_refusal=0`, the CLIENT walked into `end_door10`
(END_M01AA → END_M01AB, waypoint `from_01a`) BEFORE the host moved at all:

```
door: 0x4A at 0.0m links to module 'end_m01ab' at waypoint 'from_01a'; open state 0
door: opened 0x4A (state 0 -> 2) so the walk has somewhere to go
cheat: walking through door 0x4A (0.0m) -> 'end_m01ab' (status 1)
area transition pending: flushing spawned objects before the module is stored
announce: door 0x4A transition beginning -> 'end_m01ab' at waypoint 'from_01a'
flush, before the module is stored: destroyed phantom 0x12D (slot 0)
epoch: world unloading; clearing phantom, item, combat and door state
```

**No party-gate refusal anywhere in this sequence.** The transition ran to completion — the
client genuinely loaded into END_M01AB (the host's own log shows its phantom of the client
destroyed at the same moment, `peer left: destroyed phantom 0x12D (slot 0)`, and the client's log
shows a full module load, not a stall).

**What happened next is the PRE-EXISTING, already-documented "host does not follow" behaviour**
(roadmap item 13's own subject, `CoopAgent.cs:1434-1494`), not a new defect: ~17 seconds later,
with the host still in END_M01AA, the client's own agent pulled it back:

```
travel: moving to module 'END_M01AA' on the session's authority
```

The client ended the sequence back beside the host in END_M01AA — the SAME outcome the client-
first-door problem has always produced when nobody builds a lead-swap (§4.3, not built) — but for
a genuinely different reason than before: this time the door crossing itself succeeded and
completed; it was the FOLLOW-BACK that returned it, not a refused transition. This item's own
"note what the client sees" ask: the client experiences two real loading screens in quick
succession (into END_M01AB, then back into END_M01AA) and no on-screen explanation of why it was
pulled back — a rough edge, but a pre-existing and already-documented one (roadmap item 13),
outside this pass's scope to also fix.

### 9.3 `client_door_refusal=1` — the decisive check finally runs

§4.1 named ONE unresolved question: does `TryClearTransitionPending` (`SetTransitionPending`
`(area, 0, 0)`, called on the refusal edge) actually stop phase 2, or is phase 2 already
unconditionally scheduled by the time phase 1's fade starts? With `party_together_fix=1` and
`client_door_refusal=1` on the CLIENT, the client attempted `end_door10` first again:

```
area transition pending: flushing spawned objects before the module is stored
door-refusal: the host must lead the way - door 0x4A leads to end_m01ab
door-refusal: cleared trans_pending (INFERRED whether this stops the load - NEEDS-LIVE)
epoch: transition flushed but not taken; clearing phantom, item, combat and door state
```

**`epoch: transition flushed but not taken` is the answer: the transition genuinely did NOT
complete.** The client's world stayed END_M01AA (its own log kept receiving the host's full
enemy census immediately afterward — `enemy drive: mirroring 20 of the owner's 20 enemies`, which
only happens when both peers share a module) rather than loading END_M01AB. Repeated a second
time in the same session (the client retried the walk once more) with the identical outcome.
**`TryClearTransitionPending` stops phase 2.** The refusal is not cosmetic — it is a real,
working cancellation, exactly as designed. No crash, no stuck load screen, no desync; the client
simply stayed where the design says it should.

### 9.4 Defaults changed by this pass

| Flag | Before | After | Why |
| --- | --- | --- | --- |
| `party_together_fix` (new) | — | **ON** | §9.1: fires correctly, never over-clamps, unblocks every door crossing on a client |
| `client_door_refusal` | OFF | **ON** | §9.3: the one previously-unresolved question (does the refusal actually stop the load) is now answered yes, live, twice |

`transition_announce` and `trigger_relay` (already ON from the 2026-08-21 pass) were re-exercised
incidentally during this session's door crossings with no regression.
