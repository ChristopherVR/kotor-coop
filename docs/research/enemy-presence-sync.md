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
  everything is published and the log says so) — but the confirming evidence is the log line
  `[census] N of M ...` appearing with N small and non-zero, plus a client `slay` that kills.
- Whether ghost ids are dominated by a previous module generation (the duplicate-id pattern
  suggests it) or by ordinary mid-module destruction. The fix does not care; the answer would
  tell us whether the 15 s scan TTL is also worth shortening.

## Files

- `src/KotorCoop.Memory/EngineGlobals.cs` — the server object array walk
  (`TryGetServerObjectArray`, `TryResolveServerObject`, `TryLookupObject`).
- `src/KotorCoop.Memory/LiveObjectGate.cs` — the keep/drop policy and its self-distrust.
- `src/KotorCoop.Protocol` — `EnemyState` presence fields, snapshot codec, protocol v12.
- `src/KotorCoop.Memory/KotorGameBridge.cs` — presence reads in `ReadEnemies`, presence
  region writes in `TryWriteEnemies`, block signature v20, and the liveness confirmation in
  `EnumerateCreatures`/`Confirm`.
- `src/KotorCoop.Plugin/CheatAuthority.cs` — `MirrorIds`, the owner/local id-space rule.
- `src/KotorCoop.Plugin/SharedBlock.cs` — region `0x1400..0x1810`, `TotalSize 0x1A00`.
- `src/KotorCoop.Plugin/EnemyPresenceController.cs` — the driver.
- `src/KotorCoop.Plugin/Odyssey.cs` — `DriveEnemy` / shared `DriveBody`, gait table
  widened to 48 creatures.
