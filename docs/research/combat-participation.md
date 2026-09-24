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

---

## 9. `combat_state_sync` retired (2026-08-27, roadmap-seamless.md Tier 2 item 17)

Re-investigated once more, per this item's own instruction, whether a cheaper surface than the
raw `CSWSCreature+0x4E0` dword poke exists for "this client's engine is genuinely in combat."
Two questions, both answered.

**Does `local_combat`'s own release path already set it for the excluded creature?** No, and it
does not need to: `MaintainLocalFight`'s three release paths (dead/gone, disengaged, timed out)
only ever clear `_locallyFightingId` and the census exclusion — they never touch `+0x4E0`
directly, on purpose. They do not need to, because arming the fight in the first place
(`StartLocalFight` → `Odyssey.FireCreatureScriptSlot0x260`) already gives the local player's own
engine a REAL combat round against a REAL attacker, and the field is derived from that, not from
anything this codebase writes.

**Does the engine expose a cheaper `EnterCombat`/`SetCombatMode`-style routine `tools/kotor1_0_3.db`
names?** Searched via `tools/q.py func` for `Combat`, `EnterCombat`, `SetCombatMode`,
`ActionAttack` and `DetermineCombat`. Nothing named `EnterCombat` or `DetermineCombat*` exists in
the database at all. `CSWSCreature::SetCombatMode` (`0x0050EE80`) does exist and IS called
elsewhere in this codebase (`combat-participation.md` §1.3's own acquisition-failure path,
`SetCombatMode(this, 0, 1)` — "stand down") — disassembled fresh for this item
(`tools/disasm.py 0x0050EE80`) and confirmed it is the WEAPON-DRAW/animation-mode switch (reads
the equipped item at `this+0xA2C` slot `0x10`, dispatches on a small mode enum 0/1/5/6/…), an
entirely different field from `+0x4E0` — **not** a cheaper in-combat setter, just a
same-named-sounding function for a different concept. No alternative engine call exists;
`SetCombatState`/the raw dword write were always the only two writers of `+0x4E0`
(`SetCombatState` itself, and the second writer inside `ProcessPendingCombatActions` at
`0x004F7C0E` §8 already documents), and the second one is exactly the DERIVED write this section
has always said cannot be asserted from outside.

**The real fix — described in `CombatStateController`'s own doc comment since it was written —
has since shipped and is live-verified to work, with zero help from `combat_state_sync`.**
`docs/research/shared-state-audit.md` round 39 (2026-08-21) is `local_combat`: pull the one
genuinely engaging enemy out of the census and fire its own event script through the engine's
real dispatcher, so the creature runs an actual native combat round against the local player
instead of a puppet with no reason to fight. Round 52 (same document, same day) is the direct
proof this item asked for: with `combat_state_sync` never enabled in that session, a live
two-instance test recorded

    combat: absorbed 2 forwarded damage in total, attributed to mirrored enemies we are
      driving (last attacker 0x3B); this engine says in-combat=True
    combat ground-truth: local PC took 2 damage, attacker=0x3B (the currently
      locally-fighting creature (StartLocalFight's own id))

— `Odyssey.IsPlayerInCombat` reading true on the CLIENT's own engine, purely as a side effect of
`local_combat`'s real fight already running, confirmed against a ground-truth damage hook (not
inferred from HP). This is exactly the "does the client's own `+0x4E0` in-combat flag rise as a
result" question §7 left open, and it is now answered: yes, by the mechanism this section always
said was the real fix, with no poke involved.

**Decision: retired, not fixed.** `combat_state_sync` is now a no-op — `CombatStateController`
performs no engine call and holds no state; it exists only so the key keeps parsing without a
config-loader error, and logs one deprecation line the first frame it is found set. The dword
write it used to perform (`Odyssey.SetPlayerCombatState`, `SetCombatStateFn` at `0x004F2610`) is
kept, unused, documented, in case a future round needs the raw setter again for something other
than "hold the field on a timer" — the same precedent `Odyssey.QueueAttackAction` set when IT was
retired by the same round 39. `IsPlayerInCombat` (the READ side, `+0x4E0`) is unaffected and stays
in active use by `IncomingDamageController`'s own watchdog log — this decision only retires the
WRITE side.

## 10. Round 89 (2026-08-28): root-caused, live — `end_sith2` never reads hostile to the
## CLIENT's own local player; the phantom-decoy design and `LocalFightCandidateRule` are not at
## fault

Playthrough open issue #1 (`docs/research/playthrough-endar-spire.md`): "a client cannot land real
hits on a host-owned/GIT-identical hostile via the local `attack` mechanism". Reproduced with the
playthrough's own exact staging (fresh rig, save 1, `END_M01AA`, CLIENT `tp 45.8 33.3` — ~1m from
`end_sith2` 0x14, hp 15, GIT-placed, `census` confirms 25/25 census entries resolve directly on the
client, i.e. all GIT-identical, none materialised) and confirmed the exact same symptom: `attack
0x7FFFFFFF 0x14` (via `tools/blockpoke.ps1 attack`, since `attack` is a blockpoke-only top-level
verb, not an in-game `console` command — a wrinkle worth recording, since typing `console 'attack
...'` answers `unknown command`) produces `test: 0x7FFFFFFF ordered to attack 0x14` and the probe's
own `round(active=0,delay=0)` on both sides forever; no damage; no `local_combat:` line ever
appears in the client's plugin log this session.

**Two of the three `LocalFightCandidateRule.Evaluate` gates are DIRECTLY RULED OUT, with live
evidence, not inference:**

1. **`hasScript` — CONFIRMED present, not the round-39-class empty-slot bug.**
   `firescript 0x14` (peek only) answers `0x14 +0x260 = 'k_def_combend01'` — a real, non-empty
   script, unlike `tar03_bekcs2`'s documented empty slot (§Odyssey.Combat.ScriptFire.cs's own doc
   comment). `Odyssey.HasCreatureScriptSlot0x260` must read true for this creature.
2. **`gitIdentical` — not actually a live variable in this code path at all.**
   `EnemyPresenceController.SelectImmediateCandidate` calls `LocalFightCandidateRule.Evaluate(...,
   gitIdentical: true, ...)` as a **literal constant**, not a computed value — every census entry
   reaching this call has already survived the `MaterialisedEnemies.ToLocal(enemy.ObjectId) != 0`
   skip a few lines above, so by construction every candidate considered here IS GIT-identical.
   There is no live gate to fail here; ruling this out needed reading the code, not the rig.

**The remaining gate, `reputation == Hostile (2)`, is where this round's new evidence points, and
it was confirmed with a mechanism nobody had tried yet: firing the creature's own real AI script
directly and watching whether the ENGINE's own `DetermineCombatRound`/`GetNearestEnemy` (not our
code at all) can find the client's own player as a target.**

`firescript 0x14 fire` (on the CLIENT, player still ~1m away) runs `k_def_combend01` for real
through the same VM dispatcher the engine itself uses (`Odyssey.FireCreatureScriptSlot0x260`) —
and `Odyssey.ArmFireScriptWatch`'s per-frame diagnostic (already shipped, not new this round) shows
the aftermath directly: `round+0x9CC target=0x7F000000` (**`OBJECT_INVALID` — never acquired**),
`round+0x958 timer=0` (**never armed**), `resolvedTarget=0x0`, held steady for the whole watch
window. The client's own hp stayed `12/12` throughout. **This is the creature's OWN native AI,
using the SAME `GetNearestEnemy`/hostility path §1/§2 of this document already decoded, failing to
find the client's real, un-mirrored, un-phantomed local player as a hostile candidate at 1 meter —
not a `LocalFightCandidateRule` bug, not a script-slot bug, not a distance bug.** Per §1.4's own
decode, the only three things `GetNearestEnemy` checks are hostile reputation, a seen perception
node, and line of sight — and reputation is the one this codebase cannot itself write or observe
for this pairing (there is no console-reachable `GetAIStateReputation` probe today; the one
existing reputation diagnostic, `Odyssey.Combat.Attack.cs`'s "queueattack ROUND18 diag" line, lives
inside `QueueAttackAction`, the OPPOSITE flow — driving an enemy to attack the phantom — and never
fires for the player's own outgoing `attack`/`AttackTarget` path, which is why it never appeared in
this test).

**Verdict: root-caused as staging/engine-state, not a rule gap — per the task's own decision tree,
this is case two ("the enemy was hostile to the host only"), now with proof rather than a guess.**
`end_sith2` is a GIT-placed hostile the HOST's own instance fights normally (the playthrough's own
`end_gensith` kill and general Sith-patrol combat confirm real Sith DO fight the host's real
player), but on THIS session's CLIENT instance, at least this one creature's own engine-native
hostility check does not find the client's own player hostile — independent of anything
`LocalFightCandidateRule`, `EnemyPresenceController`, or any other code in this repo controls,
since the test above bypassed all of it and drove the creature's real AI directly. Because
reputation is a **faction-vs-faction matrix lookup** (§2.3), not a per-creature value, and factions
are never wire-synced between host and client (each engine runs its own independent world), the
likely mechanism is a per-instance asymmetry in whatever makes Endar Spire's early Sith troopers
hostile in the first place (a proximity/story trigger the HOST's own player crossed but the CLIENT
never did, in this specific session) — **not proven this round** (the exact trigger was not
traced), but consistent with every fact gathered. **Not fixed this round** — no code changed;
`LocalFightCandidateRule`/`EnemyPresenceController` need no test added, since nothing in either was
shown to be wrong.

**What would close this out, ranked:** (1) add a temporary reputation diagnostic to
`EnemyPresenceController.SelectImmediateCandidate` itself (mirroring `Odyssey.Combat.Attack.cs`'s
own `GetAIStateReputationFn` call, just logged for the CANDIDATE-selection path instead of the
opposite one) so a live pass can read `GetAIStateReputation(0x14, clientPlayerId)` directly instead
of inferring it from a script-fire side effect; (2) repeat this exact `firescript ... fire` probe on
the HOST against the SAME creature id (0x14) at the same range, as the clean side-by-side control
this round's time budget did not allow — if the host's own native AI DOES find its own player
hostile against the identical creature, that is decisive confirmation of a per-instance asymmetry
rather than a genuinely-non-hostile GIT placement; (3) trace whatever NWScript (a heartbeat,
`OnPerceive`, or a nearby trigger) is meant to make Endar Spire's Sith troopers hostile in the
first place, since vanilla KOTOR 1 does not require console intervention for this to work in a
normal single-player game.

**A workflow note for whoever continues this**: `attack <attackerId> <targetId>` is driven through
`tools/blockpoke.ps1`'s own dedicated `attack` top-level command
(`pwsh tools\blockpoke.ps1 -ProcId <pid> attack 0x7FFFFFFF 0x14`), **not** `console 'attack ...'` —
there is no `attack` verb in `ConsoleCommand.cs`'s table at all; blockpoke's `attack` writes
directly to the `TestAttacker`/`TestAttackTarget`/`TestAttackSerial` shared-block fields
`TestDriveController.HandleAttack` reads. A future pass grepping `ConsoleCommand.cs` for `attack`
and finding nothing should not conclude the primitive does not exist.

## Live pass (2026-08-28): ROOT-CAUSED with exact reads — `end_sith2` (0x14) is a FRIENDLY faction
## in its GIT starting state; this was never a client/host asymmetry at all

Task: settle round 89's own open question with exact reads instead of an inferred guess. Built the
`faction <id|me> [vsId|me]` read-only diagnostic verb this pass needed and round 89 itself
recommended (`SharedBlock.CheatKindFaction = 38`; see `galaxy-map-and-party-select.md`'s own
2026-08-28 entry for the build details — it lives there since that doc owns this session's other
new-verb bookkeeping). Ran round 89's own ranked follow-up items 1 and 2 together, on a fresh rig,
same staging (`end_m01aa`, `tp 45.8 33.3`, ~1m from `end_sith2` / object id `0x14`).

**The exact reads, IDENTICAL on both host and client:**

```
faction: 0x14(faction 2) vs 0x7FFFFFFF(faction 0): GetAIStateReputation(0x14,0x7FFFFFFF)=1
GetAIStateReputation(0x7FFFFFFF,0x14)=1 (0=neutral,1=friendly,2=hostile)
```

Cross-checked against `repute.2da` (`tools/kotorfiles.py extract repute.2da`, read fresh this
round): **row 2's own label is `Friendly_1`** (row 0 = `Player`, row 1 = `Hostile_1`, row 2 =
`Friendly_1`, row 3 = `Hostile_2`, ...). `end_sith2`'s own `CSWSCreatureStats::faction_id` reads a
plain `2` — **this specific GIT-placed creature starts in a FRIENDLY faction, not a hostile one**,
and the engine's own `GetAIStateReputation` matrix lookup for faction-2-vs-faction-0 (PLAYER)
correctly returns 1 (friendly) — this is not a bug in the read, it is the retail matrix working
exactly as designed against the creature's real starting membership.

**The host and client results are byte-for-byte identical.** `GetAIStateReputation` in both
directions read `1` on the HOST's own instance, and `1` on the CLIENT's own instance, for the
IDENTICAL pairing (`0x14` vs each instance's own local player id, `0x7FFFFFFF`). **This directly
rules out round 89's own working hypothesis (a per-instance asymmetry — "the host's own player
reads hostile, the client's does not")**: neither side reads this creature as hostile, so there
was never an asymmetry to explain in the first place, for THIS creature.

**Confirmed the creature's own combat-end script does not arm it either.** `firescript 0x14 fire`
(re-fires `k_def_combend01` for real, the same probe round 89 used) produced no change: `faction
0x14` read the identical `faction 2` / `GetAIStateReputation=1` result immediately afterward.
`k_def_combend01` is what runs at the END of a combat round already in progress (perception,
targeting, retreat logic) — it is not the trigger that ARMS hostility on a creature that starts
friendly, and this pass confirms it directly rather than assuming it from the name.

**Verdict: NOT a co-op bug, not a `LocalFightCandidateRule`/`EnemyPresenceController` defect, not
a client-vs-host divergence of any kind.** `end_sith2` genuinely is friendly at the moment either
instance's staging (`tp` directly to the creature, bypassing the Endar Spire's own story
progression) reaches it. Round 89's original symptom — a client's `attack` never lands on this
creature — is the CORRECT engine behaviour for a friendly-faction body, the same way a
single-player character could not "properly" fight a friendly NPC either without whatever normally
arms Sith hostility on Endar Spire first (a story trigger, a different creature already IN a
hostile faction, or a script elsewhere that this pass did not trace — `k_def_combend01` itself is
now ruled out). The playthrough's own successful `end_gensith` kill and general Sith-patrol combat
must involve a DIFFERENT creature or a story state this specific staging skips past.

**What this closes, and what is still open**: closes round 89's own "root-caused as
staging/engine-state, not a rule gap" verdict with PROOF rather than an inference — the "likely
per-instance asymmetry" language in that entry is now superseded; there is no asymmetry. Still
open, unchanged: which script/trigger actually arms Endar Spire's Sith hostility in a real
playthrough (round 89's own item 3), and whether the ORIGINAL playthrough's own reported "client
cannot land hits" symptom reproduces against a creature independently confirmed hostile by this
same `faction` verb — that follow-up test was not run this pass (no hostile-faction Endar Spire
creature was independently located this round to test it against) and is the natural next step.

Build: `dotnet build -warnaserror` clean, native plugin `-WarnAsError` clean, `dotnet test`
2339/2339.

## §11 — N-slot local combat (2026-09-02)

Widens `local_combat` from ONE locally-fought enemy at a time to up to
**`SharedBlock.LocalCombatSlotCapacity` (3)**, coop.ini `local_combat_slots` (default 3, clamped
1..3). `local_combat_slots=1` is required to reproduce the pre-widening behaviour bit for bit —
verified by test, not just asserted (`LocalFightSlotAssignmentRuleTests
.OneFreeSlot_ReproducesTheOldSingleWinnerRanking`).

### Design

The single scalar `_locallyFightingId`/`SharedBlock.OffsetLocalCombatExcludedId` becomes a
per-physical-slot array everywhere it mattered:

- **Shared block**: a new region, `SharedBlock.OffsetLocalCombatExcludedIds` (0x4800..0x4900,
  `SharedBlock.LocalCombatSlotCapacity` x u32), block version bumped 60 -> 61,
  `SharedBlock.TotalSize` grown 0x4800 -> 0x4B00. The old scalar
  (`OffsetLocalCombatExcludedId`, 0x1E5C) stays written too, mirroring slot 0 — kept for one
  release of backward compatibility, though in practice the agent and plugin always ship
  together (the block version is embedded byte-exact in `KotorGameBridge.PluginSignature`'s scan
  pattern, so a version-skewed pair simply never finds each other rather than reading a half-old
  layout). The agent-side reader is fully replaced: `KotorGameBridge.ReadLocalCombatExcludedIds`
  (plural) returns every nonzero slot; the old singular `ReadLocalCombatExcludedId`/
  `IEnemyBridge.ReadLocalCombatExcludedId` are removed rather than kept as dead code, since both
  halves are always shipped together in this repo. `CombatPump.PumpEnemies` filters every
  returned id out of the published census, not just one.

- **Plugin (`EnemyPresenceController`)**: `_locallyFightingId`/`_locallyFightingSince` become
  `uint[]`/`double[]` sized to capacity; so do the "STILL SEEING" leak-diagnostic state and the
  position-diagnostic timestamp, all indexed by physical slot. `SelectImmediateCandidate` now
  gathers every eligible candidate this scan into parallel arrays (owner id, priority, distance,
  census index) exactly as before, then hands the ranking to a new pure class,
  **`LocalFightSlotAssignmentRule`**: given the candidate list and which physical slots are
  currently free, it fills every free slot at once — slot 0 gets the single best remaining
  candidate (`LocalFightCandidateRule.Beats`'s own ordering, unchanged), slot 1 the next best,
  and so on, and a candidate is removed from contention the instant it wins a slot so the same
  creature can never be assigned twice in one scan. `StartLocalFight`/`MaintainLocalFight` are
  now per-slot (`MaintainLocalFightSlot`, called in a loop over `LocalCombatSlots`) but otherwise
  unchanged: same +0x260 script fire, same ClaimSnapshot restore, same dead/disengaged/timeout
  release logic, same `PluginEvents.RaiseLocalCombatEngagementChanged` — nothing about *how* one
  fight is run changed, only how many can run at once. `DriveOne`'s per-creature "is this the
  triggering slot" check now scans the small (<=3) `_immediateTriggerCensusIndex` array instead
  of comparing one int.

- **Deferral (`LocalCombatDeferralRule`)**: `ComputeDeferred` widened from a single
  `holderOwnerId` to a holder SET (`uint[] holderOwnerIds, int holderCount`) — a creature already
  held by ANY active slot is excluded from the deferred set, not just the one slot the old code
  knew about. The deferred-count HUD text (`LocalCombatDeferredNoticeRule`) is untouched; it only
  ever needed a count.

- **Companions (`PartyPresenceController`)**: gathers every currently-fighting enemy's position
  once per pass (`block.GetLocalCombatExcludedId(slot)` for each of the 3 slots) instead of
  reading the single scalar. `CompanionLocalFightRule` itself is unchanged — still a plain
  `nearEnemy` bool — per the minimum-change design: a companion counts as "near an enemy" if it
  is within `companion_local_combat_range` of ANY currently-fighting enemy, and
  `DriveAttackIfDue` aims at whichever one is nearest.

- **Logging**: every arm and release now names its physical slot —
  `local_combat: slot 2/3 armed id=0x.. reason=targeting-us (timeout 25s)` and
  `local_combat: slot 2/3 released id=0x.. reason=player disengaged` — plus a slot-numbered
  "selected" pre-arm line and a slot-numbered "STILL SEEING" diagnostic line.

### What changed, file by file

- `src/KotorCoop.Plugin/SharedBlock.cs` — `TotalSize` 0x4800 -> 0x4B00.
- `src/KotorCoop.Plugin/SharedBlock.Combat.cs` — `LocalCombatSlotCapacity`,
  `OffsetLocalCombatExcludedIds`.
- `src/KotorCoop.Plugin/SharedBlockView.Combat.cs` — `SetLocalCombatExcludedIds`,
  `GetLocalCombatExcludedId`.
- `src/KotorCoop.Plugin/PluginHost.cs` — `_block->Version = 61`.
- `src/KotorCoop.Memory/KotorGameBridge.cs` — `PluginSignature` version byte 0x3C -> 0x3D.
- `src/KotorCoop.Memory/KotorGameBridge.PluginBlock.cs` — `BlockLocalCombatExcludedIds`
  constants.
- `src/KotorCoop.Memory/KotorGameBridge.Enemies.cs` — `ReadLocalCombatExcludedIds` replaces
  `ReadLocalCombatExcludedId`.
- `src/KotorCoop.Memory/IEnemyBridge.cs` — interface member replaced the same way.
- `src/KotorCoop.Session/Pumps/CombatPump.Enemies.cs` — filters the whole returned set.
- `src/KotorCoop.Plugin/EnemyPresenceController.cs` — the N-slot rewrite described above.
- `src/KotorCoop.Plugin/EnemyPresenceController.LocalCombat.cs` — `LocalCombatSlots` config
  read; `IsLocallyFightingCreature`/`_locallyFightingIdsShared` widened to arrays.
- `src/KotorCoop.Plugin/LocalFightSlotAssignmentRule.cs` — new pure top-K assignment rule.
- `src/KotorCoop.Plugin/LocalCombatDeferralRule.cs` — `ComputeDeferred` holder-set signature.
- `src/KotorCoop.Plugin/PartyPresenceController.cs` — N-enemy gather, nearest-enemy pick.
- `profiles/coop.ini.example`, `docs/CONFIG.md` — `local_combat_slots` documented.
- `tests/KotorCoop.Tests/LocalFightSlotAssignmentRuleTests.cs` — new.
- `tests/KotorCoop.Tests/LocalCombatDeferralRuleTests.cs` — updated signature, two new
  multi-holder cases.
- `tests/KotorCoop.Tests/ItemHarnessTests.cs` (`TheBlockVersionIsTheSameNumberInBothHalves`) and
  `tests/KotorCoop.Tests/TransitionMovieTests.cs`
  (`TheBlockVersionMovedInBothHalves`) — both re-assert against 61 automatically/explicitly.

### `local_combat_slots` and the behaviour digest

`local_combat` itself already rides `BehaviourDigest` as a peer-warn (not host-authoritative) key
— `BehaviourDigestTests.cs` pins it in the "not host-authoritative" set. `local_combat_slots` is
**not** added to the digest at all: it is a further per-instance tuning of an already
non-host-authoritative flag, purely plugin-local (how many fights THIS client's own engine can
run at once has no wire representation — the shared-block array is client-local shared memory,
never serialised to a peer), so there is nothing for a peer to disagree with in the first place.

### VERIFIED (by test)

- `LocalFightSlotAssignmentRuleTests` — one free slot reproduces the old single-winner ranking
  exactly (`OneFreeSlot_ReproducesTheOldSingleWinnerRanking`, `OneFreeSlot_TiedPriority_ClosestWins`);
  a creature can never win two slots in one call, even when free slots outnumber candidates
  (`FewerCandidatesThanFreeSlots_TheSameIdCannotHoldTwoSlots`); an occupied slot is never assigned
  regardless of candidates; three free slots with three candidates assigns each a distinct
  winner, highest priority to the lowest-numbered slot.
- `LocalCombatDeferralRuleTests` — the widened `ComputeDeferred` correctly excludes a SET of
  holder ids (`ComputeDeferred_MultipleHolders_ExcludesAllOfThem`,
  `ComputeDeferred_EveryEligibleIdIsAlreadyHeld_DeferredIsEmpty`), while every pre-existing
  single-holder case still passes.
- `LocalFightCandidateRuleTests`, `CompanionLocalFightRuleTests`,
  `LocalCombatDeferredNoticeRuleTests` — unchanged, still pass (neither rule's own signature
  needed to change for N slots).
- `ItemHarnessTests.TheBlockVersionIsTheSameNumberInBothHalves`,
  `TransitionMovieTests.TheBlockVersionMovedInBothHalves` — both catch a half-bumped 61.

### NEEDS-LIVE (everything below is reasoned, not yet observed running)

- Two or three hostiles genuinely fighting the same client's engine simultaneously — does the
  engine's own combat state, animation and hit registration hold up with more than one
  script-fired `+0x260` creature active at once, the way it was proven for exactly one across
  rounds 27-55. Nothing in this design changes *how* one fight is armed, but N concurrent fights
  is a new load pattern this codebase has never run.
- The shared-block array round-trip end to end: plugin writes 3 slots -> agent
  `ReadLocalCombatExcludedIds` -> `CombatPump` filters all 3 out of the published census -> the
  OTHER peer's own census genuinely omits all 3 (not just slot 0).
- The "STILL SEEING" defence-in-depth path with more than one slot active — does a transient
  upstream-filter race on slot 2 log correctly without disturbing slot 0's own tracking.
- Companion release against multiple simultaneous fights — does `nearEnemy`'s "nearest of N"
  pick, and the attack `DriveAttackIfDue` aims from it, actually track a companion standing near
  a DIFFERENT slot than another companion.
- Whether three real fights at once meaningfully changes frame cost — `SelectImmediateCandidate`
  now runs `LocalFightSlotAssignmentRule` (O(candidates x free slots), free slots <= 3) instead
  of one linear scan; this should be negligible at the hook's own frequency but was not profiled
  live this pass.
- The exact slot-numbered log lines (`local_combat: slot 2/3 armed id=0x.. reason=..`) reading
  correctly against a live multi-slot fight, not just compiling.

### Uncertain engine behaviour, flagged rather than guessed at

Whether the engine's own AI scheduler treats N simultaneous script-fired combat rounds against
the SAME local player any differently than one — e.g., whether `GN_DetermineCombatRound` or the
attack-queue dispatcher has any shared per-player state that two concurrently-armed creatures
could contend over. Every round 9-55 investigation only ever exercised one creature at a time;
this pass reasoned by analogy (nothing in the fire-the-script mechanism is obviously
player-singular) but did not — could not, without a live rig — prove it.

### 11.x Live, 2026-09-02 (the A-slots pass, docs/live-verification.md "N-slot local combat live")

Observed on the rig, END_M01AA save 1, client vs the nd_gensith pair 0x38/0x3B:

- **Two hostiles fighting one client's engine at once** — slot 1/3 armed id=0x3B and
  slot 2/3 armed id=0x38 in the same scan, both landing real local hits on the PC
  (combat ground-truth: local PC took 2 damage, attacker=0x3B / ..=0x38, both tagged as the
  currently locally-fighting creature). The engine's per-player combat state held up under two
  script-fired +0x260 creatures; the "uncertain engine behaviour" paragraph below is no longer
  uncertain for N=2.
- **The array round-trip** — xcludedIds=[0x3B,0x38] ... filtered.Count=23 (removed 2) on the
  client's agent; both ids absent from the client's presence array while armed.
- **Slot-numbered log lines** read correctly live (slot 2/3 armed, slot 2/3 released ...
  reason=timed out, and a re-arm of the same creature into slot 1/3 on the very next scan).
- **A hole the exclusion had, now closed** — an excluded id whose owner-side body died and faded
  inside the 25 s window never reached the client as Dead (the drop was unconditional), so the
  pinned local copy stayed up as an immortal Sith after 	imed out. LocalCombatExclusionRule
  (Session) passes the owner's Dead entry through the exclusion and republishes an id the owner
  stopped listing (2 s grace, feed below cap) as dead; DriveOne releases the slot with
  eason=owner reports dead or gone and runs the ordinary death path. Live: both kills retired
  on the client within ~300 ms of the host's combat: applied N of a peer's damage
  (local combat: excluded 0x3B let through as dead (OwnerReportsDead) -> combat: killed 0x3B
  on the owner's authority). The round-55 "one honest gap" in shared-state-audit.md (0x24 timing
  out after the host had killed it) was the same hole; it is closed by the same rule.
- **Kill XP agreed across sides** with no merge needed: PC xp 150 -> 300 on both instances after
  the two cross-side kills (the host's engine paid its party, the client's engine paid the
  mirrored death).

Still NEEDS-LIVE from the list above: the STILL-SEEING race under multiple slots (not
provoked), companion release against multiple simultaneous fights, and frame cost. The
OwnerNoLongerLists fallback of the new rule was not exercised live (the Dead entry arrived
first both times); it is pinned by LocalCombatExclusionRuleTests.

## §12 — Idle hostiles never engage: the script-fire's own gap, and a direct-drive fallback (2026-09-02, second pass)

Live symptom (`docs/live-verification.md`, "Companion damage TAKEN live", "Staging caveats in
tar_m10aa"): the eight pool-room `tar10_vulkar*` GIT hostiles in `tar_m10aa` — hostile in the
census, a real (non-empty) `k_def_combend01` at `+0x260`, the client's PC standing 3-6 m away —
sat completely still for minutes under `local_combat`'s own script-fire arm
(`EnemyPresenceController.StartLocalFight` → `Odyssey.FireCreatureScriptSlot0x260`). Only
`blockpoke.ps1 attack 0x7FFFFFFF <id>` (`Odyssey.AttackTarget`, `ExecuteCommandAttack`) got the
first one moving; every other slot then armed and turned over normally for the rest of the fight.
Contrast: the Endar Spire `end_gensith` pair (§11.x) engaged instantly off the identical script
fire, no ordered attack needed.

### Why the script fire alone does nothing for an idle creature

**VERIFIED, live, this repo's own evidence (§10's "Live pass 2026-08-28" section above, same
file):** `k_def_combend01` fired directly (`firescript <id> fire`) on a creature with no combat
round already running produced no state change at all — `round+0x9CC target` stayed the
`OBJECT_INVALID` sentinel, `round+0x958 timer` stayed 0, `resolvedTarget=0x0` — and the quoted
verdict from that pass: **"`k_def_combend01` is what runs at the END of a combat round already
in progress (perception, targeting, retreat logic) — it is not the trigger that ARMS hostility on
a creature that starts friendly, and this pass confirms it directly rather than assuming it from
the name."** That test used a friendly-faction creature, but the mechanism it demonstrated —
`k_def_combend01` acting on an *existing* round/target rather than creating one — is exactly what
the `tar_m10aa` Vulkars (genuinely hostile, per the census) also show: real script, real
hostility, zero effect, because there was no round already running for it to act on.

**VERIFIED, disassembly (`peer-damage-ledger.md` §8.5):** the engine's own native retargeting
logic (`CreateNewAttackActions`/`EndCombatRound`, fully decoded in §1 of this document) reads a
creature's *current* round target (`round+0x9CC`) and its perception list — never the
`+0x158` "last hostile actor" field on its own initiative. `+0x158` is native-**write**,
script-**read** only: the engine populates it on every real damage application
(`CSWSObject::SetLastHostileActor`, `0x004CC0C0`, called from `OnApplyDamage`) and exposes it to
NWScript via `GetLastHostileActor()` (`0x0053AF40`).

**INFERRED, not verified this pass (same as §8.5's own caveat) — the one genuine gap in this
picture:** whether the *compiled bytecode* inside `k_def_combend01` itself calls
`GetLastHostileActor()` (or a nearest-enemy scan of its own) to pick a target when none exists yet.
This project has decoded the engine's native combat machinery exhaustively but has never
disassembled `k_def_combend01`'s own `.ncs` bytecode — only its *effect*, which the two rounds
above establish is "no-op when nothing was already fighting." Whether that no-op happens because
the script checks `GetLastHostileActor()`, finds nothing, and gives up, or because the script
does something else entirely that also happens to require a pre-existing round, is not
distinguished by any evidence gathered so far. **Practically this distinction does not matter for
the fix below** — either way, an idle creature with a fresh `+0x158` (nobody has hit it, nothing
has been reported to `SetLastHostileActor` yet) and no perception event fired has nothing to act
on, and the script fire alone cannot manufacture one.

The stock, single-player explanation this reasoning is consistent with: KOTOR normally arms a
GIT-placed hostile's first round through **perception** (`UpdateVisibleList`/
`DoPerceptionUpdateOnCreature`, §3 of this document, dispatching through the separate `+0x270`
slot on a `[esi+0xa24]==OBJECT_INVALID` / `[esi+0xa88]==0` gate — see
`Odyssey.Combat.ScriptFire.cs`'s own block comment) or a **story trigger** (heartbeat script,
area trigger, `OnNotice`), not through `+0x260` at all. `local_combat`'s script fire only ever
attempts `+0x260` because rounds 27-55 proved *that* one reliably starts a real fight once
something already made the creature hostile-and-targeting — it was never meant to be the thing
that manufactures hostility from cold, and the `end_gensith` pair happening to already be mid-round
(story-triggered ambush) is why they were never a counterexample until the Vulkars were tried.

### Fix options considered

1. **Direct-drive fallback (chosen).** After the script fire, if the creature still has no attack
   target after a grace window, drive its attack on the local player directly through
   `Odyssey.AttackTarget` — the exact same engine call (`ExecuteCommandAttack`) the
   `companion_local_combat` release path (`PartyPresenceController.DriveAttackIfDue`) and the
   `attack` console verb already use, and the same one that live-verified un-froze the Vulkars by
   hand. Lowest risk: it reuses a call already proven live in this codebase for the identical
   "creature has an empty action queue, give it a real target" problem, just triggered
   automatically instead of by an operator.
2. **Pre-set `+0x158` (`SetLastHostileActor`) before firing the script.** Would only help if the
   INFERRED gap above resolves "yes, the script reads it" — unverified, and even if true this
   still depends on undecoded script bytecode behaving as hoped. Also writes a field this
   investigation has only ever seen the engine itself write (§8.5), not a plugin — a
   less-proven, more speculative path than calling an already-live-verified engine entry point.
3. **Fire `+0x270` (the perception/heartbeat slot) instead of `+0x260`.** §'s own doc comment on
   that slot: gated on `[esi+0xa24]==OBJECT_INVALID` and `[esi+0xa88]==0`, then **latched** via
   `[esi+0x34c]=1` so it never re-fires — a one-shot per creature for its whole lifetime. Firing
   it out of turn on a creature the real perception system has not yet reached is exactly the
   class of engine-state graft rounds 9-38 spent this document proving is fragile (a materialised
   stand-in's body vanishing within 5 ms of an unproven +0x260 fire is the closest precedent for
   what an unproven +0x270 fire could do). Never fired live; no evidence either way.

(1) wins on evidence: it is the only option built entirely from calls this codebase has already
watched work, live, against a real fight (`AttackTarget` downing companions' targets in
`docs/live-verification.md`; the same primitive un-freezing the actual Vulkars by hand in this
very session). (2) and (3) both require the INFERRED gap to resolve favorably and neither has ever
been tried live.

### What shipped

New coop.ini key **`local_combat_engage_fallback`** (default **ON**, plugin-only —
`docs/CONFIG.md`, `profiles/coop.ini.example` — a further tuning of the already
non-host-authoritative `local_combat` flag, same "plugin-local, no wire representation" reasoning
`local_combat_slots` already carries in §11 above). The script fire in `StartLocalFight` runs
first, unchanged — this is a fallback, not a replacement.

Pure decision: `src/KotorCoop.Plugin/LocalFightEngageFallbackRule.cs`
(`LocalFightEngageFallbackRule.Decide`, tested in
`tests/KotorCoop.Tests/LocalFightEngageFallbackRuleTests.cs`, linked into the test project the
same source-sharing way every other plugin pure-rule file already is — see
`KotorCoop.Tests.csproj`'s own comment on why arithmetic-only plugin files are compiled directly
into the test host). Inputs: the flag, whether this fight (this slot's currently-armed id) already
had the fallback fire once, the creature's action-queue head type
(`Odyssey.GetHeadActionType` — the identical "has this creature got nothing queued" signal
`CompanionLocalFightRule.ShouldDriveAttack` already uses for the companion path, headType == -1
means empty), and how long it has been since `StartLocalFight` armed the slot versus a grace
window. One-shot per fight (not a retry loop, unlike the companion path's own periodic re-attempt)
— a creature that still shows no target after one direct drive is not going to gain one from
issuing the identical call again every frame, and the direct drive itself queues a real attack
action that the very next pass reads back as non-empty, which would otherwise look like grounds to
fire again.

Plugin side: `EnemyPresenceController.MaintainLocalFightSlot` calls the rule once per active slot,
every pass, right after the existing dead/disengaged/timed-out release checks (so a slot already
being released this pass is never also handed a fallback attack). On `EngageDirectly` it resolves
the local player id fresh (`Odyssey.GetPlayerObjectId()`, never cached — same discipline as every
other per-frame id lookup in this controller) and calls `Odyssey.AttackTarget(fightingId, playerId,
out failure)`, `fightingId` being the creature's own OWNER-space id — safe to use directly as the
attacker id here because `StartLocalFight` only ever arms GIT-identical creatures (mapped == 0 by
construction, per `DriveOne`'s own "GIT-IDENTICAL CREATURES ONLY" note), so the owner id and the
local resolvable id are the same number. The one-shot flag
(`_engageFallbackIssued[slot]`) is cleared everywhere a slot's fight ends or a new one arms —
`StartLocalFight` (new fight), both release paths in `DriveOne` (owner-reports-dead) and
`MaintainLocalFightSlot` (dead/disengaged/timed-out), and `Invalidate()` (module change) — the
same reset discipline every other per-slot fight-scoped array in this file already follows.

Log line a live tester should look for:

    local_combat: slot N/M engaged 0x.. directly (no attack target Kms after the script fire)

— printed once per fight, from `MaintainLocalFightSlot`, the moment the fallback fires (never
printed at all when the script fire alone was enough, e.g. the `end_gensith` case).

### VERIFIED vs INFERRED, summary

- **VERIFIED (live, this pass and §10 above):** the script fire alone does not arm a round on a
  creature with no round already running, regardless of whether that creature is genuinely
  hostile; `Odyssey.AttackTarget`/`ExecuteCommandAttack` does arm one and land real, credited
  damage on an otherwise-idle creature (the Vulkars, driven by hand this session; companions,
  driven automatically by the existing `companion_local_combat` path).
- **VERIFIED (disassembly, `peer-damage-ledger.md` §8.5):** `+0x158`
  (`SetLastHostileActor`/`GetLastHostileActor`) is native-write, script-read only — the engine's
  own native retargeting path never reads it.
- **INFERRED, still open:** whether `k_def_combend01`'s own NWScript bytecode consults
  `GetLastHostileActor()` (option 2 above) or anything else that a plugin write could usefully
  prime — genuinely unknown, since the compiled script body has never been decompiled by this
  project. Does not block the shipped fix, since option 1 does not depend on the answer.
- **NEEDS-LIVE (this fix itself):** never run against the actual `tar_m10aa` Vulkars or any other
  real idle-hostile fight yet — built and unit-tested this pass, not yet rig-verified. The grace
  window chosen (a fixed constant, not exposed as its own ini key — see the class's own doc
  comment) is a judgment call, not a measured value: long enough that a script fire which DOES
  work (the `end_gensith` case) is never contested mid-arm by a redundant direct drive, short
  enough that a genuinely idle hostile does not sit frozen for long. A live pass should watch
  whether that balance actually holds — a `local_combat_engage_fallback=0` control run stands in
  for "was the timing about right" if the fallback line fires but doesn't help, or fires too late.

---

## §13 — `local_combat_timeout_s` measures inactivity, not fight length (2026-09-02, third pass)

### The churn

Live symptom, measured this pass (`tar_m10aa`, the same Vulkar pool room §12 fixed the engage
side of), the client's own plugin log:

    [22:55:42.839] local_combat: slot 1/3 armed id=0x4D reason=targeting-us (timeout 25s)
    [22:55:43.591] local_combat: slot 1/3 engaged 0x4D directly (no attack target 752ms after the script fire)
    [22:56:07.839] local_combat: slot 1/3 released id=0x4D reason=timed out
    [22:56:07.881] local_combat: slot 2/3 selected 0x4D (priority=Proximity, dist=2.05, engageRange=11.0)
    [22:56:08.631] local_combat: slot 2/3 engaged 0x4D directly (no attack target 750ms after the script fire)

— `22:56:07.839 - 22:55:42.839 = 25.0s`, exactly `local_combat_timeout_s`'s default. Meanwhile the
host's own log showed `combat: applied 5 of a peer's damage to 0x4D (owed 13)` and further applies
of the same shape landing throughout that whole 25s window: the fight was genuinely still going the
entire time — the client kept swinging, the host kept crediting it — and got released anyway,
purely because `MaintainLocalFightSlot`'s timeout check (§11) measured elapsed time since
`StartLocalFight` **armed** the slot (`_locallyFightingSince[slot]`), not since anything last
happened. The creature went back to the census for one pump (long enough for mirroring to
potentially overwrite it — see `docs/research/warp-crash-and-snapshot-lesson.md` for why a body
left briefly unowned is not free of risk), then `SelectImmediateCandidate` (§11) re-armed the same
id into whichever slot next opened and the engage fallback (§12) re-engaged it roughly 0.8s later.
Every `local_combat_timeout_s` window, indefinitely, for as long as the fight actually lasted.

### The fix: an activity clock, not a fight-start clock

`local_combat_timeout_s` is now a measure of **no evidence**, not **elapsed fight time**. Every
`MaintainLocalFightSlot` pass (for a slot not already ending for a dead or disengaged reason)
checks two independent pieces of evidence that the fight is still real, either one enough on its
own:

1. **Damage dealt rose.** `PhantomHitHook.Dealt` (`DamageDealtTable`, the same cumulative
   per-owner-id ledger `PeerDamageController.PublishDealt` already forwards to the fight's owner
   every pass — see that class's own doc comment on why a SUM of damage dealt, never health, is
   the channel that cannot be raced) now has a `TotalFor(objectId)` read accessor. A new per-slot
   baseline, `_lastDealtSeen[slot]`, is compared against it: a rise means the local player actually
   landed a hit on this creature since the last pass.
2. **The creature currently targets the local player.** Read directly off the creature's OWN live
   combat round — `creature+0x9C8` → round, `round+0x9CC` = the round's current target (§1.2,
   VERIFIED disassembly) — via a new `Odyssey.GetCurrentAttackTargetId(creature)` accessor. This is
   the SAME field `LocalFightCandidateRule`'s `TargetingUs` priority (§11) cares about
   conceptually, but a different read: candidate SELECTION (before a fight exists to inspect) has
   only the owner's own damage report to go on (`block.IncomingDamageAttacker`, §11's
   `IsAttackingOurPhantom`), while an ALREADY-ARMED fight's creature is genuinely local and driven,
   so its own round struct can be read directly rather than inferred from the wire.

Pure decision: `src/KotorCoop.Plugin/LocalFightActivityRule.cs`
(`LocalFightActivityRule.Decide`, tested in
`tests/KotorCoop.Tests/LocalFightActivityRuleTests.cs`, same source-sharing arrangement as every
other plugin pure-rule file — see `KotorCoop.Tests.csproj`'s own comment). Either piece of evidence
returns `Refresh` unconditionally — evidence always wins over the clock, even a pass found right at
what would otherwise be the timeout boundary. With neither, the rule falls back to the same
strict-greater-than clock arithmetic the old fight-start check used
(`now - lastActivityAtSeconds > timeoutSeconds`), just measured against the new activity stamp
instead of the arming stamp, returning `TimedOut` once it elapses and `Continue` otherwise.

Plugin side: `EnemyPresenceController.MaintainLocalFightSlot` gathers both signals and calls the
rule through a small helper, `UpdateActivity`, only when the slot is not already ending for a dead
or disengaged reason this pass (the DamageDealtTable scan and the round-target read are both cheap
but not free, and a fight that is ending anyway does not need them). `StartLocalFight` seeds
`_lastActivityAt[slot] = now` (the same grace an idle fight gets before ever showing evidence) and
`_lastDealtSeen[slot] = PhantomHitHook.Dealt.TotalFor(ownerId)` — the table's CURRENT total, not
zero, since this exact creature may have been fought (and released) earlier this module load, and
that old total must not read as a fresh rise the instant a new fight on it starts. Both, plus the
new log throttle stamp `_lastActivityLogAt[slot]`, are reset in both release paths (the timeout
block in `MaintainLocalFightSlot` and `Invalidate()`) with the same discipline every other per-slot
fight-scoped array in this file already follows (`_engageFallbackIssued` et al., §12).

`_locallyFightingSince[slot]` itself is untouched and keeps its original meaning (the fight's
START) — the "t=…s" position diagnostic and the engage fallback's own grace window (§12) both still
key off it, unchanged, on purpose: the fallback's "has the script had long enough to find a target"
question is about time since ARMING, not time since last evidence, and conflating the two would
have broken it.

Log lines a live tester should look for — a refresh (rate-limited to once per 5s per slot, since a
real fight can refresh every single pass):

    local_combat: slot N/M still active (dealt +X to 0x.. / it targets us), timeout extended

and the release reason, now explicit about which kind of timeout fired:

    local_combat: slot N/M released id=0x.. reason=timed out (no activity for 25s)

### VERIFIED vs NEEDS-LIVE, summary

- **VERIFIED (live, this pass):** the churn itself — the quoted log excerpt above, cross-referenced
  against the host's own `combat: applied … of a peer's damage` lines landing throughout the same
  window the client's timeout fired in.
- **VERIFIED (disassembly, §1.2, unchanged from §11/§12):** `round+0x9CC` is the combat round's own
  live target field.
- **VERIFIED (this fix itself, live 2026-09-03):** confirmed against a real fight — see this
  document's own §14 below for the verbatim lines: a slot with ongoing traffic is extended past
  25s, and a genuinely idle slot still releases at the configured timeout.

## §14 — Live pass (2026-09-03, 04:00–04:01 and 04:08): the inactivity-timeout fix confirmed

Rig: `pwsh tools/demo.ps1 -Save 1`, `tar_m10aa`, before the `spawncreature` rounds documented
elsewhere this same night (`docs/live-verification.md` 2026-09-03).

A slot with ongoing traffic extended past the 25s boundary:

    [04:01:18.932] local_combat: slot 2/3 still active (dealt +3 to 0x4D), timeout extended

A genuinely idle slot released exactly at the configured timeout — slot 1's `0x4A` stood 7.23 u
away and never exchanged damage, the per-second diagnostic tracking it up to the release:

    local combat diag: slot 1/3 t=24.0s 0x4A at (154.87,93.84) player at (161.00,90.00) dist=7.23
    [04:01:20.431] local_combat: slot 1/3 released id=0x4A reason=timed out (no activity for 25s)

A second round at 04:08 added twelve more "still active" extensions, this time targeting-us-based:

    local_combat: slot N/3 still active (it targets us), timeout extended

and nine proximity arms:

    local_combat: slot N/3 armed id=... reason=proximity (timeout 25s)

**Verdict.** Exactly the two outcomes §13 predicted: activity (either a landed hit or the
creature targeting the local player) resets the clock, and its absence still releases the slot at
the configured timeout. This closes §13's own NEEDS-LIVE mark.

## §15 — The `attack` dev verb (2026-09-25): a real queued ActionAttack for the LOCAL PLAYER

Two things this project could not test without a human clicking a mouse:

1. **Sticky aggro.** "The joiner out-damages the host; the enemy should stay locked on the
   joiner rather than switching back." No prior verb could make the JOINER's own creature
   fight — every existing attack-staging primitive (`Odyssey.AttackTarget`, wired into
   `EnemyPresenceController`/`PartyPresenceController`/`PeerDamageController`) orders an NPC or
   phantom to attack, never the local player.
2. **A real `cast`.** §14.8 (`docs/research/effect-sync.md`) found that `AddCastSpellActions`
   is two functions selected by its own last argument: every external "please cast" entry
   point — including the VM routine `cast` dispatches and the real radial-menu click — only
   ever *stages* a pending-cast record (`bPromote = 0`). The record is only *promoted* to a
   real, FP-spending cast by `CSWSCreature::AIActionCombat` (RunActions node type 0x3E),
   which only ever runs while the creature is undergoing genuine per-round combat processing.
   A caster with no active combat round has its `cast` silently dropped a few seconds later.
   §14.8's own live recipe says as much: "aggro a genuinely hostile creature first... then run
   `cast cure me` while combat is actively ticking." Until now nothing could put the JOINER
   into that state without a click either.

### What it reuses, and what it does not

`attack <0xid>` does **not** reintroduce `Odyssey.QueueAttackAction` (`Odyssey.Combat.Attack.cs`)
— that routine hand-replicates `CreateNewAttackActions`'s round-arming steps and is documented
(`EnemyPresenceController.cs`, round 39) as a **proven permanent no-op** for a creature pulled out
of AI processing (a mirrored/driven puppet with no heartbeat). The local player is not that kind
of object — it is the engine's own always-ticking creature — but there is no reason to risk the
same class of failure when a second, independently-verified route already exists.

It calls `Odyssey.AttackTarget` (`Odyssey.Faction.cs`) instead: the engine's own `ActionAttack`
NWScript action, dispatched through the VM exactly like `cast`/`grantpower`/`pickupitem`, with
`OBJECT_SELF` redirected to the attacker for the one call. This is the SAME routine
`shared-state-audit.md` round 11 confirmed live — a genuinely fresh `sithsoldier` taken from inert
to real, landing damage (12 hp → 8 hp → 1 hp across two ordered attacks) — and the same one
`EnemyPresenceController`, `PartyPresenceController` and `PeerDamageController` already drive for
mirrored enemies/companions every day. `attack` is the first console-verb caller with the
ATTACKER as the local player rather than an NPC.

### The one PC-specific gotcha, and why the verb closes it itself

`TestDriveController.cs` (a separate, pre-existing test-battery hook that also calls
`Odyssey.AttackTarget` with the local player as attacker, driven from the shared block's `Test*`
fields rather than the console) already hit this once, round 71: a queued `ActionAttack` for a
PLAYER-CONTROLLED creature never closes the distance to its target on its own. For an AI-driven
attacker, the walk-to-range step is part of that creature's own per-tick `AIActionCombat`
processing; the player is never driven that way — a real click's accompanying move-to-click
path is what normally covers the gap, and calling `ActionAttack` through the VM directly skips
it. Round 68's failures (five targets, two modules, never a real hit) were exactly this: the
order armed (head action became 0x3E `Combat`, confirmed steady for a 20s probe window) but
nothing ever swung, because nothing was in range.

`CheatController.Attack.cs` closes the gap itself before arming the order — the same fix
`TestDriveController.CloseToMeleeRangeIfNeeded` already uses, re-derived here rather than shared
(the two live on different plumbing: `Test*` fields vs. the `CheatKind` channel): if the player is
farther than 1.5 m (planar) from the target, `Odyssey.SnapTo` moves it to melee range, approaching
from whatever direction it already stood. `SnapTo` is done **before** `AttackTarget` is called on
purpose — `SnapTo` runs `ClearAllActionsFn` as part of committing the position, which would wipe
an attack order queued ahead of it.

### Verb shape

- `attack <0xid>` — resolves the target through `Odyssey.ResolveServerObject`, requires it to be
  a live creature (`vtable == CreatureFinder.CreatureVtable`), refuses the local player's own id,
  closes to melee range if needed, then calls `Odyssey.AttackTarget(playerId, targetId, ...)`.
  Replies with the head action type before and after (`Odyssey.GetHeadActionType`) so a live run
  can tell whether the order actually took (an unchanged head, or one outside the fight set —
  `0x0B`/`0x3E`/`0x0E`/`0x31`/`0x0F`, see `Odyssey.ActionQueue.cs`'s `IsFightAction` — means it
  did not).
- `attack stop` — `CheatTarget == 0` is the sentinel (no live object id is ever 0, matching the
  convention `TestDriveController.HandleAttack` already uses for "stop fighting"): clears the
  local player's queued attack through `Odyssey.ClearQueuedCombatActions`, the same
  `CServerExoApp::ClearAllQueuedCombatActions` the TEST BATTERY's own break-off step uses, so a
  staging run can put the world back before the next check.

Wired through the standard verb path: `ConsoleCommand.Attack.cs` (parse) →
`CoopCommandDispatcher.Combat.cs` (send, `CheatKinds.Attack = 98`) → `CheatController.Attack.cs`
(apply, `SharedBlock.CheatKindAttack = 98`) → `Odyssey.AttackTarget`/`Odyssey.SnapTo` (engine
calls), plus `FuncInterpHook`/`ConsoleVerbCatalog` allowlisting and the verb-table coverage test.
Never bypasses `PhantomHitHook`'s damage-forwarding path — a mirrored enemy's HP still comes from
its owner and is still pinned min-1-hp on the joiner's own instance; this verb only queues the
same engine action a real click would, nothing about damage application changes.

### NEEDS-LIVE

Everything above is reasoned from the same verified mechanism `AttackTarget`'s existing callers
already exercise live daily, but the verb itself (and the melee-range close specifically, for a
target that is not already the fixed post-`tp` companion distance round 71's own fix was tuned
against) has not yet been run. Two recipes, meant to be run together as one session on the live
rig (`tools/demo.ps1`, two instances):

**Sticky aggro** (closes the "has never run" gap this section opened with):

1. Get both the host and the joiner into the same fight against the same hostile (or
   owner-mirrored) creature — `spawncreature`/an existing hostile encounter is fine.
2. On the HOST instance: `attack 0x<enemy>` (or let it fight normally).
3. On the JOINER instance: `attack 0x<enemy>` too, and keep re-issuing it every few seconds
   (`AttackTarget` does not repeat on its own once the round ends) so the joiner keeps
   out-damaging the host.
4. Watch the HOST's own log for its `threat`/`local_combat: slot N/M ... reason=` lines
   (`EnemyPresenceController`/`PeerDamageController`) — the enemy should stay armed against the
   joiner's forwarded damage rather than re-arming against the host once the joiner is doing
   more damage. `stats`/`where` on both sides confirms whose hp is actually dropping.

**A real cast** (closes §14.8's own NEEDS-LIVE live recipe, the one the caster-side half of this
section exists for):

1. Load (or `levelup` into, per `docs/research/class-and-power-grants.md` §9) a Jedi with real
   Force points and a known power (`grantpower` if needed).
2. `attack 0x<hostile>` to put the LOCAL PLAYER into a genuine, actively-ticking combat round —
   the exact precondition §14.8 identified as missing.
3. While the round is still active, `cast cure me` (or another known power) and watch `stats`:
   FP should actually drop and the effect should land, confirming the two-phase
   request/promote theory live rather than statically.
4. `attack stop` afterward to clear the queue before the next check, matching the TEST BATTERY's
   own between-checks convention.

See `docs/research/effect-sync.md` §14.9 for the pointer back from the cast side.
