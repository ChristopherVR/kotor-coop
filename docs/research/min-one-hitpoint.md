# Min-one-hit-point — why a player stops at 1 hp and never goes down (research 2026-08-07)

The measurement that started this: enemies beat on a client's phantom in the host's game,
`incoming: owed 46 applied 46` reached the client, and the client's own character sat at
**exactly 1 hit point with `GetDeadTemp` clear** — upright, walking, not down.

The cause is not the co-op damage forward. It is **KOTOR's own Endar Spire tutorial script**.
`end_m01aa`'s area OnEnter pins the player character with `SetMinOneHP`, and while that pin
is on nobody can be downed on that deck — not a remote player, not the local one, not in a
normal single-player playthrough either.

Read alongside `death-handling.md` (the pipeline this floor sits inside) and
`combat-participation.md` (the forward that delivers the damage). Plugin surfaces:
`src/KotorCoop.Plugin/DownBlock.cs`, `IncomingDamageController.cs`, `Odyssey.cs`.

Tag key: **VERIFIED** = read from the binary or the shipped game data here ·
**INFERRED** = deduced, not run · **NEEDS-LIVE** = confirm on the running game.

## Headline

Three things in the engine can floor a creature at 1 hit point. Only one of them is live in
retail KOTOR 1, and a module turns it on deliberately:

| Floor | Where | Live in retail? |
| --- | --- | --- |
| `min1hp` — `CSWSObject +0x200` | clamps in `SetCurrentHitPoints`, in `DoDamage`, and converts an applied Death effect into "set hp 1" | **yes**, and modules set it on the PC |
| `MinHP1` — the difficulty table | `CSWSCreature::DoDamage` 0x004F39B8, is_pc only | **no** — the cell is only filled for a difficulty level the engine never selects |
| `plot` — `+0xF8` | zeroes damage before it is applied at all | yes, but it produces a different symptom (hit points never move) |

## 1. The `min1hp` flag and its three clamps — VERIFIED

`CSWSObject + 0x200`, an int. Set by the script `SetMinOneHP` (`ExecuteCommandSetMinOneHP`
0x00545F90, nwscript routine **716**).

```
0x004CCF80  CSWSObject::DoDamage
  0x004CCF87    call vtbl+0x9C (GetCurrentHitPoints(1))   ; returns AX
  0x004CCF91    mov  ecx, [esi+0x200]                     ; min1hp
  0x004CCF97    movsx eax, ax                             ; SIGNED
  0x004CCF9A    sub  eax, edx                             ; hp - damage
  0x004CCF9E    mov  [esi+0xDC], eax
  0x004CCFA4    je   done                                 ; min1hp clear -> keep it
  0x004CCFA6    cmp  eax, 1
  0x004CCFAB    mov  dword [esi+0xDC], 1                  ; THE FLOOR
```

The same clamp is in `CSWSObject::SetCurrentHitPoints` (0x004CC280, site 0x004CC299), and
`OnApplyDeath` (0x004E0AC0) turns a Death effect on a min1hp creature into
`SetCurrentHitPoints(1)` at 0x004E0AF8 rather than killing it.

**Why the downed flag stays clear — there is no flag.** `CSWSCreature::GetDeadTemp`
(0x004EF890) stores nothing. It walks the client party (`GetControlledCharacter` 0x005ED8B0
→ `CSWParty::GetCharacter` 0x006346C0, id-compared via `ServerToClientObjectId` 0x005EDA50),
returns 0 outright for a non-member, and for a member computes
`GetCurrentHitPoints(0) <= 0` on the spot — `test ax, ax; setle cl` at 0x004EF8FD, a
**signed** 16-bit test. So "who sets the downed flag" has no answer: being down IS having
non-positive hit points, and everything else (prone body, AI off, the death choreography) is
`OnApplyDeath` reacting to it. Hit points floored at 1 never reach 0, so the party
temp-death never fires — and the Death effect is never even *created*, because the decision
in `OnApplyDamage` (0x004E0240) is `if (GetDead() || GetDeadTemp())`. Floored at 1, both are
false. There is no half-run death path here; the pipeline simply concludes the creature is
fine. The measured "1 hp, downed flag clear" is that state exactly.

## 2. The Endar Spire pins the PC — VERIFIED from the shipped module

`end_m01aa` (the Command Deck) declares `k_pend_area01` as its area **OnEnter**
(`m01aa.are` → `OnEnter: k_pend_area01`). Disassembling `end_m01aa_s.rim:k_pend_area01.ncs`:

```
104F  ACTION GetEnteringObject
1054  ACTION GetIsPC (1)
1059  JZ    -> return                 ; only for the PC
1061  JSR   <once-only check>
1067  JZ    -> return                 ; only the first time
      ... opening movie, camera, SetGlobalNumber("K_CURRENT_PLANET", 5) ...
1196  CONSTF 0.1
119C  ACTION DelayCommand (2)
11A1  CONSTI 1
11A7  ACTION GetFirstPC (0)
11AC  ACTION SetMinOneHP (2)          ; SetMinOneHP(GetFirstPC(), TRUE)
11BD  RETN
```

It is released one area later. `end_m01ab` (the Starboard Section) declares
`k_pend_1b_area` as its OnEnter, and that script does the inverse:

```
104F  ACTION GetEnteringObject
1054  ACTION GetFirstPC (0)
1059  EQUAL                           ; entering object IS the PC
1073  JZ    -> return                 ; ... and the once-only check passed
1096  CONSTI 0
109C  ACTION GetFirstPC (0)
10A1  ACTION SetMinOneHP (2)          ; SetMinOneHP(GetFirstPC(), FALSE)
```

A full sweep of every `.ncs` in `modules/`, `rims/` and the BIFs for
`SetMinOneHP(GetFirstPC(), …)` finds exactly five scripts, and they pair up:

| Script | Module | Effect |
| --- | --- | --- |
| `k_pend_area01` | `end_m01aa` Endar Spire, Command Deck (OnEnter) | **set** |
| `k_pend_1b_area` | `end_m01ab` Endar Spire, Starboard Section (OnEnter) | clear |
| `k_ptar_duelring` | `tar_m02ae` Taris duel ring | **set** |
| `k_ptar_duelend`, `k_ptar_duelend2` | same | clear |

(Another ~65 scripts call `SetMinOneHP` on named NPCs — Trask, the two `end_sith` soldiers,
Xor, Uthar — the same trick applied to scripted fights. Those never touch the PC.)

**So: on the Endar Spire Command Deck the player character is unkillable by design, and the
release is geographic.** This is the module rule the co-op layer must honour, not fight.

Reproduce the sweep:

```
python tools/kotorfiles.py 2da diffsettings.2da
# NCS: ACTION opcode 0x05, type 0x00, u16 routine, u8 argc.
#   SetMinOneHP(GetFirstPC(), TRUE)  = 04 03 00000001  05 00 0224 00  05 00 02CC 02
#   SetMinOneHP(GetFirstPC(), FALSE) = 04 03 00000000  05 00 0224 00  05 00 02CC 02
```

Routine numbers come from the engine's own registration: each handler is stored as
`mov dword [table + 4*index], <ExecuteCommandX>`, so `ApplyEffectToObject` at `+0x370` is
index 220 (which matches nwscript, confirming the base), `GetFirstPC` is 548 and
`SetMinOneHP` is 716.

## 3. The difficulty floor, and why it is dead — VERIFIED

`CSWSCreature::DoDamage` carries a second, PC-only floor:

```
0x004F39C1  push 5
0x004F39C3  call 0x4AE9F0             ; CServerExoApp::GetDifficultyOption(5)
0x004F39C8  cmp  eax, 1
0x004F39CB  jne  skip
0x004F39CD  cmp  dword [ebp+0x9D4], 1 ; is_pc
0x004F39D4  jne  skip
0x004F39D6  call vtbl+0x9C (1)        ; hp
0x004F39E6  sub  ecx, ebx             ; hp - damage
0x004F39E8  cmp  ecx, 1
0x004F39EB  jge  skip                 ; survives anyway
0x004F39FA  movsx ebx, ax             ; else damage := hp - 1
0x004F39FD  dec  ebx
0x004F3A00  xor  ebx, ebx             ; and 0 if they were already at 0
```

`GetDifficultyOption(option, difficulty)` (0x004AE9F0 → internal 0x004B1DF0 →
`CSWRules::GetDifficultyOption` 0x00550C30) is a byte lookup at
`CSWRules + 0xBC + option + 6*difficulty`, filled by `LoadDifficultyInfo` (0x00550C50) from
**`diffsettings.2da`**:

```
row    name                dmeasy  easy  normal  hardcore  dmplayers
0      NoCriticalOnPC      1       1     1
1      NoAoOWithRanged     1       1     1
2      NoAoOWithPotion     1       1     1
3      MinPCDamagePercent  100     50
4      MaxNPCDamagePercent 25      50    100     100       200
5      MinHP1              1
```

Option **5 is `MinHP1`**, and only the `dmeasy` column (difficulty index 0) holds a 1.

The difficulty index it is looked up with is `CServerInfo + 0x108`, and there is exactly one
writer in the whole binary: `CServerExoAppInternal::GetServerInfoFromIniFile` (0x004B1B00)
calls `CServerInfo::SetDifficultyLevel(2)` at **0x004B1BA5** — a hard-coded 2, "normal", where
the cell is blank. `SetDifficultyLevel` has no other caller, the constructor (0x0054EB10)
never initialises the field, and `GetServerInfoFromIniFile` runs from `StartServices`
(0x004B6881) alongside the allocation, so the pairing always holds.

The in-game difficulty slider does **not** touch it: it writes a byte at
`CClientExoApp::GetClientOptions() + 4` (0..2, persisted as `Difficulty Level` in the ini —
`CSWGuiInGameGameplay::OnDifficultyLeft` 0x006E68E0), and the only thing that byte does to
damage is pick the `MULTIPLIER` column of `difficultyopt.2da` (0.5 / 1.0 / 1.5) applied to
party members in `CSWSCreature::DoDamage` at 0x004F38B8.

So `MinHP1` cannot fire in retail KOTOR 1. The plugin still *measures* it
(`Odyssey.IsDifficultyMinOneHitPointOn`) rather than asserting it, because "the binary says
it cannot happen" is precisely the class of claim that turns out to be wrong once.

## 4. A signedness bug this dug up — VERIFIED

`Odyssey.GetCurrentHitPoints` read `+0xDC` as a **ushort**. The engine's own getter returns
AX and every caller sign-extends (`movsx eax, ax` at 0x004CCF97, 0x004F39E3), and
`OnApplyDeath` parks a dead body at **-11** (0x004E0D8F). Unsigned, a downed creature reports
**65525** hit points — healthier than it has ever been, at the exact moment that matters.
The agent side already read this signed and said why (`KotorGameBridge`, two call sites);
the plugin was the odd one out. Now `short`. `tools/sethp.ps1` had the same bug and now also
prints the `min1hp` and `plot` flags, so "why is this thing stuck at 1 hp" is one command.

## 5. What co-op does about it

The rule is the game's, so the plugin honours it and **says so** rather than fighting it.

`IncomingDamageController` still applies every point of forwarded damage through the engine's
own `EffectDamage` — inside a pinned scene the engine floors it at 1 and the health bar
tracks the beating exactly as it would for a local fight. After each application it reads the
three flags off the player and publishes the verdict:

- `SharedBlock.OffsetDownBlockReason` (0x1F60) — 0 none, 1 min1hp, 2 plot, 3 MinHP1
- `SharedBlock.OffsetDownBlockSerial` (0x1F64) — bumped on change

and logs one line per transition naming the floor. Classification order is plot → MinHP1 →
min1hp, i.e. furthest upstream first: whichever one is reported is the one that would have to
go before any of the others mattered. That ordering is unit-tested (`DownBlockTests`) because
the symptoms differ — plot means hit points never move, min1hp means they fall to exactly 1 —
and naming the wrong flag sends the next session hunting in the wrong place.

**Nothing clears the pin.** Writing `+0x200 = 0` on the PC would let a player die on a deck
where KOTOR guarantees they cannot, desynchronising co-op from single-player and from the
scripted scene that set it (the Endar Spire's opening exists to be survivable). If a session
wants a downable player, it should leave the Command Deck — the module clears the pin itself
on entering the Starboard Section.

### Risk register

| Lever | Risk | Why |
| --- | --- | --- |
| Reading `+0x200` / `+0xF8` | NONE | plain int reads on a resolved server object |
| `GetDifficultyOption(5)` call | LOW | const method, two derefs and a byte index into `CSWRules`; needs a live server (guarded) |
| Signed hit-point read | LOW | strictly more correct; the only callers compare against a positive target health |
| Publishing 0x1F60/0x1F64 | NONE | reserved range, plugin→agent, no protocol version change |
| Clearing the module's pin | NOT DONE | would break the module's own guarantee; see above |

## Verification tags

- **VERIFIED** (disasm): all three floors and their exact clamp sites; `GetDeadTemp`'s
  `hp <= 0` test and the `GetDead‖GetDeadTemp` gate on the Death effect; the
  `diffsettings.2da` index arithmetic in `LoadDifficultyInfo`/`GetDifficultyOption`; the
  single hard-coded `SetDifficultyLevel(2)`; the client-options difficulty byte feeding only
  `difficultyopt.2da` MULTIPLIER; the signed hit-point reads.
- **VERIFIED** (shipped game data): `diffsettings.2da` contents; `m01aa.are`/`m01ab.are`
  OnEnter resrefs; the `SetMinOneHP(GetFirstPC(), …)` set/clear pairs and the full
  five-script sweep; nwscript routine indices 220/548/716.
- **INFERRED**: that the observed 1-hp stall on the client was this pin specifically —
  the mechanism and the module script are both proven, but the flag was never read off the
  live client. This is the NEEDS-LIVE item below.
- **NEEDS-LIVE**: read `+0x200` on the client's PC while on the Command Deck (expect 1) and
  again in the Starboard Section (expect 0); confirm the new log line fires; confirm
  forwarded damage downs a player once the pin is gone; confirm
  `IsDifficultyMinOneHitPointOn()` reads false.

## Key addresses

```
0x004CCF80  CSWSObject::DoDamage                 (min1hp clamp 0x004CCFAB)
0x004CC280  CSWSObject::SetCurrentHitPoints      (min1hp clamp 0x004CC299)
0x004E0AC0  OnApplyDeath                         (min1hp -> SetCurrentHitPoints(1) 0x004E0AF8)
0x004EF890  CSWSCreature::GetDeadTemp            (computed: party member && hp <= 0)
0x004EF8FD    the signed `test ax,ax; setle` that IS the downed state
0x004E0240  OnApplyDamage's death decision       (GetDead || GetDeadTemp)
0x004F3830  CSWSCreature::DoDamage
0x004F38B8    difficultyopt MULTIPLIER for party members
0x004F39B8    the MinHP1 difficulty floor (is_pc only)
0x004AE9F0  CServerExoApp::GetDifficultyOption   -> 0x004B1DF0 -> 0x00550C30
0x00550C30  CSWRules::GetDifficultyOption        (CSWRules+0xBC + opt + 6*difficulty)
0x00550C50  CSWRules::LoadDifficultyInfo         (diffsettings.2da, 6 rows x 5 columns)
0x004B1B00  CServerExoAppInternal::GetServerInfoFromIniFile
0x004B1BA5    the ONLY SetDifficultyLevel call site, argument hard-coded 2
0x0054EA70  CServerInfo::SetDifficultyLevel      (writes +0x108, clamps to <= 4)
0x004B63E0  CServerExoAppInternal::StartServices (allocates CServerInfo, calls the above)
0x005ED700  CClientExoApp::GetClientOptions      (+4 = the GUI difficulty byte, 0..2)
0x006E68E0  CSWGuiInGameGameplay::OnDifficultyLeft
0x00545F90  ExecuteCommandSetMinOneHP            (nwscript routine 716)
0x0063E7F0  Global::return_zero                  (kills the dead "knocked out" branch at 0x004E015D)
```
