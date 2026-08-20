# Shared-state audit: what actually replicates, and what only looks like it does

Measured on the live pair (pids 17208 GOG / 46088 p2, Endar Spire, 2026-08-11) after a
playtest produced seven complaints at once. They are not seven bugs: one was a wrong
offset, and the rest are the same architectural gap wearing different clothes.

## 1. Max hit points: a field the engine never writes

`CSWSObject::GetMaxHitPoints` (0x004D01A0) is literally `mov ax,[ecx+0xE0]`, so +0xE0 is
right for doors, placeables and ordinary creatures. It reads **0 on both players**.

`CSWSCreature::GetMaxHitPoints` (0x004ED310) overrides it and branches on `stats+0x6C`
(`is_pc`). For a PC the engine never stores a total at all — it re-adds one from the
per-level records every time it is asked, in the loop at 0x004ED360..0x004ED3CA:

    max = Σ over levels ( levelStats[i]->hitPoints(+0x2B) + stats->conModifier(+0xEE) )

Field layout used, all verified live:

| what | where |
|---|---|
| `is_pc` | `CSWSCreatureStats + 0x6C` (int; 1 selects the accumulate path) |
| level list data ptr | `CSWSCreatureStats + 0x28` (`CExoArrayList<CSWLevelStats*>`) |
| level list count | `CSWSCreatureStats + 0x2C` (capacity follows at 0x30) |
| CON modifier | `CSWSCreatureStats + 0xEE` (byte, added once PER LEVEL) |
| hit points rolled | `CSWLevelStats + 0x2B` (byte) |

Measured: one level, rolled 10, CON modifier +2 → **12**, exactly the host's reading at
full health. Implemented as `KotorGameBridge.TryReadMaxHitPoints`, which reproduces the
branch (PC → accumulate, anything else → the stored field) and feeds both the player state
and the enemy snapshot.

⚠ The general lesson, again: a getter that is one `mov` is not proof the field is live.
Check whether a DERIVED class overrides it before trusting the base offset.

## 2. Combat state is reported, never shared — and on a client it cannot be

Measured: host creature `+0x4E0 = 1`, client `+0x4E0 = 0`, same area, same moment.

`PlayerState.FlagInCombat` is set by the bridge and read by the dashboard. **Nothing
anywhere applied a peer's combat flag.** Two players in the same room during the same
fight produced "IN COMBAT" on one readout and "exploring" on the other.

The deeper reason matters more than the missing plumbing: on a client the enemies are
MIRRORS — driven puppets, not creatures its own AI is fighting — so the client's engine
genuinely never enters combat. Forcing `+0x4E0` on the client would be a lie the engine
recomputes away, and combat state gates round timing and AI.

### The state cannot be replicated by writing it — measured

A full channel was built (`SharedBlock.OffsetSessionInCombat` at 0x1E50, block version 29,
`CoopAgent.PumpSharedCombat`, `CombatStateController`, and
`Odyssey.SetPlayerCombatState` calling `CSWSCreature::SetCombatState` at 0x004F2610). The
channel is right and stays. The last step does not work:

    trial 1 : reverted after 2 ms
    trial 2 : reverted after 0 ms
    trial 3 : reverted after 1 ms

A written in-combat flag on a real player with no attackers is gone in **0–2 ms**. The
field is not a latch the engine consults; it is continuously re-derived from whether the
creature is actually being fought. There is a second writer past the one in
`SetCombatState` — inside `CSWSCreature::ProcessPendingCombatActions` at 0x004F7C0E. No
re-assertion cadence a frame hook can manage competes with that.

`CombatStateController` is therefore **off by default** (`coop.ini combat_state_sync=1`
reproduces the measurement), rather than shipped as a feature that visibly does nothing.

⚠ Note on measuring this: a bare vtable scan for `CSWSCreature` (0x007470C8) plus an
`is_pc` check lands on a COMPANION — party members carry `is_pc` too. The first attempt at
this test wrote the flag onto Trask and concluded the channel was broken. The bridge
publishes the creature it actually chose at `block+0x258`; use that.

### The suppressor is the action wipe, not the attack bit

`MarkAsMirroredEnemy` clears the attack-permitted bit, but it has exactly ONE caller
(`MaterialisedEnemies`), so ordinary module enemies — the common case — keep theirs. What
actually stops them is `EnemyPresenceController` calling `Odyssey.ClaimCreature` on every
mirrored enemy on every pass, and `ClaimCreature` calls
`CSWSObject::ClearAllActions(0x004CCD80)`. That call does not distinguish a queued
MoveToPoint (which fights our placement, and is why the wipe exists) from a queued attack
(which is the entire fight). So the mirror's enemies have their swings deleted several times
a second, never land one, and the mirror's combat state — derived from being fought — stays
false through the whole battle.

Implemented as `coop.ini local_combat` (default 0):

* `Odyssey.ClaimCreature(nint, bool clearActions)` — the wipe becomes optional.
* `EnemyPresenceController.LocalCombat` — leaves the queue alone when set, so the local
  copies can finish a combat round. Position is still driven from the owner every pass.
* `IncomingDamageController` reads the SAME flag and stands down when it is set, absorbing
  the owner's forwarded total instead of applying it. The two must move together: with local
  enemies swinging here and the owner forwarding what its copies did to this player's
  phantom over there, every hit would otherwise land twice.

⚠ NOT YET VERIFIED, AND THAT IS WHY IT DEFAULTS OFF. The staging attempt — co-locate both
players, teleport a live enemy onto them by writing its position — produced no fight on
EITHER instance (`host entered combat: no`), so it says nothing about the change. Writing a
creature's position does not raise a perception event, and the AI never noticed the player.
A real verification needs a fight the engine itself starts.

⚠ The failure mode if it is switched on and the local enemies still do not engage is that
the client becomes INVULNERABLE: forwarded damage is absorbed and nothing local replaces it.
Verify engagement before trusting it in play.

### Adversarial review found this is not one playtest away

An independent review of the change (2026-08-11) found defects that are structural, not
tuning. Three were fixed immediately; the rest are open and are why the flag stays off.

FIXED:

1. **Materialised enemies could never swing.** `MarkAsMirroredEnemy` clears attack-permitted
   bit 0x04, and `ClaimCreature` ORs only 0x02 back — so the clear was permanent for the life
   of the body. Those bodies are exactly the host encounter spawns and scripted waves that
   `local_combat` exists to make dangerous: they would hold a target, queue attacks, deal
   nothing, and have the owner's report absorbed. **A host-triggered ambush would have left
   the client completely invulnerable.** The clear is now skipped under `local_combat`, and
   `EnemyPresenceController` re-asserts the bit every drive pass (a spawn-time decision does
   not survive the body's lifetime).
2. **No role gate.** `local_combat` is read per-install from `coop.ini` and never crosses the
   wire, so the absorb also ran on an instance mirroring no enemies. Now gated on
   `block.DamageDealtEnabled`, the same "am I mirroring" test PortraitController uses.
3. **Silent failure.** The absorb logged five times and then went quiet forever — total
   invulnerability with nothing in the log. Now a running total on a 10-second heartbeat.

FIXED SINCE (2026-08-13/14) — code-complete, still never live-verified:

4. **Target divergence breaks the authority split.** Fixed: `PhantomController.ApplyTargetable`
   (`src/KotorCoop.Plugin/PhantomController.cs:680-740`) holds a peer's phantom at faction
   Neutral on a MIRRORING instance (`holdNeutral = LocalCombat && block.DamageDealtEnabled`,
   never on the owning instance), so the only player-faction targets `GetNearestEnemy` can
   pick are the local PC and mirrored companions. `PacifyPhantom` also releases any attacker
   already locked on when the flag flips.
6. **Mirrored companions become an immortal aggro sink.** Fixed:
   `PartyPresenceController` (`src/KotorCoop.Plugin/PartyPresenceController.cs:149-316`) holds
   a mirrored companion Neutral and min-1-hp pinned under `local_combat` (same mechanism as
   phantoms), and — the sharper fix — health is now force-written unconditionally, including
   to zero (`Odyssey.GetCurrentHitPoints(creature) != member.Health`, no longer gated on
   `member.Health > 0`), so a burst between sync passes can no longer leave a companion downed
   under a full health bar forever.
7. **The position fight returns.** Fixed: `Odyssey.ClaimCreatureForFight`
   (`src/KotorCoop.Plugin/Odyssey.cs:511-512`) reads the queue's head action type and clears
   only when it is move-class, leaving a queued attack alone. `EnemyPresenceController`'s drive
   pass (`src/KotorCoop.Plugin/EnemyPresenceController.cs:384-391`) calls this instead of the
   blanket `ClaimCreature(..., clearActions: true)` when `LocalCombat` is on. The doc's own
   "leave the queue alone" first attempt was tried and rejected in the code comment at the same
   site — confirmed worse than the bug it replaced, for exactly the wedged-`MotionSmoother`
   reason predicted here.
5. **Blanket absorb, partial local reproduction.** Substantially mitigated, not merely
   asserted: `IncomingDamageController` (`src/KotorCoop.Plugin/IncomingDamageController.cs:
   109-229`) now only absorbs when `EnemyPresenceController.IsLocallyEngaged(attackerId)` is
   true — a freshness-gated (`EngagedFreshTicks`) membership check against the set of enemies
   this instance is actually driving right now — so mines, traps, scripted `EffectDamage`,
   census-shed or out-of-range enemies, and the conversation hold all fall through to APPLY
   rather than being silently absorbed, because none of them will ever appear in that set.
   On top of attribution, a **self-correcting watchdog** samples the local player's own hit
   points every pass: if `AbsorbProofThreshold` (40) worth of damage has been absorbed on the
   claim that local copies are dealing it, and this player's health has not actually fallen
   even once, the claim is treated as false and the channel reverts to applying forwarded
   damage again — logged loudly (`WATCHDOG TRIPPED`), not silently. This directly answers the
   invulnerability failure mode this section used to warn about: the fallback is now automatic
   rather than something a human has to notice and disable. Not yet closed: whether the
   3-frame attribution window (`LocalCombatIntervalFrames`) is short enough in every case to
   avoid mixing an attributable attacker's delta with an unattributable one in the same pass —
   this needs the live test below to actually stress it, not further reasoning.

None of this has been proven against a real fight. The honest test is not "did it feel like a
fight" — it is a scripted comparison of hit points lost on the mirroring instance against the
owner's forwarded total over the same battle, started by the engine's own AI perceiving a
player (not a teleported-in enemy, which raises no perception event and proves nothing — see
the still-current warning above). `local_combat` stays off by default in
`profiles/coop.ini.example` until that pass has actually run; it was found shipping as `=1` in
the example (contradicting this section's own long-standing warning) and was reverted to `=0`
on the same day this section was corrected.

### The real fix — implemented, unproven

Give the client's engine a genuine reason to be in combat instead of asserting the
conclusion: stop clearing the attack-permitted bit in `Odyssey.MarkAsMirroredEnemy` for
enemies near the local player, so its own copies swing at it and its combat state rises for
the same reason the host's does. That ownership change is built (`local_combat`) and the
damage reconciliation it needed is built too — attribution plus the self-correcting watchdog
above are exactly "a client whose mirrored enemies really attack must not also apply the
owner's damage report for the same swing." What is still missing is proof: nobody has yet
run the live pass this section has asked for since the day it was written.

`AgentDashboard.DescribeSharedCombat` meanwhile combines the flags into a session-level view
and says which engines are engaged and which are only mirroring, so the asymmetry reads as
what it is rather than as a dropped packet.

### The live pass ran (2026-08-20) — and it does not work

First real attempt, with `local_combat=1` set on both installs, against a genuine fight —
not a teleported-in enemy. A single host instance was moved next to a live, untouched
`sithsoldier` pair in `END_M01AA` (`tp`, not a position write on the enemy) and aggroed for
real within ~1 second: `state IN COMBAT combat [FIGHT in END_M01AA - engaged: you]`, HP
falling 12 → 1 under the engine's own AI, no scripting or synthetic trigger involved.

With the host still actively fighting, a second instance connected and was moved to stand
directly beside the same two hostiles (`tp` to within ~2m, matching the proximity that made
the host aggro in ~1s). Over more than 90 seconds standing there:

    connected to 127.0.0.1:27015 as peer 2 ... state exploring (not in combat, not paused)
    combat [FIGHT in END_M01AA - engaged: Lanik; mirroring only: you (their engine is not in combat)]

The client's own engine never entered combat, not once. This is not the target-divergence,
aggro-sink, or absorb-attribution failure modes items 4/5/6 above were fixed against — those
are about a fight going WRONG once engaged. This is more basic: the fight never started
locally at all. Confirmed via the log that the mechanism was genuinely exercised, not
silently skipped — `enemy drive: mirroring 25 of the owner's 25 enemies` fired on the
client at the moment of connection, meaning the two `sithsoldier` bodies were in the driven
set and `EnemyPresenceController.ApplyEnemy` ran `Odyssey.ClaimCreatureForFight` and
`Odyssey.PermitAttacking` on them every pass, per the code at lines 384-399 above. Permitting
an attack and not wiping one in progress was evidently not sufficient to make the mirrored
copy's own AI independently decide to start one — unlike the host's identical, non-mirrored
copy of the same creature type, which aggroed almost instantly under the same proximity.

**Root cause not found.** Candidates worth checking next, in rough order of suspicion:
whether `Odyssey.PermitAttacking`/`ClaimCreatureForFight` only preserve an attack action
already in the queue rather than doing anything to help the AI decide to queue one in the
first place (in which case the fix as built cannot work on a creature that never got a
`NEW ClaimCreatureForFight`-armed hostile role at all — permission and initiation may be two
different things); whether `Drive()`'s per-frame `SetPosition`/`SetFacing` writes reset or
starve whatever internal state a perception/aggro decision depends on, even with the dirty
bit suppressed; or whether these particular creatures never actually reach hostile toward
THIS SPECIFIC client object at all (reputation/faction is per-object-pair in this engine, and
a client's PC is adopted through a different code path than the host's).

**Decision: `local_combat` stays off by default.** The binary safety rule this section has
held since it was written is not "did it break something," it is "did it work" — and it did
not. `profiles/coop.ini.example` is unchanged (`local_combat=0`). This is the first live
result this section has ever had; treat "already fixed" claims about items 4-7 above as
"the identified risks in a working mechanism were closed," not as "the mechanism works" —
those are different claims, and only the second one is what a player experiences.

### The missing initiation call — implemented, NOT yet live-verified (2026-08-20)

A later `local_combat` live pass (a real fight, both instances connected, client standing
beside two genuinely hostile creatures) found something more basic than anything above: the
client's engine never entered combat AT ALL, not once in 90+ seconds, even though
`PermitAttacking`/`ClaimCreatureForFight` ran on the mirrored enemies every drive pass. The
cause, found by disassembly: `PermitAttacking` only sets the `CreAttackPermittedBit` — an
EXECUTION-time gate `AIActionPhysicalAttacks` (`0x005BBBF0`) checks right before running an
attack action ALREADY on the queue. Nothing in the native engine ever queues that first
action on its own; `CSWSCreature::CreateNewAttackActions` (the only place a NEW one gets
queued) has exactly one caller, `EndCombatRound`, reached only from round timers or a round
already in progress — starting the first one is normally the AI script's job
(`DetermineCombatRound`/`ActionAttack`), which never runs for a body this instance only
drives. Permission and initiation are two different things; the mechanism as built only ever
granted the former.

Fix: `Odyssey.QueueAttackAction(creature, targetId)` (`src/KotorCoop.Plugin/Odyssey.cs`) calls
`CSWSCombatRound::AddAttackAction` (`0x004D38B0`, thiscall on the creature's own
`CSWSCombatRound` sub-object at `+0x9C8`, args `targetId, 0, 0, 0x5DC, 1`) directly — the
SAME call `CreateNewAttackActions`'s own re-validation branch makes each round to queue "the
next swing" (independently documented from the caller side in
`docs/research/combat-participation.md` §1.2, matching this disassembly). Gated on the
creature's head action not already being a fight-type action (`Odyssey.IsFightAction`) since
`AddAttackAction` itself has no dedup — it unconditionally allocates and appends a new action
node every call. Wired into `EnemyPresenceController`'s `local_combat` drive pass
(`EnemyPresenceController.cs`, after `RecordEngagement`), gated to the same engagement-range
distance check (factored out as `IsEngaged`, shared with `RecordEngagement`) so a mirrored
enemy nowhere near the local player is never handed a target.

⚠ **NOT YET LIVE-VERIFIED.** This is a genuinely new kind of write for this codebase: a
client's plugin queuing a native combat action on a creature it does not own control input
for, via an engine address (`0x004D38B0`) never previously called from managed code. The
calling convention is read from two independent static sources (direct disassembly of both
this function and its documented caller) but a live pass needs to check, in order: (1) does
the queued action actually survive to execute — does the mirrored enemy visibly swing; (2)
does the client's own `+0x4E0` in-combat flag rise as a result, closing the original gap this
whole section is about; (3) does `IncomingDamageController`'s absorb-gate (`IsLocallyEngaged`)
correctly stand down once local hits are landing, so damage is not double-applied; (4) does
the engagement-range gate correctly stop queuing once the player and enemy separate, so a
fight does not follow a fleeing player forever; (5) whether the `0x5DC`/`1` constants (copied
verbatim from the engine's own call) produce a normal attack cadence or something faster/
slower than intended, since their exact meaning (a delay in ms, and an unidentified flag) was
never fully decoded, only matched byte-for-byte against a real engine call site.
`local_combat` stays off by default until this runs clean.

### The live pass ran (2026-08-20, same day) — the call reaches the engine and does nothing

Reproduced the exact mid-fight-join scenario from the earlier failure: host aggroed for real
on a live `sithsoldier` pair (`tp` to 2m, HP falling under the engine's own AI, pinned at
1 hp by the Endar Spire deck's own `SetMinOneHP`), then a second instance connected and was
moved to the same spot while the host was still actively fighting.

**Two hypotheses this pass killed, with evidence:**

- **Target resolution is fine.** `Odyssey.QueueAttackAction` passes `targetId = *(uint*)(player
  + 0x04)`, which reads `0x7FFFFFFF` for the local player — alarming on its own (every other
  call site in this codebase treats `0 or ObjectInvalid or 0x7FFFFFFF` as "not a real id" and
  filters it out), but `0x7FFFFFFF` turns out to be this engine's own reserved sentinel for
  "the current player," not garbage: `console 'resolve 0x7FFFFFFF'` on the live client answered
  `0x7FFFFFFF resolves to 0x19371C68 here`, and the plugin log's full chain confirms it lands on
  a genuine `CSWSCreature` (vtable `0x7470C8`, `id+4 0x7FFFFFFF`, `type 0x5`) — the actual local
  player object. `AddAttackAction`'s internal target resolution calls the exact same
  `GetGameObject` (`0x004AE750`) that `Odyssey.ResolveServerObject` wraps (proven working every
  day via damage-forwarding), so there is no reason to believe the engine's OWN resolution of
  this id fails inside `AddAttackAction` either.
- **`ClaimCreatureForFight`'s move-only wipe is not re-clearing the freshly queued attack.**
  It only clears when `IsMoveAction(GetHeadActionType(creature))`, and the fight-action and
  move-action code lists are disjoint, so a genuine fight action at the head is never touched.

**What actually happens: `AddAttackAction` is called, does not crash, and changes nothing.**
Added a throttled diagnostic to `Odyssey.QueueAttackAction` (still in the tree, useful for
whoever continues this) logging the head action type immediately before AND immediately after
the call. Real output, unedited:

    queueattack diag: CALLED AddAttackAction(round=0x19352178, target=0x7FFFFFFF) on creature
    0x19351268, head action before 0xFFFFFFFF, IMMEDIATELY after 0xFFFFFFFF (is fight action now: False)

Repeated identically every ~3s for 40+ seconds straight, on a stable non-null `round` pointer.
An "unconditionally allocates and appends" function, called successfully with a resolvable
target and a live round pointer, left the head-of-queue completely unchanged in the SAME
instant it returned — not reverted later, never applied at all. This rules out a race with
some other wipe (there was no window for one) and narrows the open question to inside
`AddAttackAction` itself: either the disassembly's read of its calling convention has a
mistake not yet found (a missing/misordered argument, a wrong flag value in the `0x5DC, 1`
tail nobody has decoded), or the function contains a precondition this investigation hasn't
identified yet (a hostility/faction check on the target, an attacker-side gate unrelated to
the target at all, or something specific to being called from outside the engine's own
call sites, none of which are ever reached from managed code anywhere else in this
codebase).

**Decision: `local_combat` stays off.** Both live installs' `coop.ini` restored to
`local_combat=0`. This is real progress — from "the client's engine never enters combat, cause
unknown" to "the queue-append call itself is a silent no-op, for a reason not yet
identified, with resolution and the wipe-guard both cleared as suspects" — but it is not a
working mechanism yet.

### Instruction-level dissection of `AddAttackAction` (2026-08-20) — the silent-discard site found

Full disassembly of `CSWSCombatRound::AddAttackAction` (`0x004D38B0`, `python tools/disasm.py
0x004D38B0 0x500`) against a live report that the call executes without crashing but never
changes the queue's head action type. Four distinct early-return paths were mapped, all
returning to the shared epilogue at `0x004D399F` with nothing appended:

| # | Address | Condition | Verdict for our case |
|---|---|---|---|
| 1 | `0x004D38CB` | `CServerExoApp::GetGameObject(targetId)` (`0x004AE750`) returns null | ruled out — this is literally the same function `Odyssey.cs`'s `GetGameObjectFn` already calls, and target resolution was independently confirmed live |
| 2 | `0x004D38DA` | `target->vtbl[0x10]()` (AsSWSObject cast) returns null | not ruled out, see below |
| 3 | `0x004D38F3` | `[that]->vtbl[0x94]()` (`GetDead`) returns non-zero | ruled out on paper — `death-handling.md` documents party members' `GetDead` as permanently false, and our target is the local player |
| 4 | `0x004D3913` | target is a door (`vtbl[0x18]` succeeds) AND its `+0x2CC` destroyed flag is set | ruled out — target is a creature, not a door |

**But none of those is actually the mechanism that matches the symptom.** Every branch that
DOES reach the allocation path (`0x004D3919` onward, all five call shapes further down the
function for the different `AddAttackAction` overload variants at `0x004D3B30`/`0x004D3BA0`/
`0x004D3C30`/`0x004D3CE0`) funnels through **one shared helper, `0x004D3660`**, to actually
link the freshly-allocated `0x88`-byte action node into the round's live queue. That helper's
first two instructions are the whole answer:

```
0x004D3664  mov eax, dword ptr [esi + 0x9b0]   ; esi = the round struct (creature+0x9C8)
0x004D366A  test eax, eax
0x004D366D  je 0x4d374b                         ; NULL -> free the new node, return, append NOTHING
```

`round+0x9B0` is already documented (`combat-participation.md` §1.2, line 98 and the field
table) as `CExoLinkedList<CSWSCombatRoundAction>*` — the round's own pending-action list —
and as **set by `StartCombatRound` (`0x004D60A8`)**. If that pointer is null, `AddAttackAction`
allocates a brand-new action node, discovers there is nowhere to put it, frees it via
`0x006FA390`, and returns — silently, with no error, exactly matching the live observation
(call succeeds, head-action-type unchanged, no crash).

**Cross-checked against the "proper" NWScript entry point.** `CSWSCreature::AddAttackActions`
(`0x004FDE40`, plural — what `ExecuteCommandAttack` calls for `ActionAttack()`) was also
disassembled. It calls `ClearAllActions(1)` (`0x004CCD80`), then two more round-struct helpers
(`0x004D3770` — frees the existing action list's contents IF `+0x9B0` is already non-null, a
no-op otherwise; `0x004D37E0` — reinitializes five embedded `0x14C`-byte sub-structures at
`round+4`, unrelated to `+0x9B0`) — **then calls the exact same `AddAttackAction`
(`0x004D38B0`) our fix already calls**, with the same five-argument shape
`(targetId, 0, 0, 0x5DC, 1)`. Neither of those two helpers allocates `+0x9B0` if it is null.

**What this means, precisely:** either `+0x9B0` is allocated once at the round struct's own
construction time and stays allocated for the object's whole lifetime (in which case a
genuinely retail-spawned creature — which is exactly what our mirrored `sithsoldier` bodies
are, per the client-encounters/enemy-presence docs: both installs spawn them naturally from
the module's own creature list, not from a client-side blueprint) should already have a
non-null container, and this bail is NOT what's firing — or something about being driven as a
mirrored puppet (a prior `ClearAllActions` call, or some other engine path) nulls it out. The
round struct's own constructor was NOT dissected this round; that is the one remaining gap.

**The one decisive next step, and it is cheap — a live read, not more disassembly:** on a live
mirrored enemy, read `*(void**)(creature + 0x9C8 + 0x9B0)` (e.g. via a throttled diagnostic
log line, the same mechanism the prior round's `QueueAttackAction` diagnostic already used) at
the moment `QueueAttackAction` is about to call `AddAttackAction`. Null confirms this exact
mechanism and makes `StartCombatRound` (`0x004D5F70`, thiscall on `creature+0x9C8`, five stack
args — call sites found at `0x005BC537`/`0x005BC56D`/`0x005BC59E`, argument semantics not yet
fully pinned down beyond "one of them is the candidate/target id") the next thing to call
first. Non-null means this whole branch is a dead end and the real cause is upstream of
`AddAttackAction` entirely (something before it ever gets called, or a precondition on the
*attacker* rather than the target).

No source code changed this round — the risk of guessing `StartCombatRound`'s five-argument
thiscall wrong (stack corruption on a live game process) was judged higher than the value of a
guess, when one cheap live read settles which of the two remaining branches is real.

## 3. A client's XP is actively erased

`ProgressionSnapshotPacket` is host→client only and absolute (CoopAgent.cs, `PumpProgression`):
the host publishes gold and XP; the client **writes the host's totals over its own** every
tick. A client's own kill grants XP locally and then has it stamped back down within a
tick. Both instances read 300 in the audit only because neither had gained any since load.

### FIXED (2026-08-13), and the obvious fix was wrong

"Publish deltas and take the max" is right for one of these two values and would have been a
disaster for the other.

**Experience cannot be a delta.** Both engines already grant kill XP independently: a
client's mirrored enemy is killed through `Odyssey.KillCreature`, which applies a real lethal
damage effect precisely so the death runs the engine's own path — and that path reaches
`CSWSEffectListHandler::OnApplyDeath` (0x4E0AC0), whose call at 0x4E0CB7 is the **only**
caller of `CSWSCreature::ApplyDeathExperience` (0x4FB1E0). So the mirror grants the same XP
the owner does, for the same kill. Forwarding a delta would have doubled every kill in the
game. XP is therefore absolute and merges by **max**, in both directions — which carries the
thing that actually diverges (script-granted XP: `GivePlotXP` from a conversation only one
instance is having, stealth awards) and leaves the rest alone.

**Gold cannot be a max.** It is the one shared value that goes DOWN, and a max would
resurrect anything anyone spent. It travels as a cumulative earned/spent **ledger** — the
shape `EnemyDamagePacket` already proved — which the host applies against its own party
table; the host's balance IS the purse. A client's purchase now comes out of the party's
money exactly once, in any datagram order, however many times the report arrives, with a
generation field so a re-attached agent's re-reported totals are adopted rather than spent
a second time.

`ProgressionReportPacket` (type 38, protocol 23) is the peer direction the channel never had.
Covered by `ProgressionPumpTests`. ⚠ Not yet live-verified: gold changes are inferred from
the level, so the agent must not count its own writes as the party earning money — there is a
test for that, but it has not been watched against a real shop.

## 4. The root of most of the list: ownership is one-directional

"Clients are not triggering aspects", "Trask still walks around on the host while a client
is talking to him", death and dialogue not replicating from a client — one cause. The host
owns enemies, companions, doors and containers; `PartySnapshotPacket` says so outright
("every client freezes its own copies and mirrors these"). The client→host channels that
exist are narrow and specific: `EnemyDamagePacket`, `EncounterTriggerPacket`,
`ItemOpPacket`, `FlagOpPacket`, `JournalOpPacket`, `DialogueRequestPacket`.

There is no channel by which a client says *"hold this companion still, I am talking to
him"*. So the client's Trask freezes (it is mirroring), the client opens a conversation
with what is effectively a puppet, and the host's Trask carries on walking because the
host's AI was never told otherwise. Everything a client does to a host-owned object has
this shape.

⚠ Do not fix these one at a time. The next real step is an **interaction lock in the
owner's direction**: a client-claimed object that the owner's engine agrees to hold.
Click-to-join is the same missing primitive seen from the other end, which is why it has
resisted three passes of gate-hunting.

## 5. The black window until you click it — FIXED

A launched instance draws nothing until the player clicks into it. With two windows that
means clicking both, every launch.

`0x7A3A38` is the render-window-active latch: `Global::ActivateRenderWindow` (0x00401E00,
__stdcall, NO arguments — 0x00401E83 is a plain `ret`) sets it and early-outs if it is
already set; `Global::DeactivateRenderWindow` (0x00401D90) early-outs if it is already
clear. Measured on the live pair:

| moment | instance 1 | instance 2 |
|---|---|---|
| straight after launch | 0 | 0 |
| after focusing instance 1 | 1 | 0 |
| after focusing instance 2 | **1** | 1 |

Both come up unactivated, and — the useful part — instance 1 stayed at 1 after losing
focus. It is a latch, not a focus mirror, so activating once per launch is enough and
nothing turns it off again in play.

`FocusKeeper` now calls `Odyssey.EnsureRenderWindowActive()`, which invokes the engine's own
activation rather than poking the latch (its body also clears the unfocused pause reason via
`SetPauseState(2, 0)` — the same one FocusKeeper clears). Level-triggered, so the several
deactivate/reactivate cycles the load chain performs are absorbed. Verified: both instances
render fully with nothing ever clicked. `coop.ini activate_render_on_start=0` restores stock
behaviour.

### The spurious startup pause it exposed

With both windows finally drawing, the first launch came up with `PAUSED / PRESS THE PAUSE
BUTTON TO CONTINUE` on both. Not the pause adoption — the log said `space fallback ->
paused`. `PauseKeyController` starts with `_spaceWasDown = false`, so the very first poll is
a rising edge whenever space happens to read down, which it does during startup; the
instance paused itself and the peer mirrored it. The first observation now only records the
key state (`_spaceSeen`). Verified: the pause log is clean through a launch.

## 6. The pause button

`Odyssey.ReconcilePause` has a `union == 0` branch that force-unpauses both world timers
and clears the GUI banner whenever neither pause mask holds a bit. It runs every 20 frames,
session or no session. During live play at 14:48 it logged five repairs in three seconds.

If the engine's own HUD-button path moves the world timer and the GUI belief without
setting a mask bit, that branch is a second force-unpause — reinventing exactly the bug
`PauseSanityPatch` exists to delete, this time on our side of the line. That would present
as "clicking pause does nothing at all", on both instances.

CONFIRMED and FIXED. One press produced:

    .600  reconciler UNPAUSED (banner) — no mask held a bit (server 0x00, client 0x00)
    .744  space fallback -> paused

The engine's own pause paths move the banner and the world timers but set NO mask bit, so
the reconciler read it as stranded and tore it down within ~60 ms. Space only ever appeared
to work because `PauseKeyController` re-paused it 200 ms later; the HUD button has no such
fallback and did nothing at all. The reconciler now ADOPTS the banner instead — writing the
mask the engine forgot — which also makes the pause publishable, so a HUD-button pause now
reaches the other player. Verified: adopted in 27 ms, mirrored in 39 ms, no fallback.

### Releasing a shared pause — FIXED for the key, OPEN for the button

A release could only ever be made by the player who STARTED the pause. Reproduced: paused on
instance 1, released on instance 2, instance 1 stayed frozen. The cause is that a release is
not observable as the absence of `FlagPaused` — a mirroring player never had that flag set,
because mirrored pauses are excluded at the source. It needs its own signal:
`PlayerState.FlagPauseReleased` plus a serial each way through the block (version 30).
Verified: instance 2 released, instance 1 followed 92 ms later.

⚠ STILL BROKEN: **the HUD hourglass can pause but cannot UNPAUSE.** Measured — click it once
and the pause is adopted and mirrored (81 ms); click it again and *nothing happens at all*,
on either instance, with no reconciler line. The masks were read live straight afterwards and
both halves still held `0x01` on the instance that was clicked, so this is **not** a
propagation failure — the second click never unpauses even locally.

The likely cause, unconfirmed: an adopted pause has no entry in the session's `PausedBy` list
(we write the mask bit directly rather than going through the engine's pause registration),
so the button's unpause path finds nothing to remove and no-ops. `PauseKeyController`'s
200 ms fallback is what rescues the space key, and there is no equivalent for the mouse.

NEXT: find the HUD pause button's handler and either hook it (the click is the intent, and
we already own the pause) or give the mouse the same fallback the key has. Do NOT "fix" this
by re-asserting in the reconciler — that is the loop that made the banner unclearable before.

---

## §4 answered: the per-object interaction lock (2026-08-11, live-verified)

§4 recorded that ownership is one-directional and that nothing tells an object's owner when
somebody else is using it. That channel now exists.

**The reported symptom:** a client walks up to Trask and starts talking. The client sees a body
it drives from the host's position stream; the host's own AI, never told anything happened,
carries on walking the real Trask around. Both screens show a conversation with a companion
who is strolling out of it.

### Shape: a lease, claimed early

* **A lease, not a handshake.** The claim is re-sent every tick while it is held and expires
  on its own. There is no release message, so losing one cannot strand an NPC for the rest of
  the session — which is the failure that actually matters, because the case to design for is
  a client dying mid-conversation. Same discipline as the encounter-trigger channel.
* **Claimed the moment a player starts interacting**, which is earlier than a conversation
  exists. That is what makes a contested NPC fair, and it fixes a bias nothing had noticed:
  "first speaker wins" is only fair if both claims race on equal terms, and they never did —
  the host's dialogue claim reaches the arbiter **in-process at zero latency** while a
  client's crosses the network. Two players clicking the same NPC at the same instant
  resolved to the host every time, however early the client clicked. `DialogueArbiter` now
  takes a `lockHolder` and refuses a claim on an NPC someone else holds **even when the token
  is free**.
* **A claim with no partner tag is never refused.** The tag is empty for a real window at the
  start of every conversation (`gui+0x170/0x174` stay `OBJECT_INVALID` until the server
  resolves the participants), and refusing on an empty tag would refuse every conversation in
  the game for its first few hundred milliseconds.

### The hold itself

`Odyssey.HoldCreatureStill` clears the action queue **only when the head action is a move**.
A blanket `ClearAllActions` every pass is what stopped a mirrored enemy ever landing a swing —
the queue does not distinguish a walk from the whole fight, so the test has to.

The agent filters the local peer's own claim out before handing the list down, so the plugin
holds everything it is given without needing to know its own peer id. Sending it would be
actively wrong: the object *this* player is talking to is already frozen by the engine's own
dialogue system.

### Measured

Client starts the conversation, host owns the NPC:

```
  t   host#  client#  hostNode  clientNode  hLock  cLock   hostPartner  clientPartner
  0       0        1        -1          29      1      0                end_trask
  -> host clicks 'end_trask' (join)
  5       1        1        29          29      1      0   end_trask    end_trask
NODES: on the same entry for 9 of 9 paired samples.
```

`hLock 1` from t=0 — the host is holding Trask still at the client's request, before the host
is in the conversation at all.

`verify.ps1` carries it permanently: *an interaction lock reaches the other instance*.

### `force_dialogue_party`, and why it is OFF

With the lock in place, being *pulled into* a peer's conversation rather than clicking to join
is a few lines — it sends the same request a click sends and takes the same adopt path.
Verified live: with nobody clicking, the host joined **531 ms** after the client started and
landed on the same node (14/14 paired samples).

It ships **off**, for two reasons:

1. It is the behaviour that was already removed once. Players were yanked out of whatever they
   were doing from anywhere in the module; joining became a click because walking away while
   your partner talks to a shopkeeper is a legitimate thing to want.
2. **Unresolved:** a conversation that recruits or dismisses a companion calls
   `AddPartyMember` / `RemovePartyMember`, and those have **no channel in this mod**. A player
   driven into a recruitment conversation ends it holding a party roster the other instance
   does not share — and the roster is what the companion channel keys on. Ordinary
   conversation is fine; recruitment is not.
