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

### Summary of the whole investigation (2026-08-11 through round 27, 2026-08-21) — read this first

Everything below this point, through "Round 27," is the full dated trail — kept in full because
each entry is load-bearing evidence for the next, and because the false leads are as useful as
the true ones to whoever picks this up. This summary is for orientation; it does not replace the
trail, and nothing below has been pruned.

**What `local_combat` is.** A client's forwarded-damage mirroring already works without it
(enemy health/death and damage numbers all replicate correctly). What doesn't: the client's OWN
engine never believes it is "in combat," because its enemies are driven puppets, not creatures
its own AI is fighting. `local_combat` (`coop.ini`, default OFF) is the attempt to fix that
properly — let a mirrored enemy genuinely fight the local player through the engine's own combat
system, with `IncomingDamageController` standing down from absorbing forwarded damage once a
local hit is confirmed landing, so nothing is double-counted.

**Phase 1 (2026-08-11 to 2026-08-14) — the design's own structural defects, found and fixed.** An
independent review found four defects that would have made a naively-enabled `local_combat`
actively unsafe: target divergence (a mirrored enemy could lock onto the wrong player — fixed by
holding peer phantoms Neutral on a mirroring instance), a full-queue action wipe that would have
left materialised/ambush enemies permanently unable to swing (fixed with a move-only wipe), a
companion aggro sink that could leave a party member permanently downed with a full health bar
(fixed with unconditional health force-writes and min-1-hp pinning), and a blanket damage absorb
with no per-attacker attribution (fixed, gated on `IsLocallyEngaged`, with a self-correcting
watchdog that reverts to forwarding damage if local absorption isn't actually happening). All
four are confirmed still present and correct as of round 11 (2026-08-20) — this part of the
mechanism is safe.

**Phase 2 (2026-08-20, one day, 27 rounds) — is the mechanism itself real, and it wasn't, until it
nearly was.** The first live test (with all four safety fixes in place) found the mechanism does
NOTHING: a mirrored enemy standing next to a genuinely-fighting host's remote player never enters
combat, not once in 90+ seconds, even though it is correctly in the driven set. Twenty-seven
rounds of disassembly-first reverse engineering (rounds 1-27, all dated `### Round N` below)
progressively found and fixed FIVE real, independently-verified bugs in the attempted mechanism:

1. **The round-timer was never initialized** (`round+0x958`, set via a real engine call,
   `0x004D28F0`, confirmed against 3 real engine call sites).
2. **A second, undocumented gate right next to it** (`round+0x960`, same call, second field) also
   had to be set for the timer to actually run.
3. **Two self-defeating "fires every frame instead of once" bugs**, each looking exactly like
   the original problem until diagnosed (the round-delay setter accumulating into the far future;
   the attack-append call spamming duplicate, unprocessed nodes) — both fixed by gating on a
   one-shot "has this round already been armed" check.
4. **`StartCombatRound`'s real 5-argument signature**, traced to full confidence across three
   rounds and finally called for real — after which the round-timer genuinely STUCK for the first
   time (previously it silently reverted to 0 within the same tick).
5. **The `AttackTarget` test-staging tool itself was broken by this exact same missing
   precondition** — round 11 proved the whole mechanism was sound in principle by fixing this
   separate tool and producing the first real, independently-measured fight of the whole
   investigation (host-local creature, player HP 12→8→1, verified via position tracking, not a
   log line).

**Every other candidate explanation was tested and individually refuted, with live evidence,
across the remaining rounds:** target-resolution correctness, hostility (`GetAIStateReputation`,
both directions, measured exactly `2` as required), perception/the "seen" bit, area membership,
the `AsCreature` cast, the search-range value (measured live: `2.6` vs an actual distance of
`0.688` — the player was never outside range), the outward-walk scan itself (fully disassembled,
structurally sound, and — importantly — the "it's a live timing race against our own position
writes" theory that several rounds were built on was itself refuted in round 24: this engine is
single-threaded, so a scan in progress cannot be corrupted mid-walk by a position write; what
looked like a race was just different-but-internally-consistent snapshots), and
`UpdateAttackTargetForAllAction` (fully opened in round 26: a real dead end, redirects targets on
actions already queued, never arms anything).

**Where it stopped (round 27).** The actual write that arms a round to process a queued attack
was finally located — but it lives inside the engine's own bounded loop over combat participants,
with multiple conditional setup steps per iteration, called from exactly one site deep inside the
engine's own stateful per-tick round-advancement machinery. It is not a small utility a plugin can
call in isolation with a hand-traced signature, the way every other piece of this mechanism was.
That is the wall: not a missing call, but a piece of engine logic that only exists to run as part
of a combat round the engine itself is already advancing — which a driven puppet, by definition,
never is.

**Status after round 27.** `local_combat` stays off by default. Nothing about this investigation
found or left anything unsafe — every fix from rounds 1-27 is real, tested, and stays in the tree;
they were all genuine, verified improvements even though the final step turned out to be
structurally out of reach this way. Two ways forward were identified: (a) reverse-engineer the
engine's own round-advancement loop and its full caller chain as its own project; or (b) stop
externally replicating the engine's native combat calls, and instead make the engine's OWN AI
script actually run for a driven puppet.

**Strategy 2 (2026-08-21, same day, immediately after) — route (b), and it is real.** Found the
engine's own generic script-event dispatcher (`RunScript`, already proven live-working elsewhere
in this codebase for a door's OnOpen script) and the creature's own combat-AI script slot
(`creature+0x260`, confirmed live to hold `k_def_combend01`, KOTOR's real default
`OnCombatRoundEnd` script). **Firing it on a HOST-LOCAL creature produced a genuine, real fight —
player HP 12→1, independently verified — the first positive result of the entire investigation,
using none of the 27 rounds' hand-replicated native calls.** Firing the SAME thing on a
MIRRORED/driven creature does NOT work — round 28 reran this cleanly isolated
(`local_combat=0`, no competing mechanism) with a real per-frame watch on the round struct
itself, across four attempts on two creatures over ~50 seconds, and the round never armed once.
(A first HP-based reading in that round looked positive but was a confound — the pre-existing,
unrelated phantom damage-forwarding feature, not this mechanism; see round 28 for the full
account.) So: PROVEN to work for a host-local creature, PROVEN not to work for a driven one —
the same wall rounds 1-27 hit, reached by a different route.

**Round 29 then refuted round 28's own "driven puppet" hypothesis.** A creature with GENUINELY
ZERO writes from this plugin (no position, no facing, no claim — a new diagnostic,
`local_combat_diag_undriven_id`) hit the exact same wall via the exact same organic script
fire: the round genuinely armed and counted down (`round+0x958`/`round+0x960`, smoothly,
organically, proving those are real fields a live decision process uses) and then reset at
expiry instead of committing — never reaching `sub+0xB0` — with no artificial driving involved
at all. So the block is not about being mirrored, driven, or touched by this plugin in any way
that's been tested. The one constant across every failure in 29 rounds is a connected co-op
session with a remote peer present. The strongest remaining candidate: whatever
participant/roster table the engine's per-tick round-advancement loop consults before
committing (round 27's "party-table-style field" gate) may simply never register the connected
remote player as a valid participant — a session/party-table-level question, not a
creature-level one. See round 29's own entry for the full evidence.

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

### Round 21 — the discrepancy resolved (it was never a discrepancy), two candidates refuted, one sharp new lead

**Phase 1, resolved.** `CreateNewAttackActionsFn` was never throttled — `EnemyPresenceController.OnFrame()` runs unconditionally every real game frame (`PluginHost`'s main per-frame callback calls it right after the combat pass, no interval gate), and `QueueAttackAction`'s only throttle is on the DIAGNOSTIC LOG LINE (`diagLog`, 3s), not on the call itself. So round 20's "0/many over 40+ seconds across three creatures" already represented hundreds of real per-frame attempts, not a handful — the apparent 1-in-5 sample from round 19 was a small, one-shot-per-creature sample (each of those five diagnostic calls came from a DIFFERENT creature's own round-init transition, not five retries on one creature) and should not have been read as a stable per-attempt success rate to begin with. There was no discrepancy to explain; round 20's zero-success retry result is the more trustworthy measurement.

**Two candidates tested live this round, both refuted:**

- **Point-blank distance.** Every prior round used ~2m separation ("tp beside" the target). This round positioned BOTH the host and, separately, the client's own local player, standing exactly on top of (0m separation from) their respective sithsoldiers. `CreateNewAttackActionsFn` still returned `round+0x96C sub-index=0 sub+0xB0=0` on every throttled sample over 40+ seconds — identical to the ~2m case. Whatever gate is failing, it is not a distance/range gate at any measured proximity, including zero. This weakens (though does not fully retire, since `MaxCleaveRange`'s actual computed value was never read directly — see below) the theory that `CreateNewAttackActions`'s internal `MaxCleaveRange(ownId,0,1)` range parameter (never actually verified — round 19's own diagnostic used a fixed `50.0f` stand-in, explicitly flagged as such, NOT the real value) is the blocker.
- **Degenerate/null weapon-equipment state.** `MaxCleaveRange` (`0x004FFAD0`) delegates to `0x4FB0F0`, whose first real step reads a pointer at `creature+0xA2C` (an equipment-slot reference) and branches on data reached through it — a null pointer here would make the range computation degenerate for every candidate, independent of any race. Read live (safe, read-only): `creature+0xA2C` is non-null and looks like a real heap pointer (`0x3357ED0`, `0x33583A0`, distinct per creature) on the actual engaged mirrored puppet. Not a smoking gun — REFUTED as an obvious null-pointer explanation.

**A sharper new lead, not yet followed to a conclusion.** The existing round-19/20 diagnostic (still one-shot, inside the `roundTimerBefore==0` gate) logged both search attempts on several creatures this round, using its `50.0f` stand-in range:

    GetNearestEnemy attempt1(from playerObj)=0x7F000000 attempt2(from attacker)=0x7F000000   (creature 0x40, 0x2, 0x3E)
    GetNearestEnemy attempt1(from playerObj)=0x7F000000 attempt2(from attacker)=0x7FFFFFFF   (creature 0xE, 0x10 — the ACTUAL engaged pair)

**Attempt 1 (searched from the player's own position) never once succeeded, across every sample gathered this round or any prior round.** Attempt 2 (searched from the attacker's own position, the documented fallback) succeeded for both real engaged creatures at least once. `combat-participation.md` §1.3 documents attempt 2 as an unconditional fallback ("Fallback: ..."), but no round has ever verified — by disassembly — that `CreateNewAttackActions`'s real internal logic actually falls through to attempt 2 unconditionally when attempt 1 fails, as opposed to some intermediate condition (checked only inside the real function, never replicated by this file's manual two-call diagnostic) gating whether the fallback runs at all. If the real function's fallback path is gated on something attempt 1's failure alone doesn't satisfy, that would explain everything measured so far without needing any further range or registration theory: the internal call could be silently stopping after attempt 1 fails, never reaching the attempt-2 shape that our own diagnostic proves CAN succeed for these exact creatures.

**What round 22 needs, precisely:** disassemble `CreateNewAttackActions`'s (`0x005B6980`) own acquisition body (`§1.3`'s address range, roughly `0x005B6AF0`-`0x005B6B58` — the actual internal calls to `GetNearestEnemy`, not the tail already settled in round 19) instruction by instruction, specifically the branch between the two `GetNearestEnemy` calls — what condition, if any, gates whether the second call runs at all, and whether it matches what this round's evidence shows attempt 2 can do. Separately, and only if that doesn't resolve it: read `MaxCleaveRangeFn`'s actual returned value live (calling convention not yet pinned with real confidence — `this` appears to be the creature per `0x4FB0F0`'s ECX pass-through, but the exact stack-argument shape implied by `combat-participation.md`'s `MaxCleaveRange(ownId,0,1)` notation was not resolved to real confidence this round and should not be guessed live without disassembling the doc's own worked call site for it first).

`local_combat` stays off; both `coop.ini` restored to `0`, rig torn down and confirmed clean (`Get-Process` returned none, both process names). Full suite 953/953, both builds clean. The `creature+0xA2C` diagnostic line is left in the tree (small, safe, and load-bearing for whoever re-reads this log next).

### Round 22 — the real acquisition body disassembled; attempt 2 is unconditional; a sharper lead than "gating" found instead

**Static only this round — no live test.** Full instruction-level disassembly of `CreateNewAttackActions` (`0x005B6980`) from entry through the known success tail (`python tools/disasm.py 0x005B6980 0x400`), answering round 21's exact question.

**The real signature has (at least) THREE stack parameters, not the one bool this investigation has been calling `bMayAcquireNewTarget`.** After accounting for the prologue (`sub esp,0xc` + 4 pushes = 0x1c bytes), the original caller-side stack slots are: param1 (`orig[esp+4]`, read late as `[esp+0x20]`) — a gate checked ONLY at a merge point this investigation's own call path bypasses (see below, so it does not matter for us); param2 (`orig[esp+8]`, our `bMayAcquireNewTarget`) — selects whether the "already have a target?" check reads `creature+0x524` (when 1, our case) or `round+0x9CC` (when 0); param3 (`orig[esp+0xC]`, read as `[esp+0x28]`) — an object-id passed to `GetGameObject` (`0x4ae770`) as the origin for search **attempt 1 only**. This codebase's `Odyssey.CreateNewAttackActionsFn` wrapper has only ever declared and passed the one bool — param1 and param3 are whatever garbage/zero happens to be on the stack, never intentionally supplied.

**Round 21's "is attempt 2 conditionally gated?" question is answered: no, it is unconditional.** With `bMayAcquireNewTarget=1`: `ebp = [attacker+0x524]`. If that reads `0x7F000000` (no cached target — true for a fresh engagement) AND the round's pending-list head is empty (`[[round+0x9B0]]==0`, also true for a fresh engagement), execution jumps DIRECTLY to the search block at `0x5B6AEF`, bypassing param1's gate entirely. There, **attempt 1** resolves `param3` via `GetGameObject` — if param3 is 0 (as our wrapper effectively passes), this resolves to null, `edi` stays 0, and the code falls straight through to **attempt 2** (`0x5B6B2E`: `ecx=esi`, i.e. the attacker itself) with no condition beyond "attempt 1's object didn't resolve." Attempt 2 always runs for our case. Its result (`edi`) is what's been observed succeeding sometimes across rounds 19-21 (`0x7FFFFFFF`) and failing other times (`0x7F000000`) for the identical creature pair. **The "hidden gate" theory is refuted** — there is no gate skipping attempt 2; the mechanism this investigation has been exercising via diagnostics IS the real one, faithfully.

**A sharper, concrete, untested lead: the search-range argument passed to `GetNearestEnemy` on the live call has never been read.** Both attempts compute a float via a call to `0x4FFAD0` (thiscall, `ecx`=the searching object — `esi`/attacker for attempt 2) immediately before `GetNearestEnemy`, and that float (left in `ST(0)`, stored via `fstp [esp]` as `GetNearestEnemy`'s stack argument) is the search radius. `0x4FFAD0` is a thin thunk: 3 args forwarded to `0x4FB0F0`, then `fadd dword ptr [0x741be0]` (a global float constant added to whatever `0x4FB0F0` returns) before returning. This is almost certainly the real `MaxCleaveRange`-family computation `combat-participation.md` names — but **round 19's diagnostic used a hardcoded `50.0f` stand-in for this value, never the real one**, and round 21 confirmed the underlying equipment pointer (`creature+0xA2C`) is non-null but never read what `0x4FB0F0` actually computes from it. If this real computed range is small, zero, or NaN for a mirrored puppet specifically (as opposed to a host-local creature, where the identical function presumably returns a sane value, since host-local combat via `AttackTarget` staging worked in round 11), that alone would explain an intermittent-to-never success rate independent of anything about the outward-walk logic `GetNearestEnemy` itself runs — no further disassembly of the walk would be needed.

**What round 23 needs, precisely:** a live diagnostic that calls `0x4FFAD0` (or `0x4FB0F0` directly, thiscall `ecx`=the attacking creature, 3 stack args — the exact 3-argument shape at the `0x5B6B08`-`0x5B6B14` call site, not guessed) on the actual engaged mirrored creature, and logs the real returned float. Compare it against the same read on a host-local creature (round 11's working case, if still reproducible) as a control. If the mirrored value is degenerate, find why (a field `0x4FB0F0` reads that differs between a normally-loaded host creature and one only ever driven/mirrored) and fix it there, rather than anywhere in the round-init or acquisition-calling code this investigation has already spent nine rounds getting right.

`local_combat` stays off (unchanged this round — no code touched, docs only). No live rig used this round.

### Round 22 correction (2026-08-20, same day) — a param-order error, and the real acquisition semantics were already verified elsewhere

Round 22's own analysis mislabeled `CreateNewAttackActions`'s arguments — it is fully decoded
already, in `docs/research/combat-participation.md` §1 (verified independently of this
investigation): `arg1 = bMayAcquireNewTarget`, `arg2 = bUseAttemptedTarget`, `arg3 =
candidateId`. Our call, `CreateNewAttackActionsFn(creature, 1, 0, targetId)`, passes
`candidateId = targetId` (the real player id) — NOT unset/zero as round 22 concluded. Attempt
1 genuinely resolves and searches (per §1.3) `GetNearestEnemy(candidateCre=player, ...,
excludeId=ownId, vsId=ownId)` — hostility judged FROM the attacker's perspective, TOWARD
whatever third creature the walk finds near the PLAYER's position, excluding both the player
itself and the attacker itself. Near a lone player with no other hostiles standing next to
them, this legitimately finds nothing almost every time — expected behaviour, not a bug.
Attempt 2 (`GetNearestEnemy(this=attacker, ..., vsId=OBJECT_INVALID)`, hostility judged from
the attacker's own perspective toward whatever the walk finds near ITS position) is the real
"find the player" search, exactly as every diagnostic since round 19 has modeled it. Round
22's core, still-standing finding — there is no hidden gate between the two attempts, and the
diagnostics have been faithfully exercising the real code path — is unaffected by this
correction; only the specific claim about `candidateId` being unset was wrong.

### Round 23 — reading the real weapon-derived range instead of the 50.0f stand-in

Full disassembly of `MaxCleaveRange`'s sub-call (`0x004FB0F0`, thiscall, `ecx`=the searching
creature): resolves the equipped weapon at slot `0x10` via `0x005A4C20(equipSlotPtr, 0x10)`;
if non-null, an accessor (`0x005B4790(weaponObj)`) yields a struct whose `+0x1A` byte gates
the branch (nonzero → capable weapon) and whose `+0x34` float is the weapon's own range
field, used or replaced by a global constant depending on a second comparison. If the
equipped-weapon lookup returns null, a DIFFERENT fallback function (`0x004FB156`, calling
`0x004F1310` plus a global constant — an unarmed/body-based range) runs instead. Nothing in
either path obviously produces zero/negative/NaN under normal conditions — this weakens, but
does not retire, the "degenerate range" hypothesis; only a live read settles it.

Added a read-only diagnostic (`GetEquippedItemFn`/`WeaponAccessorFn` in `Odyssey.cs`,
replicating these two clean thiscalls directly rather than `0x004FFAD0`'s own confusing
caller-side stack-argument reuse, which is not safe to guess blind on a live process) that
logs `weaponObj`, the accessor result, the `+0x1A` flag byte and the `+0x34` range float
alongside the existing round-init diagnostic.

**Live result — the degenerate-range hypothesis is now weakened further, likely refuted.**
Reproduced the standard scenario: host `tp`'d beside the real `sithsoldier` pair, staged via
`blockpoke attack 0x10 0x7FFFFFFF`, HP fell 12→10→8→1 under genuine engine AI (the deck's own
min-1-hp pin). Client connected mid-fight, `tp`'d beside the same pair. Two mirrored creatures
logged:

    creature 0x1926C1E8: weaponObj=0x0 (unarmed slot) — the "no weapon" fallback runs directly
    creature 0x1925B960 (the one with head action 0xFFFFFFFF, i.e. the one QueueAttackAction is
      actively retrying every pass): weaponObj=0x1925D250 (non-null — it DOES have a weapon),
      weaponAccessor=0x32892E4, flagByte@+0x1A=0x00, rangeField@+0x34=0

`flagByte=0` routes this creature into the SAME `0x004FB156` fallback `weaponObj=0` would have
used — the weapon exists but its `+0x1A` capability bit is off, so the weapon-derived
`+0x34` float (which does read `0`) is never actually consulted; a completely different
computation runs instead. Traced that fallback fully: `0x004FB156` → `0x004F1310` (with a
nonzero third arg, confirmed by disassembly of the `je 0x4f137d` branch) → two calls to
`0x00413350`, a properly bounds-checked (`edi≥0 && edi<[esi+0x24]`, `ecx≥0 && ecx<[esi+0x28]`)
2D table lookup keyed off each creature's own appearance id (`creature+0xA60`) — the same
"personal-space radius from appearance data" pattern `combat-participation.md` §1.4 test 5
already documents for a different (stored-field) source. Both results get summed with a
global constant added at each step. **Nothing in this traced path is degenerate** — a
standard `sithsoldier` appearance id will resolve to a real, positive radius from a real data
table, not zero or garbage. `flagByte=0`/`rangeField@+0x34=0` reads exactly as they do simply
because that whole branch is never taken, not because anything is broken.

**Verdict: the degenerate-range hypothesis does not hold up under a full trace of the actual
code path our engaged creature takes.** This was the last untested static candidate in the
acquisition chain proper. What remains, per round 20/21's own conclusion (never fully
resolved): `GetNearestEnemy`'s outward-walk loop itself, past the anchor step, has still never
been disassembled to completion, and the timing-race framing (our own per-frame `SetPosition`
calls re-sorting the x-sorted array the walk depends on) is the best-supported remaining
explanation. Round 24's job, precisely: disassemble the outward walk
(`0x004F3144`/`0x004F341A` per `combat-participation.md` §1.4) instruction by instruction and
determine whether a mid-walk reorder can make it terminate early/wrongly (in which case a real
fix exists — e.g. holding the array stable for the duration of one scan), or whether the walk
is provably robust to a reorder (in which case the race framing itself needs to be
reconsidered, going back to first principles on why attempt 2 succeeded even once).

`local_combat` stays off (unresolved). Both `coop.ini` restored to `0`; rig torn down and
confirmed clean. Full suite 953/953, both builds clean.

### Round 24 — the outward walk disassembled in full; the "race" framing itself was wrong

Full disassembly of `GetNearestEnemy` (`0x004F2DE0`) from the anchor step (round 20's stopping
point) through the walk body, both arms, to their shared per-candidate check block
(`0x004F2ECF`-`0x004F3144`) and the loop-continuation logic (`0x004F3179` onward, symmetric
for the second arm). No black box remains in this function.

**Structure, precisely:** the area pointer (`ebp = area+0x190`) and the anchor index (found by
the self-lookup's linear scan) are computed once, at entry — as they must be, there is only
one array to search. But **the array's own data pointer (`[ebp]`, i.e. `area+0x190` itself)
and its count (`[ebp+4]`, i.e. `area+0x194`) are both re-read FRESH on every single iteration
of the walk**, not cached into a local at entry (`0x004F2ECF`: `mov eax, [ebp]` — inside the
per-step loop body, executed every pass; `0x004F2EB2`/`0x004F3189`: `cmp edx, [ebp+4]` — the
bound re-checked live every pass, both arms). Per-candidate, each step independently re-runs
resolve (`GetGameObject`), self/exclude checks, hostility (`GetAIStateReputation`, confirmed
in round 18's own live read), the `AsCreature` cast, alive checks, perception
(`GetVisibleListElement` + the seen bit), the range comparison, and line-of-sight
(`ClearLineOfSight`, `0x0050C330`) — nothing here trusts a pre-filtered set or a snapshot from
the anchor step; every check combat-participation.md §1.4 already documents is re-run exactly
as described, live, per step, matching every live measurement this investigation has taken.

**This refutes the "mid-walk corruption from a reorder" mechanism outright — not because the
array can't reorder, but because it structurally can't happen mid-instruction.** This plugin's
own position-write drive pass and the game's own `GetNearestEnemy` call both run on the SAME
single game thread, sequentially. There is no possible interleaving where a `SetPosition` call
executes DURING a `GetNearestEnemy` call already in progress — the entire function, both arms,
runs to completion atomically within one frame's worth of code before anything else on that
thread executes. A stale cached pointer/count could have been corrupted by a reorder that
happened moments before a NEW call started (which is why fresh re-reads exist — they are
DEFENSIVE against exactly that, and they work); nothing can corrupt a call already underway.
The round 20/21 "race" framing borrowed language from multi-threaded reasoning that does not
apply to this single-threaded engine.

**What intermittent success/failure across otherwise-identical calls actually means, then: the
array's CURRENT valid sort order genuinely differs from one call to the next** (because this
plugin's own drive pass churns positions for many creatures every frame, and the array is
correctly, atomically re-sorted after each write) **and the x-distance-bounded early
termination is a provably correct algorithm on whatever that current, valid order is** — so a
"miss" is not evidence of a broken walk. It is evidence that, at the exact frame a given call
ran, either the real spatial arrangement did not actually put the player within the computed
range along BOTH the x-bound and the true 3D distance-minus-radii check, or (still open, see
below) the walk's OWN early termination — while individually correct per step — interacts with
a range value tight enough that ordinary positional noise decides the outcome.

**The one number this investigation has still never read: the actual final float
`MaxCleaveRange` returns for our engaged pair.** Round 23 confirmed the computation is not
degenerate (a real, bounds-checked appearance-radius sum, not zero/NaN) but never read its
magnitude. If that sum is genuinely small — a personal-space radius is not a weapon-reach
number, and round 23's traced fallback path sums exactly that for BOTH creatures plus a small
constant — a value under, say, 2-3 meters would make round 21's "point-blank, 0m separation"
test the decisive control it was meant to be (and that test's own null result, unchanged from
the ~2m case, already argues against range magnitude being the answer) — but it was never
directly measured, only inferred from a null result on a positioning test that could itself
have been imprecise (`tp`'s target coordinates vs. the creature's actual body-center, personal
space radius, or collision geometry are not necessarily one and the same "0m").

**Round 25's job, precisely, and it is now a single, cheap, decisive live read:** log the exact
float returned by the `0x4FFAD0`/`0x4FB0F0` call chain (not its intermediate inputs, which
round 23 already read) for the real engaged pair, and separately log the actual computed
distance-minus-radii value the walk compares it against (`0x004F3078`-`0x004F3096`'s FPU
sequence) on both a call that succeeds and one that fails, side by side. This settles, with a
number instead of a theory, whether the margin is genuinely razor-thin (a real, if awkward,
range-tuning issue with a concrete fix — widen the constant, or accept it as intentionally
tight melee range and adjust the test's positioning instead) or whether something else neither
this nor any prior round has considered is still hiding in the comparison itself.

`local_combat` stays off (unresolved — no code change this round beyond documentation; the
walk itself needed no fix, since none was found). No live rig used. Full suite 953/953, both
builds clean.

### Round 25 — range is decisively not the gate; a bigger discovery than the measurement itself

**First, `0x004FFAD0`'s real argument mapping, fully re-traced.** It is a straight 3-argument
passthrough to `0x004FB0F0` (its two "extra" pushes at the real call site are the SUBSEQUENT
`GetNearestEnemy` call's own setup, pre-staged before `0x004FFAD0`'s `ret 0xc` consumes only
the top 3 of 5 pushed values — not part of this call at all). The real call site (attempt 2,
`0x005B6B08`-`0x005B6B14`) resolves to `MaxCleaveRange(ownId, 0, 1)` — matching
`combat-participation.md`'s own already-verified notation (line 134/136) exactly, arg-for-arg.
Called `0x004FB0F0` directly (bypassing `0x004FFAD0`'s stack-reuse entirely, now that the
passthrough is proven exact) with these confirmed-safe arguments — `MaxCleaveRangeFn` in
`Odyssey.cs`.

**The measurement, live, on the actual engaged creature, repeated 15+ times over 45+ seconds
without variation:** `MaxCleaveRange(ownId,0,1) = 2.6`. Real distance-minus-both-radii,
computed in managed code from the same fields the walk itself reads (position `+0x90`,
personal-space radius `[+0x340]+0x08`) rather than hooking mid-function: `adjustedDistance =
0.688`. **Range exceeds distance by 1.91 — comfortably in range, not a close call, and never
once the other way around.** This decisively answers the coordinator's question: range/distance
is NOT the gate. The player is well inside the computed search radius every single time this
was sampled.

**The real discovery this round: `round+0x9CC` (the round's own "current target" field) is
ALREADY `0x7FFFFFFF` — the real player id, not the `0x7F000000` invalid sentinel — on EVERY
sampled call.** This means target acquisition has genuinely succeeded and written this field at
some point for this creature this session; it is not the case that `GetNearestEnemy` simply
never finds the player. Full disassembly of `CreateNewAttackActions`'s branch structure with
this in hand: since `bUseAttemptedTarget=0` in our call, `round+0x9CC` (not `this+0x524`) is
read as the existing target at `0x005B69A1`; being non-invalid skips the "totally fresh, jump
straight to search" shortcut at `0x005B69AD`, but — critically — since `bMayAcquireNewTarget=1`
(our other argument) is checked again at the `0x005B6AE3` merge point regardless of which path
got there, the function re-runs the FULL acquisition search (both `GetNearestEnemy` attempts)
on every call either way. The "VALIDATE-EXISTING vs ACQUIRE-NEW" framing this round's own
diagnostic label used is technically accurate about which target-source is read, but does NOT
mean acquisition is skipped — a false alarm caught and corrected within this same round rather
than chased further.

**What actually matters: `round+0x9CC` being correctly populated separates two previously
conflated questions.** "Does the search find the player" — evidently yes, at least once, since
something wrote this field with the real player id. "Does the round ever get armed to actually
process a swing" (`sub+0xB0`, the field rounds 16-17 traced as `UpdateCombatRoundTimer`'s real
gate) — still, after 25 rounds, always 0. Whatever writes `round+0x9CC` and whatever arms
`sub+0xB0` are evidently NOT the same effect of the same success — `combat-participation.md`
§1.3's own success-tail description (`this+0x84`, `this+0x50C`, `UpdateAttackTargetForAllAction
(round, newId)`, `this+0x4E8=1`) does not list `round+0x9CC` as one of the direct writes, so
`UpdateAttackTargetForAllAction` (`0x004D3E50`) — named twice now across this investigation,
never once disassembled — is the strongest remaining candidate for where `round+0x9CC` actually
gets set, and for whether it is ALSO responsible for arming `sub+0xB0` (in which case something
inside IT is the real, final gap) or whether arming is a separate, later step this investigation
still hasn't located at all.

**Round 26's job, precisely:** disassemble `UpdateAttackTargetForAllAction` (`0x004D3E50`)
completely — it is the one function this investigation has cited by name and address twice
without ever opening it. Confirm whether it writes `round+0x9CC` (settling where that field's
correct value comes from) and trace everything else it does, looking specifically for any write
to `round+4+(round+0x96C)*0x14C+0xB0` or a call to whatever `0x004D2920`
(`SetRoundDelayFn`-adjacent, already used in this codebase for `round+0x958`/`0x95C`) or a
sibling function might be needed to actually arm the sub-structure this investigation has spent
five rounds trying to light up.

`local_combat` stays off. Live rig used and torn down clean (`Get-Process` confirmed empty for
both `swkotor`/`KotorCoop.Agent`), both `coop.ini` restored to `local_combat = 0`. Full suite
953/953, both builds clean.

### Round 26 — `UpdateAttackTargetForAllAction` fully opened: a genuine dead end, and a correction to round 25

**No live rig needed — static analysis settles both questions this round was asked.**

**`UpdateAttackTargetForAllAction` (`0x004D3E50`) is a real function, and much smaller than its
name suggested — only `0x5F` bytes (`0x004D3E50`-`0x004D3EAE`, `ret 4`). An earlier request for
too large a byte range walked past its `ret` into an unrelated adjacent function (a
field-by-field debug dump, `0x004D3EC0`+, recognisable by its string-labelled calls to
`0x412620`/`0x412760`/`0x4127B0` for dozens of round fields — not part of this function at
all, and not investigated further). The real function's complete body: if `round+0x9B0` (the
pending-action list) is null or empty, it returns immediately, touching nothing. Otherwise it
walks every node in that list and, for each node whose `[node+0x10]==1` AND `[node+0x18]==1`,
overwrites `[node+0x14] = newId` (the second argument) — redirecting an EXISTING queued
action's target. **It never writes `round+0x9CC`. It never touches `sub+0xB0` or anything in
the `round+4+(round+0x96C)*0x14C` sub-structure array. If the list is empty when it's called
(exactly our case, every time), it is a complete no-op.** This is a definitive, fully-verified
dead end — not a candidate for either the target-write or the arming step.

**Correction to round 25: `round+0x9CC`'s correct value is not evidence acquisition ever
succeeded.** `python tools/fieldwrites.py 0x9CC` lists 13 writers; cross-referencing against
what this codebase already calls narrows it immediately: `0x004D60A8` sits inside
`StartCombatRound`'s own body (confirmed by direct disassembly of `0x004D6090`-`0x004D60BA`) —
`mov dword ptr [esi+0x9cc], ebx` where `ebx` derives from `StartCombatRound`'s own arguments
(the same `targetId` `Odyssey.QueueAttackAction` already passes as `StartCombatRoundFn`'s
second argument, per the existing call at `Odyssey.cs` line 6732:
`StartCombatRoundFn(round, targetId, contextSlot, context2, 0, 0)`), gated by a comparison
against `[ecx+4]` where `ecx = [round+0x9b4]` (a different, not-yet-identified field) — real
value on one branch, the `0x7F000000` sentinel on the other. **`round+0x9CC` reading the real
player id was never proof `GetNearestEnemy` found anything — it is a direct, mechanical
artifact of the `StartCombatRound` call this investigation has been making since round 17,
which writes the target we already told it to use.** The distinction round 25 drew between "the
search finds the player" and "the round gets armed" still stands as the right frame, but the
evidence for the FIRST half was never real; only the second half (`sub+0xB0` never arming) was
ever actually established.

**What's genuinely left, now that both `UpdateAttackTargetForAllAction` and the `round+0x9CC`
question are closed:** round 17 already named the real remaining candidate and no round since
has opened it. `sub+0xB0`'s only real `Add` (`0x004D57A0`) is reached exclusively through
`0x004D5A50`, which "assembles a full attack descriptor from weapon/roll fields computed deep
inside an untraceable ~7-argument validation loop" (round 17's own words, never revisited) —
and `0x004D5A50` itself is called from `UpdateCombatRoundTimer`'s own processing branch
(`0x5B8050`, flagged by round 15 as doing "real per-tick interdependent logic... not a single
counter a hand-written poke could satisfy," also never fully dissected). This is now the ONLY
unopened box left in the entire chain from round-init through acquisition through queueing —
everything else, across 26 rounds, has been individually verified sound.

**Round 27's job:** disassemble `0x5B8050` and `0x004D5A50` together, in full, to find what
precondition — beyond everything already confirmed correct (round timer active, delay set,
target valid, action queued, range fine, walk sound) — gates whether a genuine engine tick
ever actually calls the real `Add` into `sub+0xB0`. This may require accepting that arming
only happens across MULTIPLE ticks of genuine round processing rather than synchronously
within one `QueueAttackAction` call, in which case the fix may be patience (poll across several
frames after queueing) rather than a missing call.

`local_combat` stays off. No live rig used this round (pure static analysis; `UpdateAttackTargetForAllAction`
needed no live confirmation once fully disassembled — it provably does nothing on an empty
list). Full suite 953/953, both builds clean.

### Round 27 — the arming write is found, and it is structural, not a missing call

**`0x5B8050` (`UpdateCombatRoundTimer`'s real processing branch) is downstream of the gate,
not the gate itself, and much larger than a state machine.** Disassembled its opening several
hundred bytes: it reads `round+0x96C` to select the sub-structure exactly as documented, then
begins copying dozens of individual byte/word fields (`sub+0xC1` through at least `sub+0xD5`)
one at a time through a per-field setter call (`0x4D76B0`) — recognisably an
animation/attack-style parameter block being assembled, not anything that decides WHETHER to
process. This function only runs once `sub+0xB0>0` already — it cannot explain why that field
never becomes nonzero, only what happens after it does. Not pursued further; it is provably the
wrong target for this question.

**`0x004D5A50` is genuinely the arming write — round 17's suspicion confirmed.** Full
disassembly: `this` (`ebx`, from `mov ebx,ecx` at entry) is the per-participant SUB-STRUCTURE
itself, not the round — it copies a large block of fields FROM `ebx` (an `0xD0`-byte descriptor:
weapon/roll-adjacent fields at `ebx+0x1c` through `ebx+0x128`, matching the "attack descriptor"
round 17 described) and, at its very end, calls the real `Add` (`0x004D57A0`) with
`this = ebx+0xac` — four bytes before `sub+0xB0`, the exact `CExoArrayList`-style
data-pointer-then-count pair this whole investigation has been trying to get populated. This
is the confirmed, real, final arming write.

**But it is only ever reached from inside a large, stateful per-participant loop, not
callable in isolation.** `xrefs.py` finds exactly one call site (`0x005BBA15`), inside a
function with a genuine bounded loop (`xor ebp,ebp` at `0x5BB8C6`, iterating to a count read
earlier, `inc ebp; cmp ebp,eax; jl 0x5bb8d4` closing it at `0x5BBA2A`-`0x5BBA2D`). Each
iteration, BEFORE `0x004D5A50` is called: two unconditional setup calls (`0x5EDCE0`,
`0x5B4F30`); a conditional block gated on `word [ebx+0x64]` that calls `0x5BA8E0` only if
nonzero; a range check on a BYTE field (`[ebx+0x5c]`, the same field flagged in rounds 15 and
18 as a party-table-style lookup) — only values `-1` or `0..3` continue toward the arming call
at all, anything else falls through to a DIFFERENT pair of calls (`0x5BB440`, `0x5B8B00`)
instead; only past all of that does `0x004D5A50` finally run, immediately followed by another
call (`0x5B7400`) before the loop advances. This is not a small utility with a guessable
signature — it is a substantial, multi-step engine subroutine with its own internal
preconditions, iteration state, and branch-dependent setup that no single additional call this
plugin could make would safely or correctly replicate. Its own caller was not identified (one
call site found, its enclosing function's start address was not traced) — doing so, and then
tracing THAT function's own preconditions for reaching this loop at all, would be its own
multi-round undertaking with no better odds than the 27 already spent.

**Conclusion: this is the structural wall the coordinator's own fallback anticipated.** Every
individual piece this investigation has touched — round initialization, target validity, the
weapon/range computation, the acquisition scan and its walk, the append call, target
redirection — has been traced, measured, and confirmed sound in isolation, across 27 rounds.
The one piece that remains, the actual write that arms a round for real processing, exists only
inside the engine's own multi-step, stateful, per-participant round-advancement loop — the same
kind of thing a genuine AI-driven or player-driven combat round goes through as a side effect of
normal engine flow, and not something this investigation's approach (finding the missing native
call and making it directly) can reach. Replicating it correctly would mean re-implementing a
meaningful slice of the engine's own round-participant bookkeeping externally — a fundamentally
different, much larger undertaking than anything attempted in this investigation, with a real
chance of getting the iteration/precondition state subtly wrong in ways that surface as new
crashes or corruption rather than a clean pass/fail.

**Recommendation:** stop the single-function-hunting approach here. `local_combat` stays off,
correctly, and every fix landed across rounds 9-26 (the round-timer/delay setters, the second
gate, `CreateNewAttackActions` itself, the safety fixes from the earlier structural-defect
pass) is real, verified progress that should stay in the tree even though the feature itself
remains unfinished — each of those closed a genuine gap and left the mechanism measurably
closer to working, even though the final arming step turned out to be out of reach by this
method. A future attempt at this specific goal should consider either (a) the much larger
project of reverse-engineering the enclosing round-advancement loop and its own caller chain in
full, accepting the scope that implies, or (b) a different strategy entirely — finding a way to
make the engine's OWN AI script genuinely run for a driven puppet (the original, harder-seeming
option this investigation moved away from after round 1's "permission vs initiation" finding),
since that would get this loop to run through its own normal, engine-native path instead of
needing external replication at all.

`local_combat` stays off. No live rig used this round (the conclusion is fully supported by
static evidence — the loop's existence and preconditions are what settle this, not a live
read). Full suite 953/953, both builds clean.

### Strategy 2 (2026-08-21) — firing the creature's own script: a real breakthrough, and a real open question

A different approach from all 27 rounds above: instead of replicating the engine's native
combat-round mechanics piece by piece, ask the engine's own event system to notify the
creature the way it notifies every other creature in the game, and let its assigned NWScript
AI (which normally decides to fight) run.

**The mechanism.** `CVirtualMachine::RunScript` (`0x005D0FC0` is an MI thunk; the real body is
`0x005D45D0`, a queue-based script dispatcher with a pending-list at `+0x1AC` and a counter at
`+0x28`) is already used, live and working, by `CSWSDoor::OpenDoor` to fire a door's OnOpen
script (`Odyssey.cs:1208`, call site `0x00589D32`). Searching all 83 real call sites to that
dispatcher for one inside a known AI-tick function found `0x004DA8B6`: a per-creature event
dispatcher that, on a DIFFERENT branch of the same 13-case jump table, also calls
`UpdateVisibleList` (`0x0051B050`, the perception scan already confirmed unmodified for a
mirrored creature) — i.e. this is a general "signal event N to this creature" function, and the
branch reaching `0x004DA8B6` fires whatever script sits at `creature+0x260`.

A first live read of that field, taken naively as raw ASCII bytes, produced garbage
(`'.q/..'`) — a real methodology bug, not a dead end: `+0x260` is not an inline resref buffer,
it's an indirect string handle (`{ char* data; int length; }`, CExoString-shaped). The first
4-5 raw bytes are a little-endian HEAP POINTER, which happens to look like short,
printable-ish noise when read as text. Dereferencing it correctly (confirmed with a
standalone `ReadProcessMemory` call against the live pointer) reads **`k_def_combend01`** — a
real, well-known KOTOR default AI script: the `OnCombatRoundEnd` slot, whose body normally
calls NWScript's `DetermineCombatRound()`, the exact routine that decides to queue an attack.
`Odyssey.PeekCreatureScriptSlot0x260` was corrected to dereference properly rather than
ship the ASCII-garbage version.

Added `Odyssey.FireCreatureScriptSlot0x260` (calls `RunScriptFn` with the SAME argument shape
`OpenDoor` uses: `this` = one dereference of `VirtualMachinePtr` (`0x7A3A00`, NOT the
double-derefed "inner" object `GetImplementer` resolves for the stack-push routines — matched
`OpenDoor`'s proven-working call exactly), arg1 = `&creature+0x260`, arg2 = the creature's own
id, arg3 = 1) and a new `firescript <id> [fire]` console verb (`CheatKindFireScript = 12`,
wired end to end through `SharedBlock`/`IGameBridge`/`ConsoleCommand`/`CoopCommandDispatcher`/
`CheatController`/`ConsoleVerbCatalog`/`FuncInterpHook`, matching the existing
`resolve`/`giveloot` pattern) so the field can be peeked safely before ever being fired.

**Live result, HOST-LOCAL case: CONFIRMED WORKING, and it is the first genuinely NEW positive
result of the whole investigation.** Fired `k_def_combend01` on a fresh, untouched, host-local
`sithsoldier` standing ~2m from the local player. No crash. Within ~12 seconds: player HP
`12/12 → 1/12` (floored by the Endar Spire deck's own min-1-hp pin, the same pin every other
real fight this investigation has measured hits), independently confirmed via `console 'where'`
— a genuine, physically-verified hit through the real engine damage pipeline, produced entirely
by the creature's OWN AI deciding to fight after being told to check, not by any native call
this investigation wrote.

**Live result, MIRRORED (driven) case, first pass: INCONCLUSIVE, not refuted.** The same fire on
a client-mirrored copy of an untouched `sithsoldier` (client player `tp`'d to ~1.2m, confirmed
in-range via the existing `ROUND25` distance diagnostic) produced no HP change over 20+
seconds. This was NOT a clean result: `local_combat=1` was set for this test (reasoned that
`local_combat=0`'s full `ClearAllActions` wipe would delete a script-queued attack action
before it executes), which means the OLD per-frame `QueueAttackAction`/`CreateNewAttackActionsFn`
machinery from rounds 9-27 was ALSO running concurrently on the same creature, every frame,
throughout the test — its own diagnostic lines are interleaved with the fire in the log, so
whether the mirrored case's failure was (a) the script-triggered path genuinely not working on a
driven puppet, or (b) interference from the old parallel mechanism was NOT distinguished by this
pass. `local_combat` correctly stays off; both `coop.ini` restored.

⚠ **UPDATE — round 28 (below) reran this cleanly isolated (`local_combat=0`, so none of the old
machinery runs) with a proper unthrottled per-frame watch, and the mirrored case is now
REFUTED, not merely inconclusive.** A first HP-based reading in that round looked positive but
turned out to be a confound (the pre-existing phantom damage-forwarding feature, unrelated to
this mechanism) — see round 28's full account for why, and for the actual decisive evidence.

**What round 28 needs to do, precisely:** rerun the exact same mirrored-case test with
`local_combat=0` this time (so the OLD automatic mechanism is fully off), and watch CLOSELY —
within roughly one engine tick, not the 3-second-throttled diagnostic cadence — whether the
script-fire has ANY visible effect (a position/facing change toward the player, a head-action
change) before the next frame's full `ClearAllActions` wipe could plausibly land. If the
arming itself happens synchronously inside the script-triggered call chain (plausible, since
`DetermineCombatRound`/`ActionAttack` go through the engine's OWN `EndCombatRound`/
`CreateNewAttackActions` path, not a bare `AddAttackAction` append), it may not matter that the
ACTION gets wiped a frame later — the round could already be armed and reprocess a fresh
action on its own. That distinction (was the round ever actually armed, independent of whether
one particular queued action survived) is the one thing this pass did not measure.

Full suite: 953/953 (`DisconnectRecoveryTests.HeavyPacketLossStillConverges` is a known-flaky
probabilistic test, confirmed passing in isolation both before and after this round — not a
regression). Both builds clean.

### Round 28 (2026-08-21) — cleanly isolated, and the mirrored case is REFUTED (the earlier "it worked" reading was a confound)

Added `Odyssey.TickFireScriptWatch`, called unconditionally from `PluginHost.OnFrame` (right
after `_enemyPresence?.OnFrame()`, independent of `local_combat` — it runs regardless of that
setting) — logs the creature's head action, `round+0x9CC` target, `round+0x958`/`+0x960` timer
fields, and `round+0x96C` sub-index/`sub+0xB0` EVERY SINGLE FRAME for 180 frames after a
`firescript ... fire` call, armed by `FireCreatureScriptSlot0x260` itself. This exists because
`RunScript`'s own body is a queue-based dispatcher (a pending-list at `+0x1AC`, a counter at
`+0x28`) — the fired script does NOT run synchronously inside the call that fires it, it runs on
a LATER engine frame, so only real per-frame observation (not the 3-second-throttled diagnostics
rounds 9-27 used) can see a fast arm-then-something cycle.

**Procedure:** fresh two-instance session, `local_combat=0` on BOTH installs (confirmed via
`grep` before launch), host hosting via `console 'host'`, client connecting via
`console 'connect'` — clean, with NONE of rounds 9-27's automatic per-frame
`QueueAttackAction`/`CreateNewAttackActionsFn` machinery running (that machinery is gated on
`LocalCombat`, which was off). Client `tp`'d to within ~2m of a mirrored, untouched
`sithsoldier`, `firescript <id> fire` sent on the CLIENT.

**First signal, and why it was wrong.** The client's `kotorcoop-plugin.log` showed
`combat: took 2 damage from a hit on our phantom (attacker 0xE)` starting ~5s after the fire,
and `console 'where'` confirmed real HP loss (12→1, the same min-1-hp floor every real fight in
this investigation hits). This looked like confirmation. **It was not the mechanism working —
it was a pre-existing, unrelated, already-documented feature catching the test off guard.**
`IncomingDamageController.cs:293`'s exact log text ("hit on our phantom") is the FORWARDED
damage channel (`block.IncomingDamageAttacker`/`IncomingDamageApplied`, wire-synced fields) —
not a local hit. Checking the HOST confirmed why: `creatures.ps1` on the host showed a live
`coop_phantom` sitting at `(29.70, 142.40, 0.00)` — EXACTLY the coordinate the client `tp`'d to,
because a connected player's own position always publishes a phantom body at that same spot on
every peer's screen, by design (README: "Your partner's body is a real combat target in your
game… enemies pick it, swing at it"). The host's OWN untouched `sithsoldier`s, sitting at their
original unmoved spawn positions the whole session, evidently found this nearby PLAYER-faction
phantom fair game and fought it organically on the HOST's own engine — completely independent of
anything fired on the CLIENT. Positioning a connected client's real player near ANY module
creature unavoidably also positions their phantom there on every other peer's screen, so this
confound is inherent to the very scenario being tested, not a mistake specific to this run — it
just needs to be told apart from the real signal, which is why the per-frame native-state watch
exists at all.

**The real, unambiguous evidence: `sub+0xB0` never left 0.** Across FOUR separate
`firescript ... fire` calls on TWO different mirrored creatures (`0xE` and `0x10`), each
followed by a full 180-frame (~3s) unthrottled per-frame watch, spanning roughly 50 seconds of
real time in total: `round+0x9CC` target read `0x7FFFFFFF` (the player) from the very first
sampled frame — already set, before this round's own fire, most likely a leftover from an
earlier session's `AddAttackAction`/`CreateNewAttackActionsFn` diagnostic calls, since
`+0x9CC` was never reset between sessions and nothing in this pass wrote it — `round+0x958`
stayed `1` and `round+0x960` counted down smoothly and organically the entire time (proof the
round IS ticking normally, not stuck or corrupted), and `head action` never once left
`0xFFFFFFFF` (not a fight action). **Not one of the 720+ sampled frames across all four attempts
showed `sub+0xB0` as anything but `0`.** This is the same wall rounds 1-27 hit, reached this time
by a completely different route (a real, engine-native script event instead of hand-replicated
calls) — which is itself informative: whatever prevents a driven puppet's round from arming is
not specific to HOW the round gets initiated (our own calls, or the engine's own script-driven
path), it is specific to the creature BEING a driven puppet.

**Conclusion.** Strategy 2 remains a genuine, confirmed-real result for a HOST-LOCAL creature
(round 27's fight — HP 12→8→1, unconfounded, no phantom involved since a host-local test has no
connected peer at all). It is now REFUTED, not merely unconfirmed, for the MIRRORED case — four
clean attempts, no ambiguity, `local_combat=0` throughout so no competing mechanism was running.
`local_combat` stays off. Both `coop.ini` restored to `0` (unchanged — they already were).
Rig torn down and confirmed clean. Full suite 953/953 (no flake this run), both builds clean.

**What this actually narrows down, for whoever continues:** the block is not in initiating the
round (proven — a real engine script can do it, on a host-local creature) and not in reaching
`AddAttackAction`'s queue (round 17-19 proved that separately). It is specifically in whatever
makes a round ACTUALLY PROCESS once armed-or-arming, and that process appears to depend on the
creature not being externally driven — something about being a `local_combat`/`EnemyPresenceController`-tracked
puppet (even with the position/action-wipe machinery OFF, since `local_combat=0` was set this
round) still marks the creature as not eligible for the engine's own round-advancement loop to
ever complete for it. The next concrete step is not another call to trace — it's determining
WHAT marks a creature as "driven" in the engine's own eyes in the first place, since that mark
appears to persist and block round-arming even when every co-op-specific per-frame write is
turned off.

### Round 29 (2026-08-21) — the "driven puppet" hypothesis is refuted too: zero writes, same wall

Round 28 closed on a specific hypothesis: that something about a creature being tracked by
`EnemyPresenceController`'s base presence path (`SetPosition`/`SetFacing`, unconditional
regardless of `local_combat`) marks it as ineligible for the engine's own round-advancement loop,
independent of `local_combat`'s own extra calls (already ruled out with `local_combat=0`). That
was never actually tested — round 28's "undriven" creature still had its position/facing written
every frame by the base drive, which runs for every mirrored enemy unconditionally. Nobody had
ever tested a creature with genuinely ZERO writes from this plugin.

**What this round built.** A temporary, hand-picked-id diagnostic in `EnemyPresenceController`
(`DiagUndrivenObjectId`, `coop.ini local_combat_diag_undriven_id`) that skips the ENTIRE
per-pass touch for one creature — no `ClaimCreature`, no `PermitAttacking`, no `Drive`
(position/facing), no `ClearPositionDirty`, no `RecordEngagement`, no `QueueAttackAction`. The
creature is present in the world, perceivable, alive, and completely untouched by the co-op
plugin — its position on the mirroring client is left to drift from the owner's real position,
which is expected and correct for this diagnostic, not a bug.

**Live test, clean.** Two `sithsoldier`s in `END_M01AA`: `0xE` (attacked normally via
`blockpoke.ps1 attack`, staging a genuine host-side fight — `state IN COMBAT`,
`combat [FIGHT ... engaged: you, Malak]`) and `0x10` (the diagnostic target, `tp`'d the client
player beside it, then fired its own `+0x260` script — `k_def_combend01` — via
`FireCreatureScriptSlot0x260`, the SAME Strategy 2 mechanism proven host-local in round 27).
`0x10` was never touched by anything else this session.

**Result: the round genuinely armed and ran, organically, then reset at expiry — never reaching
`sub+0xB0`.** The per-frame native-state watch (unthrottled, all 180 frames captured) shows,
starting the very first sampled frame after the fire:

    frame -180: round+0x9CC target=0x7FFFFFFF, round+0x958 timer=1, round+0x960 delay=335, sub+0xB0=0
    frame -106: round+0x958 timer=1, round+0x960 delay=13
    frame -104: round+0x958 timer=1, round+0x960 delay=4
    frame -103: round+0x958 timer=0, round+0x960 delay=0     <- reset, not armed
    (every frame -103 through -1: timer=0, delay=0, sub+0xB0=0, head action=0x3F, never a fight action)

`round+0x9CC` (the current-target field) was set to `0x7FFFFFFF` — the local player — by
something in this run, and NOTHING in this run could have set it except the script itself: no
`StartCombatRound`, no `QueueAttackAction`, no prior-round diagnostic call touched this
creature. That is genuine, organic evidence the script's own `DetermineCombatRound` logic
picked the player as a target. `round+0x958`/`round+0x960` then armed and counted down
SMOOTHLY and ORGANICALLY from 335 to 0 over ~77 real frames — proof these are exactly the
fields a real decision process uses, driven by the engine's own logic, not by anything this
investigation wrote. And then, at the moment the delay expired — the exact point where round
27 found the real arming call lives, deep inside the engine's own per-tick loop over combat
participants, "a branch gated on a party-table-style field that routes to an entirely different
path if it's out of range" — the round reset to inactive instead of committing. `sub+0xB0`
never went nonzero across all 180 frames. The client's own HP did drop (12→1) and briefly
showed `state IN COMBAT`, but that is the SAME phantom-forwarding confound round 28 already
identified: the client's phantom stood near the HOST's own genuine fight against `0xE`, and
the host's engine forwarded that unrelated damage — not evidence of anything happening on
`0x10`, which the native-state watch settles independently of HP.

**Conclusion: the "driven puppet" hypothesis from round 28 is REFUTED.** A creature this
plugin has NEVER touched — no position write, no facing write, no claim, no permit, nothing —
hits the exact same wall, reached organically, through the engine's own real AI decision
process. This rules out every per-creature, per-write theory this investigation has produced
across 29 rounds: it is not about being mirrored, not about being driven, not about
`local_combat`'s extra calls, not about any specific field this plugin writes. The one thing
every failing test has shared, including this one, is a CONNECTED CO-OP SESSION with a REMOTE
PEER standing near the creature. Round 27's "party-table-style field" gate — never identified
precisely, only located structurally — is now the single strongest remaining candidate:
whatever roster or participant-table the engine's per-tick round-advancement loop consults
before committing to a swing may simply never have the connected remote player registered in
it as a valid combat participant, regardless of anything happening at the individual creature
level. That is a SESSION-level or PARTY-TABLE-level question, not a creature-level one — a
genuinely different kind of thing to investigate than anything the first 29 rounds tried.

`local_combat` stays off (untouched this round; the diagnostic runs independently of it).
`coop.ini local_combat_diag_undriven_id` diagnostic left in the tree, default unset (0,
disabled) — remove once this question is settled. Rig torn down and confirmed clean
(`Get-Process` empty for both `swkotor` and `KotorCoop.Agent`). Full suite 953/953, both
builds clean.

### Round 30 (2026-08-21) — the party-table-style gate opened, and it is not the blocker

Round 27's "party-table-style field" candidate, never previously disassembled, is now open —
and it is real, understood, and **refuted** as the explanation for the arming failure.

**The mechanism, fully traced.** `UpdateCombatRoundTimer`'s real processing branch (`0x5B8050`,
per round 15) branches on `sub+0x5c` compared against `2`, cross-checked through a call chain:
an adjustor thunk (`0x4AEA40`, `mov ecx,[ecx+4]; jmp 0x4B4CB0`) into `0x4B4CB0`, which — despite
being reached as if it were a virtual member call — takes NO real arguments and never uses the
adjusted `this`: it reads a fixed global singleton at `0x7A39FC`, derefs `+8` (a
party-table-shaped sub-object), and calls a second thunk chain (`0x4AE920` → `0x4B2AE0`) with
two HARDCODED stack arguments `(0, 1)`. `0x4B2AE0` walks one of two lists at
`[that+0x10064]`/`[that+0x10068]` (selected by the second argument, `1` in our case) searching
for an entry whose id-field equals the FIRST argument, `0` — i.e. this whole chain answers one
fixed question, "is party slot 0 (this instance's own local player) currently resolvable,"
returning either the resolved id or the not-found sentinel `0x7F000000`. It is a pure read, two
accessor calls, no writes observed anywhere in the chain.

**Live-tested directly, reusing round 28/29's proven `firescript`/watch infrastructure — no new
scenario needed.** Extended `TickFireScriptWatch` to log `sub+0x5c` and a bare call to
`0x004B4CB0` every frame. Fired on a completely untouched, never-driven mirrored `sithsoldier`
(`0x10`, object pointer `0x1835A2D8` on the client) exactly as round 29 did. Across the full
180-frame watch: **`sub+0x5c` reads `0` on every sample — which IS within the "-1 or 0..3"
range round 27's OWN loop-entry gate requires to continue toward the arming call at all** (i.e.
this specific creature was never being rejected by that gate in the first place). And
**`0x4B4CB0` resolves party slot 0 to `0x7FFFFFFF` on every sample — the CLIENT's own local
player, correctly** (each KOTOR process trivially has its own PC as party slot 0; there was
never a "the remote peer isn't registered as slot 0" mismatch to find, because nothing in this
chain ever asks about the remote peer — it only ever asks about whoever is running THIS
process). `round+0x958`/`+0x960` ran their normal countdown to exhaustion (timer=0, delay=0 at
the watch's end) and `sub+0xB0` stayed 0 throughout — the same wall, unmoved.

**What this settles and what it doesn't.** The `0x5B8050`/party-slot-0 cross-check is real and
now fully understood, but it was never actually on the critical path to arming to begin with —
round 27 already established `0x5B8050` only runs AFTER `sub+0xB0>0`, so it cannot be gating the
transition to `sub+0xB0>0` in the first place; this round closes out that open question from
rounds 15/18 rather than finding the actual blocker. The REAL loop-entry gate round 27 found
(the range check on `[ebx+0x5c]` inside the function containing the arming call site
`0x005BBA15`) is now directly confirmed passing (`0`, in-range) for a creature that still never
arms — which narrows the search to the three pieces of that loop round 27 flagged but never
opened: the two unconditional setup calls (`0x5EDCE0`, `0x5B4F30`), the conditional block on
`word[ebx+0x64]`, and — likely the more fruitful target — the loop's OWN caller, never
identified, whose preconditions for reaching this loop at all have never been examined. Given
round 29's finding that the wall persists even with zero co-op-plugin writes of any kind, the
next genuinely new question is whether that outer caller is ever reached/ticked with the same
frequency or under the same conditions for a creature a REMOTE peer is fighting versus one only
the local session's own player is fighting — i.e., is the enclosing function itself gated on
something session-related, upstream of everything traced so far.

`local_combat` untouched, stays off. `Odyssey.cs`'s `TickFireScriptWatch` diagnostic extended
(not removed — the underlying question this investigation opened is still unresolved). Full
suite 953/953, both builds clean, rig torn down and confirmed clean.

### Round 31 — the caller identified as `CSWSCreature::ResolveAttack`/`ResolveMeleeAttack`, a
### real candidate gate refuted after live measurement, and the FULL round-timer cycle watched
### to completion for the first time — it resets to idle instead of ever arming

Round 30 left two things unidentified: the two unopened setup calls inside `ResolveMeleeAttack`'s
loop, and — flagged as more promising — the loop's own caller, never traced.

**The caller chain, found via the symbol database rather than blind disassembly.** The
`functions` table in `tools/kotor1_0_3.db` already names the function containing the arming call
site (`0x005BBA15`): `CSWSCreature::ResolveMeleeAttack` (`0x5bb890`). `xrefs.py` on that address
finds exactly one caller, `CSWSCreature::ResolveAttack` (`0x5bba80`), which itself has exactly two
callers: `AIActionCastSpell` (`0x514af0`) and `AIActionPhysicalAttacks` (`0x5bbbf0`) — the SAME
function round 17 already identified as the execution-time handler for a queued attack action,
gated on `CreAttackPermittedBit`. This confirms the structural picture: `AIActionPhysicalAttacks`
only runs when a creature's action QUEUE (what `GetHeadActionType` reads) has a fight action at
its head, and only then does it resolve down into `ResolveAttack` → `ResolveMeleeAttack` → the
arming write. Since head action has never once changed from `0x6`/`0xFFFFFFFF` in 30+ rounds,
`AIActionPhysicalAttacks` structurally never runs for our creature — `ResolveMeleeAttack`'s own
loop was never reachable at all, which is consistent with `sub+0xB0` never arming, but does not
yet explain WHY the head action itself never promotes.

**A concrete candidate found and live-refuted.** Full disassembly of `ResolveAttack` found a real
early-exit gate inside `UpdateCombatRoundTimer`'s processing branch (`0x5B8050`, reached once
`round+0x958` ticks past its own gates): `if (sub+0x40 != 0 && sub+4 == 0) → skip the entire
weapon/attack-descriptor build`. Extended `TickFireScriptWatch` to log both fields. Live-measured
on the actual failing creature: **`sub+0x40 = 0` on every sample** — the gate's own condition
requires `sub+0x40 != 0` to even consider bailing, so this specific branch is never taken; the
code always falls through into the descriptor-build block normally. Refuted as the blocker.

**The decisive new measurement: nobody had ever watched a full cycle before.** The existing
180-frame watch turned out to be a hard ceiling nobody had questioned — `round+0x960` (delay) sat
at `0` for the first ~128 ticks after firing, then jumped to `1500` and began counting down at
only ~4-5 per tick. At that rate a full cycle needs roughly 470+ ticks, well past every prior
round's 180-frame window. Extended the watch to 2400 frames and reran the identical live scenario
(host staged for real via `firescript 0xE fire`, HP 12→10 under genuine AI; client connected
mid-fight, `firescript 0x10 fire` on the untouched target). This time the delay counted all the
way down — `1500 → ... → 31 → 26 → 22 → 18 → 14 → 9 → 5 → 1 → 0` — and at the instant it hit
zero, **`round+0x958` (timer-active) reset to `0` too**. `sub+0xB0` stayed `0` for the entire
2400-frame watch, including the moment of expiry. **The round does not stall waiting for more
ticks — it runs its full, real cycle and then resets to idle instead of ever committing an
attack.** This closes off "the window was too short" as an explanation for good; the actual
question is now why expiry resets rather than arms.

**An ambiguous, NOT independently confirmed observation, flagged rather than claimed.** In the
same run, the client's own real player HP (`console 'where'`) dropped to `1/12`, and
`kotorcoop-combat.log` showed several `combat: took 2 damage from a hit on our phantom (attacker
0x10)` lines. Tracing `IncomingDamageController.cs` line 285 confirms this damage is applied
directly to `Odyssey.GetPlayerObjectId()` — the client's real character — via the cross-network
phantom-damage-forwarding channel (`CoopAgent.cs`'s `TryWriteIncomingDamage`, sourced from
`bridge.TryReadPhantomHit` on the HOST side), the exact same pre-existing, unrelated mechanism
rounds 28/29 already identified as a confound (a connected client's own phantom, rendered in the
host's world wherever the client's own position places it, can draw real aggro from the host's
untouched local creatures). The host's own combat log shows zero activity involving `0x10` in the
current session, which argues against the organic-host-aggro explanation, but the host's
phantom-hit hook is only confirmed to log at install time, not per-hit, so this could not be
ruled in or out with confidence in the time available. **Do not read this HP drop as evidence
`0x10` fought locally — the clean, decisive evidence this round produced is the full-cycle
round-timer measurement above, not this.** The next round should isolate this properly: either
position the client's own player far enough from anything that could organically aggro on its
phantom, or add a direct read of the HOST's own `DamageDealtTable`/`sub+0xB0` for `0x10` to settle
whether the host's untouched copy is quietly fighting the phantom on its own.

`local_combat` untouched, stays off. `TickFireScriptWatch`'s window is now 2400 frames (up from
180) and its `sub+0x40`/`sub+4` reads stay in the tree — both are load-bearing for the next round
to avoid re-measuring what this one already settled. Full suite 953/953, both builds clean, rig
torn down and confirmed clean (verified via `Get-Process`, not assumed).

### Round 32 — the expiry-decision branch fully mapped, `GetDeadTemp` measured false throughout,
### and the round's own target turns out to be a stale phantom, not the real player

Full disassembly of `UpdateCombatRoundTimer`'s EXPIRY path itself (`0x004FAFE3` onward, the
branch reached when `delay - creature+0xCC <= 0`), separate from the entry gates every prior
round mapped. Both the "still armed, drain the loop" path and the "never armed" path converge at
`0x004FB017`, which — regardless of which one led there — re-resolves the round's OWN target
field (`round+0x9CC`) fresh via `GetGameObject` (`0x004AE750`), casts it (`vtbl+0x10` twice,
"AsSWSObject"), checks `GetDead` (`vtbl+0x94`), and on a live target casts again (`vtbl+0x30`,
"AsCreature") and calls `CSWSCreature::GetDeadTemp` (`0x004EF890` — a NAMED function in
`tools/kotor1_0_3.db`, not a guessed address) on the result. `GetDeadTemp` returning TRUE falls
straight into the round-reset call (`0x004D4620`, already identified in round 15/20 as the
helper that zeroes the arming state); returning FALSE takes a path this disassembly found ends
in a bare return with **no reset call at all** — which does not match the empirically-observed
behaviour (the timer DOES reset every cycle) and is flagged below as unresolved.

`GetDeadTemp`'s own body (fully disassembled this round, also confirmed via named symbols):
walks `CClientExoApp::GetSWParty()`'s roster (`CSWParty::GetCharacter` per slot), converts the
passed-in target's id via `CClientExoAppInternal::ServerToClientObjectId`, and only if the
target is found in the CLIENT's own local party does it go on to read `vtbl+0x9c(0)` on the
target and return `(that value <= 0)`.

**Live-measured the entire chain, unconditionally, every frame, for the full 2400-frame watch
(two full round cycles observed in one run — the round re-armed and reset a SECOND time within
the window, not just once).** Across every single sample, in both cycles: `GetDead=0`,
`vtbl+0x9C(0)=30` (a real, stable, positive value), `GetDeadTemp=0` (FALSE) — never once true.
**This refutes `GetDeadTemp` as the reset trigger, at least as directly measured** — the branch
that reads FALSE from `0x4ef890` (per the disassembly) should return WITHOUT calling
`0x004D4620`, yet the timer still resets every cycle. Either there is a still-unfound second
writer of `round+0x958`, or the live-measured `GetDeadTemp` value differs from what the ENGINE's
OWN internal call sees at the precise instant it checks (a timing mismatch this diagnostic,
sampled independently rather than hooked inline, cannot rule out).

**The bigger finding, and probably the more important one: `round+0x9CC` (the round's own
target) reads `0x12D` — not `0x7FFFFFFF`, the "current player" sentinel every prior round's
manual `QueueAttackAction`/`AddAttackAction` calls have hardcoded as the target.** Resolved and
confirmed live: `0x12D` is a genuine `coop_phantom` — specifically the CLIENT's own phantom of
the HOST player, rendered at the host's real position. The creature's OWN, genuinely
AI-acquired target is a co-op phantom body, not the "real" player object every round to date
assumed it would target. Given the round was ALREADY active (`timer=1`, a nonzero delay already
counting down) at frame -2400 — the very first sample after firing the script this round — the
target was almost certainly acquired and the round started well BEFORE this test began, likely
left over from an earlier, unrelated interaction with this same creature/phantom pair earlier in
the session. This reframes the test methodology itself: firing `+0x260`
(`k_def_combend01`/`OnCombatRoundEnd`) repeatedly appears to CONTINUE whatever round already
exists rather than freshly re-running target acquisition each time — `OnCombatRoundEnd` is an
end-of-round continuation handler, not necessarily a fresh-decision entry point. A genuinely
clean test would need either a creature with NO pre-existing round state at all, or a script
slot that unambiguously forces fresh acquisition (`OnPerception`/`OnHeartbeat`, not
`OnCombatRoundEnd`) before drawing conclusions from the target it ends up with.

**Status: the actual "commit vs. reset" branch is now fully mapped at the instruction level**,
but the live evidence gathered this round doesn't yet cleanly explain WHY it always resets —
`GetDeadTemp` reads false throughout, which per the disassembly should skip the reset entirely,
not always trigger it. Two concrete next steps, precise enough not to need re-deriving: (1) hook
`0x004D4620` itself (or its call site) directly rather than independently re-measuring
`GetDeadTemp` after the fact, to see definitively whether it's even being reached, and what
routes it there if `GetDeadTemp` genuinely reads false at the real call site too; (2) rerun the
whole test against a creature and target pair with NO pre-existing round state (a genuinely
fresh combat round, target-acquired from scratch during the observation window, ideally against
the real local player rather than a leftover phantom target) to know whether the phantom-vs-
real-player distinction found here matters at all.

`local_combat` untouched, stays off. Full suite 953/953, both builds clean, rig torn down and
confirmed clean (`Get-Process`, not assumed).

### Round 33 — the "stale phantom" theory is refuted; the round genuinely spins forever regardless
### of target, and Strategy 2's own script may never reach the queue step at all

Round 32's `round+0x9CC = 0x12D` was NOT contaminated leftover state. Reproduced on THREE separate
fresh sessions (fresh `demo.ps1` launches, `0xE` never previously fired): every single time,
`k_def_combend01`'s own acquisition picked the `coop_phantom` (`0x12D`) over the client's true
local player (`0x7FFFFFFF`) — even once measured while the true local player was PHYSICALLY CLOSER
to `0xE` (2.27m vs 3.47m) than the phantom. This is genuine, repeatable engine behavior, not test
contamination: a phantom is a real, alive, PLAYER-faction `CSWSCreature` and a legitimate
acquisition target in its own right.

**Extended the watch from 2400 to 6000 frames** (`Odyssey.cs`, `_fireScriptWatchFramesLeft`) —
2400 was cutting off mid-cycle; the round genuinely re-arms and restarts its own ~1500-tick
countdown roughly every 1.5 real seconds, on a clean, perfectly regular loop
(`timer=1,delay=1500` → counts down → `timer=0,delay=0` → jumps back to `timer=1,delay=1500` →
repeat), confirmed unbroken across **12+ complete cycles** spanning three test runs.

**The decisive result: `sub+0xB0` never went nonzero ONCE, in any cycle, against either target.**
Then the phantom was eliminated as a variable entirely — the host was moved far from `0xE`
(`(30,141)` → `(20,22)`), removing its phantom from `0xE`'s vicinity — and `0xE`'s script was
refired. Acquisition correctly locked onto the TRUE local player this time
(`round+0x9CC = 0x7FFFFFFF`, confirmed), and a full clean 6000-frame capture (multiple complete
1500-tick cycles) still never saw `sub+0xB0` leave 0. **The phantom-vs-real-player distinction
this round set out to resolve makes no difference at all — the round spins forever against either
target.**

**A sharper, more fundamental observation than the target question: across every single sample in
every Strategy 2 test this round ran — both targets, all 12+ cycles — `head action` never once
left `0xFFFFFFFF` (a genuinely empty queue).** This is a real, structural difference from every
Strategy 1 test (rounds 9-27), which consistently showed `head action=0x6` (SOMETHING queued, just
never promoted to a committed fight action) after calling `AddAttackAction`/
`CreateNewAttackActions` directly. Strategy 2 fires `k_def_combend01` — literally
"`OnCombatRoundEnd`" — and per standard Aurora-engine scripting convention this is very plausibly
a MID-ROUND continuation/retarget handler, not the script responsible for INITIATING a fight from
cold. If so, firing it on a creature with no round already in flight may just be exercising the
round-timer's own idle-reset bookkeeping (which the `timer=1,delay=1500 → 0 → 1500` cycle is
completely consistent with) without ever reaching whatever queues the first action — which would
mean Strategy 2, as built, has never actually tested "let the engine's real AI decide to fight,"
only "let the engine's round-timer spin." Round 27's original host-local success (real, verified
HP loss) used this SAME script and SAME mechanism — that result stands and is not contradicted
here, but this round did not re-instrument it with the current diagnostic, so whether IT also
showed `head action=0xFFFFFFFF` the whole time (fight happening through some path this diagnostic
can't see) or `0x6`+ (matching Strategy 1) is now an open, checkable question rather than an
assumption.

**Next concrete step, precise enough not to need re-deriving:** find and fire whatever script slot
governs `OnPerception`/`OnNotice` (the "I just saw a hostile, should I act" handler) instead of
`OnCombatRoundEnd`, on a creature with a verified-idle round, and watch the SAME diagnostic fields.
If THAT reaches a nonzero head action or arms `sub+0xB0`, it confirms `k_def_combend01` was simply
the wrong trigger all along, independent of every driven/undriven/target-identity finding from
rounds 9-32. If it shows the identical spin-forever symptom, the block is confirmed to be entirely
downstream of script selection, in the round-advancement machinery itself, exactly as rounds 27-32
concluded.

`local_combat` untouched, stays off (Strategy 2 doesn't read or need it). Diagnostic watch budget
raised 2400→6000 frames — kept, since it's what let this round's clean multi-cycle capture happen
at all. Full suite 953/953, both builds clean, rig torn down and confirmed clean (`Get-Process`).

### Round 34 — round 27's win confirmed genuinely real; the `+0x270` guess was wrong, but what it
### revealed matters more than the miss

**Part 1: round 27's host-local result is real, not a false positive.** Reproduced the exact
scenario (single host instance, `k_def_combend01` fired on a fresh, untouched `sithsoldier`
targeting the host's own local player) with round 33's full diagnostic running. Result:
**`sub+0xB0` went nonzero (`=1`) for the first time in the entire investigation**, `sub+0x5c`
changed from 0 to 4, and the target's `vtbl+0x9C(0)` readout (very plausibly HP) fell from 12 to 4
over the capture window — all independently consistent with a genuinely committing, progressing
fight. This settles the open question cleanly: `k_def_combend01` DOES reach a real commit when
fired host-local, and round 27's claim was not a confound. The wall is specific to the
mirrored/client case, not to script choice in general.

**Part 2: the `+0x270` candidate was wrong.** Found via `xrefs.py` on `RunScriptFn` — a call site
at `0x004EB79D` (inside the perception/heartbeat code area) fires `creature+0x270`, gated on "no
current target yet" and latched so it only fires once, which read as a strong OnNotice match by
shape alone. Live peek on a real `sithsoldier`, though, read `k_pend_room3_02` — an area/room
script by naming convention, not a combat-AI script. Firing it produced the identical spin-forever
symptom on a mirrored creature (`sub+0xB0=0` throughout a full 6000-frame capture, `head
action=0xFFFFFFFF` throughout) — but that result doesn't test the hypothesis it was meant to,
since the fired script itself has nothing to do with deciding to fight. The `+0x260` dispatcher
(`0x004DA846`, fully mapped this round via its remap table and jump table, read directly from the
binary) turned out to fold 9 of its 13 event codes into that SAME `+0x260` slot; the other two
branches never call `RunScript` at all. There is no genuinely separate "OnNotice" script reachable
through that specific dispatcher — a different one, if it exists, is fired from somewhere else in
the binary not yet found.

**The one thing this miss still proved, and it's significant:** the round-timer's own 1500-tick
cycle ran identically (delay counting down, `timer` flipping 0↔1 on schedule) whether the fired
script was `k_def_combend01` (a real combat script) or `k_pend_room3_02` (an unrelated area
script). **The round-timer machinery does not appear to depend on what the fired script actually
does at all** — something in the dispatch call sequence itself (not the script's own NWScript
body) is what starts/maintains that cycle. This reframes the open question again: it was never
really about finding the "right" script to fire — round 27 already had the right one. It's about
what's different, in the CALL SEQUENCE ITSELF or the STATE IT RUNS AGAINST, between a host-local
creature (which commits) and a mirrored one (which spins forever, target identity and script
choice both proven irrelevant across rounds 29-34).

**Honest assessment.** 34 rounds have now individually ruled out: per-creature drive state (round
29), the party-table gate (round 30), the acquisition chain in full — hostility, perception,
range, the walk (rounds 18-25), target identity — phantom vs true local player (round 33), and
script choice, so far as this round could test it (round 34). What remains genuinely unexplored:
(a) whether `+0x270`'s ACTUAL semantic slot (a real OnNotice/OnHeartbeat script, still unfound) has
been miscategorized to a completely different offset the `k_def_combend01` dispatcher doesn't
reach and this round didn't find either; (b) anything in the ATTACKER's or the SESSION's state
that differs specifically between "playing as the host, no client connected" and "hosting with a
client connected" that has nothing to do with any individual creature at all — untested, because
round 27's positive result was measured on the same rig, in the same connected session, as every
mirrored failure, and nobody has yet tried the control of firing on a host-local creature WHILE a
client is actively connected versus with no session at all. That specific control — same script,
same creature type, same host, session connected vs not — is the most concrete next step this
round can hand off. Absent that, this is a reasonable point to call the practical edge of what
disassembly-first, live-diagnostic RE can resolve without a fundamentally different technique
(full engine decompilation of the round-advancement caller chain, or abandoning the
replicate-the-engine approach for something that doesn't need to understand the internals at all).

`local_combat` untouched, stays off. Both `coop.ini` unchanged (Strategy 2 doesn't use it). Full
suite 953/953, both builds clean, rig torn down and confirmed clean (`Get-Process`). New console
verb: `firescript <id> fire 270` fires the `+0x270` slot instead of the default `+0x260`.

### Round 35 — "a peer is connected at all" is REFUTED as the variable, decisively

Round 34's own hand-off, run immediately: reproduced round 27's exact winning scenario (host-local
creature, untouched, `k_def_combend01` fired, targeting the host's own local player) with a client
**genuinely connected the whole time** (`phantoms 1 remotes 1` confirmed on the host) but never
touched, never moved, never brought anywhere near the test creature.

**Result: it still works.** `sub+0xB0` armed (`=1`), `sub+0x5c` changed 0→4 (matching round 35's
own earlier host-local success exactly), and the target's `vtbl+0x9C(0)` readout fell 12→6 across
the capture — a real, progressing fight, with a peer connected the entire time.

**This settles it: "a session exists / a peer is connected" is not the variable.** Combined with
round 29 (a completely untouched, zero-driven creature on the CLIENT still fails) and round 33
(target identity — phantom vs. true local player — makes no difference on the CLIENT), the
remaining candidates narrow to one specific axis nothing has yet isolated: **something about being
the CLIENT process/role itself** — not "is a session connected," not "is this specific creature
driven," not "which target" — since a host-local creature on the SAME connected session, same
script, same everything, works fine.

**The next precise test, not yet run:** is this really about CLIENT ROLE (e.g. `CClientExoApp`
vs. `CServerExoApp` — this engine's own internal client/server split, already documented elsewhere
in this codebase as real and load-bearing, not just this mod's own host/client distinction), or is
it still, somehow, about the specific creature being part of whatever the co-op plugin's own
enumeration/census touches AT ALL — even read-only, even with every write skipped (round 29 only
verified writes were skipped, not that the creature was absent from every census/list the plugin
walks)? The cleanest way to separate these: on the CLIENT, fire `k_def_combend01` on a creature
FAR outside any census this session's plugin would ever enumerate (a creature in a part of the
module the mirrored-enemy census caps or excludes entirely, not merely one this session chose not
to touch) and see if it STILL fails. If it does, that points at the client role/process itself; if
it finally works, it points at census membership, not client-vs-host role.

`local_combat` untouched, stays off. Full suite 953/953, both builds clean, rig torn down and
confirmed clean (`Get-Process`).

### Round 36 — no naturally-excluded hostile exists here; the companion substitute is invalid;
### the intended test could not be cleanly run

**No creature in this area/save is naturally outside the census.** `census` on the client read
`MIRROR 25 id(s) 'END_M01AA': 25 resolve here, 0 do not`, against 27 real distinct creatures
confirmed by the object-array check (`array 0x353DDF0 confirmed 27 of 27 (no ghosts)`). The 27-25
gap is exactly `carth` and `end_trask` — the two party members, excluded because they're not
enemies, not because of the 32-slot cap (`BlockEnemyCapacity`) or any distance cutoff; every real
hostile in the area (25 of them) is comfortably under the cap and genuinely in the census. There
was no naturally-occurring test subject to find.

**Constructed one instead, and it doesn't answer the question.** Built real, working diagnostic
infrastructure to make this possible: a new cheat kind (`CheatKindMoveNear`, 13; no shared-block
version bump, same pattern as `FireScript`) that snaps a target creature to a hardcoded position
near `sithsoldier 0xE`, plus a `movenear <id>` console verb, wired through
`ConsoleCommand.cs`/`CoopCommandDispatcher.cs`/`CheatController.cs`. Used it to move `end_trask`
(genuinely, structurally never touched by `EnemyPresenceController` — party members go through
`PartyPresenceController` entirely) to 1.21m from `0xE`, well inside the ~2.6m range round 25
measured working. **Position write independently confirmed** by reading it straight back
(`ObjPosition +0x090`) rather than trusting `creatures.ps1`'s known-unreliable raw scan (which
still showed the old, stale position — the ghost-scan artifact this investigation has hit
repeatedly, not a real failure).

Fired `+0x260` on Trask at that position anyway: `round+0x9CC` stayed at the not-found sentinel
(`0x7F000000`) throughout a full 6000-frame capture, `round+0x958 timer=0` the entire time — the
round never even STARTED, let alone armed. Checked what script was actually fired:
**`k_hen_combend01`, not `k_def_combend01`** — "hen" for "henchman," KOTOR's own naming for the
companion/party-AI script family, a genuinely different script than every hostile creature this
investigation has tested. Party members do not independently perceive-and-decide the way hostile
monsters do (they follow the party leader's own combat engagement rather than run their own
`GetNearestEnemy` loop), so this result says nothing about census membership at all — it's
confounded by testing an entirely different AI script, not a controlled variant of the same one.

**Honest conclusion: this round's intended test could not be cleanly executed with what's
available in this save/area.** A genuinely clean version needs either (a) a hostile creature
verified to run `k_def_combend01` (or equivalent) that the census provably excludes — which
doesn't exist here, everything is under the 32-cap — or (b) forcing more than 32 real hostiles
into one census scope to trigger the cap for real, which this save's population can't reach. The
`movenear`/`CheatKindMoveNear` infrastructure is kept (not removed) since it's real, working,
and would let a future round retest this cleanly against a genuine hostile-AI creature once one
is available (a more crowded area, or a different save).

Both `coop.ini` unchanged, `local_combat` untouched. Full suite 953/953, both builds clean, rig
torn down and confirmed clean (`Get-Process`).

### Round 37 — ANSWERED. Census membership is the variable, not client role. `sub+0xB0` armed
### for real, on the client, for a creature outside the census

Round 36's construction (moving a real creature) was overkill — round 37 used a far simpler one:
temporarily lowered `KotorGameBridge.BlockEnemyCapacity` 32→1 (the same constant that already
sorts candidates by "fighting first, then nearest distance" and truncates the published census —
`docs/research/enemy-presence-sync.md`/`peer-damage-ledger.md`'s "census cap and distance
shedding," confirmed live, not assumed). With this save's ~25 real `sithsoldier`s, that split them
cleanly across the census boundary using nothing but the existing mechanism: `census 0xE` on the
client read `listed hp 15/15 at 29.7,144.5` (still tracked, the nearest one), `census 0x10` read
`not listed in this census` (genuinely, structurally un-enumerated — the plugin's own log
confirms the drop: `enemy drive: mirroring 1 of the owner's 1 enemies`).

**Fired `k_def_combend01` — the exact same proven script every round has used — on both, from the
CLIENT process, in the same session, same positions, same everything except census membership:**

- **`0xE` (still tracked):** `sub+0xB0=0` throughout, timer ran a normal cycle, never armed —
  identical to every mirrored-case failure in rounds 9-36. Confirms the baseline is unchanged.
- **`0x10` (genuinely outside the census):** **`sub+0xB0=1`** — armed, for real, on the client
  process. Sustained across 2081 of 6000 sampled frames (not a one-tick blip), first observed
  ~70ms after firing and still armed at the end of the capture window. The target's `vtbl+0x9C(0)`
  readout fell 16→12 across that armed window — a real, ongoing fight, matching every other
  confirmed-genuine success in this investigation (rounds 27, 35).

**This answers the question round 35 opened and round 36 couldn't close.** "A session/peer
connected at all" was already refuted (round 35: host-local still works with a peer connected).
Now "being the client process/role itself" is refuted too: the SAME client process, same
connected session, armed a real fight for a creature it had simply never enumerated. **The
variable is census membership** — being tracked/enumerated by the co-op plugin's `EnemyPresenceController`
at all, independent of whether its position is ever driven (round 29 already showed writes alone
aren't it; this round shows read-only enumeration is enough by itself).

**What this means for a fix, and why it wasn't found by looking at position/per-frame writes:**
something about the act of ENUMERATING a creature into the tracked set — not writing to it —
marks it in a way the engine's own combat-round machinery respects. The most likely mechanism,
given everything already ruled out in rounds 4-30 (the round struct fields, the acquisition
chain, the party-table gate): `Odyssey.ClaimCreature`/`ClaimCreatureForFight`'s initial claim
(called once when a creature FIRST enters the tracked set, separate from the per-frame drive
pass) sets something that persists for the creature's whole time in the census, not just during
active writes. That one-shot claim call — not the per-frame position/facing writes rounds 14 and
29 already cleared — is the concrete next target: find exactly what it sets, on FIRST entry to
the tracked set specifically, and whether un-claiming (or narrowing the claim) a creature the
moment `local_combat` wants it to genuinely fight would let this work without sacrificing visual
position sync for enemies that are just being mirrored, not actively fought.

Restored `BlockEnemyCapacity` to 32. Both `coop.ini` unchanged, `local_combat` untouched. Full
suite 953/953, both builds clean, rig torn down and confirmed clean (`Get-Process`).

### Round 38 — the boundary is confirmed real (order-controlled), but no write-based mechanism
### explains it; `local_combat` NOT flipped, and here is exactly why

**Part 1: `ClaimCreature`/`ClaimCreatureForFight` is NOT a one-shot claim.** Its own doc comment
already says so — `EnemyPresenceController re-claims every mirrored enemy every pass` — and its
body (`Odyssey.cs:369-385`) writes exactly three things, every single frame a creature is driven:
`ObjCommandable=1`, `CreMovePermitted|=2`, `CreRunFlag=0`, plus a conditional `ClearAllActionsFn`.
None of round 29's or round 37's diagnostics ever reach this function for their test creature in
EITHER condition — round 29's `DiagUndrivenObjectId` bails out of `DriveOne` BEFORE `ClaimCreature`
is ever called (`EnemyPresenceController.cs:410-413`); round 37/38's cap-excluded creature is
never in the loop at all. So `ClaimCreature` was never a live variable in either test — it was
already controlled out before this round started.

**Part 2: reproduced round 37's result with the confound the coordinator raised in mind — order.**
Re-ran with `BlockEnemyCapacity=1` again, but fired the EXCLUDED creature (`0x10`) FIRST this
time (not second, as round 37 had it), then the still-tracked one (`0xE`) third. Result unchanged:
`0x10` armed again (1865/6000 samples nonzero `sub+0xB0`, HP-like value 28→24, and — a new
confirming detail — `sub+4` finally read a real, nonzero pointer (`0x43203AC`) by the end of the
capture, something never once observed in 37 rounds of failures); `0xE` failed again (0/6000).
**Order is ruled out as a confound.** The boundary is real, reproducible, and specifically about
census membership, exactly as round 37 found.

**Part 3: exhaustive disassembly of every candidate write site found NONE.** Traced every piece of
code that runs differently between "published, resolved, but otherwise untouched" (round 29's
condition) and "never published at all" (round 37/38's condition):
- `EnemyPresenceController.Resolve()` (`EnemyPresenceController.cs:521-542`) — caches a pointer
  in a MANAGED array (`_creature[slot]`), calls `Odyssey.ResolveServerObject`. No engine write.
- `Odyssey.ResolveServerObject` (`Odyssey.cs:679-728`) — calls the engine's own
  `CServerExoApp::GetGameObject` (`0x004AE750` → `0x004B1700`), then tests the returned pointer's
  layout. `0x004B1700` itself (disassembled in full this round) does nothing but forward into
  `CGameObjectArray::GetGameObject` (`0x004D8230`) with a scratch stack local — no writes to
  anything the caller doesn't own.
- `CGameObjectArray::GetGameObject` (`0x004D8230`, disassembled in full this round, every
  instruction) — a hash-bucket lookup and linked-list walk, comparing the masked id against each
  node and writing the FOUND POINTER into the CALLER's own out-parameter (`[edx] = ...`) on
  success. **No write to the object it finds, confirmed at the instruction level.** This is the
  single engine-level call `Resolve()` makes that round 29's creature received and round 37/38's
  excluded creature never did — and it is provably, completely read-only.
- `MaterialisedEnemies.Seen()` (`MaterialisedEnemies.cs:131-141`) — a managed-array timestamp
  write, no engine touch, called for every published slot before the per-creature diagnostic
  bail-out in both round 29 and this round.
- The HOST-side census scan (`KotorGameBridge.cs:3888-4020`, re-read this round) — every field
  (`OffCurrentHitPoints`, `OffPosition`, `OffOrientation`, `OffAnimation`, `OffAppearanceType`,
  `OffCreatureStats`) is read through `_memory.TryRead*` — raw `ReadProcessMemory`, not an engine
  call, and identical for every candidate whether or not it survives the cap. The only place a
  candidate's fate diverges is a pure managed-list `Add()` after the cap/sort — no further engine
  touch of any kind.

**Conclusion: the boundary is real and reproducible, but nothing in the entire call chain this
investigation can find writes to the creature, the round struct, or anything engine-side,
differently for a published-but-untouched creature versus a never-published one.** Every
plausible write-based mechanism has now been checked and ruled out at the instruction level. This
either means the real mechanism is something this investigation's method — tracing explicit
field writes — cannot see (a heap-layout or memory-paging effect from `ReadProcessMemory`/
`WriteProcessMemory` traffic itself, something at the OS/hardware level rather than the game's own
logic, which would be a genuinely different kind of investigation entirely), or there is a
touch-point still unfound despite this round's search.

**Decision: `local_combat`'s default is NOT flipped.** Not because the mechanism doesn't work —
round 37 and this round both proved, twice, with order controlled, that it genuinely does, for a
creature outside the census — but because shipping this needs an actual DESIGN (some way to
temporarily exclude an actively-fighting creature from the published census without breaking its
visual position sync for the rest of the fight, which is a real host↔client protocol question,
not a config flip) and this round could not identify why the boundary exists, only that it does.
Flipping a default on a mechanism whose cause is unknown, however well-reproduced the symptom, is
exactly the kind of thing this section's own standing rule — "did it work, not did it avoid
breaking something" — argues against; "it works when a creature happens to fall outside a
cap that was never meant to gate combat" is not yet "it works."

**What a round 39 needs, concretely:** either (a) find the actual mechanism — the most promising
unexplored angle is OS/engine-level (does an external process's `ReadProcessMemory`/
`WriteProcessMemory` traffic on a specific creature's page(s) trigger something — a copy-on-write
fault, a guard page, an anti-tamper heuristic — that a purely code-level trace cannot see, which
would need a genuinely different kind of instrumentation than disassembly); or (b) skip finding
the mechanism and design the actual fix directly from the empirical result — a protocol addition
letting the client tell the host "stop publishing creature X to me for N seconds while
`local_combat` lets it genuinely fight," accepting a brief position-sync gap during that window,
which is buildable and testable without ever knowing why it's needed.

Both `coop.ini` unchanged, `local_combat` untouched, `BlockEnemyCapacity` restored to 32. Full
suite 953/953, both builds clean, rig torn down and confirmed clean (`Get-Process`).

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
