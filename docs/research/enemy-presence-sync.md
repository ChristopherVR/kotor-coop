# Enemy presence sync — one visible fight instead of two private ones (design 2026-08-06)

Why each player used to watch a different battle, and how the mirroring instance now drives
its enemy copies to wherever the owner's engine has them. This is a DESIGN doc over already
verified research, not new RE: every engine fact used here is proven in
`docs/research/realtime-creature-movement.md` (the drive recipe) and the enemy channel's own
history (`Handoff.md` section 1, addendum 0). Items that still need a live pass are marked.

Tag key: **VERIFIED** = proven in a cited doc or live run · **DESIGN** = a choice made here ·
**NEEDS-LIVE** = must be confirmed on the running game.

---

## The gap

The enemy channel replicated OUTCOMES only: health and death, host-authoritative, keyed by
object id (VERIFIED live — the same Sith soldier is 0x14 in both instances). Positions,
facing and animation were never sent, so both instances ran their own AI over their own
copies and the same soldier charged the host on one screen while standing in a doorway on
the other. Health agreed; nothing you could *see* did.

## The model — DESIGN

**The owner's fight is the fight. The mirroring instance presents it.**

- The owning peer's agent already reads every enemy each pump (`ReadEnemies`). It now also
  reads position (`+0x90`), orientation (`+0x9C`, converted to a facing angle) and the
  looping animation (`+0xD4`) — three external reads per enemy, all long-verified offsets.
- `EnemyState` carries presence on the wire **in the snapshot only** (protocol v12). The
  peer direction carries neither presence nor health: a client's local copies are mirrored
  and pinned, so nothing about them is authoritative. It reports the damage it has DEALT
  instead (`EnemyDamage`, 2026-08-07 — see `docs/research/peer-damage-ledger.md`).
- On the mirroring side the agent writes presence into a new shared-block region (block
  v20, `0x1400..0x1810`, id-written-last per slot like every other channel), and a new
  `EnemyPresenceController` drives each resolvable enemy every frame with the exact
  phantom recipe: measure speed from consumed displacements, smooth the shown position
  (`smooth_ms`, snap on >8u cuts), hysteresis on "is it moving", then
  `Odyssey.DriveEnemy` = `SetOrientation` + gait/animation + `SetPosition` (dirty bit
  suppressed) + twin desired-position with a **zero ease budget**. All VERIFIED mechanics;
  their application to engine-spawned creatures is the NEEDS-LIVE part.

### What deliberately changes: the local enemy AI is disarmed

Every driven enemy is re-claimed **each pass** (`Odyssey.ClaimCreature`: commandable = 1,
`ClearAllActions`, movement permitted). Reasoning:

- An armed `ActionMoveToPoint` at the queue head re-commits the creature's position at the
  server loop's rate — the contended-writer problem that made direct placement look broken
  for phantoms (VERIFIED, realtime-creature-movement §2.1). Enemies re-queue actions
  constantly, so a one-time claim is not enough.
- Consequence, accepted with eyes open: **on the mirroring instance, enemies no longer act
  locally.** They stop attacking the mirroring player. That fight was already cosmetic
  (copies are pinned min-1-hp; the owner's health is truth) and it diverged from what the
  owner saw. The mirroring player still attacks INTO the fight — damage reports min-merge
  on the owner exactly as before.
- What is genuinely lost: enemy THREAT to the mirroring player. That is combat-participation
  work (the owner's enemies must be able to choose the phantom as a target and the hit must
  travel to the mirroring player's real body) and it is tracked as its own design problem —
  see §Options below. Presence sync neither helps nor hurts it.

### Standing animation — DESIGN + NEEDS-LIVE

While an enemy moves, gait comes from the measured stream speed (the owner's `+0xD4` lags,
same reason as phantoms). While it stands, the owner's looping id is applied through the
twin's animation sync handler if it is in the plausible looping band (`0x2710..0x27FF`),
else idle. That should carry combat-ready stances across. Fire-and-forget swings do NOT
travel (they never touch `+0xD4`); a mirrored fight therefore shows bodies in the right
places holding the right stances, but the owner-side swing animations are still local.
Wiring enemy one-shots through the existing `AnimationHook` choke point is a natural
follow-up if it reads as wrong in play.

## Safety rails

| Guard | Why |
| --- | --- |
| Cached pointers revalidated every frame (id @`+0x04` and type @`+0x08` == 5) | the 0g lesson: module changes free creatures and reused memory reads plausible |
| Dead flag → stop driving, forget the slot | driving a corpse stands it back up; the kill path owns the body |
| Skip ids matching any local phantom id | host encounter spawns and local phantom spawns allocate from the same id range — collision is possible, not paranoia |
| Skip the local player and any `+0x4C0 != 0` creature | party sync owns companions; two writers again otherwise |
| Whole pass skipped while a local conversation runs | the dialogue system positions and disarms creatures itself; the owner's agent holds publication during its own dialogues for the same reason |
| `enemy_drive=0` in coop.ini | full kill switch back to health-only mirroring |
| Implausible positions rejected (0,0 / NaN) | a half-written slot or failed read must not teleport a body |

## NEEDS-LIVE checklist — first pass run 2026-08-06 (Handoff 0i.3), same day as the design

1. ~~**Does a driven enemy render at the owner's position?**~~ **VERIFIED LIVE**: two-instance
   run, 27/27 tag-matched creatures at identical positions and health across the games,
   and a probe of every client twin (`+0x224 -> +0x24`) against its server object read
   **28/28 twins, worst gap 0.000**. Health mirroring onto a driven copy also intact
   (`sethp` on the host reached the client within seconds).
2. **Does re-claiming every frame starve anything that matters?** Watch for: script
   heartbeats that expect their queued actions to survive (patrol loops are expected to
   stop — that is the point — but a quest script wedging is not), and the perception
   system spamming. No adverse signs on the inert Endar Spire save; a scripted module is
   the real test.
3. **Commandable forced to 1:** a cutscene that sets an enemy non-commandable gets it
   forced back. The dialogue skip should cover the visible cases; verify none leak.
4. **The standing-stance mirror:** does applying the owner's `+0xD4` through the twin sync
   handler show combat-ready correctly, and does the `0x2710..0x27FF` band ever admit an
   id that misbehaves? Needs a human watching a real fight.
5. **Client attack targeting:** the mirroring player clicking a driven enemy must still
   start their attack round normally (the enemy's own queue being empty should not matter
   — targeting reads the body, not the queue — but verify in play).
6. **Perf:** no measurable trouble in the live run (census 25, both instances smooth for
   the duration); re-check only if a fuller module misbehaves.

One census observation from the run, working as designed: an NPC that is party-followed on
the MIRRORING side (`end_trask`, +0x4C0 non-zero there) is skipped by the enemy drive and
owned by the party drive instead — that is why the client logged 24 of 25.

## Extension 2026-08-06 (later): plot creatures ride — scripted scenes sync

The census originally excluded plot-flagged creatures ("its health is not ours to
replicate") — which is exactly what most CUTSCENE ACTORS are, so scripted scenes played
out differently on the two screens. Protocol v13 includes them with a `Plot` flag:

- **Presence replicates like anyone else's** — the mirroring instance drives the body, so
  a scripted walk/confrontation is blocked identically on both screens (the drive already
  pauses during a local conversation, so the dialogue system keeps the stage when it wants
  it).
- **The combat half must never touch a plot creature**: no min-1-hp pin, no health mirror,
  no death (`CombatController` skips on the flag), and a peer's damage REPORT for one is
  ignored on the owner (`TryApplyEnemyFloors` checks the owner's own plot set — a peer
  cannot un-plot a body). Un-killable is the module's rule; the session only agrees.
- **The cap now sheds the right bodies.** A module can hold more creatures than the
  32-slot snapshot; the census sorts engine-flagged in-combat creatures first (the honest
  fight signal — faction semantics are a matrix lookup the external agent cannot make,
  combat-participation.md §2), then everyone else nearest-first from the local player, and
  truncates. A fight can no longer be shed in favour of a distant bystander.

NEEDS-LIVE added: whether driving a plot actor mid-scripted-scene (outside a
conversation) fights the script's own movement — the owner's positions win by design, but
the visual result needs eyes; and a census-over-32 module to see the prioritisation shed
gracefully.

## Combat participation — the options this sets up, none built

The remaining seam: enemy threat to the mirroring player. Recorded so the next session
starts from the fence posts, not from scratch.

- **(a) Phantom as decoy target on the owner.** Make the owner-side phantom targetable by
  enemy AI, replicate "attacked the phantom" events to the mirroring player and roll/apply
  the damage on their instance. Hazard, already paid for once: a targetable-but-unkillable
  body is an **immortal aggro sink** (`CreateNewAttackActions` releases a target only when
  it reads dead — VERIFIED, Handoff 7.4). Any version of (a) must make the phantom's death
  state follow the real player's, which death-handling already mirrors as a pose.
- **(b) Threat ledger without engine targeting.** The owner computes which enemies WOULD
  engage the mirroring player (distance/faction), streams a per-enemy target hint, and the
  mirroring instance applies scripted damage ticks locally. No engine AI involvement, no
  aggro sink, but the fight choreography (who swings at whom) stays visually wrong.
- **(c) Full role swap per encounter.** Whoever an encounter "belongs" to simulates it;
  the other mirrors. Generalises presence sync; needs an ownership handoff protocol.

(a) is the honest one and the phantom's damage-visibility redesign (plot flag already
removed) points that way. It needs its own RE pass on target selection
(`CreateNewAttackActions 0x005B6980`) before any code.

## ⚠ Correction 2026-08-07: the mirror never CREATES an enemy

This doc and Handoff 0i both read as though an owner-side spawn arrives on the mirror
through this channel. It does not, and never did. `CombatController.Apply` and
`EnemyPresenceController.DriveOne` both open with `Odyssey.ResolveServerObject(id)` and
return on 0: the channel drives copies that already exist in both instances (everything the
module's GIT placed), and silently skips anything the owner alone has. Encounter spawns are
exactly that.

Found while building client-triggered encounters; the fence posts for closing it — including
the cheap cross-instance key for the blueprint — are in
[client-encounters.md](client-encounters.md) §8.

## ⚠ Root cause 2026-08-15: the census published GHOSTS — ids their own publisher could not resolve

The defect this closes was found from the other end, in the peer-damage ledger: a client's
`slay` banked 200 points of damage against enemy `0x35`, forwarded it to the host, and the
host answered `combat: a peer owes 200 damage to 0x35, which does not resolve here` — for an
id the host had **published in its own enemy census**. `tools/creatures.ps1` on the host then
found no `0x35` at all among its 38 creatures. See `docs/live-verification.md`
(2026-08-15) for the full measurement chain and
[peer-damage-ledger.md](peer-damage-ledger.md) for what such a debt costs.

### What was actually wrong

**A vtable scan cannot tell a live object from a destroyed one.** Everything the agent knows
about creatures comes from `KotorGameBridge.ScanCreatures`, which sweeps private committed
memory for the 4-byte `CSWSCreature` vtable value `0x007470C8` and reads the object id out of
the following word. `operator delete` does not clear either of them, so a freed creature block
keeps reading as a creature — with a plausible tag, position and hit-point count — until
something else claims the memory. The census then publishes an id that belongs to nothing.

The host dump carries the proof independent of any theory about how the ghosts arise: it
shows **the same object id at two different addresses** (`0x38` at `0x1505B674` and at
`0x1923A620`). The engine's array maps one id to exactly one object, so at least one of those
two was never in it.

**Nothing downstream could catch it**, which is why it survived so long:

- ids in the census are only ever *consumed* by the peer, which cannot know what the
  publisher can resolve;
- the mirror MATERIALISES anything it cannot find (`materialise: owner 0x1A -> local 0x135`),
  so presence, health and death all look healthy on a ghost id;
- the one channel that must resolve the id on the publisher's side — a peer's forwarded
  damage — skipped it silently until the 2026-08-15 log line was added.

A second, quieter consequence of the same scan: a duplicated id makes the census publish
whichever of the two addresses the scan reached last, so a *live* creature's presence and
health could be read out of a dead body's memory.

### The fix — ask the engine, on the publishing side

`CGameObjectArray::GetGameObject` (0x004D8230) is a short open-hash lookup with no state of
its own, so the agent reproduces it rather than calling it — an external process cannot call
into the game anyway
(`EngineGlobals.TryLookupObject`, VERIFIED by transcription from `tools/disasm.py`):

```
array  = [[0x007A39FC] + 0x08]   CServerExoApp
         + 0x04                  CServerExoAppInternal
         + 0x1005C               CGameObjectArray*        (CServerExoApp::GetObjectArray 0x004AED70)
tree   = [array + 0x00]          GameObjectArrayEntry**   (symbol DB: object_tree)
bucket = ((id >> 31) & 1) << 12 | (id & 0xFFF)            8192 buckets; the high bit is a
                                                          SPLIT, not a mask — the engine
                                                          allocates objects and characters
                                                          from two id spaces
for (e = tree[bucket]; e; e = [e + 0x08])                 GameObjectArrayEntry, size 0xC
    if ([e + 0x00] == (id & 0x7FFFFFFF)) return [e + 0x04]
```

`KotorGameBridge.EnumerateCreatures` now confirms every scanned creature through it before
anyone sees the list: kept only when **the id resolves to that creature's own address**. The
address test is the part that matters for duplicates — "the id resolves" is true of both
copies and only one of them is the object the engine will hand a peer's damage to.

This is the right authority precisely because it is the SAME lookup the debt is spent through
(`Odyssey.ResolveServerObject` → `CServerExoApp::GetGameObject`). "Confirmed here" therefore
means exactly "an id a peer can key work by", which is the property the census silently
lacked.

Costs and rates: the scan still runs at most every 15 s (it is expensive and creature
addresses are module-stable), but the confirmation re-runs every 250 ms — because "where are
the creature-shaped objects" is stable while "which of them does the engine still own"
changes the moment anything dies. Two small reads per creature, so a confirmation pass is a
few hundred microseconds.

### Failing safe, in both directions

| Situation | Behaviour |
| --- | --- |
| No server (main menu, mid-transition) | nothing to ask — the scan passes through untouched |
| The gate confirms NONE of many candidates | **distrust the gate, not the world**: publish everything and log it once. A wrong offset would otherwise empty the session of enemies, phantom bodies and the player |
| A transient read failure on one creature | dropped for one 250 ms window, back on the next; the census is a level, and the materialisation lease is 5 s |
| Ghosts found | dropped, and `[census] N of M creature-shaped objects are not in the engine's object array` says so — never silent |

### The other half: an id is only meaningful in one id space

The same run exposed a second id-space fault in the cheat path. On a mirroring instance the
bare `slay` sweep reads the OWNER's census (that is the only space a forwarded debt can be
keyed by), but `CheatController.TryResolveTarget` then resolved that number against the LOCAL
engine — so wherever the mirror had materialised a stand-in (`owner 0x1A -> local 0x135`) the
kill was refused as "no creature here answers to that", and where the number happened to name
a different local creature the phantom/companion/plot rules were judged against the wrong
body. `CheatAuthority.MirrorIds` now sorts the spaces out before anything is resolved:
`ToLocal` first (an arriving id is nearly always the owner's), then `ToOwner` (somebody typed
one of our stand-in ids), then GIT identity. The body is resolved in local space; the debt is
still keyed in the owner's.

### Tests

`tests/KotorCoop.Tests/LiveObjectGateTests.cs` — the bucket arithmetic (including that the
high bit selects the other half of the table rather than being masked away), chain walking,
a circular chain terminating, a ghost dropped beside a live object, a duplicate id keeping
only the address the array names, and the distrust-yourself fail-safe.
`CheatAuthorityTests` covers the four `MirrorIds` cases.

### NEEDS-LIVE

- The `+0x1005C` array pointer and the bucket walk were transcribed from the instruction
  stream, not observed. The fail-safe makes a wrong one harmless (it confirms nothing, so
  everything is published and the log says so) — but the confirming evidence is a `[census]`
  line naming a live array address, plus a client `slay` that kills.
- Whether ghost ids are dominated by a previous module generation (the duplicate-id pattern
  suggests it) or by ordinary mid-module destruction. The fix does not care; the answer would
  tell us whether the 15 s scan TTL is also worth shortening.

### ⚠ Round two, 2026-08-15: A SILENT DIAGNOSTIC IS NOT A DIAGNOSTIC

The first live run of the fix produced a result nobody could read, and the reason is worth
more than the fix itself.

The gate only spoke when it DROPPED something. So an empty log meant any of:

1. it ran and found no ghosts;
2. it never ran, because the object array did not resolve — the pass-through case, which was
   coded to return quietly;
3. the diagnostic sink was never wired at all.

Those three need three different fixes, and the run could not distinguish them. Worse, the
one number that did move (census 32 → 25) was equally consistent with all three, because the
32 was the census CAP and any change to the world moves the count. A whole round produced no
usable evidence.

What changed:

- **Every pass reports, including the do-nothing ones.** `CensusGateReport` has a state for
  each of the four outcomes — never run, nothing scanned, no object array (`NOT CHECKING`),
  confirmed, distrusted — and each describes itself. A line is emitted on change and, failing
  that, every 30 s. Silence is now provably "this code is not running".
- **The drop names the ids it dropped.** "7 ghosts" does not settle an argument about `0x35`;
  "7 ghosts, one of which was `0x35`" does.
- **The gate is applied at the PUBLISH point too** (`ConfirmPublished`, sharing
  `LiveObjectGate.ConfirmIds` so there is one rule and not two). The scan gate sits behind a
  cache and a cap, and after a round spent asking "is the gate even on the list being
  published?" the answer needed to be structural rather than argued: nothing leaves
  `ReadEnemies` that the object array did not confirm on the way out.
- **The state can be ASKED for, not waited for**: a new `census [id]` console verb prints the
  gate state, how many published ids this engine resolves, and — for any disputed id — its
  verdict in all three lists at once (object array, raw memory scan, census). Their
  disagreement IS the finding, so they are printed on one line:

  ```
  census: 0x35  object array: NOT HELD  |  raw scan: absent  |  census: hp 12/12 at 33.6,22.7
  ```

  In the raw scan but not the array is a ghost the gate should have caught. In neither means
  the id never came from this instance at all — which would move the search to the wire (a
  snapshot from an older world) rather than to memory.

Open, and the reason the verb exists: on the failing run the disputed id was in the host's
published census but in NEITHER the host's `creatures.ps1` scan nor its in-process
`ResolveServerObject`. Nothing in this codebase can currently produce that combination — the
census is built only from `EnumerateCreatures` (verified by reading every line of
`ReadEnemies`) — so one of the three observations is not measuring what it appears to.
`creatures.ps1` is a candidate: it reads each region in ONE `ReadProcessMemory` and silently
skips the whole region when that fails, where the agent's scanner reads in chunks. `census`
answers the question from inside the publisher instead of inferring it from three outside
views.

### Round three, 2026-08-15: the two lookups disagree IN THE SAME PROCESS

Round two's diagnostics worked and narrowed the defect to a single contradiction. Verbatim
from the rig (host in `END_M01AA`):

| Question | Answer |
| --- | --- |
| agent: `census` on the host | `25 id(s); 25 resolve in MY engine, 0 do not` |
| agent: `census 0x35` on the host | `object array: held at 0x19179E38 | raw scan: 0x19179E38 'end_weld'` |
| plugin: `slay 0x35` on the host | `no object 0x35 in this module` |
| host plugin log | `combat: a peer owes 200 damage to 0x35, which does not resolve here` |

So the agent's external transcription of `CGameObjectArray::GetGameObject` says the id is
held, at an address the raw vtable scan agrees carries an `end_weld` — and the plugin's
in-process `Odyssey.ResolveServerObject`, which calls the engine's own
`CServerExoApp::GetGameObject` (0x004AE750), says nothing answers to it. **Same process, same
id.**

What the binary rules OUT (all read this round, so nobody re-derives them):

- **It is not a different array.** `CServerExoApp::GetObjectArray` (0x004AED70) is
  `mov eax,[ecx+4]; mov eax,[eax+0x1005C]; ret`, and `CServerExoAppInternal::GetGameObject`
  (0x004B1700) reads `[ecx+0x1005C]` for its own lookup. The agent walks
  `[[0x007A39FC]+0x08]+0x04+0x1005C`. Identical field, identical chain.
- **It is not the not-found sentinel.** The engine returns the object only when the array's
  return byte equals `[0x74666C]`, and that byte is **0x00 in `.rdata`** — read-only, so
  nothing writes it at runtime. `CGameObjectArray::GetGameObject` returns `al = 0` on a hit,
  so the gate passes. (The neighbouring typed getters — `GetItemByGameObjectID` 0x004B1740,
  `GetCreatureByGameObjectID` 0x004B1790 — use the SAME sentinel and then additionally check
  the object type at `+0x08`; the generic one this code calls does not.)
- **It is not the bucket arithmetic.** Both sides compute
  `((id>>31)&1)<<12 | (id&0xFFF)` and compare `id & 0x7FFFFFFF`.

Which leaves possibilities that only the running game can separate, and every previous
attempt compared measurements taken seconds and processes apart. So this round ships the
instrument that asks both questions **in one frame, on the game thread**: a `resolve <id>`
verb whose plugin half (`Odyssey.DescribeObjectResolution`) prints the pointer chain, the
array reached BOTH ways (the `+0x1005C` field and the engine's own accessor), the engine's
lookup, an in-process transcription of that same lookup, and what `ResolveServerObject`
finally decides — plus the vtable, `+0x04`, `+0x14` and type at whatever pointer came back.
The full line goes to the plugin log; only a summary to the 96-byte console answer.

Reading its answer decides the fix:

| `engine` | `walk` | Meaning |
| --- | --- | --- |
| `0` | non-zero | The engine's call is not the lookup we think it is; the agent's gate is right and `ResolveServerObject` must change |
| non-zero | non-zero | The call works and the POST-CHECK is rejecting it — `+0x04`/`+0x14` in the same line say why |
| `0` | `0` | The array genuinely lacks the id, and `array(+0x1005C)` vs `array(engine)` in the same line says whether the agent walked a different table |

Until that line exists, any "fix" would be a guess dressed as a change.

### Two footguns closed in passing

- **`#NN` parsed as DECIMAL.** `slay #35`, typed to kill the disputed `0x35`, silently killed
  `0x23`. Every id this project prints is hex, and the drop harness already read `#1f` as hex
  (`AgentDashboard`), so the console parser was the odd one out rather than this being a
  matter of taste. All three spellings are hex now; a BARE token is only an id when it is all
  digits, because real tags (`beef`, `cad`) are valid hex and `slay beef` must still mean the
  tag.
- **Diagnostics were being truncated at 96 characters** (`SharedBlock.ConsoleReplyLength`),
  which cut every answer off exactly where the numbers started while keeping the prose that
  explained them. Every diagnostic line is now built to fit, with a unit test asserting it
  against `CensusGateReport.MaxLineLength`, and the long form goes to the logs instead.

### Round four, 2026-08-15: the lookup is exonerated, and `+0x14` is a red herring

`resolve` answered, and it answered cleanly (host, plugin log, verbatim):

```
resolve: 0x35: ... array(+0x1005C) 0x33EE540 array(engine) 0x33EE540
   | engine 0x19169BB8 walk 0x19169BB8 resolve 0x19169BB8
   | at 0x19169BB8: vtable 0x7470C8 id+4 0x35 id+0x14 0x130014 type 0x5
resolve: 0x1A: ... array(+0x1005C) 0x33EE540 array(engine) 0x33EE540
   | engine 0x191DD240 walk 0x191DD240 resolve 0x191DD240
   | at 0x191DD240: vtable 0x7470C8 id+4 0x1A id+0x14 0x0 type 0x5
```

**So the lookup is not the defect.** One array (the `+0x1005C` field and the engine's own
accessor agree), and for the disputed id the engine's call, the in-process transcription and
`Odyssey.ResolveServerObject` all return the same live pointer. Everything this investigation
suspected of the lookup is now excluded by measurement rather than by argument.

#### What `+0x14` is: part of the object's name, not a flag

`CSWSObject::Constructor` (0x004CFCB0) builds its members in address order —
`lea ecx,[esi+0x0C]` into the `CExoLocString` constructor (0x005E9E10), then
`lea ebp,[esi+0x18]` into the `CExoString` constructor for `tag`, then `[esi+0x20]` for the
`CResRef` portrait — and it never touches `+0x10` or `+0x14` separately.
`CSWSObject::GetLastName` (0x004D0190) is one instruction, `lea eax,[ecx+0x0C]`. So
**`0x0C..0x18` is the whole of `last_name`, and `+0x14` is its third word** (the symbol
database's `CExoLocString size=0x8` is one of its known-stale rows; the constructor is the
authority).

`0x130014` versus `0x0` therefore says only that `end_weld` carries a localised name payload
and `0x1A` does not. It is not a type, not a flag, not a liveness marker, and it cannot be the
discriminator: `ResolveServerObject`'s FIRST probe (`+0x04 == id`) matched for both ids, which
is exactly what the `resolve` line shows it returning. (For completeness: creature display
names do not live here at all — `CSWSCreature::GetFirstName`/`GetLastName` (0x004F8310 /
0x004F8330) both read the stats block at `+0xA74`.)

#### Which getter each call site reaches — traced, and it is the same one

| Call site | Reaches |
| --- | --- |
| `Odyssey.ResolveServerObject` | `GetGameObjectFn` = **0x004AE750**, `CServerExoApp::GetGameObject` — the generic getter |
| `CheatController.TryResolveTarget` (the `slay` refusal) | `Odyssey.ResolveServerObject` |
| `PeerDamageController.ApplyOwed` (the debt refusal) | `Odyssey.ResolveServerObject` |

There is no second path: a grep for the typed getters (`0x004AE760` / `0x004B1740`
`GetItemByGameObjectID`, `0x004AE770` / `0x004B1790` `GetCreatureByGameObjectID`) finds them
nowhere in the codebase — the only mention is a comment about the CLIENT-side creature getter
in an unrelated camera path. The type-filter hypothesis is a good one and it is dead in this
tree, though the reason it was worth having is now written into `ResolveServerObject`'s doc
comment: those getters re-test the object type byte at `+0x08`, which is the field the
object-type trap says never to trust, so a future call site that reaches for one "because it
wants a creature" will reject live creatures intermittently and silently.

#### What is left, and how the next run tells them apart

Three observations that cannot all be of the same instant: `resolve` finds the object,
`slay 0x35` refuses it, and the peer-damage pass refuses it. What survives is either that the
object is TRANSIENT (the three commands looked at different moments), or that something
between `ResolveServerObject` returning a pointer and the caller acting on it differs from
this branch (the integration tree carries two other agents' cheats in the same file). So every
refusal now prints its own working:

- **`ResolveServerObject` no longer discards a found object silently.** The engine returning a
  pointer whose `+0x04` and `+0x14` both fail to match was the last silent branch on this
  path; it now logs the pointer, both fields and the vtable. From a caller's side that case
  was indistinguishable from a missing object.
- **A refused cheat target logs the full chain at the instant it refuses** — the same line
  `resolve` prints, taken inside the failing call. If the engine resolves the id there and the
  cheat still refused, the fault is between the two and the line proves it.
- **The damage ledger says WHICH KIND of unresolvable.** It records the ids that have resolved
  at least once this epoch, so a refusal now reads either "does not resolve here ANY MORE - it
  did earlier this epoch, so the body was destroyed" (a race, and the debt is held for its
  return) or "never has this epoch - the peer keyed it in an id space this instance does not
  share" (the census defect, and the debt can never land). It also complains **once per
  distinct id** instead of ten times about one — three rounds of logs were ten lines about a
  single id while a second id would have been the whole story.

#### Cheat kinds collided in the merge, and now a test catches that

Three agents in parallel each took "the next" cheat kind; two picked 7, and a human caught it
while merging and renumbered both halves by hand. The table is written twice on purpose (the
halves cannot share a type) and a mismatch runs a DIFFERENT VERB from the one typed — a silent
failure of exactly the family this whole investigation is about. `Resolve` is **9** here now,
and `CheatKindSyncTests` reads both source files and asserts they agree and that no two kinds
share a number.

## Files

- `src/KotorCoop.Memory/EngineGlobals.cs` — the server object array walk
  (`TryGetServerObjectArray`, `TryResolveServerObject`, `TryLookupObject`).
- `src/KotorCoop.Memory/LiveObjectGate.cs` — the keep/drop policy, its self-distrust, and
  `CensusGateReport`, which makes every outcome (including the two that check nothing) speak.
- `src/KotorCoop.Agent/CoopCommandDispatcher.cs` — the `census [id]` and `resolve <id>` verbs.
- `src/KotorCoop.Plugin/Odyssey.cs` — `DescribeObjectResolution`, both lookups in one frame.
- `src/KotorCoop.Protocol` — `EnemyState` presence fields, snapshot codec, protocol v12.
- `src/KotorCoop.Memory/KotorGameBridge.cs` — presence reads in `ReadEnemies`, presence
  region writes in `TryWriteEnemies`, block signature v20, and the liveness confirmation in
  `EnumerateCreatures`/`Confirm`.
- `src/KotorCoop.Plugin/CheatAuthority.cs` — `MirrorIds`, the owner/local id-space rule.
- `src/KotorCoop.Plugin/SharedBlock.cs` — region `0x1400..0x1810`, `TotalSize 0x1A00`.
- `src/KotorCoop.Plugin/EnemyPresenceController.cs` — the driver.
- `src/KotorCoop.Plugin/Odyssey.cs` — `DriveEnemy` / shared `DriveBody`, gait table
  widened to 48 creatures.
