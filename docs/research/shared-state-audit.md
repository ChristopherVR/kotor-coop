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

### The decisive read ran (2026-08-20) — `round+0x9B0` is non-null; the branch is dead

Reproduced the exact mid-fight-join scenario again: host `tp`'d beside the live `sithsoldier`
pair, aggroed for real (`hp 1/12`, `state IN COMBAT`, `combat [FIGHT in END_M01AA - engaged:
you]`), then a second instance connected and was `tp`'d to the same spot while the host kept
fighting.

The added diagnostic (`Odyssey.QueueAttackAction`, commit `3fc0937`) answered cleanly, on
**two different mirrored creatures**, every ~3s for over a minute straight:

    queueattack diag: CALLED AddAttackAction(round=0x192BC5A0, target=0x7FFFFFFF) on creature
    0x192BBAD0, round+0x9B0 pending-list pointer BEFORE the call = 0x3400780, head action
    before 0x6, IMMEDIATELY after 0x6 (is fight action now: False)

    queueattack diag: CALLED AddAttackAction(round=0x1920E510, target=0x7FFFFFFF) on creature
    0x1920D600, round+0x9B0 pending-list pointer BEFORE the call = 0x3400F20, head action
    before 0xFFFFFFFF, IMMEDIATELY after 0xFFFFFFFF (is fight action now: False)

`round+0x9B0` is **non-null on both creatures**, every single read. The
`0x004D3660`-helper's null-check bail is NOT what's firing here — that branch is a dead end.
Both `sithsoldier` bodies stayed at full HP (`creatures.ps1`) and the client's own state stayed
`exploring (not in combat, not paused)` / `their engine is not in combat` for the full duration,
identical to every prior run. `local_combat` restored to `0` in both installs' `coop.ini`
afterward; no source change this round (nothing to implement — the leading hypothesis was
refuted, not confirmed). Full suite 953/953, both builds clean.

**Where this leaves it.** `AddAttackAction` is called with a resolvable target, a live round
pointer, AND a non-null pending-action list — every precondition this investigation has checked
is satisfied — and it STILL appends nothing. The remaining open branches from the disassembly
round: (a) the `vtbl[0x10]` `AsSWSObject` cast on the resolved target (branch 2 in the table
above) was never actually ruled out, only deprioritized — worth checking directly now that the
more likely candidate is dead; (b) the round struct may have some OTHER gate this investigation
hasn't read yet — the shared append helper at `0x004D3660` was only disassembled as far as its
first two instructions (the null check); the REST of that function, past the null check, has
never been read and may contain its own silent bail; (c) whether `head action type`
(`GetHeadActionType`) is even reading the same list `AddAttackAction` appends to — worth
directly re-verifying the field this reader uses against the field `0x004D3660` links into,
rather than assuming they're the same list. (a) and (c) are both cheap, targeted disassembly;
(b) is the natural next full read.

### The append was never the problem — nothing was running to consume it (2026-08-20, fourth round)

Wrong question. `AddAttackAction` appending to a genuinely non-null `round+0x9B0` is not evidence
the append *worked* in the sense that matters — it only proves the LIST HAS SPACE. Whether
anything ever looks at that list again is a separate mechanism entirely, and it does not.

**`UpdateCombatRoundTimer` (`0x004FAF20`) is the function that would.** Confirmed called
unconditionally every tick for every creature, no mirrored/combat-state gate anywhere near its
call site (`0x004FE300`) — so it fires on our mirrored `sithsoldier` bodies exactly as it does on
a real one. Its very first real decision, disassembled directly:

```
0x004FAF4A  mov ecx, dword ptr [edi + 0x9c8]   ; round = creature->CombatRound
0x004FAF50  mov eax, dword ptr [ecx + 0x958]   ; eax = round->+0x958
0x004FAF56  test eax, eax
0x004FAF58  je 0x4fb0d3                         ; ZERO -> bare fallback, nothing else runs
...
0x004FB0D3  mov edx, dword ptr [edi + 0xcc]
0x004FB0D9  push edx
0x004FB0DA  call 0x4d4c10                       ; IncrementTimer(edx) — literally just this, then ret
0x004FB0DF  ...ret
```

If `round+0x958` is zero, `UpdateCombatRoundTimer` runs `IncrementTimer` and returns — it never
reaches the branch (`0x4d45e0`/`0x4d45b0`/`0x5b8050`) that would process a queued action into
anything `GetHeadActionType` sees change. **This is the actual silent-discard mechanism**,
downstream of `AddAttackAction` entirely — not inside it, which is why every precondition inside
`AddAttackAction` checked out clean and it still did nothing observable.

**`StartCombatRound` (`0x004D5F70`) was disassembled in full and does NOT set `+0x958` nonzero —
it sets it to ZERO**, unconditionally, near the end of its own body:

```
0x004D6019  mov dword ptr [esi + 0x934], 1      ; the round "active" flag -> 1
0x004D6023  mov dword ptr [esi + 0x944], ebp     ; ebp = 0 (xor ebp,ebp earlier)   elapsed timer -> 0
0x004D6029  mov dword ptr [esi + 0x958], ebp     ; -> 0, a FRESH round's timer-active field starts at 0
```

This directly refutes the prior round's hypothesis (`StartCombatRound` sets `+0x958`) — it does
the opposite. `+0x934` (a real boolean, confirmed set to `1` here) is the round's "active" flag;
`+0x958` is something else, reset to a clean 0 as part of a fresh start, same as `+0x944` (the
elapsed-time accumulator) and several neighbouring fields.

**Found the real setter by using `fieldwrites.py` instead of guessing further.** `python
tools/fieldwrites.py 0x958` lists every instruction in the binary that writes that offset — 9
total, one of which is `StartCombatRound` (above, zeroing it) and one of which (`0x004D2906`) is
inside a small, self-contained two-argument function that does nothing else:

```
0x004D28F0  mov edx, dword ptr [esp + 8]        ; edx = arg2
0x004D28F4  mov eax, dword ptr [esp + 4]        ; eax = arg1
0x004D28F8  mov dword ptr [ecx + 0x95c], edx    ; +0x95c = arg2   (unconditional)
0x004D28FE  mov edx, dword ptr [ecx + 0x964]
0x004D2904  test edx, edx
0x004D2906  mov dword ptr [ecx + 0x958], eax    ; +0x958 = arg1   (unconditional, both stores
0x004D290C  je 0x4d2918                          ; happen before this branch is even tested)
...
```

**`xrefs.py 0x4D28F0` finds 23 call sites.** Three of them sit in the exact same function that
made all three `StartCombatRound` calls this section already traced (the per-attacker
combat-decision routine near `0x5BC49F`–`0x5BC6C4`) — and all three are byte-identical in shape:

```
0x005BC60F  push edx            ; edx = the relevant creature's own object id (esi+4, or a
                                 ; resolved target's, depending which of the 3 sites)
0x005BC61C  push 1              ; arg1 (last pushed) = literal 1, EVERY site, no exceptions
0x005BC61E  call 0x4d28f0
```

Same pattern repeats at `0x5BC67D` and `0x5BC6AD`, once for the attacker's own round (right after
the `StartCombatRound` branch converges) and twice more for a resolved target's round (retaliation
setup, paired with `target+0x4DC = 1` a few lines earlier — a field 4 bytes from `+0x4E0`, the
`FlagInCombat` field this entire section opened with). **This is the missing initiation step the
engine's own code always pairs with `StartCombatRound`, and our driven puppet — whose AI script
never runs — never got it.**

**Implemented, `src/KotorCoop.Plugin/Odyssey.cs`:** `SetRoundTimerActiveFn` wraps `0x004D28F0`.
`QueueAttackAction` now calls `SetRoundTimerActiveFn(round, 1, creature's own object id)`
immediately before `AddAttackActionFn`, and the diagnostic now reads `round+0x958` before and
after so a live pass sees the actual flip rather than inferring it.

**Deliberately did NOT call `StartCombatRound` itself.** Its own 5-argument signature remains
only partially traced (arg1 = a target/attacker id depending on call site, confirmed; arg2 →
`+0x9b8`, arg3 → `+0x9bc`, confirmed by reading its body; args 4 and 5's semantics were not
pinned down to real confidence) — a wrong thiscall argument corrupts the stack on a live process,
and per this whole investigation's standing rule that risk is not worth taking on an unproven
argument. It also turned out not to be necessary for this fix: `UpdateCombatRoundTimer`'s gate,
read in full, depends on `round+0x958` alone — nothing in the branch past it was seen checking
`+0x934` or any other `StartCombatRound`-initialized field. `SetRoundTimerActiveFn`'s own 2-arg
signature, by contrast, has full confidence: verified both by disassembling the function's own
body AND against three real, byte-identical engine call sites, with no branch or ambiguity in
either argument's role.

### The live pass ran (2026-08-20, fifth round) — the flip works, and it still does not work

Same scenario as every round: host `tp`'d beside a live, untouched `sithsoldier` pair in
`END_M01AA`, aggroed for real (`state IN COMBAT`, HP falling under the engine's own AI, floored
at 1 by the deck's min-1-hp pin — `god was already ON`), then a second instance connected and was
`tp`'d to stand beside the same two hostiles while the host kept fighting. Watched for **2 minutes
40 seconds**, not just the usual 90.

**Check (1) passed, cleanly and repeatedly.** The diagnostic fired every ~3s for the entire
window, and the flip is real every single time:

    [18:26:57.791] queueattack diag: CALLED SetRoundTimerActive+AddAttackAction(round=0x1847F948,
    target=0x7FFFFFFF) on creature 0x1847EE78, round+0x9B0 pending-list = 0x32D7BE8, round+0x958
    timer-active BEFORE=0 AFTER=1, head action before 0x6, IMMEDIATELY after 0x6
    (is fight action now: False)

`SetRoundTimerActiveFn` genuinely writes the field, on the real object, every call, no exception —
that part of the fix is proven, not inferred. **Checks (2) and (3) both failed.** The head action
never once changed from `0x6` across ~50 logged calls spanning 2:40. The host's own dashboard
confirms the client side from the other end the whole time:
`combat [FIGHT in END_M01AA - engaged: you; mirroring only: Malak (their engine is not in
combat)]` — identical wording to every prior round's failure, unchanged for the full duration.
`kotorcoop-combat.log` on the client independently confirms the absorb path is still the only
thing moving: `combat: absorbed N forwarded damage in total ... this engine says in-combat=False`.

**The genuinely new fact this round adds: `round+0x958` reads BEFORE=0 on EVERY call, not just the
first.** If the field we set to 1 were actually being consumed by `UpdateCombatRoundTimer`'s
"process the round" branch — the behaviour the whole hypothesis depends on — a successful
consumption resetting it back toward 0 as part of normal round bookkeeping would be unsurprising.
But that is indistinguishable, from this diagnostic alone, from `UpdateCombatRoundTimer` **never
reaching that branch at all** and something else entirely resetting the field to 0 every tick
before our own write is checked next cycle — which would mean the branch this whole fix aims at is
still never firing, just at a point one step further down than round four thought.

`local_combat` stays off. Config reverted (`local_combat=0` in both installs), full suite 953/953,
both builds clean. **This is the sixth round to narrow the mechanism without closing it.** The
next diagnostic that would actually separate the two explanations above: log `round+0x958`
immediately on entry to `UpdateCombatRoundTimer` itself (not just before/after our own write, three
seconds apart) — if it reads 1 there and the processing branch still isn't reached, the bug is
inside `UpdateCombatRoundTimer`'s branch itself (the disassembly needs to go further than
`0x004FAF58`); if it reads 0 there, something between our write and the next tick is clearing it,
and that write needs to be found with `fieldwrites.py 0x958` cross-referenced against a timestamp
window rather than assumed to be `UpdateCombatRoundTimer`'s own consumption.

### `UpdateCombatRoundTimer` has a SECOND gate past `+0x958`, and it was never established (2026-08-20, seventh round)

Full disassembly of `UpdateCombatRoundTimer` (`0x004FAF20`) past the `+0x958` check settles both
prior explanations at once: **neither is right.** Passing the `+0x958` gate is necessary but not
sufficient. Immediately after, the function computes `ebx = round+0x960 - creature+0xCC` (creature's
own current-tick-ish field) and only when `ebx > 0` does it reach the real action-processing call,
`0x005B8050` (via `0x004FAFA7`-`0x004FAFD9`). When `ebx <= 0` — which it always is if `round+0x960`
was left at its constructed/reset default of 0, since `creature+0xCC` is a live, positive,
ever-increasing tick counter — the function instead takes a shortcut (`0x004FAFE3` onward) that
resolves a leftover `round+0x9CC` target through a chain of vtable validity checks
(`AsSWSObject`/`GetDead`/two more vtable calls) and, if all pass, calls the SAME round-reset helper
(`0x004D4620`, the one `fieldwrites.py 0x958` already found) that zeroes `+0x958` back to 0 along
with `+0x934`, `+0x944`, `+0x94c`, `+0x960` and others — **cross-confirmed via `xrefs.py 0x4D4620`**,
which lists `0x004FB0BA` (the exact end of this shortcut) among its 9 call sites. This is why the
diagnostic always read `BEFORE=0`: the only reachable branch without `+0x960` being set ends in
exactly this reset, every time, without ever reaching `0x5B8050` — the call that would actually
change the head action.

**`StartCombatRound` does NOT establish `+0x960` either — it zeroes it too** (`0x004D602F`,
confirmed by reading its full body), so the previous round's conclusion that `StartCombatRound`
"turned out not to be necessary" was based on an incomplete trace; it stopped at the first gate.

**Found the real setter with the same method as before — `fieldwrites.py 0x960`, not more
guessing.** `0x004D2932` sits five NOP-padding bytes after `SetRoundTimerActiveFn`'s own `ret 8`
at `0x004D2918` — a genuinely SEPARATE, complete function (`0x004D2920`, three `ret 8` exits, fully
disassembled, no unexplored branches): `round+0x964 = arg2` always first, then if `arg2 != 0`,
`round+0x960 = arg1` directly; if `arg2 == 0`, either zeroes `+0x960` (both args 0) or
sets-or-accumulates it from `arg1`. **`xrefs.py 0x4D2920` finds 21 call sites**, two of which
(`0x005BC630`, `0x005BC6BF`) sit in the EXACT SAME engine combat-decision routine that makes the
already-confirmed `StartCombatRound` and `SetRoundTimerActiveFn` calls, immediately after each
`SetRoundTimerActiveFn` call, both `push 0; push <delayMs>; call 0x4D2920` — the third setup step
the engine always pairs with the other two, that this fix was still missing.

**Implemented**, `src/KotorCoop.Plugin/Odyssey.cs`: `SetRoundDelayFn` wraps `0x004D2920`.
`QueueAttackAction` now calls `SetRoundDelayFn(round, 0x5DC, 0)` immediately after
`SetRoundTimerActiveFn` and before `AddAttackActionFn` — `0x5DC` (1500ms) matches the delay already
used elsewhere in the same call for consistency; the function's own logic does not care about the
source of the value, only that a positive duration lands in `round+0x960`. Diagnostic extended to
log `round+0x960` before/after alongside the existing `+0x958`/`+0x9B0` reads. Full suite 953/953,
both builds clean.

**Live pass attempted, blocked by an unrelated baseline reproduction failure — not by this fix.**
Rebuilt, reinstalled into both live game directories, `local_combat=1` set on both, host launched
alone and `tp`'d beside the same `sithsoldier` pair every prior round used (`(29.74,144.46)` /
`(31.86,141.99)` in `END_M01AA`, player landed within ~1.7m of both) — but the host's OWN NATIVE AI
never aggroed at all: 40+ seconds of no movement, no HP change, no combat-state change. A second
attempt at a completely different creature pair elsewhere in the module (`end_sith2`/`end_sith3` at
`(46.78,33.27)`/`(45.18,32.45)`) showed the identical non-response over another 20+ seconds. This is
the HOST's own unmodified engine failing to do what every prior round's host reliably did in ~1s —
something this session's environment, save state, or the specific `sithsoldier` pair's own scripting
(nearby `end_bridgerep1`/`end_bridgerep2`/`carth` tags suggest this location may be a scripted bridge
sequence, not a generic wild encounter) is different about, and it is NOT something `local_combat` or
this fix could cause: the host plays with its own completely unmodified engine, no plugin code from
this investigation runs on host-owned creatures at all. `creatures.ps1` also read the player's own
`hp` column as `0` throughout despite `god` reporting "1 hp floor" already on — plausibly the same
"a getter that is one `mov` is not proof the field is live" PC-read limitation noted elsewhere in
this document, but worth a look before the next live pass in case it is connected.

**The fix itself is implemented, unit-tested, and evidence-complete** (full disassembly of both
`UpdateCombatRoundTimer` past both gates and `SetRoundDelayFn` in its entirety, cross-verified
against real engine call sites for every value used) — what remains is purely procedural: get a
real fight going on the host at all, in this environment, before the client-side mechanism this
section is actually about can be exercised. `local_combat` stays off. Config reverted to
`local_combat=0` in both installs, rig fully torn down.

### Round 10 (2026-08-20) — the "dead enemies" theory is refuted, and the real blocker is bigger than this fix

Chased why the host's own baseline aggro failed. `creatures.ps1` on a completely fresh launch
(save slot 1 reloaded from scratch) showed both `sithsoldier`s — and all 27 creatures — alive at
full stated HP (`15 10` columns unchanged from a clean load). **The save is not corrupted or
exhausted by nine rounds of prior testing.** That theory is dead.

What's actually true, confirmed by reading `tools/blockpoke.ps1`'s own `attack` verb source
(`TestDriveController.HandleAttack`, `src/KotorCoop.Plugin/TestDriveController.cs:104-107`) —
its own doc comment says it outright: **"the starting area's enemies never aggro on their own,
so without it the damage forward, the swing animations and the phantom's targetability have no
way to be exercised outside a human playthrough."** Every prior round's "aggroed in ~1 second"
result was staged through this tool (`Odyssey.AttackTarget`, the native `ActionAttack()` VM
call), not organic proximity aggro. `tp`-ing next to a `sithsoldier` alone was never going to
work, in this round or any prior one that reported it working from proximity alone — a
methodology gap in how the result was described, not a finding about the engine.

Corrected the target: player object id is not a normal small id here — it reads back as
`0x7FFFFFFF`, the engine's own "current player" sentinel (confirmed via `console 'resolve
0x7FFFFFFF'` resolving to a live, valid creature, matching an earlier round's finding). Ran
`attack 0xE 0x7FFFFFFF` correctly this time (a first attempt with no explicit target silently
means "break off combat", logged plainly as `test: 0xE ordered to break off combat` —
`TestDriveController.cs:126-138`, working as designed once understood).

**`Odyssey.AttackTarget` reported success — `ok=true`, no failure code — and still nothing
happened.** No movement, no swing, no HP change, over 40+ seconds. `god`/console responsiveness
confirmed the engine and script VM are NOT frozen (`god` correctly answered "already ON", the
tutorial deck's own `k_pend_area01` min-1-hp pin, matching documented behaviour exactly). So this
is not a hung process — it is the SAME "call succeeds, nothing observable happens" symptom this
entire nine-round investigation has been chasing inside `local_combat`'s own code, except this
time on the OFFICIAL, previously-reliable native `ActionAttack()` staging path itself, which
every earlier successful round depended on to manufacture a fight at all.

**This reframes the open question.** Either: (a) something has changed in this specific
environment/session since the rounds that reported staged attacks working (worth checking
against an entirely fresh game launch with a clean process, not just a fresh save-load inside a
session that has been repeatedly launched/torn down many times today), or (b) the "worked before"
reports were never as clean as described and the underlying fragility has been present the whole
time, surfacing now by chance. Round 11 should start here — with `AttackTarget`/`ActionAttack()`
itself, on a completely fresh process launch, before returning to `local_combat`'s own code at
all, since if the OFFICIAL path cannot manufacture a fight, no amount of further work on
`QueueAttackAction` can be verified either.

No source code changed this round. `local_combat` untouched, `coop.ini` never modified in either
install (phase 1 never completed, so phase 2 — the actual `local_combat` retest — did not run).
Process torn down clean.

### Round 11 — the unification hypothesis holds, and a real fight finally happened

Before any live work: read `Odyssey.AttackTarget`'s implementation directly. It dispatches
through the NWScript VM's own `ActionAttack()` command table entry — i.e. the exact
`ExecuteCommandAttack` (`0x0052E7F0`) → `CSWSCreature::AddAttackActions` (`0x004FDE40`) →
`AddAttackAction` (`0x004D38B0`) chain this whole investigation has been fixing.
`python tools/xrefs.py 0x004D28F0` and `0x004D2920` (the two setters `QueueAttackAction` now
calls) list 21-23 call sites each, and NONE of them are inside `AddAttackActions`'s own preamble
helpers (`0x004D3770`/`0x004D37E0`) or `AddAttackAction` itself — confirmed by address, not
inferred. So `AttackTarget`, the "official" path, is missing the identical precondition
`QueueAttackAction` was missing before `fb8d5dc`. Round 10's "the staging tool itself is broken"
was never a second mystery; it was the same bug, reached through a different door.

Applied the same two calls (`SetRoundTimerActiveFn`/`SetRoundDelayFn`) to `AttackTarget` itself,
immediately before its VM dispatch, and tested live on a genuinely fresh single host process
(fresh launch, no prior console commands, verified clean process teardown beforehand):

- `attacktarget diag: 0xE round+0x958 0->1, round+0x960 0->5DC` — both fields read exactly 0
  immediately before the call (a truly untouched creature, not a reused/damaged one from a prior
  round) and flipped correctly.
- Ordered first on `0xE` while the player was ~120 units away (an accidental control: nothing
  visibly happened while out of reach, ruling out a "the flip alone teleports/forces an attack"
  false positive).
- Moved the player to `tp 29.74 141.46 -0.00`, ~3m from `0xE`. Within 5s: player HP `12/12 -> 8/12`
  (`console 'where'`), `0xE`'s position moved from `(29.74, 144.46)` toward the player, to
  `(29.74, 143.26)` (`creatures.ps1`) — a real, engine-driven approach and swing, not a number
  written from outside.
- Ordered a SECOND, still-untouched creature (`0x10`) to attack with the player already close:
  within 4s, player HP `8/12 -> 1/12` and `0x10` closed from `(31.86, 141.99)` to
  `(31.48, 141.90)`. Fast, and consistent with the ~1s aggro speed every earlier round measured
  for genuinely organic host combat.
- `god` was unexpectedly already ON on this fresh process (not set by anything in this session,
  no `coop.ini` key, no code default found) — this is why the player survived to 1 hp instead of
  dying; noted as an open anomaly, not chased further, since it does not change what was measured
  (damage genuinely flowed and was only floored, not absent).
- Broken off cleanly afterward (`attack <id> 0` on both, the documented "target zero = stop
  fighting" convention) before process teardown; player left at a stable 1/12, not downed.

**This is the first real, physically-observed fight this entire investigation has produced.**
It confirms the `round+0x958`/`round+0x960` mechanism `fb8d5dc` implemented is genuinely correct
and sufficient to take a creature from cold to a real, damage-dealing fight — on the HOST's own
engine, driving its own (non-mirrored) creature. It does NOT yet directly prove the CLIENT
mirrored-enemy case (`local_combat`'s actual purpose: a driven puppet fighting a REMOTE player
through the co-op session, on a machine that owns none of the input) — `QueueAttackAction` calls
the identical two functions, but the mid-fight-join, two-instance scenario has still never
completed end to end. Given the mechanism is now proven in the general case, the next round
should go straight back to that scenario with real confidence instead of re-litigating whether
the fix works at all.

`local_combat` and both installs' `coop.ini` were not touched this round (this test needed only
a single host instance). Process torn down and confirmed clean. Full suite 953/953, both builds
clean.

### Round 12 — the actual mid-fight-join test, a real bug found and fixed, and the symptom survives it

The scenario `local_combat` exists for, finally run against a mechanism already proven correct in
the general case. Host launched alone, `tp`'d to `(28, 143, 0)`, `attack 0xE 0x7FFFFFFF` staged —
HP `12/12 -> 10/12` within 5s, confirmed the same way round 11 did. With the host still actively
fighting, a second instance launched, loaded, connected (`phantoms 1 remotes 1` both sides), and
`tp`'d to the identical `(28, 143, 0)` — genuinely mid-fight, not staged separately.

**A real bug surfaced immediately, before the actual question could even be asked.**
`round+0x960` climbed by exactly `+1500` on every single logged call — `BEFORE=2238005
AFTER=2239505`, then `BEFORE=4483506 AFTER=4485006`, `BEFORE=6871505 AFTER=6873005` — hundreds of
times a second, because `QueueAttackAction`'s whole setup block ran unconditionally on every
`EnemyPresenceController` drive pass whenever the head-action gate at the top of the function
wasn't satisfied, which it never was (nothing was making it true — the exact thing being tested).
`SetRoundDelayFn` **accumulates**, it does not set, so the round's own due time was pushed further
into the future faster than real time could ever catch up — a self-defeating feedback loop that
also explains why round 11 worked at all: `AttackTarget` fires from a single console command,
once, never from a per-frame drive loop.

**Fixed live, mid-round-12, and retested in the same session** (`Odyssey.cs`,
`QueueAttackAction`): gated `SetRoundTimerActiveFn`/`SetRoundDelayFn` behind `roundTimerBefore ==
0` — initialize the round ONCE, on the genuine 0->1 transition, and let the existing head-action
gate do its job for every pass after that. Rebuilt, reinstalled, retested the identical
mid-fight-join scenario end to end a second time: `round+0x960` now behaves sanely (`BEFORE=0
AFTER=1500` once, then small organic drift — `4`, `7`, `332`, `331`, `333`... — never another
`+1500` jump). The accumulation bug is real and is fixed.

**The core symptom survives it.** Over the full retest, the client's own HP never moved from
`12/12` (`console 'where'`, checked repeatedly), head action stayed `0x6`/`0xFFFFFFFF`
throughout, and the plugin's own absorb line kept firing (`combat: absorbed N forwarded damage in
total ... this engine says in-combat=False`) — the same pre-existing forwarded-damage path that
works regardless of `local_combat`, not new local engagement. The host's own fight, checked in
parallel, correctly ran the whole time (its HP reached `1/12`, the deck's own min-1-hp floor) —
confirming nothing else broke and the test rig itself was sound throughout.

**What this narrows the question to.** Round 11 proved the round-init mechanism sufficient for a
HOST-LOCAL creature (no mirroring, no drive loop touching it every frame, no peer involved) —
that was ALWAYS a single-instance test. Round 12 is the first time a genuinely MIRRORED creature
has been driven through a correctly-initialized (not spamming) round, and it still does not
attack. The one structural difference between the two cases that round 12 did not yet isolate:
`EnemyPresenceController`'s own per-frame `SetPosition`/`SetFacing` drive calls run on this exact
creature every single pass, continuously, for as long as it is mirrored — round 11's creature had
no such competing per-frame writer. Candidate 2 from earlier in this investigation (drive writes
starving perception) was refuted specifically for PERCEPTION; it was never re-checked against
ROUND PROCESSING specifically, and round processing is a different code path
(`UpdateCombatRoundTimer`) than perception (`UpdateVisibleList`). Worth a targeted check next: does
suspending the position/facing drive on ONE test creature (leaving everything else — the
attack-permitted bit, the round-init call — untouched) let its round process normally? That would
cleanly separate "driven puppets structurally cannot complete a combat round while being driven"
from "something else, specific to the mirrored/remote-target case, is still missing."

`local_combat` and both installs' `coop.ini` restored to their code defaults (`0`) afterward.
Process torn down and confirmed clean (`Get-Process KotorCoop.Agent,swkotor` returned none). Full
suite 953/953, both builds clean.

### Round 13 — the append itself had the same bug the setters did, and fixing it is not enough

Before touching the drive-loop-interference question round 12 left open, a closer read of
`QueueAttackAction` found a second instance of round 12's exact bug, one call further down:
`AddAttackActionFn(round, targetId, 0, 0, 0x5DC, 1)` was running UNCONDITIONALLY on every
call, gated only by the head-action check at the top of the function — which never trips,
because head action never leaves `0x6`. `AddAttackAction` has no dedup of its own
(documented since the instruction-level dissection round: "unconditionally allocates and
appends a new action node every call"), so a driven, engaged mirrored creature was having a
fresh, undeduplicated action node appended to `round+0x9B0`'s pending list on every single
drive pass — tens of times a second, for as long as it stayed in range. The engine's own
normal flow (`CreateNewAttackActions`, reached only from `EndCombatRound`) calls this exactly
once per round, never in a tight per-frame loop; this is the same shape of self-defeating bug
as round 12's `SetRoundDelayFn` accumulation, one layer closer to the actual append.

**Fixed** (`Odyssey.cs`, `QueueAttackAction`): moved `AddAttackActionFn`'s call inside the same
`roundTimerBefore == 0` gate as the two setters, so all three fire together, exactly once, on
the genuine 0->1 transition — matching round 11's only working case (`AttackTarget`, a single
staged call, never repeated) as closely as this code path can.

**Live-retested, full mid-fight-join scenario, third time end to end.** Host staged into a
real fight (`AttackTarget` via `blockpoke.ps1 attack`, HP `12/12` -> confirmed dropping under
the fight, reaching the deck's `1/12` min-hp floor); client connected mid-fight and `tp`'d
beside the same two `sithsoldier`s. Confirmed the fix holds: `round+0x958`/`round+0x960` on
the mirrored copies now show a clean single `queued-this-pass=True` transition followed by
`queued-this-pass=False` on every subsequent pass, with `round+0x960` drifting by small, sane
per-tick amounts (`14`, `16`, `18`, `20`, ...) rather than exploding — the append-side bug is
real and is fixed, the same way the setter-side one was.

**The core symptom still survives it, and there is a new, sharper piece of evidence.** Head
action never left `0x6`/`0xFFFFFFFF` across the whole test. And the client's own damage log
now shows the actual shape of the failure rather than just its absence: `combat: absorbed 2 /
6 / 10 / 18 / 24 forwarded damage in total ... this engine says in-combat=False` — a strictly
growing absorbed total, tracking the host's real fight — while `console 'where'` confirmed the
client's own HP never moved from `12/12` at any point. This is the exact INVULNERABLE failure
mode `local_combat`'s own safety notes have warned about since it was first built: forwarded
damage is being absorbed on the promise that the local copy is dealing the same damage for
real, and it is not, so the damage is simply discarded. Two round-13 fixes in, the mechanism
that would make that promise true (round processing itself) is still not completing for a
driven creature, even though round-init and the append are both now individually correct.

**What remains open, precisely:** round 11 proved the round-init + append mechanism sufficient
end-to-end for a creature nothing else was writing to every frame. Rounds 12 and 13 both used
that same, now-doubly-fixed mechanism on a creature `EnemyPresenceController` continuously
drives (`SetPosition`/`SetFacing` every pass), and it still never completes a round. The
targeted check round 12 proposed and did not yet run — suspending the position/facing drive on
one test creature while leaving the attack-permitted bit and the round-init/append calls
untouched, to see whether ITS round processes normally — is still the next concrete step, and
is now better-motivated than before: with both known self-inflicted bugs fixed, whatever is
left is either genuinely specific to being continuously driven, or a third, still-undiscovered
gate this investigation has not yet reached.

`local_combat` and both installs' `coop.ini` restored to their code defaults (`0`) afterward.
Process torn down and confirmed clean (`Get-Process KotorCoop.Agent,swkotor` returned none). Full
suite 953/953, both builds clean.

### Round 14 — the drive-loop-interference hypothesis is a dead end; `UpdateCombatRoundTimer`'s real gate was misread, and `StartCombatRound` turns out to do far more than two fields

Static-only round (no live rig used). Two corrections to round 13's own conclusions, found by
disassembling further than round 13 had time for.

**`EnemyPresenceController`'s per-pass order was re-checked and is not the problem.**
`ClaimCreatureForFight`, `PermitAttacking`, one-shot replay, `Drive()` (position/facing),
`ClearPositionDirty`, `RecordEngagement`, THEN `QueueAttackAction` — in that order, every pass.
Nothing runs between `QueueAttackAction`'s writes and the next pass's `ClaimCreatureForFight`
that could plausibly reset round state; `ClaimCreatureForFight`'s move-only wipe was already
confirmed (round 9) to be structurally incapable of touching a fight-type action, and nothing in
this pass touches `round+0x9xx` fields directly by address. The interference hypothesis this
round was assigned to test is **not supported by the call sequence itself**.

**`UpdateCombatRoundTimer`'s gate was misread in round 13.** Full disassembly of `0x004FAF20`
past the point round 13 stopped: the `round+0x960 - creature+0xCC <= 0` branch (`jle 0x4fafe3`)
is **not** a dead end that always resets — it falls into a loop that calls the SAME processing
functions (`0x4d45b0`, `0x5b8050`) as the "positive" branch, gated instead on
`[esi + 0xb0] > 0`, where `esi = 0x004D2B50(round, byte at round+0x96C)` — an array indexer:
`esi = round + 4 + (that byte, clamped >= 0) * 0x14C`, i.e. ONE of **five embedded 0x14C-byte
per-participant sub-structures** starting at `round+4`, selected by `round+0x96C`. The `ebx`
sign only decides which of two paths reaches the same processing calls; it is not itself a
block. What actually blocks all processing is `[esi+0xb0] <= 0` on whichever sub-structure
`round+0x96C` currently selects — a field this investigation has never read or written.

**And `round+0x96C` was never set by anything this investigation has done**, because
`StartCombatRound` (`0x004D5F70`, now fully disassembled end to end, `0x4D5F70`-`0x4D60EA`)
does far more than the two fields `SetRoundTimerActiveFn`/`SetRoundDelayFn` cover: it zeroes
`+0x944`/`+0x958`/`+0x960`/`+0x938`/`+0x93c`, sets `+0x934=1`, `+0x95c=0x7f000000` (a sentinel),
`+0x94c=0xbb8` (3000, likely a real default interval), stores two of its five args into
`+0x9b8`/`+0x9bc`/`+0x940`, conditionally resets a stale prior round via `0x4d4620` — **and runs
a loop over all five `round+4+i*0x14C` sub-structures, calling `0x004D3010` (an
initializer/constructor, not yet dissected) on each one**, before three more setup calls
(`0x4d2a10`, `0x4d2a70`, `0x4f34a0`). None of that has ever run on a driven puppet's round. The
five sub-structures `UpdateCombatRoundTimer` indexes into via `round+0x96C` are very likely
sitting in whatever state the creature's constructor left them in — not the state
`StartCombatRound`'s loop would put them in — and `[esi+0xb0]` is a field inside one of them.

**Argument-shape work on `StartCombatRound` itself, real but incomplete.** Fresh disassembly of
call site `0x005BC537` (with full caller context this time, `0x5BC480`-`0x5BC63B`) maps the
push order to argument roles more precisely than round 12 managed: arg1 = a caller-local value
also reassigned into `ecx` (`[esp+0x14]`, stored into `round`'s `+0x9c0`/`+0x9ac`/`+0x9cc`
target-like fields), arg2 = the return of `call 0x4d2c30([esp+0x10])`, arg3 = the return of
`call 0x4d2d60([esp+0x10], &[esp+0x54])`, arg4 = `[esp+0x50]` (never seen explicitly read
inside the callee body captured so far — possibly genuinely unused), arg5 = `[esp+0x6c]`
(stored into `+0x940`, passed on into `0x4d2a10`). **This is NOT full confidence** — arg2 and
arg3 both derive from two OTHER function calls (`0x4d2c30`, `0x4d2d60`) whose own return values
were not traced to a concrete meaning, and the caller-local stack slots (`[esp+0x10]`,
`[esp+0x50]`, `[esp+0x6c]`, `[esp+0x14]`) were not traced back to where the ENCLOSING function
(a large combat-decision routine starting well before `0x5BC480`) itself populates them from.
Per the standing rule this investigation has followed since round 12 — do not guess a
thiscall's argument shape against a live process without real confidence — **no live test was
attempted this round and no code was changed**.

**What round 15 needs, precisely:** either (a) trace `0x4d2c30`, `0x4d2d60`, and the enclosing
function's population of `[esp+0x10]`/`[esp+0x50]`/`[esp+0x6c]`/`[esp+0x14]` back far enough to
pin all 5 `StartCombatRound` arguments with real confidence, then call the real function (which
handles the five-sub-structure init and `+0x96C` internally — no need to hand-replicate
`0x4D3010` or the three follow-up calls), or (b) find and dissect whatever sets `round+0x96C`
directly (11 write sites found via `fieldwrites.py 0x96C`, not yet individually checked against
this creature's case) and the five sub-structures' constructor (`0x4D3010`) to see if `+0xb0`
specifically can be set directly and cheaply without needing the full `StartCombatRound` call.
Route (a) is very likely more correct (it is what the engine's own working combat AI actually
calls) but route (b) may be cheaper if `0x4D3010` turns out to be a simple zero-fill the
creature's own constructor already performs adequately, in which case only `round+0x96C` itself
(picking sub-structure 0, which is almost certainly what a fresh, never-fought creature already
has by construction) might be the missing single write. Either route is genuinely findable with
the same static tools this round used; it was not a matter of the tools failing, only of budget.

### Round 15 — route (b) refuted: `0x4D3010` clears `+0xb0`, it does not set it

Static-only round (no live rig used; route (b) collapsed before reaching a testable fix).

**`0x4D3010` is not an initializer that leaves a sub-structure ready — it is a reset/clear
function, and it explicitly ZEROES `+0xb0`.** Full disassembly (`0x4D3010`-`0x4D310D`): after
tearing down two embedded lists (`call 0x5e5140` twice) and freeing an owned array's elements
(a loop over `[esi+0x88]`/`[esi+0x8c]` calling `0x503590` then `0x6fa390` — the SAME free
helper `AddAttackAction` itself uses to discard its orphaned node when `+0x9b0` is null, round
9), the tail of the function is a block of zeroing writes: `[esi+0x8c]=0`, `[esi+0x98]=0`,
`[esi+0xa4]=0`, **`[esi+0xb0]=0`**, `[esi+0xb8]=0`, all from the same zeroed register. This
refutes round 14's cheaper hope directly: a "fresh, never-fought" sub-structure is not sitting
at `+0xb0 > 0` by construction — if anything, whatever state a sub-structure is born in, THIS
function (which per its call sites is a per-sub-structure RESET, called in a loop from
`StartCombatRound` before the real setup happens) actively drives it to `+0xb0 = 0`, i.e. the
gate-CLOSED state. Setting `round+0x96C` alone, with nothing else, would at best point the
gate at a sub-structure already known (by this function's own behavior) to read `<= 0`.

**The actual processing function (`0x5B8050`, `UpdateCombatRoundTimer`'s real destination) was
also disassembled far enough to rule out a shortcut.** It re-derives the sub-structure pointer
itself (`ecx = round; movzx eax, byte [round+0x96c]; call 0x4d2b50` — confirming `+0x96C` IS
read live, every call, not cached), then branches on a STATE BYTE at `sub+0x5c` compared
against `2`, cross-checked against a lookup through `0x4aea40` (looks like a party-table or
target-resolution call, `ecx` loaded from a global at `0x7a39fc+8`), conditionally calls a
notification pair (`0x4aed80`→`0x4b08d0`) on a mismatch, then reads `sub+0x40` and `sub+4` to
decide between two further branches — one of which allocates via `0x6fa7e6(0x34)`. This is
real per-tick round-advancement logic with several interdependent checks, not a counter a
single hand-written write could satisfy; forcing `+0xb0` positive without also matching
whatever `sub+0x5c`/`sub+0x40`/`sub+4` are supposed to hold would very likely just fail a
different check one line later, or read genuinely uninitialized garbage through those other
fields.

**Verdict: option (b) is dead. Route (a) — fully tracing `StartCombatRound`'s five arguments
and calling the real function — is now the only remaining path**, exactly as round 14 predicted
it might be ("very likely more correct... it is what the engine's own working combat AI
actually calls"). Per the standing rule, no live test was attempted and no code was changed
this round; forcing a partial write into a structure this interconnected, without the real
constructor, risks producing a NEW, harder-to-diagnose failure mode (a sub-structure that looks
selected and half-initialized) rather than the current clean "does nothing" failure.

**What round 16 needs, precisely:** trace `0x4d2c30` and `0x4d2d60` (the two sub-calls whose
return values become `StartCombatRound`'s arg2/arg3 at call site `0x5BC537`) to a concrete
meaning, and trace the enclosing combat-decision routine's own population of
`[esp+0x10]`/`[esp+0x50]`/`[esp+0x6c]`/`[esp+0x14]` back to where THEY originate, far enough to
pin all 5 arguments with real confidence. Once pinned, `StartCombatRound` handles the whole
five-sub-structure reset-then-init sequence (including the exact `0x4D3010` calls this round
just dissected) and `+0x96C`/`+0x9B8`/`+0x9BC`/`+0x940` internally — calling it for real,
rather than hand-replicating pieces of it, is the correct shape for the fix once the argument
trace closes.

### Round 16 — `StartCombatRound` called for real, and it sticks — but the sub-structure gate stays closed

Static phase first, then live. Re-disassembled all three known call sites with full enclosing
context (`0x005BC480`-`0x005BC63B`) and pinned the full 5-argument thiscall with real
confidence:

- `this` = the round (`creature+0x9C8`).
- arg1 = the OPPONENT's object id — confirmed by `0x004D5F70`'s own body: it resolves arg1 via
  `GetGameObject` (`0x004AE770`) and stores it into `round+0x9C0`/`+0x9C4`, THEN mirrors those
  same writes onto the resolved opponent's OWN round at the identical offsets — which is why
  the engine's real caller makes a second, symmetric call right after (`this`=target's round,
  arg1=attacker's own id, at `0x005BC56D`). Independently confirms round 14's own finding from
  the caller side (arg1 stored into "target-like fields").
- arg2/arg3 — not independent constants; the exact return values of a fixed two-call sequence
  the real caller performs on the SAME targetId immediately before this call
  (`0x005BC4B3`-`0x005BC4D6`): `arg2 = 0x004D2C30(round, targetId)` written to a stack slot,
  then `arg3 = 0x004D2D60(round, targetId, &thatSlot)` — which may itself modify the slot —
  after which arg2 is RE-READ from the slot post-call. `0x4D2C30` resolves the target,
  double-casts it via `vtbl+0x30` (the SAME slot pattern seen twice in a row — a compiler
  artifact of an un-cached virtual call across a branch, not two different casts), then reads
  the TARGET's own `round+0x934`/an id-sentinel comparison at target+0x50c to decide "does the
  target need a round too." `0x4D2D60` reads that same result back through the out-pointer and
  does further validation against `round+0x9b4` (a per-round pointer to something not yet
  identified) before returning. Replicated call-for-call rather than reasoned about
  semantically — round 14/15 already confirmed the destination fields (`round+0x9B8`=arg2,
  `round+0x9BC`=arg3) are load-bearing (`0x4D2C30` itself reads a resolved target's
  `round+0x9BC` as part of its own decision).
- arg4 — read inside `0x4D2A10` as a 16-bit value compared against a small fixed constant set
  (`0xb`/`0x5b`/`0x35`/`0x1e`/`0x5c`/`0x1a`) to decide whether to increment a derived counter at
  `round+0x990`; never dereferenced as a pointer, so any integer is memory-safe. 0 matches none
  of the listed constants (the ordinary/no-special-case path) — but see the live result below,
  this default is now the prime suspect.
- arg5 — stored directly into `round+0x940` (`0x004D604B`), a plain DWORD, not part of the
  confirmed critical gate chain. Also memory-safe for any value. 0.

`ret 0x14` (5 DWORDs) confirms the stack shape matches exactly what was traced. Implemented
`Odyssey.ComputeRoundContextFn`/`ResolveRoundContextFn`/`StartCombatRoundFn`, wired into
`QueueAttackAction`'s existing one-shot gate (`roundTimerBefore == 0`) BEFORE the two setters,
matching the engine's own program order.

**Live result: real, measurable progress — the first-ever non-zero persistence — but the core
symptom did not resolve.** Reproduced the mid-fight-join scenario (host staged into a genuine
fight via `AttackTarget`, second instance connected and `tp`'d beside the same hostiles).
For the first time in this entire investigation, `round+0x958`/`round+0x960` on the mirrored
client STUCK: `round+0x958 timer-active BEFORE=1 AFTER=1` (never `0` again once set),
`round+0x960` drifting sanely (403→407→411→414, small organic increments, not the earlier
explosive accumulation bug). `StartCombatRoundFn` executed without crashing and produced
durable state — real confirmation the call is landing correctly.

Added one more targeted diagnostic (`round+0x96C` sub-index and the selected sub-structure's
`+0xB0`) and reinstalled to get the decisive read:

    round+0x96C sub-index=0 sub+0xB0=0

A valid sub-structure IS selected (index 0, not garbage) — but its gate field reads exactly
`0`, unchanged, on every sampled tick. `StartCombatRound`'s own sub-structure loop (`0x4D3010`,
round 15) explicitly zeroes `+0xB0` as a RESET step; whatever the "three more setup calls"
(`0x4d2a10`, `0x4d2a70`, `0x4f34a0`) that follow are supposed to do to actually ARM it never
lands with arg4/arg5 defaulted to 0. Prime suspect: arg4, the one argument whose full semantic
role (beyond "compared against six constants, never dereferenced") was never traced past
`0x4D2A10`'s partial body, and whose value is passed on into functions this round didn't open
at all (`0x4d2a70`, `0x4f34a0`).

Also measured, real but secondary: the `AttackTarget` staging call itself is flaky
PER-CREATURE, independent of any of this investigation's changes — the same command against
one fresh `sithsoldier` (`0xE`) produced no damage after 45+ seconds this round, while the
SAME command against its twin (`0x10`) worked within 8 seconds, on the same save, same
session. Worth a look separately; it is not this section's mechanism.

**Decision: `local_combat` stays off.** Both `coop.ini` restored to `0`. Full suite 953/953
(one pre-existing flaky test in `DisconnectRecoveryTests`, confirmed passing in isolation, not
a regression from this round's changes — only `Odyssey.cs` was touched).

**What round 17 needs, precisely:** fully disassemble `0x4d2a70` and `0x4f34a0` (not opened
this round at all) and finish `0x4D2A10`'s body past the constant-comparison block already
read, looking specifically for a write to `+0xB0` on the sub-structure `round+0x96C` selects —
and whether that write is conditioned on arg4 (or arg5) being a specific non-zero value rather
than the 0 defaults used here. If none of the three setup calls ever write `+0xB0` regardless
of argument value, the write happens somewhere else entirely (possibly triggered by the
ENGINE'S OWN next tick reading a state this round's call correctly primed but round 16 did not
wait long enough to observe — the live pass ran ~45s per attempt, and `+0xB0` might only get
set on a delayed follow-up condition, e.g. once `round+0x960`'s delay actually elapses against
`creature+0xCC`, which `SetRoundDelayFn`'s own doc comment already flags as the OTHER live gate
`UpdateCombatRoundTimer` checks alongside this one).

### Round 17 — `sub+0xB0` decoded: it is an array-list count, and the real Add call still does not arm it

Full instruction-level tracing (not guessing) of every function that touches `+0xB0` in the
combat-round code region:

- **Refuted the round-16 "arg4 arms it" hypothesis outright.** `0x4D2A10` (arg4's consumer)
  only ever writes `+0x990` (a counter, unrelated). `0x4D2A70` writes `+0x994` (a boolean).
  `0x4F34A0` (the third setup call) writes `+0x944` and calls `0x4CD170` (target-lock
  bookkeeping). None of `StartCombatRound`'s three follow-up setup calls, disassembled in full,
  ever touch `+0xB0`.
- **`+0xB0` is the COUNT field of a `push_back`-style array-list whose data pointer lives at
  `+0xAC`** (0xD0-byte elements). `0x004D2EE0` is its `RemoveAt` (decrements `+0xB0`, shifts
  elements down) — called from `0x004D32D0`, itself called at the tail of
  `UpdateCombatRoundTimer`'s per-tick processing loop, confirming `UpdateCombatRoundTimer`
  actually LOOPS "while `sub+0xB0` > 0", consuming one element per real attack processed. This
  is a genuinely separate mechanism from `round+0x9B0` (the list `AddAttackAction` appends
  to) — the two have never been the same list.
- **The real `Add`: `0x004D57A0`** (`this = sub+0xAC`, one 0xD0-byte element pushed on the
  stack, `ret 0xD0`) — confirmed by full disassembly, writes `[this+4]` = count+1. Its only two
  callers both live inside `0x004D5A50`, a function that assembles the full 0xD0-byte element
  from fields ALREADY PRESENT on the sub-structure itself (`+0x1c`, `+0x3a`, `+0x5c`, `+0x64`,
  `+0xc0`, `+0xe0`/`+0xf4` — weapon/roll data, `+0xfe`, `+0x128`) before calling `Add` — i.e.
  this is a "commit a fully-computed attack" step, not something that could be satisfied by a
  bare struct poke; those fields would need populating first, and where they come from was
  never traced (its own single caller sits deep inside an unrelated, ~7-argument per-target
  validation loop near `0x5BB890`/`0x5BB590`, never traced to real confidence, and this thread
  was abandoned as too large and too risky to guess-call).
- **The actual fix implemented instead: `CSWSCreature::CreateNewAttackActions`
  (`0x005B6980`)** — the engine's own single, already-fully-decoded-and-VERIFIED entry point
  for this exact job (`docs/research/combat-participation.md` §1, written independently of
  this investigation). `QueueAttackAction` now calls it with `bMayAcquireNewTarget=1` instead
  of a bare `AddAttackAction`: the bare-revalidation line inside `CreateNewAttackActions` only
  ever fires when `round+0x9B0` is EMPTY (per §1.2), which our own prior `AddAttackAction` call
  had already made false — so every earlier round's direct `AddAttackAction` call was hitting a
  path this function's own body would always skip. The ACQUISITION path (§1.3, only runs when
  `bMayAcquireNewTarget != 0`) does real `GetNearestEnemy` searches and, on success, calls
  `UpdateAttackTargetForAllAction(round, newId)` (`0x004D3E50`) — never previously called from
  this file, and the strongest remaining candidate for arming the round's real state.

**Live result: `CreateNewAttackActionsFn` executes cleanly — and still does not arm `sub+0xB0`.**
The first live attempt showed `queued-this-pass=False` on every throttled (3s) sample, because
the one-shot gate fires within a single engine frame — far narrower than any external poll can
catch. Added an UNCONDITIONAL log immediately after the `CreateNewAttackActionsFn` call
(no throttle) and reran the mid-fight-join scenario clean: **five independent mirrored
creatures** each called `CreateNewAttackActionsFn(creature, 1, 0, 0x7FFFFFFF)`, all returned
without crashing, and every single one showed `round+0x96C sub-index=0 sub+0xB0=0` and
`head action right after=0xFFFFFFFF` **immediately after the call returned** — i.e. the function
ran to completion and did not queue anything, on 5/5 creatures, not a fluke.

**What this rules out and what it points at.** The function itself is proven safe (no crash
across 5 live invocations) but its internal logic must be bailing before reaching the
acquisition-success path. Per §1.3's own documented gate, `GetNearestEnemy`'s hostility test is
`GetAIStateReputation(candidateCre, vsId) == 2` (exactly 2 = hostile; NOT the same
`GetReputation ≤ 10` numeric test the separate revalidation table's test 6 uses) — this specific
mapped value has never been directly checked for the pair (mirrored creature, local player id
`0x7FFFFFFF`). If it returns anything other than 2 for this pair, `GetNearestEnemy` finds
nothing and the whole acquisition path silently no-ops, which matches the observed symptom
exactly. **Round 18 should read `GetAIStateReputation` live for this exact pair before touching
any more code** — a read-only diagnostic, not another implementation guess. The `local_combat`
default stays off; both live installs' `coop.ini` restored to `0`. Full suite 953/953, both
builds clean. Diagnostic logging (throttled and unconditional) both left in the tree
deliberately — this is still not settled.

### Round 18 — both `GetNearestEnemy` gates measured live, both pass, and it still does not fire

Read live, for the exact pair in question. Added a diagnostic reading `GetAIStateReputation`
AND `GetVisibleListElement`'s seen bit right before `CreateNewAttackActionsFn` is called, for
the mirrored creature actually engaged in the host's fight (attacker real id `0xE`, target
`0x7FFFFFFF`):

    GetAIStateReputation(attacker,player)=2 visibleListNode=0x1C571738 flags=0x3 seenBit=True
    playerObj=0x182D96A0 attackerRealId=0xE GetAIStateReputation(player,attacker)=2

**Both directions measured, both hostile (=2), symmetric.** §1.4's own worked example checks
`GetAIStateReputation(candidateCre, vsId)` — for a scan anchored at the player's position with
`vsId=attackerId` (`GetNearestEnemy` attempt 1), that's `player.GetAIStateReputation(attackerId)`,
the OPPOSITE thiscall direction from what round 17's hypothesis assumed. §2.3 flags faction 0
(PLAYER) as going through an argument-swapped matrix lookup, so symmetry was not guaranteed —
measured directly rather than assumed, and it holds: both directions return exactly 2. **The
reputation candidate is REFUTED**, in both directions this time, not just the one round 17
originally suspected. The seen-bit perception gate is also confirmed set (`flags=0x3`, bit 0
set) — genuinely perceived, not a stale/missing node.

So of `GetNearestEnemy`'s six documented gates (§1.4), the first four (resolves, hostility,
alive, perception) are now directly measured and all pass for the actual engaged pair. The
two never yet measured are #5 (distance beats the best-so-far seed) and #6 (line of sight) —
both plausible next candidates, though the player was `tp`'d to within ~2m of the attacker with
nothing between them, so neither looks likely on its face. More probable: something downstream
of a SUCCESSFUL acquisition (§1.3's own tail — `SetLockOrientationToObject`,
`UpdateAttackTargetForAllAction`, the final "queues the attack action" step) is where this
actually breaks, since every INPUT gate to acquisition now checks out clean. Round 19 should
either measure #5/#6 directly (cheap, same technique) to close out the acquisition-gate list
entirely, or — more likely productive — trace what `UpdateAttackTargetForAllAction`
(`0x004D3E50`) actually does and whether it, not `GetNearestEnemy`, is where a found target
fails to translate into `sub+0xB0` gaining an entry.

`local_combat` stays off; both `coop.ini` restored to `0`. Rig torn down, confirmed clean
(`Get-Process` returned none). Full suite 953/953, both builds clean. Diagnostic logging from
rounds 9-18 all left in the tree deliberately — still not settled, and each round's diagnostic
has been load-bearing for the next.

### Round 19 — the acquisition tail is settled; `GetNearestEnemy`'s own scan never finds the player

Full disassembly of `CreateNewAttackActions`'s (`0x005B6980`) success tail (`0x005B6B58`-
`0x005B6C34`) closes the "queues the attack action" ambiguity §1.3's own prose left open:
**both** the re-validation path (§1.2, `round+0x9CC` valid and survives the six release tests)
and the acquisition-success path (§1.3, `GetNearestEnemy` finds a new target) converge on the
identical tail call — `AddAttackAction(round, target, 0, 0, 0x5DC, 1)` at `0x005B6C1D`. There
is no separate, untested "queue" step; `CreateNewAttackActionsFn` already reaches the same
`AddAttackAction` every earlier round called directly. `UpdateAttackTargetForAllAction`
(`0x005B6BA0`) and `SetLockOrientationToObject`/`0x4F34A0` (`0x005B6B88`) both run, unconditionally,
on the way there — neither gates the tail call, so round 17/18's suspicion of them was
misplaced; they are cosmetic/bookkeeping relative to the actual queue.

**Two live-measured hypotheses this round, both refuted with direct evidence:**

- **`AsCreature` (`vtbl+0x30`) cast on the re-resolved target, `0x005B6B6B`** — the acquisition
  path's one previously-untested step, since a failed cast takes the exact same silent
  "acquisition failed" bail (`0x5b48b0`, disassembled: `SetLockOrientationToObject
  (OBJECT_INVALID)`, pause animation, `SetCombatMode` stand-down) as if nothing were found.
  Measured live, unconditional per-call: `AsCreature(vtbl+0x30) cast on the player object:
  SUCCEEDS`, every sample, both test passes. REFUTED.
- **`area_id` mismatch (`creature+0x8C`)** — `docs/research/realtime-creature-movement.md` §1
  documents `SetPosition`'s own area-resolve step verbatim: `this->area_id` (`+0x8C`) is looked
  up through the engine's object array to find the `CSWSArea`, and only THEN does
  `UpdatePositionInObjectsArray` register the object into that area's x-sorted spatial index
  (`CSWSArea+0x190`/`+0x194` — the exact array `GetNearestEnemy` walks, §1.4). A stale/wrong
  `area_id` on the client's local player object would let raw x/y/z read correct while the
  object sits in the wrong area's index entirely, invisible to a scan anchored where the
  sithsoldiers actually are. Measured live: `area_id (creature+0x8C) - player=0x1
  attacker(sithsoldier)=0x1 MATCH=True`, every sample. REFUTED.

**The actual, decisive measurement — `GetNearestEnemy` called directly, both of §1.3's search
attempts, replicating the exact scan `CreateNewAttackActionsFn` performs internally:**

    GetNearestEnemy attempt1(from playerObj)=0x7F000000 attempt2(from attacker)=0x7F000000
    (repeated identically, every sample, both live test passes)

**`GetNearestEnemy`'s own area scan never finds the player as a candidate, in EITHER search
direction, ever** — not "found but rejected by a gate downstream," genuinely never found, even
though every individual per-candidate condition the function is documented to check (resolve,
hostility both directions, perception/seen-bit, and now the `AsCreature` cast) independently
measures as passing, and even though the spatial-index area assignment matches. This means the
failure is not in a targeting RULE at all — it is somewhere in the scan MECHANISM itself: either
the x-sorted array genuinely does not contain the player's entry despite matching `area_id` (a
registration-step failure, not an assignment failure), or the scan's own "find my anchor's index,
then walk outward" first step (§1.4's opening line) fails for one of the two anchors used here
(`playerObj` for attempt 1, the attacker itself for attempt 2) in a way that aborts the walk
before it ever reaches a real candidate.

**What round 20 needs, precisely:** `GetFirstObjectIndiceByX` (`0x00506F20`, already tabulated in
`combat-participation.md` as "binary search into the x-sorted array") is the most likely
"find my own index" entry point `GetNearestEnemy` calls before walking outward — disassemble
`GetNearestEnemy` (`0x004F2DE0`) itself in full for the first time (every round so far has
treated it as a black box and only measured its RESULT) to see exactly what it does with its own
`this` argument before the outward walk begins, and specifically whether it independently
re-resolves the anchor's area/array position or trusts a cached index that a driven,
never-transitioned client puppet might never have established correctly.

`local_combat` stays off; both `coop.ini` restored to `0`. Rig torn down and confirmed clean
(`Get-Process` returned none, both process names). Full suite 953/953, both builds clean.
Diagnostic logging from rounds 9-19 all left in the tree deliberately — still not settled, and
each round's diagnostic has been load-bearing for the next.

### Round 20 — the registration theory is REFUTED; the real mechanism is a scan race, and a natural fix for it did not close the gap

Full disassembly of `GetNearestEnemy` (`0x004F2DE0`-`0x004F2E4F`) — never dissected before this
round, every prior round only read its RESULT — confirms `combat-participation.md` §1.4's own
description exactly: the very first thing it does, before any candidate check, is a LINEAR
self-lookup: get `this`'s own area (`GetAreaFn`, `0x004CB120` — a zero-arg thiscall, called
unconditionally as the function's first instruction; if it returns 0 the whole call bails to
`ObjectInvalid` immediately, `0x004F2DF0`-`0x004F2DFE`), then scan the area's x-sorted id array
(`area+0x190` data pointer, `area+0x194` count) for an entry equal to `this+0x04` (own id). If
`this` itself is never found in its own area's array, the function returns "nothing found"
before ever looking at a candidate — matching round 19's universal failure exactly.

**The registration hypothesis, directly measured and REFUTED.** Implemented `FindSelfInAreaArray`
(`Odyssey.cs`) — the identical self-lookup, callable as a read-only diagnostic — and ran it live
on both the real engaged attacker and the real player object, mid-fight-join, same rig as every
round: `area=0x33899F8 arrayPtr=0x192A4B60 count=221 found=True index=85` (attacker) and
`found=True index=65` (player), same area, same array, both PRESENT with valid indices. This is
a plain linear scan (order-independent for a presence check), so this rules out a
registration-step failure outright — both objects are exactly where `GetNearestEnemy`'s own
anchor step would find them.

**What the SAME live session's own `GetNearestEnemy` calls showed instead: not a categorical
failure, a RACE.** The existing round-19 diagnostic (still in the code, same call) logged
`attempt2(from attacker)=0x7FFFFFFF` on one of five mirrored creatures that session —
`0x7FFFFFFF` is not a sentinel here, it is the PLAYER'S OWN REAL OBJECT ID (confirmed earlier in
this investigation: `console 'resolve 0x7FFFFFFF'` resolves it to a real, alive creature) — i.e.
`GetNearestEnemy` genuinely found the player as a candidate and returned its id, a real success,
on that one call. The other four creatures' one-shot calls returned `0x7F000000` (not found) in
the same session, sub-second apart. The attacker's own index in the array was also observed
bouncing between samples (`85, 86, 110, 121, 118, 85` across consecutive ~1s-apart diagnostic
reads) — consistent with our own per-frame `SetPosition` calls constantly re-sorting this exact
array, which the outward-walk's x-distance-bounded termination (`combat-participation.md` §1.4:
"terminating each arm as soon as the x-distance alone exceeds the best hit so far") could plausibly
miss depending on exactly when, relative to the last re-sort, a given call happens to run.

**Fix attempted: retry instead of one shot.** The existing round-init gate
(`roundTimerBefore == 0`) had `CreateNewAttackActionsFn` fire exactly ONCE per creature — a single
roll of what looked like a race. Moved the call OUT of that one-shot gate (keeping
`StartCombatRoundFn`/`SetRoundTimerActiveFn`/`SetRoundDelayFn` one-shot, unchanged — those ARE
confirmed self-defeating if repeated, rounds 12/13) so it retries every drive pass instead. This
is safe against round 13's spam failure mode by construction: `CreateNewAttackActionsFn`'s
acquisition path re-runs `GetNearestEnemy` fresh each call (a genuine new roll, not a duplicate
append) and the existing `IsFightAction(headType)` gate at the very TOP of `QueueAttackAction`
already stops all future calls the instant one attempt actually lands a queued swing — the retry
can only keep trying until it succeeds, then the outer gate closes the door on its own.

**Live-retested, and the retry did NOT reproduce success.** Rebuilt, reinstalled, ran the
identical mid-fight-join scenario a second time from a genuinely fresh pair of instances. Over
40+ seconds, `CreateNewAttackActionsFn` retried on a ~3s cadence across THREE different mirrored
creatures (`0x1826BBE0`, `0x1825B358`, `0x18264420`) — `sub+0xB0` read 0 on every single logged
call, no exceptions. If the true per-attempt success rate were anywhere near what the single
round-19 sample suggested (1 real success logged out of 5 one-shot attempts, ballpark 20%), this
many independent retries should very likely have landed at least once; that it did not suggests
either the round-19 sample's apparent success rate was itself an outlier (small-sample noise, not
a stable rate), or something about this session's specific conditions (a fight already in
progress longer, the specific creatures' positions relative to the player, or something not yet
identified) made every attempt systematically less favorable than that one sample — the two test
runs are not proven to be equivalent trials of the same stationary process.

**Left in place, not reverted:** the retry-instead-of-one-shot change is strictly safe (confirmed
non-spamming via the existing outer gate, confirmed non-crashing across 40+ seconds and three
creatures) and gives strictly more chances at success than the one-shot version it replaced, so
there is no reason to revert it even though it did not close the gap this round. ⚠ **Performance
caveat for whoever continues:** unlike the one-shot version, this now runs a full
`CreateNewAttackActionsFn` (including a real `GetNearestEnemy` area scan, up to array-size
iterations) on EVERY drive pass for every engaged-but-not-yet-successfully-attacking mirrored
creature, indefinitely, for as long as it keeps failing — no retry cap exists yet. Acceptable
while this stays off by default and unresolved; worth capping (or backing off) before this is
ever considered for real use.

**What round 21 needs:** the outward-walk loop itself (`0x4F2E52` onward — past the anchor
step this round fully mapped) has never been disassembled to completion in this investigation;
neither has `GetFirstObjectIndiceByX` (`0x00506F20`), which round 19 flagged as the likely
mechanism and this round's evidence did not actually require (the linear anchor-scan this round
disassembled does not call it — check whether the OUTWARD walk uses it instead, or whether it
is a separate/unused code path for this particular call shape). Given the race-not-registration
finding holds, the more promising direction may be instrumenting the outward walk itself to see
exactly which pruning branch discards the player as a candidate on a representative FAILING call
— round 20's diagnostics answered "is it present" (yes) but not "why does the walk that starts
from a present anchor still not reach a present candidate."

`local_combat` stays off; both `coop.ini` restored to `0`. Rig torn down and confirmed clean.
Full suite 953/953, both builds clean.

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
