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
- `EnemyState` carries presence on the wire **in the snapshot only** (protocol v12). A
  client's `EnemyReport` still carries health alone: reports exist so a client's damage
  counts, and its local positions are never authoritative.
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

## Files

- `src/KotorCoop.Protocol` — `EnemyState` presence fields, snapshot codec, protocol v12.
- `src/KotorCoop.Memory/KotorGameBridge.cs` — presence reads in `ReadEnemies`, presence
  region writes in `TryWriteEnemies`, block signature v20.
- `src/KotorCoop.Plugin/SharedBlock.cs` — region `0x1400..0x1810`, `TotalSize 0x1A00`.
- `src/KotorCoop.Plugin/EnemyPresenceController.cs` — the driver.
- `src/KotorCoop.Plugin/Odyssey.cs` — `DriveEnemy` / shared `DriveBody`, gait table
  widened to 48 creatures.
