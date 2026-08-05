# Combat participation — making owner-side enemies threaten the phantom, safely (research 2026-08-06)

How a hostile creature picks, keeps and releases an attack target; how it comes to know a
candidate exists; where a resolved hit can be observed with one small detour; and the exact
recipe that makes the client's phantom a real combat target **without** re-creating the
immortal aggro sink of Handoff §7.4. Verified against `swkotor.exe` (GOG retail 1.0.3;
disk == memory, no ASLR, image base `0x400000`) by disassembly (`tools/disasm.py`), caller
scans (`tools/xrefs.py`) and the sha256-matched symbol DB `tools/kotor1_0_3.db`. Nothing was
modified, no game was run, and no game function was called for this document.

Sibling docs: `docs/research/enemy-presence-sync.md` (this is option (a) from its closing
section), `docs/research/death-handling.md` (the damage/death pipeline on the receiving
side), `docs/research/realtime-creature-movement.md` (drive recipe and doc conventions),
`Handoff.md` §7.4 (the aggro-sink post-mortem this design must answer).

Tag key: **VERIFIED** = read from the instruction stream here (or in a cited doc) ·
**INFERRED** = deduced from code I did read · **NEEDS-LIVE** = must be confirmed on the
running game.

---

## Headline

**The decoy-target design is feasible, and it is one dword write per state change.** The
entire hostility decision — engine retargeting, script AI, perception events — funnels
through a single live-read chain: `CSWSObject::GetReputation` (`0x0057CB80`) →
`CSWSCreatureStats.faction_id` (`stats + 0x78`) → the faction matrix in `CFactionManager`.
There is **no personal-reputation list in KOTOR 1** (VERIFIED, §2.3): flip the phantom's
faction id to 0 (PLAYER) and every enemy evaluates it exactly as it evaluates the real
player; flip it back to 5 (Neutral) and every targeting path lets go.

**§7.4's "releases a target only when it reads dead" was incomplete.** Full decode of
`CSWSCreature::CreateNewAttackActions` (`0x005B6980`, §1) shows **four** independent release
conditions, and one of them is `GetReputation(attacker → target) > 10` (`0x005B6A46`,
VERIFIED). The 7.4 sink happened because the first design satisfied *none* of them: plot
kept HP frozen (never reads dead), faction 0 kept reputation ≤ 10 (never reads friendly),
and nothing called `PacifyCreature`. The new design holds **three** release levers at once —
reputation flip on death, `PacifyCreature`, and (optionally) a real `GetDead` via an HP ≤ 0
write — so no single missed edge can wedge an attacker.

**Perception is already solved.** `DoPerceptionUpdateOnCreature` (`0x00502AC0`, §3) inserts
into the visible list on spot/listen/LOS alone — **there is no faction filter anywhere in
the perception path** (VERIFIED). A neutral phantom standing in range is already on every
nearby enemy's perception list with the seen bit set; the faction flip instantly makes it
eligible, because `GetNearestEnemy` re-reads reputation per candidate per scan.

**The hit choke point is `CSWSEffectListHandler::OnApplyDamage` (`0x004DFA40`)** — the
damage-application analog of `InsertFireForgetAnimation`: one function through which every
point of weapon, grenade and force damage in the game passes, `__stdcall` with the target
object and the `CGameEffect` (which carries the attacker in `creator_id +0x18` and the
per-type amounts in its integer list) on the stack, and a clean 7-byte relocatable SEH
prologue for a verify-bytes-or-refuse detour (§4.4). Filter on `target == phantom`, emit
`(attackerId, damageTotal)`, forward to the client, apply there through the VM
`EffectDamage` + `ApplyEffectToObject` pattern the plugin already uses for `KillCreature`.

What is genuinely still open is timing, not mechanism: how fast an *idle* enemy aggros onto
a freshly-flipped phantom (the initial "seen" perception event fired while it was still
neutral and does not re-fire; §5.4), and whether a dead-reading phantom body attracts any
cleanup. Both are one live session away (§7).

---

## 1. Target acquisition and retention — `CreateNewAttackActions` decoded (Q1)

`0x005B6980`, `__thiscall`, `ret 0xC`. Disassembled in full (704 bytes); everything in this
section is **VERIFIED** unless tagged.

```c
void __thiscall CSWSCreature::CreateNewAttackActions(
    CSWSCreature* this,          // the attacker
    int  bMayAcquireNewTarget,   // arg1: 0 = only validate/renew the current target
    int  bUseAttemptedTarget,    // arg2: current target = this+0x524 instead of round+0x9CC;
                                 //       also enables the range re-check
    uint candidateId);           // arg3: preferred search origin for re-acquisition
```

### 1.1 The single caller

`tools/xrefs.py 0x005B6980` finds exactly **one** call site: `0x004D4B6E`, inside
`CSWSCombatRound::EndCombatRound` (`0x004D4620`). `EndCombatRound` itself is called from the
round timers (`IncrementTimer 0x004D4C10`, `DecrementPauseTimer 0x004D4E80`,
`UpdateCombatRoundTimer 0x004FAF20`), from `StartCombatRound` (`0x004D5F70`), from
`OnApplyDeath` (`0x004E0CFA` — the dying creature's round is closed), from `SetCombatState`
(`0x004F283C`) and from `CancelCombat` (`0x004FDAD7`). So **retargeting happens at combat
round boundaries**, driven by the per-frame round timer — this is the loop that kept an
enemy locked onto the 7.4 phantom forever.

`EndCombatRound` also pre-validates: if the round's current target (`round + 0x9CC`) is
`GetDead`/`GetDeadTemp` (or a door with `+0x2CC` set / a destroyed placeable), it writes
`round+0x9CC = OBJECT_INVALID` itself (`0x004D47BD`–`0x004D482B`) and passes
`bMayAcquireNewTarget = 1` with `candidateId` = the creature's attempted target (`+0x524`
if `round+0x938` set, else `+0x50C`).

### 1.2 Validation of the current target — the RELEASE conditions

The current target id is `this+0x524` (arg2 ≠ 0) or `[this+0x9C8] + 0x9CC` (the combat
round's live target — the field Handoff 7.4 identified). If it is not `OBJECT_INVALID`,
and the round's pending action list (`round+0x9B0`, a `CExoLinkedList<CSWSCombatRoundAction>*`)
is empty, the target is re-validated:

| # | Test | Instruction | On failure |
| --- | --- | --- | --- |
| 1 | `CServerExoApp::GetGameObject(targetId)` (`0x004AE750`) resolves | `0x005B69E2` | **release** and write `round+0x9CC = OBJECT_INVALID` (`0x005B6AC2`) |
| 2 | target is a creature (`vtbl+0x30` AsCreature) | `0x005B69F1` | **kept** — a door/placeable target skips the dead tests entirely |
| 3 | target `GetDead` (`vtbl+0x94` → `CSWSCreature::GetDead 0x004EF820`) | `0x005B6A0C` | **release** (no attack action queued this round) |
| 4 | target `GetDeadTemp` (`0x004EF890`) | `0x005B6A1B` | **release** |
| 5 | **self** `GetDead` / `GetDeadTemp` | `0x005B6A2D` / `0x005B6A3C` | release (dead attackers stop) |
| 6 | **`CSWSObject::GetReputation(this, targetId) > 10`** (`0x0057CB80`, call at `0x005B6A46`, `cmp eax, 0xA / jle` at `0x005B6A4B`) | | **release** — a target that stops reading hostile is dropped |
| 7 | (arg2 only) planar distance² ≤ `MaxCleaveRange(targetId,0,1)²` (`0x004FFAD0`) | `0x005B6AB6` | release |

**Correction to the 7.4 note, and it matters:** the function does not release *only* on
"reads dead" — test 6 is a live reputation check, every round. It never fired in 7.4
because faction 0 kept the phantom's reputation at ≤ 10 permanently. It is the release
lever this design uses on purpose. There is still **no plot, min1hp or HP-value check**
anywhere in the function — Handoff is right about that part — so a min-1-hp hostile body
is released only by tests 1, 3/4 (dead) or 6 (reputation).

If the target survives validation, `AddAttackAction(round, targetId, 0, 0, 0x5DC, 1)`
(`0x004D38B0`) queues the next swing and the lock continues.

### 1.3 Acquisition of a NEW target

Only when `bMayAcquireNewTarget != 0`. Two attempts, both through
`CSWSCreature::GetNearestEnemy` (`0x004F2DE0`, `__thiscall`, `ret 0xC`):

```c
uint __thiscall CSWSCreature::GetNearestEnemy(
    CSWSCreature* this,   // whose area + array position the scan starts from
    float fMaxRange,      // best-distance seed; nothing farther is accepted
    uint  excludeId,      // never returned
    uint  vsId);          // if valid: hostility judged vs THIS id, distance from ITS position
```

1. `GetNearestEnemy(candidateCre, MaxCleaveRange(ownId,0,1), ownId, ownId)` — searched from
   the *candidate*'s position, "nearest enemy of mine, excluding me" (`0x005B6B1F`).
2. Fallback: `GetNearestEnemy(this, MaxCleaveRange(ownId,0,1), ownId, OBJECT_INVALID)`
   (`0x005B6B49`).

On success: `SetLockOrientationToObject(this, newId, 0)` (`0x004F34A0`), writes
`this+0x84 = newId` and `this+0x50C = newId` (attempted attack target — VERIFIED: the VM's
`GetAttemptedAttackTarget 0x00536860` reads `+0x50C`; `GetAttackTarget 0x00536800` reads
`+0x504`), `UpdateAttackTargetForAllAction(round, newId)` (`0x004D3E50`), `this+0x4E8 = 1`,
notifies the client GUI (`ServerToClientObjectId` → `CClientExoApp::SetLastTarget
0x005EDD70`), then queues the attack action. On failure: target cleared, `SetAnimation(0x2710)`
(pause), `SetCombatMode(this, 0, 1)` (`0x0050EE80`), `ClearAllSpecialAttacks` — the creature
stands down.

### 1.4 What `GetNearestEnemy` scans, per candidate — VERIFIED

The **area's x-sorted object-id array** (`CSWSArea+0x190` ids, `+0x194` count — the same
array `SetPosition` keeps sorted via `UpdatePositionInObjectsArray`): it finds its own index,
then walks outward in both directions at once, terminating each arm as soon as the
x-distance alone exceeds the best hit so far (`0x004F3144`/`0x004F341A`). Checks per
candidate, in order (first failure skips):

1. resolves via `GetGameObject`; not self; not `excludeId`;
2. **hostility**: if `vsId` valid → `GetAIStateReputation(candidateCre, vsId) == 2`
   (`0x0057CD90` — the vs-object's reputation *of the candidate* mapped to
   1 = friendly ≥ 90, 2 = hostile ≤ 10, 0 = neutral); else
   `GetAIStateReputation(this, candidateId) == 2`. **This is the engine's GetIsEnemy.**
3. is a creature; not `GetDead` (`vtbl+0x94`); not `GetDeadTemp`;
4. **perception**: `GetVisibleListElement(this, candidateId)` (`0x00517A20`) returns a node
   AND that node's flag byte has **bit 0 (seen)** set (`test byte [node+4], 1` at
   `0x004F2FC0`);
5. distance (candidate pos − anchor pos, minus both creatures' personal-space radii from
   `path_find_info +0x340 → +0x08`) beats the best so far;
6. line of sight: `CSWSArea::ClearLineOfSight` (`0x0050C330`) between the two positions
   raised by `[0x0073F414] = 1.5` (eye height).

So a candidate needs exactly three things: **hostile reputation, presence in the attacker's
visible list with the seen bit, and a clear line**. Health, plot, min1hp, commandable —
none of it is consulted at acquisition time.

---

## 2. The hostility test — where reputation values actually come from (Q1)

### 2.1 `CSWSObject::GetReputation` — `0x0057CB80`, thiscall `(targetId, int)`, ret 8 — VERIFIED

- Global override first: if `dword [0x007A2068] != -1`, that value is returned for
  everything (a debug lever; normally −1).
- Non-creature target → 50 (neutral). AOE objects on either side proxy to their creator
  (`AOE + 0x248`).
- `targetId == thisId` → 100.
- If this is a creature → **`CSWSCreature::GetCreatureReputation` (`0x00513FF0`)**;
  doors/placeables/triggers fall back to the plain faction matrix.
- Result clamped to 0..100; negative → 0.

### 2.2 `CSWSCreature::GetCreatureReputation` — `0x00513FF0` — VERIFIED

```c
myFaction     = this->stats(+0xA74)->faction_id(+0x78);     // read fresh, every call
if (this->inParty(+0xA88)) {
    if (target && target->inParty(+0xA88)) return 100;       // party loves party
    myFaction = 0;                                           // party member == PLAYER faction
}
if (target && target->inParty(+0xA88)) targetFaction = 0;
return CFactionManager::GetNPCFactionReputation(fm, targetFaction, myFaction);
```

### 2.3 The faction matrix — and the absence of personal reputation — VERIFIED

`CFactionManager::GetNPCFactionReputation` (`0x0052B420`) is a pure table lookup:
`matrix[N * factionB + factionA - 1]` with `N = fm+0x1C` (faction count), the int array at
`fm+0x20`, faction 0 (PLAYER) handled by an argument swap that selects the "vs player" row.
Out-of-range → 50. `fm = serverInternal + 0x10054`.

`CSWSCreature::AdjustReputation` (`0x00514070`) — the script `AdjustReputation` — does
**not** write anything per-creature: it reads the matrix entry for
(my stats faction, target faction) and writes it back `+delta` via
`SetNPCFactionReputation` (`0x0052B3D0`). **KOTOR 1 hostility is entirely
faction-vs-faction plus the in-party override. There is no per-creature reputation list to
manage, which is what makes the decoy design one write.** The script-side `GetReputation`
(`ExecuteCommandGetReputation 0x0053CED0`) calls the same `0x0057CB80`, so script AI and
engine retargeting cannot disagree.

### 2.4 The engine's own faction-change recipe — VERIFIED

`CSWSFaction::AddMember` (`0x005BFA70`, thiscall `(objectId, bSuppressClientUpdate)`):
removes the creature from its old faction's member-id list, adds it to the new one, sends
the client faction update, and writes **`dword [stats+0x78] = faction->id(+0xC)`**
(`0x005BFADB` — note: a **dword** write; `GetCreatureReputation` reads a dword too, so the
field is int-sized in practice even though the DB types it ushort).

`ExecuteCommandChangeByFaction` (`0x00546340`) shows the shipped pattern for changing the
faction of a creature that may be *in combat right now*: `AddMember(newFaction, id, 0)`,
then either `SetCombatState(0,1)` + `ClearAllActions(1)` + **`PacifyCreature`**
(surrender variant), or clear + `UpdateVisibleList(0)` (plain variant). That is the exact
combo §5 borrows.

### 2.5 `PacifyCreature` — the force-release — `0x004FD960`, thiscall, no args — VERIFIED

Walks the area object array; for every creature whose `+0x50C`, `+0x504`, `+0x524`,
`+0x528` or `+0x158` (last hostile actor — `SetLastHostileActor 0x004CC0C0`) equals *this*
creature's id: `SetCombatState(attacker, 0, 1)`, clears `+0x158` to `OBJECT_INVALID`, and
`ClearHostileActionsVersus(attacker, this)` (`0x004F61D0` — removes queued actions of type
`0xC`/`0xF`/`0x2E` whose target parameter names this creature). Callers in the shipped
game: `OnApplyDisappearAppear` and `ChangeByFaction`. The plugin already binds it
(`Odyssey.PacifyPhantom`).

---

## 3. Perception — how an enemy learns a candidate exists (Q2)

### 3.1 Where the list lives — VERIFIED

`CSWSCreature + 0x920` — `CExoArrayList<CSWVisibilityNode*>` (pointer array at `+0x920`,
count at `+0x924`; DB-named `perceptions`). Each node is 8 bytes:

```
+0x00  uint32  object_id
+0x04  byte    flags:  bit0 = SEEN    bit1 = HEARD
                       bit2/3 = extra state from DoStealthDetection
                       bit4 = saw-through-invisibility memory
```

Lookup: `GetVisibleListElement` (`0x00517A20`) — linear scan, returns the node.
Insert: `AddToVisibleList` (`0x0051B370`, 5 args: id, seen, 2-bit state, heard, invis-seen) —
allocates the node and appends. Remove: `RemoveFromVisibleList` (`0x00517960`).

### 3.2 Who inserts — the update cascade — VERIFIED

`CSWSCreature::SpawnInHeartbeatPerception` (`0x004EB6E0`, called per creature from the
server's creature-update path at `0x004FE30A`/`0x004FED0B`) runs, on randomized timers:

- **mode 1** (`UpdateVisibleList(1)`, `0x0051B050`): scans **only the client party's
  members** — the cheap frequent pass (interval `+0x35C` ≈ 300–700 ms for PC-flagged
  creatures, ≥ 4000 ms otherwise). *A phantom is not a party member and is invisible to
  this pass.*
- **mode 2 / 0** (`UpdateVisibleList(2)` for non-party perceivers, `(0)` for party ones):
  first re-runs `DoPerceptionUpdateOnCreature` against every existing node (dropping ids
  that no longer resolve), then scans the **area's x-sorted object array** inside a
  ±max(spot `0x004EFC70`, listen `0x004EFD20`) window (binary-searched via
  `GetFirstObjectIndiceByX 0x00506F20`) and calls `DoPerceptionUpdateOnCreature` for every
  in-range object not already listed. Mode 2 skips in-party candidates (mode 1's job);
  **it does not skip anything else — the phantom is scanned by this pass.**

`DoPerceptionUpdateOnCreature` (`0x00502AC0`, thiscall `(CSWSObject* other)`) rolls the
spot/listen contest (`DoStealthDetection 0x00500C00`), tests LOS
(`ClearLineOfSight`, eye height `1.5`), handles invisibility (`GetInvisible 0x00501950`),
and updates/creates the node. **At no point does it read faction, reputation, or
hostility** (VERIFIED — every call in the function body named). Neutral, hostile,
friendly: everyone in range is perceived alike.

### 3.3 The perception events scripts see — VERIFIED

On every seen/heard **transition** the function posts a `CScriptEvent` (ctor `0x004D7540`,
`SetInteger(0, kind)`, `SetObjectID(0, otherId)`) through
`CServerAIMaster::AddEventDeltaTime` (`0x004B08D0`) with event id `[0x007441DC] = 10`:
kind `2` = seen, `0` = heard, `3` = lost sight, `1` = lost hearing. This is what the
default AI's OnNotice script receives (`GetLastPerceived 0x0053B230` /
`GetLastPerception 0x0053B2A0`). Events are suppressed while `this+0xAA0` is set (the
initial post-spawn population pass) and fire only on transitions thereafter.

**Consequence for the design (INFERRED from the above, the deduction not itself live-tested):**
enemies within sight range already hold a `seen` node for the neutral phantom, so the
faction flip needs no perception work at all for `GetNearestEnemy` (it re-reads reputation
per scan). But the *script-visible* "I just saw an enemy" event fired while the phantom was
neutral and will not re-fire on the flip — an *idle* enemy whose aggression comes only from
its OnNotice script may not react until its heartbeat AI next evaluates targets. If live
testing shows sluggish first aggro, `RemoveFromVisibleList(enemy, phantomId)` on nearby
enemies forces a fresh insert-plus-seen-event on their next perception pass — engine calls
only, both already decoded. NEEDS-LIVE (§7.3).

---

## 4. The hit — observation choke point (Q3)

### 4.1 The resolution chain, attacker side — VERIFIED (shape), for context

`AIActionPhysicalAttacks` (`0x005BBBF0`, the queued attack action) →
`CSWSCreature::ResolveAttack` (`0x005BBA80`, thiscall, `ret 0x1C`; writes
`this+0x504 = targetId` at `0x005BBAD3`, picks melee vs ranged from the weapon's
`baseitems.2da` ranged byte) → `ResolveMeleeAttack 0x005BB890` / `ResolveRangedAttack
0x005BB590` → `ResolveAttackRoll 0x005BACA0` / `ResolveDamage 0x005BB440` → results stored
in the round's `CSWSCombatAttackData` (`round+0x004`, seven `0x14C`-byte slots).
`SetAttackResult` (`0x005B7400`) folds the raw attack-result code into `this+0x558`
(0 = hit family 1–3, 1 = miss family 4–6, 2 = parried family 8–10).
`ResolvePostMeleeDamage` (`0x005B8B00`) shows the effect plumbing: if the rolled damage
will kill, it pre-builds a `CGameEffect` of type `[0x007466C8] = 19` (Death) with
`SetCreator(attackerId)` (`0x00503A00` at `0x005B8BDE`) and parks it on the attack data;
the damage effect (type 38) rides the same channel and is applied at the swing's impact
frame.

**Everything lands in one place:**

### 4.2 `CSWSEffectListHandler::OnApplyDamage` — `0x004DFA40` — the choke point — VERIFIED

`__stdcall`, `ret 0xC` (registered for effect type 38 in the handler table built at
`0x004E4A50`; see `death-handling.md`):

```c
int __stdcall OnApplyDamage(
    CSWSObject* pTarget,     // [esp+4]  the creature being damaged
    CGameEffect* pEffect,    // [esp+8]  creator_id at +0x18 = the ATTACKER's object id
    int bGameLoading);       // [esp+C]  third arg; use unread on the paths decoded here (INFERRED name)
```

Body, decoded as far as this design needs:

- `0x004DFA7B`–`0x004DFAA8`: early-out if the target is already `GetDead` or `GetDeadTemp`
  — a dead phantom body absorbs stray splash harmlessly.
- `0x004DFAC0` loop: reads the 15 per-damage-type amounts `pEffect->GetInteger(0..0xE)`
  (`0x00503690`; slot `0xE` is the plain/universal slot, kept separately at `0x004DFAEA`);
  **`cmp [esi+0xF8], ebp` at `0x004DFAC8` is the plot test** Handoff cites — plot (or the
  global `[0x00832878]`) zeroes every component.
- Then resistances/immunities, `CSWSCreature::DoDamage 0x004F3830` →
  `CSWSObject::DoDamage 0x004CCF80` (the `+0xDC` HP write, min1hp clamp), floaty text, and
  the death decision at `0x004E0240` (`GetDead‖GetDeadTemp` → build Death effect type 19
  with `SetCreator(damager)`), all as documented in `death-handling.md`.

Why this is the right hook and not the resolve functions: it is **after** hit/miss, crits,
sneak-attack, resistances-independent raw amounts are final on the effect; it fires for
melee, ranged, grenades, mines and force powers alike; the attacker travels *with* the
event (`creator_id`); and a miss simply never arrives (misses are cosmetic for co-op — the
client's health is what must be right). If swing-by-swing hit/miss is ever wanted,
`ResolveAttack 0x005BBA80` (prologue `A1 FC 39 7A 00` — a single relocatable 5-byte
instruction) sees every attempt with `targetId` at `[esp+4]`; not needed for v1.

### 4.3 What the detour reads — VERIFIED offsets

```
target  = [esp+4]         (stub entered by jmp; +0x24/+0x28 after a pushad)
tId     = *(uint*)(target + 0x004)
effect  = [esp+8]
aId     = *(uint*)(effect + 0x018)      // creator_id — the attacker
ints    = *(int**)(effect + 0x034)      // per-type damage, count at +0x030 (15 here)
total   = Σ max(0, ints[i]) for i in 0..14
```

Filter first — `tId == phantomObjectId` — before touching anything else; this function runs
for every damage event in the game. Record `(aId, total, sequence)` exactly like
`AnimationHook` records `(anim, sequence)`; no engine calls, no allocation inside the hook.

### 4.4 Byte-level prologue for the verify-bytes-or-refuse detour — VERIFIED from disk

```
0x004DFA40:  6A FF                    push -1
0x004DFA42:  68 08 AA 71 00           push 0x0071AA08        ; SEH handler record
0x004DFA47:  64 A1 00 00 00 00        mov eax, fs:[0]
0x004DFA4D:  50                       push eax
0x004DFA4E:  64 89 25 00 00 00 00     mov fs:[0], esp
```

Verify the first **13 bytes** (`6A FF 68 08 AA 71 00 64 A1 00 00 00 00`); relocate the
first **7** (two instructions, no position-dependent bytes — the `68` immediate is
absolute); write `E9 rel32` + 2 × `90` at `0x004DFA40`; resume at `0x004DFA47`. Same shape
as `AnimationHook.Install` (`src/KotorCoop.Plugin/AnimationHook.cs`), which relocates the
identical SEH idiom minus the handler push. `pushad` shifts the stack args by `0x20`, so the
stub reads target at `[esp+0x24]`, effect at `[esp+0x28]`.

---

## 5. THE DESIGN — phantom as decoy target (Q4)

Current phantom state (VERIFIED in `Odyssey.MarkAsRemotePlayer`, `Odyssey.cs:3079`): plot
cleared, `min1hp +0x200 = 1`, `dead_selectable +0xF4 = 0`, attack bit (`+0x9F0` mask 4)
cleared so it can never swing, faction **forced to 5 (Neutral)**, `PacifyPhantom` bound and
called when the peer leaves combat or goes down; `PhantomController` mirrors the peer's
death as a pose (`0x2716/0x2718/0x27AC`) plus a pacify, and stands it back up on revive.

### 5.1 Make it targetable — the state machine

All writes on the game thread, from the frame hook, by resolved pointer-of-the-frame:

**Peer alive and fighting (or simply present):**

```
faction := 0 (PLAYER)         // via CSWSFaction::AddMember(playerFaction, phantomId, 0):
                              //   fm = *(serverInternal + 0x10054)
                              //   f  = CFactionManager::GetFaction(fm, 0)   // 0x0052B3B0
                              //   CSWSFaction::AddMember(f, phantomId, 0)   // 0x005BFA70
```

That is the entire targetability change. Hostile factions read ≤ 10 against faction 0
(identically to the real player), so: `GetAIStateReputation == 2` → `GetNearestEnemy`
eligible (§1.4); `GetReputation ≤ 10` → retention test 6 passes (§1.2); the same value
feeds every script-AI reputation query (§2.3). Perception needs nothing (§3). Keep
`min1hp = 1`, keep plot off, keep the attack bit off — none of them are read by targeting.

Prefer `AddMember` over the raw `stats+0x78` poke (engine-write rules: it maintains the
faction member lists and performs the engine's own **dword** write; the plugin's existing
ushort write at `Odyssey.cs:3089` works only because the upper half is already zero —
tighten that while in there). Accepted side effect: the phantom joins the PLAYER faction's
member list, so script loops over `GetFirstFactionMember(player)` can now see it —
assessed low-risk (K1 module scripts rarely iterate the player faction) but it is the one
behavioral surface the raw poke would avoid; decide after live test §7.6. INFERRED.

**Peer's character dies (HP ≤ 0 arrives on the wire):** release everything, keep the body.

```
1. faction := 5 (Neutral) via AddMember          // retention test 6 now fails next round
2. PacifyCreature(phantom)                        // 0x004FD960 — immediate release of every
                                                  // attacker + queued hostile actions
3. existing death pose path runs as today
4. (optional hardening, NEEDS-LIVE §7.5)
   min1hp := 0;  SetCurrentHitPoints(phantom, 0)  // 0x004CC280 — GetDead now true, so
                                                  // release tests 3 and GetNearestEnemy's
                                                  // dead-skip also engage, and OnApplyDamage
                                                  // early-outs on the corpse
```

**Peer revives:** restore in reverse — `min1hp := 1`, `SetCurrentHitPoints(phantom, 1)` (or
the mirrored HP), faction back to 0 via `AddMember`, existing stand-up path.

**Peer leaves combat / disconnects / module change:** faction back to Neutral +
`PacifyCreature` — exactly the current `PacifyPhantom` call sites, plus the faction word.

### 5.2 Why this cannot become an aggro sink — the proof against §1.2

The 7.4 sink required an attacker for whom *every* release test failed forever. Walk the
table for this design's worst case (enemy locked on, peer just died):

| Release path | 7.4 phantom | This design |
| --- | --- | --- |
| §1.2 test 1 (target gone) | fails (body persists) | same — body persists, by design |
| §1.2 tests 3/4 (`GetDead`/`GetDeadTemp`) | **never true** (plot + min1hp, HP pinned) | true if the optional HP write ships; otherwise still false — but irrelevant, see next row |
| §1.2 test 6 (`GetReputation > 10`) | **never true** (faction 0 forever) | **true within one combat round of death** — faction back to Neutral reads 50 |
| `PacifyCreature` | never called | called at the death edge and on every leave-combat edge (already shipped behavior) |
| `EndCombatRound` dead-target pre-clear (`0x004D47BD`) | never | engages with the optional HP write |
| Re-acquisition after release | n/a (never released) | `GetNearestEnemy` hostility test reads the restored Neutral faction → phantom not eligible; with the HP write, additionally skipped as dead |

Three independent levers (reputation, pacify, dead) where 7.4 had zero. The failure mode
that remains is a *stale state machine* — the owner never learning the peer died — which is
a transport-liveness problem, not an engine one; the enemy channel already carries peer HP
every snapshot.

One deliberate asymmetry: while the peer is **alive**, an enemy's lock on the phantom
releases only when the enemy or phantom dies or the fight ends — which is precisely the
wanted behavior (it *is* a real target now), and the client player, unlike 7.4's decoration,
is fighting back through the damage forward.

### 5.3 The hit flow, end to end

```
owner:  OnApplyDamage detour (§4)               — target==phantom → (attackerId, total, seq)
   →    wire: PhantomHit { attackerId, damage, seq }   (reliable channel, like FlagOp)
client: VM pattern already used by KillCreature/EffectResurrection:
            eff = ExecuteCommandEffectDamage(total, DAMAGE_TYPE_UNIVERSAL…)  // 0x00531AE0
            ApplyEffectToObject(DURATION_TYPE_INSTANT, eff, localPlayerId)   // 0x0052E570
```

Applying through the VM's `EffectDamage` is the **safest engine call** for "hurt my real
player": it runs the full shipped pipeline — resist/immunity on the receiving side, the
`+0xDC` write with the party difficulty scaling, floaty text, and, at ≤ 0, the engine's own
temp-death for a party member (`death-handling.md` §1) — no derived state to rebuild by
hand. Two refinements:

- **Attacker attribution (optional):** the enemy's object id is identical on both
  instances (VERIFIED live, enemy channel). Dispatching the VM call with `OBJECT_SELF`
  redirected to the local mirrored enemy (the pattern `PickUpItem` documents) makes
  `GetLastHostileActor`/`GetLastAttacker` on the client name the right creature. NEEDS-LIVE
  that the VM caller propagates into `creator_id` on this path.
- **The swing visual (optional):** the mirroring instance's enemies are action-starved by
  presence sync, so the client sees damage arrive without a swing. Forward the owner-side
  attack animation through the existing `AnimationHook`-style apply
  (`AddFireForgetAnimation` on the mirrored enemy's twin, guarded by the `0x0074F5F8`
  vtable check) keyed off the same `PhantomHit` event. Cosmetic; ship after the mechanics.

Double-damage cannot happen: on the client, local enemy AI is disarmed (presence sync) and
mirrored enemies never resolve attacks, so the forwarded damage is the only path into the
client player's HP. The owner-side phantom HP does drop too (floored at 1 by min1hp) —
harmless, and the phantom's health is not authoritative for anything; the peer's true HP
keeps arriving on the wire as today.

### 5.4 What this deliberately does not do

- No changes to `CreateNewAttackActions` or any engine code path — the design is entirely
  data (faction word, HP) plus one read-only detour.
- No threat *shaping*: enemies split between the owner and the phantom purely by the
  engine's nearest-hostile rule. Good enough for v1; a threat ledger remains option (b).
- Misses are not forwarded (§4.2). The client sees enemy swings (once the visual forward
  ships) and takes damage only on real hits.

---

## 6. Crash-risk register

| # | Risk | Severity | Mitigation |
| --- | --- | --- | --- |
| 1 | `OnApplyDamage` detour runs on **every** damage event in the game; a slow or faulting callback stalls or kills combat globally. | High | AnimationHook discipline: filter to `target id == phantom id` first, record two words, no engine calls, no allocation, `try/catch` that never unwinds into the engine. Verify all 13 prologue bytes or refuse to hook. |
| 2 | The detour reads `effect+0x34` (integer list). A malformed/foreign effect with a short list would over-read. | Medium | Bound the sum loop by `min(15, *(int*)(effect+0x30))`; null-check the pointer. |
| 3 | Faction flip while an enemy is mid-swing: the current round completes against the phantom even after release (release is evaluated at round end, §1.1). | Low — one extra swing lands on a body that clamps at 1 HP or reads dead. | Accept; `PacifyCreature` also strips queued actions immediately. |
| 4 | Raw ushort write to `stats+0x78` vs the engine's dword read (`0x00513FF8`/`0x005BFADB`). | Medium if upper bytes ever non-zero (faction id becomes garbage → reputation 50 → silent no-op). | Use `CSWSFaction::AddMember`, or at minimum a dword write. Fix the existing ushort write in `MarkAsRemotePlayer` at the same time. |
| 5 | `AddMember` on a stale phantom pointer after module unload. | High — wild dereference. | Same epoch/invalidate rules as every other phantom write: resolve by id each frame, `PhantomController.Invalidate()` on module change, never cache across frames. |
| 6 | Phantom in the PLAYER faction's member list is baked into a save. | Medium (save pollution — the 4.8 class of bug). | Pre-store flush already despawns phantoms; ensure the flush runs *before* `SaveGame` serializes factions, and that despawn removes the member-list entry (NEEDS-LIVE §7.6 — `CSWSFaction::AddMember` back to Neutral on despawn is the belt-and-braces). |
| 7 | Optional HP ≤ 0 write: a dead-reading phantom might trigger corpse logic (decay, loot GUI on click, `dead_selectable`). | Medium | `dead_selectable +0xF4` is already 0; K1 corpse fade is script-driven per-creature and the phantom has no scripts (INFERRED). Gate this write behind its own live test (§7.5); the design works without it. |
| 8 | Applying forwarded damage on the client while the client PC is in a conversation/cutscene. | Low–Medium | Reuse the existing dialogue gate (skip/queue `PhantomHit` application while the input class reads 3), mirroring the enemy channel's dialogue freeze. |
| 9 | Feedback loop: forwarded damage drops client HP → HP change streams back → owner re-mirrors phantom pose/health. | Low — the phantom's HP is not written from the wire today. | Keep it that way; only the death/revive edges act. |
| 10 | `SetLastTarget` GUI notify (§1.3) runs when an enemy acquires the phantom on the owner — harmless, but the owner's "last target" reticle may flick. | Cosmetic | Accept. |

---

## 7. NEEDS-LIVE checklist

1. **The flip works at all:** spawn phantom, set faction 0 (AddMember path), stand it near
   a hostile already fighting the owner. Prediction: within one combat round the enemy's
   `round+0x9CC` (or `+0x50C`) reads the phantom's id and swings connect (floaty text on a
   min-1-hp body). Log `GetReputation(enemy, phantom)` before/after: 50 → ≤ 10.
2. **The release works:** mid-fight, flip back to Neutral + `PacifyCreature`. Prediction:
   attacker drops the phantom at the current round's end (test 6), acquires the real player
   or stands down; `round+0x9CC` never re-acquires the phantom. This is the direct
   anti-7.4 measurement — soak it for minutes, not seconds.
3. **Cold-aggro latency (§3.3):** phantom flipped hostile near *idle* enemies (nothing else
   in combat). Measure time to first attack. If sluggish, test the
   `RemoveFromVisibleList` kick and re-measure.
4. **The detour:** verify the 13 bytes on the installed GOG binary, hook, fight, and diff
   the detour's `(attacker, total)` stream against the phantom's observed HP deltas and the
   owner's combat log. Confirm zero events while no phantom is present costs nothing
   measurable.
5. **Dead-reading phantom (§5.1 step 4):** with min1hp cleared and HP 0 — does the body
   attract clicks, decay, or `TempDeathUpdate` interference (it should not: not in party)?
   Does `GetNearestEnemy` verifiably skip it? Does standing it back up (min1hp, HP, pose)
   leave `+0x9F0`/`+0x19C` sane, given `OnApplyDeath` never ran on it?
6. **Faction member-list hygiene:** save a game with a faction-0 phantom present (after the
   pre-store flush) and inspect the save's faction lists; iterate
   `GetFirstFactionMember(player)` from a console script with the phantom alive.
7. **Client apply:** forwarded `EffectDamage` on the client PC — difficulty scaling
   matches expectation, death at ≤ 0 enters temp-death cleanly, and the VM-caller redirect
   does or does not populate `creator_id` (decides whether attacker attribution ships).
8. **Two-sided fight:** owner and client both fighting the same mirrored group — confirm
   the client attacking INTO the fight (existing path) and the new threat path do not
   fight over enemy HP mirroring.

---

## 8. Key addresses

### Targeting and combat round (server)

| Address | Symbol | Conv / stack | Note |
| --- | --- | --- | --- |
| `0x005B6980` | `CSWSCreature::CreateNewAttackActions` | thiscall, 12 | §1; single caller `0x004D4B6E` |
| `0x004D4620` | `CSWSCombatRound::EndCombatRound` | thiscall, 4 | round-end retarget driver; dead-target pre-clear at `0x004D47BD` |
| `0x004F2DE0` | `CSWSCreature::GetNearestEnemy` | thiscall, 12 | `(float range, uint excludeId, uint vsId)`; x-sorted outward scan |
| `0x004FFAD0` | `CSWSCreature::MaxCleaveRange` | thiscall, 12 | range seed for acquisition |
| `0x004D38B0` | `CSWSCombatRound::AddAttackAction` | thiscall, 20 | `(targetId, 0, 0, 0x5DC, 1)` |
| `0x004D3E50` | `CSWSCombatRound::UpdateAttackTargetForAllAction` | thiscall, 4 | |
| `0x005B7400` | `CSWSCreature::SetAttackResult` | thiscall, 4 | folds result → `+0x558` (0 hit / 1 miss / 2 parry) |
| `0x005BBA80` | `CSWSCreature::ResolveAttack` | thiscall, 0x1C | writes `+0x504`; alternate swing-observer hook (5-byte relocatable prologue) |
| `0x005BBBF0` | `CSWSCreature::AIActionPhysicalAttacks` | thiscall, 4 | also calls `GetNearestEnemy` (`0x005BC0A1`) |
| `0x005B8B00` | `CSWSCreature::ResolvePostMeleeDamage` | thiscall, 4 | pre-builds Death effect type `[0x007466C8]=19`, `SetCreator` at `0x005B8BDE` |
| `0x004FD960` | `CSWSCreature::PacifyCreature` | thiscall, 0 | force-release; callers: `OnApplyDisappearAppear`, `ChangeByFaction` |
| `0x004F61D0` | `CSWSCreature::ClearHostileActionsVersus` | thiscall, 4 | strips queued actions naming the target |
| `0x004CC0C0` | `CSWSObject::SetLastHostileActor` | thiscall, 8 | `+0x158` |
| `0x004F34A0` | `CSWSCreature::SetLockOrientationToObject` | thiscall, 8 | |
| `0x0050EE80` | `CSWSCreature::SetCombatMode` | thiscall, 8 | `(0,1)` = stand down |
| `0x004F2610` | `CSWSCreature::SetCombatState` | thiscall, 5 | |
| `0x004FDAA0` | `CSWSCreature::CancelCombat` | thiscall | clears `round+0x9CC` |

### Reputation / faction

| Address | Symbol | Conv / stack | Note |
| --- | --- | --- | --- |
| `0x0057CB80` | `CSWSObject::GetReputation` | thiscall, 8 | THE hostility source; override global `0x007A2068` (−1 = off) |
| `0x0057CD90` | `CSWSObject::GetAIStateReputation` | thiscall, 4 | → 1 friendly ≥90 / 2 hostile ≤10 / 0 neutral |
| `0x00513FF0` | `CSWSCreature::GetCreatureReputation` | thiscall, 8 | stats faction + in-party (+0xA88) overrides |
| `0x0052B420` | `CFactionManager::GetNPCFactionReputation` | thiscall, 8 | matrix `[N*b + a − 1]`; N at `fm+0x1C`, array at `fm+0x20` |
| `0x0052B3D0` / `0x0052B3B0` / `0x0052B280` | `SetNPCFactionReputation` / `GetFaction` / `GetIsNPCFaction` | thiscall | `fm = serverInternal + 0x10054` |
| `0x00514070` | `CSWSCreature::AdjustReputation` | thiscall, 8 | writes the matrix — faction-wide, not per-creature |
| `0x005BFA70` | `CSWSFaction::AddMember` | thiscall, 8 | the engine faction change: lists + **dword** `stats+0x78` |
| `0x00546340` | `ExecuteCommandChangeByFaction` | thiscall | the shipped change-faction-in-combat pattern (AddMember + pacify / vis refresh) |
| `0x0053CED0` | `ExecuteCommandGetReputation` | thiscall | script → `0x0057CB80` (same source) |

### Perception

| Address | Symbol | Conv / stack | Note |
| --- | --- | --- | --- |
| `0x004EB6E0` | `CSWSCreature::SpawnInHeartbeatPerception` | thiscall, 4 | timers `+0x35C` (party pass) / `+0x388` (full pass), heartbeat `+0x358` |
| `0x0051B050` | `CSWSCreature::UpdateVisibleList` | thiscall, 1 | mode 1 party-only / 0 full / 2 full-minus-party |
| `0x00502AC0` | `CSWSCreature::DoPerceptionUpdateOnCreature` | thiscall, 4 | **no faction filter**; posts event id `[0x007441DC]=10`, kinds 2/0/3/1 |
| `0x0051B370` / `0x00517960` / `0x00517A20` | `AddToVisibleList` / `RemoveFromVisibleList` / `GetVisibleListElement` | thiscall | list at `+0x920/+0x924`; node `{id, flags}` — bit0 seen, bit1 heard |
| `0x00500C00` | `CSWSCreature::DoStealthDetection` | thiscall, 20 | spot/listen contest |
| `0x004EFC70` / `0x004EFD20` | `GetSpotCheckDistance` / `GetListenCheckDistance` | thiscall, 0 | scan window radius |
| `0x0050C330` | `CSWSArea::ClearLineOfSight` | thiscall, 32 | eye height `[0x0073F414] = 1.5f` |
| `0x00506F20` | `CSWSArea::GetFirstObjectIndiceByX` | thiscall, 8 | binary search into the x-sorted array |

### Damage funnel

| Address | Symbol | Conv / stack | Note |
| --- | --- | --- | --- |
| `0x004DFA40` | `CSWSEffectListHandler::OnApplyDamage` | **stdcall, 12** | **the hook** — `(target, effect, int)`; prologue §4.4; plot test `0x004DFAC8` |
| `0x00503690` / `0x00503A00` | `CGameEffect::GetInteger` / `SetCreator` | thiscall, 4 | creator at `effect+0x18` |
| `0x004F3830` / `0x004CCF80` | `CSWSCreature::DoDamage` / `CSWSObject::DoDamage` | thiscall, 8 | the `+0xDC` write, min1hp clamp |
| `0x00531AE0` | `ExecuteCommandEffectDamage` | thiscall | client-side apply, via the existing VM pattern |
| `0x0052E570` | `ExecuteCommandApplyEffectToObject` | thiscall | |

### Offsets

```
// CSWSCreature (server; CSWSObject embedded at +0)
0x084  written alongside +0x50C on acquisition (companion target word)
0x158  last_hostile_actor            (SetLastHostileActor; pacify clears)
0x504  attack target                 (script GetAttackTarget; written by ResolveAttack)
0x50C  attempted attack target       (script GetAttemptedAttackTarget; written on acquisition)
0x524  attempted target (alt slot)   (validated/cleared by EndCombatRound; pacify checks)
0x528  attempted spell target        (pacify checks)                    [INFERRED name]
0x558  last attack result summary    (0 hit / 1 miss / 2 parried)
0x920  perceptions  CExoArrayList<CSWVisibilityNode*>   (+0x924 count)
0x9C8  combat_round  CSWSCombatRound*
0x9D4  is_pc (object-side; −10 dead window)
0xA74  stats  CSWSCreatureStats*
0xA88  in-party flag (reputation override)
0xAA0  perception-events-suppressed flag
0xAA4  combat reaction timer (6000 on perceiving a PC; −1 once attacking)

// CSWSCreatureStats
0x06C  is_pc
0x078  faction_id — dword in engine reads/writes; THE targetability lever

// CSWSCombatRound
0x938  use-attempted-target flag (selects +0x524 as retarget candidate)
0x9B0  actions  CExoLinkedList<CSWSCombatRoundAction>*
0x9B4  owner creature (back-pointer)
0x9CC  current attack target — the field the retarget logic reads
       (set: StartCombatRound 0x004D60A8; cleared: OnApplyDeath 0x004E0D05,
        SetCombatState 0x004F273D, CancelCombat 0x004FDAE2, EndCombatRound 0x004D47C3,
        CreateNewAttackActions 0x005B6AC2, CServerExoApp::ClearAllActions 0x004AEF73)

// CSWVisibilityNode (8 bytes)
0x000  object_id
0x004  flags: bit0 SEEN (GetNearestEnemy requires) · bit1 HEARD · bit4 invis-seen memory

// CGameEffect
0x008  type (19 Death, 38 Damage)    0x018  creator_id
0x030  num_integers                  0x034  integer_list (damage: 15 slots, 14 = universal)
```

### Constants and globals

```
0x007A2068  int   global reputation override; −1 = disabled (anything else wins everywhere)
0x00832878  int   global damage-zeroing flag checked beside plot (bss, 0 at rest)
0x0073F414  1.5f  perception / LOS eye height
0x007441DC  10    AI script-event id for perception (kinds: 2 seen, 0 heard, 3 lost-sight, 1 lost-hearing)
0x007466C8  19    effect type pre-built by ResolvePostMeleeDamage (Death)
reputation bands: ≤10 hostile · 11–89 neutral · ≥90 friendly (0x0057CDE1..0x0057CDF6)
factions: 0 PLAYER · 5 Neutral (repute.2da; Handoff 7.4)
0x7F000000  OBJECT_INVALID
```

---

## Verification tags — summary

- **VERIFIED** (instruction stream read for this doc): all of §1 (`CreateNewAttackActions`
  full decode, its single caller, `EndCombatRound`'s pre-clear and argument wiring,
  `GetNearestEnemy` full decode including the seen-bit perception gate and LOS); all of §2
  (`GetReputation`, `GetAIStateReputation` bands, `GetCreatureReputation`, the faction
  matrix and its writers, `AddMember`'s dword faction write, `ChangeByFaction`'s
  change-then-pacify pattern, `PacifyCreature`'s five target fields and action stripping);
  §3's list layout, node flags, update cascade modes, the absence of any faction filter in
  `DoPerceptionUpdateOnCreature`, and the perception event ids; §4's `OnApplyDamage`
  signature, early-outs, plot test at `0x004DFAC8`, damage-integer loop, prologue bytes,
  and `ResolvePostMeleeDamage`'s creator-carrying effect; every address, offset and
  constant in §8 (constants read from the disk image).
- **INFERRED**: `OnApplyDamage`'s third argument name; `+0x528` = attempted spell target;
  the cold-aggro latency mechanism (§3.3/§5.4) — the event plumbing is verified, the
  script-side consequence is deduced; default AI scripts choosing targets via
  reputation-backed routines (they demonstrably attack the real player, and every such
  routine funnels through `0x0057CB80`, but the .ncs files were not decompiled here);
  save-pollution and player-faction-member-list side effects of `AddMember`.
- **NEEDS-LIVE**: everything in §7. Nothing in this design should be built before items
  1–2 (flip and release) are measured, and nothing should ship before item 2 has soaked.
