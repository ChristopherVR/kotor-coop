# Live-test plan: does a released companion's swing land as CREDITED damage?

Status: **RUN, 2026-08-23 (round 82)** — decisive result on hypothesis 2 (solo mode), the
first row of the decision tree below. See `party-reconciliation.md`'s "Round 82" entry for the
full log evidence: a real, sustained multi-Vulkar fight staged via `blockpoke.ps1 attack`, two
independent release cycles (11 samples total), `head` never left `0xFFFFFFFF` (empty) on a single
sample while `soloMode=1` held and the client's own PC was demonstrably taking real damage from
multiple attackers. **Solo mode is confirmed, not merely suspected, to block a released
companion's AI from ever queuing an attack.** No live fix was attempted (the task's own gate for
a scoped per-companion release was not met — see that entry). `companion_local_combat` stays OFF.
The original plan below is left intact as the recipe that was actually followed, for whoever
picks up the "scoped solo-mode release" design next.

**UPDATE, 2026-08-23 (round 83, static pass, no rig access):** the "scoped solo-mode release next"
pointer above is superseded, not merely picked up — a comprehensive disassembly scan (every one of
the 226 call sites to `CServerExoApp::GetPartyTable` in swkotor.exe, followed out to a `ret` or
250 instructions either way) found exactly six places in the ENTIRE binary that ever read party
table +0x190 (solo mode): two GUI button-state updates, one client-side follow-toggle read, one
stealth-menu read, one temp-death update, and the script-facing `GetSoloMode()` nwscript command
itself. **None of them sit anywhere on the path from `ExecuteCommandAttack` through
`CSWSCreature::AddAttackActions`/`AddAttackAction` to `RunActions`** — the exact chain
`Odyssey.AttackTarget` already drives (the same call the `attack` console cheat and
`TestDriveController` use, live-proven round 11 to queue a real attack and land real damage).
Solo mode's "companions stand still" behaviour is therefore the party AI SCRIPT's own choice not
to act on what `GetSoloMode()` tells it — not an engine-level gate on the action queue itself. A
directly-queued attack action never touches solo mode at all, in either direction: no read, no
write. That makes the party-wide-write risk the round 82 entry's own "no live fix attempted" gate
was protecting against **moot** — this fix candidate needs no write to +0x190, so it cannot
re-enable a second, still-mirrored companion's own solo-suppressed AI, regardless of how many
companions are in the party. See the round 83 entry appended at the end of this document, and
`party-reconciliation.md`'s matching dated entry, for the full scan methodology and the
implementation shipped behind `companion_local_combat` (still OFF by default — this is a
disassembly-backed hypothesis, not yet live-verified).

## What this settles, precisely

`companion_local_combat`'s release/reclaim gate is already live-proven clean
(`party-reconciliation.md`, 2026-08-22: three release/reclaim cycles, correct faction/pin
handling, no stranding). What has never been observed is the thing the flag exists for: a
companion released to its own engine AI actually landing a hit on the locally-fought enemy, that
hit reaching the fight's OWNER (the host) as real, authoritative damage, and both sides agreeing
on the enemy's HP afterward — the same "credited, not cosmetic" bar `peer-damage-ledger.md`
already proved for the PLAYER's own local-combat damage.

Two prior blockers, both on record, need to be worked around or ruled out in the same pass:

1. **Party-presence staleness** (`party-reconciliation.md`, 2026-08-22): a mirroring client's
   own copy of a companion's position (`SharedBlock.PartyPresenceSlots`) can freeze for minutes
   even in a healthy, connected session, self-clearing only on `disconnect`/`reconnect`. If the
   companion's mirrored body never reaches the fight, it has nothing to swing at, and the run
   proves nothing either way.
2. **Solo mode possibly blocking companion combat AI entirely, not just follow-AI** (new,
   `party-reconciliation.md`'s 2026-08-23 entry): `KotorGameBridge.TryWritePartyMembers` holds
   `CSWPartyTable+0x190` at 1 for the whole client session regardless of release state. Two
   independent, pre-existing documents in this project describe solo mode as stopping
   companions from fighting, not merely from following. This pass ships a read-only diagnostic
   (`PartyPresenceController`'s release-diagnostic log line) built specifically to answer this
   live, cheaply, without touching the engine.

Structure the pass so it produces a clear answer to **both**, not just "did HP move."

## Exact coop.ini settings

On **both** installs (host and client), starting from `profiles/coop.ini.example` defaults:

```ini
local_combat=1                      ; default already; confirm explicitly
companion_local_combat=1            ; OFF by default — this is what is being tested
companion_local_combat_range=15     ; wider than the default 10: gives real walking room to
                                     ; get all three (player, companion, enemy) inside the gate
                                     ; without needing sub-metre positioning
mirror_smooth_ms=150                ; default; do not raise — a slower mirror widens the
                                     ; staleness window this pass is trying to work around
party_fast_pump=0                   ; leave off (still unproven per lag-audit.md's own 2026-08-23
                                     ; entry) unless the staleness bug is not otherwise workable
                                     ; in the time available, in which case try =1 as a SEPARATE
                                     ; leg and say so in the writeup — do not conflate the two
auto_start_agent=1                  ; tools/demo.ps1 restores this on teardown; do not leave off
```

Everything else at `profiles/coop.ini.example` defaults. Do not set `local_combat_bisect_mask`,
`local_combat_bypass_id`, or `DiagUndrivenObjectId` — this pass wants the shipped path, not a
bisect leg.

## Rig and staging

1. `pwsh tools\demo.ps1` — cold start, save 1 (Endar Spire opening, Trask recruited, matching
   every prior round's own recipe so results are comparable). Confirm both instances connect
   clean (`shared`, no `[W_SAVE]`) before doing anything else.
2. Identify both process ids: `Get-Process swkotor | Select Id,MainWindowTitle` (or reuse
   whatever `demo.ps1` itself echoes on startup).
3. On the **HOST**, `pwsh tools\blockpoke.ps1 -ProcId <hostPid> console 'warp tar_m10aa'` —
   HOST-ONLY, per the 2026-08-22 finding: both instances independently warping produced a
   persistent `[W_SAVE]` mismatch that never self-cleared, because each instance's own
   autosave-on-transition genuinely diverged. Let the client follow through the co-op
   travel-follow channel instead of issuing its own warp.
4. Confirm both instances are in `tar_m10aa` and still `shared` (no `[W_SAVE]`) before staging
   combat. If a `[W_SAVE]` mismatch appears and does not clear within ~10-15 s on its own
   (the fast-clearing transient `shared-state-audit.md` already documents), stop and re-roll the
   warp rather than staging combat on top of a desynced session.

## Working around the party-presence staleness bug: real walking, not a raw poke

The 2026-08-22 pass's own conclusion was that a raw position poke to stage the companion
defeats the very channel being tested (the companion is released from wherever its CLIENT-side
body was already standing, which was never smoothly mirrored to begin with). This pass should
NOT use `teleport.ps1`'s raw-poke technique on the companion. Instead:

1. On the HOST, walk (or `console 'tp <x> <y> <z>'` — a REAL engine teleport, which
   `PartyFollowController`'s own round-73 methodology note already established as the one thing
   that keeps every downstream consumer, including the party-presence publish, correctly in
   sync — never a raw `+0x90` poke) toward a Black Vulkar in `tar_m10aa`'s point-blank pool
   (`shared-state-audit.md` round 55's own recipe, object id `0x33` in that save/module).
   `console 'tp'` is a real engine position change; a raw poke to the HOST's own creature is not
   what is being avoided here — only the COMPANION's position must never be raw-poked, because
   that is the value the client-side mirror channel is supposed to carry, and poking it locally
   on the host bypasses the very publish/mirror hop this pass is testing.
2. Let the host's own native companion AI/follow bring Trask along BEHIND the host as the host
   walks the last ~15-20 units toward the Vulkar, rather than warping/poking her into position.
   This is slower but is the only way to know the client's own mirrored copy is tracking a
   REAL, continuously-updated position rather than a single stale snapshot — matching the
   2026-08-22 verdict's own suggested fix ("a live pass staged through genuine incremental host
   movement... patient enough to let real mirroring keep the client's copy caught up
   throughout").
3. On the CLIENT, before engaging, run `pwsh tools\partystate.ps1 -ProcId <clientPid>` a few
   times, a couple of seconds apart, while the host is still walking. Confirm Trask's reported
   position is CHANGING between samples (not frozen) — this is the direct staleness check. If it
   is frozen, `disconnect`/`reconnect` the client (per the 2026-08-22 workaround) before
   continuing rather than staging combat on a known-stale mirror.
4. Once the host's walk has closed to point-blank range on the Vulkar and Trask's client-side
   mirror is visibly tracking (not frozen), have the CLIENT'S player attack the same Vulkar
   (`local_combat`'s own exclusion should arm — confirm via the existing
   `party drive: companion 0x... released to fight locally` log line, which only fires once the
   triple-proximity gate — player, companion, and the excluded enemy all mutually close — is
   satisfied).

## What to capture, and what each line proves

Tail both instances' plugin logs (`PluginHost.Log` output — wherever `demo.ps1`/the agent
launcher redirects it, or `tools\blockpoke.ps1 -ProcId <pid> console` if reading it live through
the console channel is more convenient) from the moment the client engages the Vulkar.

### 1. The release edge (already proven, confirm it still fires)

```
party drive: companion 0x<id> released to fight locally — within range of both you and the
enemy you are fighting (head=0x.. moveState=.. soloMode=.. t=..s slot=..)
```

The parenthetical is new this pass (`PartyPresenceController.ReleaseDiagSnapshot`). Record the
first sample's `soloMode` value — expect `1` on the client (it is held for the whole session);
if it reads `0`, something upstream already changed and the whole solo-mode hypothesis needs
re-examining before going further.

### 2. The release-diagnostic trickle (new this pass) — THE decisive line for hypothesis 2

```
party drive: companion 0x<id> still released — head=0x.. moveState=.. soloMode=.. t=..s slot=..
```

Logged every ~3 s while the companion stays released, capped at 60 lines. Collect every line for
the duration of the fight (or as many as land before the 60-line cap) and answer:

- **Does `head` ever read anything other than `0x-1` (`0xFFFFFFFF`/empty)?** A nonzero, non
  "-1" value means the companion's own AI queued SOMETHING — attack, move, or otherwise. Cross
  reference the value against `tools/kotor1_0_3.db`'s own RunActions jump table (the same lookup
  `Odyssey.cs`'s own `IsMoveAction` comment already did for the follow-AI investigation) to
  classify it: an attack-shaped action id here is the single strongest piece of evidence this
  whole investigation has been missing.
- **Does `moveState` ever change from whatever it read at release** (expect 1 "idle" or similar
  at the moment of release, given the companion was mid-mirror a frame earlier)? Movement toward
  the enemy is a secondary, weaker signal than an attack action id, but corroborating.
- **Does `soloMode` ever read anything other than `1` for the client** during the whole window?
  It should not, per how `KotorGameBridge.TryWritePartyMembers` is currently written — if it
  ever reads `0` unexpectedly, that is itself worth flagging (a different bug, an unexpected
  release of the flag).

**If `head` stays `0x-1` (empty) for the WHOLE release window, every sample, while `nearEnemy`
was true throughout (corroborate against the position samples from partystate.ps1 taken in
parallel) — that is first-hand, live-measured proof the companion's AI never even tries to
attack while solo mode holds, independent of proximity or staleness.** This confirms hypothesis
2 (`party-reconciliation.md`'s 2026-08-23 entry) and points the next round at
`KotorGameBridge.TryWritePartyMembers`/`ReleasePartyFreeze`, gated on a new "is any companion
currently released" shared-block signal `PartyPresenceController` would need to publish.

**If `head` DOES show attack-shaped activity but no damage ever gets credited (see §3-4 below)**,
the AI is not the blocker — the credit pipeline itself has a real bug worth chasing directly
(re-check `PhantomHitHook.IsOursToCredit`'s corpse/vtable/attacker-id gates against the actual
runtime ids involved, and `PeerDamageController.RefreshAttackers`'s `ResolveServerObject`
re-validation, with the concrete ids from this run in hand).

### 3. The client's own damage-dealt ledger picking up the companion's hits

```
combat ground-truth: local PC took N damage, attacker=0x.. (...)
```
is the wrong line to watch here (that is damage LANDING on the local PC). What to watch instead
is silent unless something is wrong or unresolved — `PeerDamageController`'s "combat: a peer owes
N damage to 0x.., which does not resolve..." lines fire ONLY on the OWNER (host) side and only
for a failure. On the CLIENT, confirm indirectly: `tools\blockpoke.ps1 -ProcId <clientPid> state`
(or an equivalent block dump) should show `DamageDealtCount > 0` and a nonzero cumulative total
against the Vulkar's object id once the companion has landed at least one swing — if you can read
the block layout, this is the most direct confirmation that `PhantomHitHook.OnDamage`'s
`IsOursToCredit` branch actually fired for the companion's attacker id, not just the PC's.

### 4. The host applying it as real, authoritative damage

```
combat: applied N of a peer's damage to 0x<enemyId> (owed N)
```

On the **HOST**. This is `PeerDamageController.ApplyOwed` on the owning side, spending the
peer's reported total through `Odyssey.ApplyDamageEffect` — the actual authoritative HP change.
This is the line that proves "credited," not merely "the client's screen showed an animation."

### 5. HP consistency both sides

After a few of line-4's applies have landed (or the Vulkar dies), compare the Vulkar's HP (or
its death) on both instances:

- **HOST**: read directly (console `getlocalvar`-style inspection is unnecessary; the plugin's
  own enemy census / `tools\blockpoke.ps1` state dump, or simply observing the on-screen health
  bar/death, is sufficient — the host is authoritative by construction).
- **CLIENT**: the mirrored enemy's HP as displayed should track the host's (mirrored, per
  `EnemyPresenceController`'s ordinary drive) EXCEPT for the brief `local_combat`-excluded
  window, during which the client's own screen shows the enemy fighting for real. A kill should
  produce a corpse, loot and an `OnDeath` script run identical to the host's own experience
  (`PeerDamageController`'s own class doc: "an enemy whose accumulated damage exceeds its health
  simply dies here").

If the Vulkar dies from the COMBINED damage of the client's PC AND the released companion (i.e.,
fewer swings from the PC alone were needed than a solo `local_combat` fight takes — compare
against `shared-state-audit.md` round 55's own baseline timing for a PC-only kill of the same
enemy pool if available, or simply note the elapsed time and swing count), that is strong
corroborating evidence the companion's damage is landing and being credited, on top of the direct
line-4 log evidence.

### 6. `verify.ps1`

`tools\verify.ps1 -Host1 <hostPid> -Client2 <clientPid>` after the fight — specifically the
existing checks "a client hit costs the enemy real hit points" and "the peer really loses those
hit points" (both pre-existing, general `local_combat`/damage-ledger checks, not
companion-specific, but a clean pass on them alongside a clean companion-specific observation
above rules out a companion-swing test having desynced the channel for the PC's own damage too).

## Decision tree

| Observation | Conclusion | Next step |
|---|---|---|
| `head` never leaves `0x-1` for the whole release window, position samples confirm the companion was genuinely in range throughout | Solo mode (or something else party-wide) blocks a released companion's AI from ever acting | Design and live-test a scoped solo-mode release (`KotorGameBridge`, gated on a new "companion released" shared-block signal), per `party-reconciliation.md`'s 2026-08-23 entry. Do not ship it unverified. |
| `head` shows attack-shaped activity, but §3/§4 show no damage ever credited | The AI fires but the credit pipeline drops it | Re-trace `PhantomHitHook.IsOursToCredit`/`PeerDamageController.RefreshAttackers` against the SPECIFIC ids this run used; check whether `MaterialisedEnemies.ToOwner` is wrongly mapping (or failing to map) the Vulkar's id if it turned out to need materialisation this run |
| `head` shows attack-shaped activity, §4 shows real applies landing, HP/kill consistent both sides | Roadmap item 4 is fully proven — release/reclaim (already known clean) AND the swing landing as credited damage | Flip `companion_local_combat` default to `1` per this project's own "flip on if clean" bar, with this document's own log lines as the evidence trail |
| Position samples show the companion's mirror still frozen despite real host walking | The staleness bug is not (only) a raw-poke artifact — it can occur under real movement too | Chase `CoopAgent.PumpParty` → `bridge.ReadPartyMembers()` → `CoopHost.PublishParty`/`BroadcastParty` directly, per the 2026-08-22 entry's own pointer to where the answer likely lives |

## Teardown

`pwsh tools\demo.ps1 -Stop`. Confirm `Get-Process swkotor,KotorCoop.Agent` returns nothing
afterward. Revert any coop.ini overrides this pass made (`companion_local_combat`,
`companion_local_combat_range`, and `party_fast_pump` if it was tried as a separate leg) back to
`profiles/coop.ini.example` defaults on both installs unless the pass's own conclusion is to ship
a changed default.

## Round 83 (2026-08-23, static pass, no rig access) — the engine-call-bypass fix, and why it
needs no solo-mode write at all

The task authorizing this round asked two questions round 82 left open: (1) is solo mode checked
per-companion anywhere in the engine, so a narrower lever than the party-wide flag might exist,
and (2) if not, can the project's own established engine-call-bypass pattern (`usedoor`, `attack`,
`skipline`, `Odyssey.TryDriveFollowLeaderStep`) drive the released companion's attack directly
instead of depending on the engine's own AI action-queueing under solo mode.

### Question 1: no per-companion equivalent of solo mode exists

`CSWPartyTable::SetSoloMode` (0x00565500, disassembled) takes exactly one boolean and writes it
to a single `dword [this + 0x190]` — no NPC-slot or object-id parameter anywhere in its
signature. It is a party-wide switch, full stop; there is no finer granularity to find. (When
turning solo mode OFF it also calls a helper at 0x00563c60 that iterates the PC plus every
`+0x00`-counted party member — `CSWSCreature::SetActivity(this, 1, 0, notify)` on each — which is
itself further confirmation the engine's own model of "leaving solo mode" is whole-party, not
per-member: there is no code path in that helper that could be pointed at one companion only.)

### Question 2: where solo mode is actually consumed — six sites, none on the attack path

Every direct call site to `CServerExoApp::GetPartyTable` (0x004AEE70) in swkotor.exe was
enumerated (`tools/xrefs.py 0x4AEE70`: 226 sites) and each followed forward (250 instructions or
until `ret`, whichever came first) looking for any instruction reading `[reg + 0x190]` off the
returned pointer — a full static census of every place in the ENGINE that ever reads solo mode,
not just the places this project already knew about. It found exactly six:

| Site | Class::Function | What it's for |
|---|---|---|
| 0x004B7114 | `CServerExoAppInternal::TempDeathUpdate` | server-side temp-death bookkeeping |
| 0x00546AFD | `CSWVirtualMachineCommands::ExecuteCommandGetSoloMode` | the `GetSoloMode()` nwscript command itself |
| 0x005F2A5B | `CClientExoAppInternal::TogglePartyFollow` | client-side follow-toggle read |
| 0x0060F4C0 | `CSWCCreature::PartyMenuStealth` | stealth radial-menu behaviour |
| 0x00686CC1 (×2 call sites) | `CSWGuiMainInterface::Update` | solo-mode GUI button state |
| 0x006C24CF | `CSWGuiInGameSoloModeQuery::SetQueryText` | the "enter solo mode?" dialog's text |

A fully-inlined access (party table's own base offset 0x1B770 + 0x190 = 0x1B900, read directly off
`CServerExoAppInternal` without going through `GetPartyTable` at all) was also searched for and
found nowhere in `.text`. **None of the six sites are anywhere near `RunActions`
(0x0057F4A0), `CSWSCreature::AddAttackActions` (0x004FDE40), `CSWSCombatAttackData`/
`CSWSCombatRound::AddAttackAction` (0x004D38B0), or `CSWVirtualMachineCommands::
ExecuteCommandAttack` (0x0052E7F0)** — the whole chain `Odyssey.AttackTarget` already drives, and
which this project's own `attack` console cheat and `TestDriveController` already use, live-proven
(round 11) to queue a real attack action and land real, ordered damage on a fresh creature.

Cross-checked against `party-reconciliation.md`'s own earlier "Trap 5" finding (RunActions never
even reads `commandable`, `+0xE8`) — the pattern is consistent: RunActions processes whatever
sits in a creature's action queue (`+0xFC`) without asking who queued it or why, and nothing in
the queueing path itself reads party-table state. `AddAttackActions` (0x004FDE40, disassembled in
full) does read `commandable` (`+0xE8`) on one of its two branches, bailing out early if it is 0 —
but a party member's `commandable` is normally 1 and `PartyPresenceController` never touches it on
a released companion (`StayReleased` hands off "no claim, no position/health write, nothing"), so
this precondition holds for the companion case exactly as it does for any other creature.

**Conclusion**: solo mode's "companions stand still" behaviour is not an engine-level gate that
blocks the action queue — it is the party AI SCRIPT's own choice not to act on what
`GetSoloMode()` tells it (that nwscript command is one of the six read sites; the rest are GUI).
A directly-queued attack action, issued the exact way `AttackTarget` already does for the player
or a staged test creature, bypasses that choice entirely without reading or writing solo mode at
all. This is a materially different, and materially safer, fix than the "scoped solo-mode
release" round 82 pointed at next: it makes **zero writes** to the party-wide flag, so it cannot
possibly re-enable a second, still-mirrored companion's own solo-suppressed AI — the exact drift
risk `party-reconciliation.md`'s 2026-08-22 roster-freeze entry flagged for touching +0x190 at
all. There is nothing to race, because nothing about +0x190 changes.

### What was shipped

`companion_local_combat` (still OFF by default) now drives a released companion's attack directly:

- `CompanionLocalFightRule.ShouldDriveAttack(headActionType, now, lastAttemptAt,
  minIntervalSeconds)` — a new pure decision function, unit-tested — is true only when the
  companion's own action-queue head currently reads `-1` (empty — nothing to contest) AND at
  least 1.5 s (`PartyPresenceController.AttackDriveIntervalSeconds`) has passed since the last
  attempt (a throttle against hammering `ExecuteCommandAttack` every frame, not a cadence the
  engine's own combat-round timing is known to require — this constant is itself untested live).
- `PartyPresenceController.DriveAttackIfDue`, called from the existing `StayReleased` branch of
  `DriveOne` (the same branch that already does nothing else — "no claim, no position/health
  write, nothing" — for a released companion), calls `Odyssey.AttackTarget(companionObjectId,
  localEnemyObjectId, out failure)` when the rule above says to, targeting the exact enemy
  `local_combat`'s own exclusion is already fighting (`block.LocalCombatExcludedId`, mapped
  through `MaterialisedEnemies.ToLocal` — the same resolve `OnFrame` already did to compute
  `nearEnemy`, threaded through rather than re-resolved).
- A loud, capped log line either way (`party drive: drove companion 0x.. 's attack on 0x.. directly
  (head was 0x..)` / `could not drive ... (failure N, head was 0x..)`) — deliberately separate
  from the quieter release-diag trickle, so a live pass can tell at a glance whether the call is
  even being attempted.
- No write to solo mode. No shared-block/protocol change (nothing new crosses the wire; this is
  entirely local engine-call plumbing on the instance that already released the companion).

`dotnet test tests/KotorCoop.Tests` (Release): **1643/1643 passed** (1638 baseline + 5 new
`ShouldDriveAttack` cases). Both plugin projects build clean.

### What is still just a hypothesis — NEEDS-LIVE

The disassembly is comprehensive (every solo-mode read site in the binary, the whole
attack-queueing call chain traced instruction-by-instruction), but nothing here has been run
against a live game. Three things specifically have never been observed:

1. Whether `Odyssey.AttackTarget` called with a PARTY MEMBER as the attacker (round 11's own live
   proof used a plain hostile creature) behaves identically — the `commandable` branch in
   `AddAttackActions` was checked statically and should hold, but "should hold" is not "measured."
2. Whether one queued attack action is enough to start a self-sustaining combat round (the way it
   visibly does for a human player clicking attack once) or whether this needs to keep re-issuing
   once every ~1.5 s for the whole fight to land more than one swing — `ShouldDriveAttack`'s
   design already handles the latter (re-fires the moment the queue reads empty again) but this is
   untested against a real multi-round fight.
3. **The two-companion drift risk this whole investigation exists to avoid** — see the exact live
   steps below. The static case for "this makes no write to solo mode" is solid, but the task's
   own bar is a live check, not a disassembly argument, before calling the risk closed.

### Exact live-test steps for the next round (rig required)

Needs a save with **two companions recruited** — Endar Spire's Trask-only party cannot exercise
the drift risk this fix exists to avoid, because there is no second companion to watch. The
earliest two-companion point in a fresh game is the Taris apartment/underworld once both a second
companion (e.g. Mission or Zaalbar) and the story's later party members are recruited; failing an
early save file being available, stage it by playing forward past Taris to wherever the save
library already has two companions in the active roster, or recruit a second companion live during
the pass and take a fresh save afterward for repeatability.

1. `profiles/coop.ini.example` overrides on **both** installs: `local_combat=1` (default),
   `companion_local_combat=1`, `companion_local_combat_range=15` (same widened value round 82
   used, for real walking room). Leave everything else at defaults.
2. `pwsh tools\demo.ps1` with a save that has two companions in the active party (not just
   available — actually seated, both slots of `partyTable+0x0C` occupied). Confirm both instances
   connect `shared`.
3. Identify both process ids (`Get-Process swkotor`).
4. Walk (real engine movement/`console 'tp'`, never a raw poke — see this document's own §
   "Working around the party-presence staleness bug") until BOTH companions and the host are
   standing together, then engage a hostile with the CLIENT's own player exactly as the original
   recipe above describes, so `local_combat` arms and `companion_local_combat`'s triple-proximity
   gate releases the ONE companion that is close enough to both the client's player and the enemy.
   Deliberately position the SECOND companion nearby but not quite in range (or arrange for only
   one to close proximity first) so the release gate picks exactly one, leaving the other mirrored
   — this is the scenario the risk is about.
5. Tail both instances' logs. On the **released** companion, confirm the round 82 diagnostic line
   now shows real activity: `party drive: drove companion 0x.. 's attack on 0x.. directly (head was
   0x..)` firing repeatedly (every ~1.5 s while the queue empties), and check whether `head` in the
   `still released` trickle line now reads something other than `0xFFFFFFFF` shortly after each
   drive attempt (0x3F/63 — `AddAttackActions`' own attack action id — is the expected value if the
   engine actually queued it). This is the direct test of open question 1 above.
6. **The drift check, specifically**: watch the SECOND, still-mirrored companion for the entire
   window the first one is released. It must stay exactly where `PartyPresenceController.DriveOne`
   puts it (the host's own reported position, smoothed) — no independent movement, no swings, no
   attack log lines naming it as either attacker or target. Any of the following would mean the
   fix's "zero write to solo mode" static claim was wrong in a way this pass did not anticipate
   (e.g., some other side effect of `AttackTarget`'s VM impersonation touching party-wide engine
   state) and the fix must be reverted, not patched around:
   - The second companion's own `party drive: companion 0x.. released ...` log line ever fires
     (it should never — its own `nearEnemy`/`nearPlayer` gate should keep it mirrored the whole
     time, but confirm it explicitly).
   - `tools\partystate.ps1 -ProcId <clientPid>` sampled a few times during the window shows the
     second companion's position diverging from what the host reports for it (drift).
   - Any `combat ground-truth:` line names the second (still-mirrored) companion's object id as
     either attacker or victim.
   - `tools\verify.ps1 -Host1 <hostPid> -Client2 <clientPid>` after the fight still passes clean on
     every existing check, not just the companion-specific ones — a regression anywhere else in the
     party/combat channel is exactly the kind of collateral damage the risk describes.
7. Compare elapsed fight time / swing count against a companion-local-combat-OFF baseline of the
   same fight (same enemy, same starting HP) to get at open question 2: did the released
   companion's swings measurably speed up the kill, corroborating that more than one attack action
   actually landed, not just the first one.
8. `tools\verify.ps1` and the existing §3/§4/§5 checks from the original plan above, unchanged —
   confirm the credited-damage pipeline (already sound by inspection, per round 82) actually fires
   now that the AI blocker is worked around.

If all of the above comes back clean — released companion visibly attacking, second companion
provably undisturbed, `verify.ps1` clean — that is the evidence bar this document's own decision
tree sets for flipping `companion_local_combat`'s default to `1`. If the drift check in step 6
fires, revert `DriveAttackIfDue`/`ShouldDriveAttack` entirely rather than attempting a live patch
mid-session, and record exactly what was observed here for whoever picks this up next.

## Live pass (2026-08-24, round 85): the two-companion drift check, run for real — NO drift

Ran the exact steps above (Carth recruited alongside Trask via `console 'recruit 2 carth'` +
`dialogue_recruit_sync=1`, `companion_local_combat=1`/`_range=15`, real `tp`/`walkto`, never a raw
poke). Full log evidence and the crash discovered along the way are in
`party-reconciliation.md`'s "Round 85" entry (not duplicated here); the summary that matters for
this document's own decision tree:

- **Step 1 (open question 1 — party-member attacker)**: confirmed. `Odyssey.AttackTarget` on a
  party member behaves identically to round 11's plain-hostile case — `head` went from `0xFFFFFFFF`
  to `0x3F` (`AddAttackActions`' own attack action id) and held there across eight repeated
  `still released` samples.
- **Step 2 (open question 2 — one attack vs. sustained)**: confirmed NOT self-sustaining alone —
  `DriveAttackIfDue`'s 1.5s re-issue fired repeatedly for the whole ~25s release window, exactly as
  designed, and the host's own `combat: applied N of a peer's damage...` lines (11, 4, 8 to one
  enemy; 5, 4, 9 to a second, after the first died) show multiple real, separate landed hits, not
  one action generating a whole fight on its own.
- **Step 6 (the drift check itself)**: **did not fire.** Trask (the second, non-released companion)
  never appears in a single `party drive: companion 0x127 ...` line, her client-mirrored position
  matched the host's real position to within 0.05 units after the fight, and the `mirroring N of the
  host's companions` counter (2 → 1 during Carth's release → 2 after reclaim) directly confirms
  exactly one companion stayed mirrored throughout. None of the four disqualifying conditions this
  step's own checklist named were observed.
- **Step 8 (`verify.ps1`)**: NOT clean — 7 checks failed, but every one traced to this round's own
  post-fight state disruption (a killed/damaged enemy near the client's new, ~130-unit-displaced
  position; the already-known warp crash still sitting in the Windows event log) rather than a
  regression in the channel under test — see `party-reconciliation.md`'s own entry for the
  per-check trace. The credited-damage channel's own health is instead confirmed directly by this
  round's host-log evidence (a real kill plus 18 more damage applied to a second enemy), which does
  not depend on `verify.ps1`'s own fresh-nearest-enemy pick succeeding.

### Verdict against this document's own decision tree

Row 3 of the decision tree ("head shows attack-shaped activity, real applies landing, HP/kill
consistent both sides") is now true, live, AND the two-companion drift risk this document's round-83
entry flagged as the harder, still-untested half is now also closed clean. Per this project's "flip
on if clean" bar, this clears every item this document's own NEEDS-LIVE list named — but this
round's task explicitly capped the outcome at "safe to consider for a real (still off-by-default,
pending broader testing) validation," not an immediate flip, both because of the newly-found
warp-transition crash (a real, separate stability risk in the same follow-AI code area, not yet
root-caused) and because `verify.ps1`'s own fails were not re-checked on a fresh, undisturbed rig.
`companion_local_combat` stays OFF by default. Whoever next has a clean rig and time should: (1) get
`verify.ps1` fully green on an undisturbed session to close the last procedural gap, and (2)
root-cause the warp crash before recommending a default flip, since it lives in code this feature's
own host-side companion following depends on.
