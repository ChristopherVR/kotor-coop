# Shared progression sync in swkotor.exe (research 2026-07-31)

Problem: two co-op instances each run their own full simulation. Party **gold**,
character **XP/level**, and **inventory** all live in per-instance engine state and will
drift (or double-count) unless one instance is made authoritative and the others mirror it.

Every address below is labelled **VERIFIED** (checked against the binary and/or the symbol
DB for this exact build), **INFERRED** (reasoned from the disassembly but not observed
live), or **NEEDS-LIVE** (must be confirmed on the running game). Read `docs/re/object-model.md`
(creature layout, `is_pc`, no-ASLR), `docs/re/spawn.md` (the VM-routine calling contract),
and `docs/research/party-reconciliation.md` (party table layout, keying on NPC slot) first.
This document only reads memory and the symbol DB; nothing was written to the game.

Pointer roots reused from those docs, all **VERIFIED**:

```
appMgr     = *(u32*)0x007A39FC                        ; APP_MANAGER_PTR
serverApp  = *(u32*)(appMgr + 0x08)                   ; CServerExoApp*
internal   = *(u32*)(serverApp + 0x04)                ; CServerExoAppInternal*
partyTable = internal + 0x0001B770                    ; CSWPartyTable (embedded)
             == CServerExoApp::GetPartyTable(serverApp)   0x004AEE70
pc         = creature with is_pc==1, object id 0x7FFFFFFF   (vtable-scan 0x007470C8)
stats      = *(u32*)(pc + 0x0A74)                     ; CSWSCreatureStats*
```

---

## 1. Party gold / credits

### Where it lives

**Party gold is a single field on the party table: `CSWPartyTable+0x0FC` (`pt_gold`, u32).**
**VERIFIED** — the DB names the offset (`0xFC pt_gold`) and both accessors dereference it.

`CSWSCreature::GetGold` (`0x004EDD60`, `__thiscall`) is the whole story:

```
0x004EDD60  mov eax,[ecx+0xA88]     ; creature->in_party flag (SetInParty target, party-recon.md)
0x004EDD66  test eax,eax
0x004EDD68  je  0x4EDD7E            ; not in party -> private gold
0x004EDD6A  mov eax,[0x7A39FC] ; appMgr
0x004EDD6F  mov ecx,[eax+8]         ; serverApp
0x004EDD72  call 0x4AEE70           ; GetPartyTable
0x004EDD77  mov eax,[eax+0xFC]      ; return partyTable->pt_gold
0x004EDD7D  ret
0x004EDD7E  mov eax,[ecx+0x9D0]     ; return creature->private_gold   (merchants, corpses)
0x004EDD84  ret
```

So there are **two** gold stores and `creature+0xA88` (the in-party flag) selects between
them:

| store | field | who uses it |
| --- | --- | --- |
| **party wallet** | `partyTable+0x0FC` | every party member (PC + companions); this is the "Credits" number the UI shows |
| private gold | `creature+0x9D0` | non-party creatures — merchants, containers, dropped-on-death piles |

**We only care about `partyTable+0x0FC`.** It is exactly the field the reconciliation doc
predicted would be "near `CServerExoAppInternal+0x1B770`", and it is: base `+0x1B770`, field
`+0xFC`. **VERIFIED.**

### Read path

```
gold = *(u32*)( (internal + 0x1B770) + 0xFC )
```

### Mutators (all VERIFIED __thiscall on a creature)

| Fn | Addr | ABI | Effect |
| --- | --- | --- | --- |
| `GetGold` | `0x004EDD60` | `(creature)` | returns the wallet (or private) value |
| `SetGold` | `0x004EDD90` | `(creature, int v)` ret 4 | clamps `v` to `0x3B9AC9FF`, writes wallet or private |
| `AddGold` | `0x004F3DB0` | `(creature, int delta, int bFeedback)` | `v = clamp(cur+delta)`, writes; feedback path builds a GUI credit-roll |
| `RemoveGold` | `0x004F3ED0` | `(creature, int delta, ...)` | symmetric subtract |
| `TransferGold` | `0x004FD730` | | creature-to-creature |

`SetGold` clamp is load-bearing — **max gold is `0x3B9AC9FF` = 999,999,999** (`cmp esi,0x3B9AC9FF; jle`).
**VERIFIED.** Any write we do must respect it or the engine's own next `AddGold` will read a
value it never clamped.

NWScript surface (VM routines, same calling contract as `CreateObject` in `spawn.md`):
`ExecuteCommandGetGold 0x00539030`, `ExecuteCommandGiveGoldToCreature 0x0053E690`,
`ExecuteCommandTakeGoldFromCreature 0x00544970`. `GiveGoldToCreature` (**VERIFIED** disasm)
resolves the target creature, calls `AddGold(amount, 1)`, and **if the target is in-party
(`+0xA88`) additionally calls `0x0062EEB0` via `0x005ED690`** — the on-screen credits
feedback/GUI refresh. So the *only* thing a raw field write skips versus the engine path is
that feedback animation.

### Safest write

**Prefer a direct field write to `partyTable+0x0FC`, clamped to `0x3B9AC9FF`.** It is
byte-identical to what `SetGold` does on its in-party branch, touches no VM stack, fires no
script, and cannot desync `ESP`. This is strictly safer than any engine call and is the
recommended host-authoritative mirror mechanism.

- Field write — **safe, recommended.** Just `*(u32*)(partyTable+0xFC) = min(v, 0x3B9AC9FF)`.
- `SetGold(pc, v)` `__thiscall` — safe ABI (verified `ret 4`), but needs a valid `this`
  creature and does the identical write; no advantage over the field poke.
- `GiveGoldToCreature` VM route — **only** if you want the on-screen credit-roll animation;
  costs a 2-arg VM push/pop sequence and the double-count exposure below. Flag as
  higher-risk (guessed-nothing, but it re-enters the feedback GUI).

**NEEDS-LIVE:** whether the persistent HUD/UI credits readout refreshes after a bare field
write, or only when the inventory/equip screen is next opened (KOTOR shows Credits only
inside those GUIs, which re-read on open — so a field write is expected to be sufficient,
but confirm).

---

## 2. Experience / level

### Where XP lives

**`CSWSCreatureStats+0x068` (`experience`, u32).** **VERIFIED** — DB offset, and it is the
value the live census in `object-model.md` printed as `XP 3155` matching the character sheet.
Neighbours on the same struct: `+0x024` creature back-pointer, `+0x06C is_pc`,
`+0x078 faction_id`. The stats block hangs off the creature at `creature+0x0A74`. **VERIFIED.**

Read path for the local PC:

```
xp = *(u32*)( *(u32*)(pc + 0x0A74) + 0x068 )
```

There is also a **party XP pool** at `partyTable+0x0F8` (`pt_xp_pool`, i32) — an accumulator,
not per-character progression. **VERIFIED** (DB + `DistributeExperience` increments it).

### The grant chain (all VERIFIED __thiscall unless noted)

| Level | Fn | Addr | ABI | Role |
| --- | --- | --- | --- | --- |
| stats, raw | `CSWSCreatureStats::SetExperience` | `0x005AF480` | `(stats, int xp)` | overwrite `+0x068` |
| stats, add | `CSWSCreatureStats::AddExperience` | `0x005AF6A0` | `(stats, int xp)` | add to `+0x068`, consults class/level-cap 2DA (`RULES_PTR 0x7A3A28 → +0xB0`) |
| creature | `CSWSCreature::ReceiveExperience` | `0x004EF930` | `(creature, int xp)` | per-creature grant + journal/floaty-text feedback |
| party | `CSWPartyTable::DistributeExperience` | `0x005653A0` | `(partyTable, int xp)` | `pt_xp_pool += xp`, then grants **each** member the **full** `xp` and flags level-up availability |
| plot | `CSWPartyTable::GivePlotXP` | `0x00566600` | | quest XP |

`DistributeExperience` (**VERIFIED** disasm) is the hub. Loop body per member:

```
member slot  = partyTable[+0x04 + i*4]            ; NPC slot index (0..8), per party-recon.md
member objid = partyTable[+0x0C + slot*4]
creature     = GetCreatureByGameObjectID(objid)   0x004AE770
ReceiveExperience(creature, xp)                    0x004EF930   ; FULL xp, not divided
if (canLevelUp(stats @ +0xA74))  set level-up-available flag    0x005B27E0(1)
```

Note it hands **every** member the same full `xp` — KOTOR does not split XP. **VERIFIED.**

VM surface: `ExecuteCommandGiveXPToCreature 0x0053E750`, `ExecuteCommandGivePlotXP 0x00545E20`,
`ExecuteCommandSetXP 0x005437B0`, `ExecuteCommandGetXP 0x0053E600` (**VERIFIED** — resolves
object → `AsCreature` via `vtable+0x30` → reads stats XP).

### How level-up is triggered

XP **accrues**; the character does **not** auto-level. `DistributeExperience` only *flags*
that a level-up is available (`GetExpNeededForLevelUp 0x005A6610` / `CalculateExpNeededForNextLevel 0x005A5490`
→ notify `0x005B27E0`). The actual class-up is player-driven through the level-up GUI.
So mirroring `+0x068` changes the number and lights the "level up" prompt but never spends
the level for the player. **INFERRED** (from the flag-only path; the GUI half was not traced)
— **NEEDS-LIVE:** confirm that writing `+0x068` past the threshold lights the level-up
indicator, and that a mirrored client's manual level-up doesn't diverge stats/feats from host.

### Kill-reward path — the double-count origin

Enemy death drives XP with **no network awareness**:

```
CSWSCreature::ApplyDeathExperience   0x004FB1E0   (on the dying creature)
   -> CalculateDeathExperience       0x004F19E0
   -> CSWPartyTable::DistributeExperience 0x005653A0   (call site 0x004FB308, VERIFIED xref)
```

**VERIFIED** that `ApplyDeathExperience` reads a per-corpse guard at `creature+0x9D4`
(prevents re-awarding the same corpse) and then distributes. There are **10 call sites** into
`DistributeExperience` (xref: `0x004FB308`, `0x005087CB` stealth, `0x0052DE5A` VM,
`0x0053E817` GiveXP VM, `0x0054860D` journal, `0x005666AF`/`0x0056678F` plot,
`0x0059F02F` dialog, `0x0060A9CC`/`0x0060B25B`). Each instance fires these **locally** for
its own copy of every kill/quest — so both instances independently award the same XP. That
is the core desync/double-count hazard for progression (section 4).

### Safest write

**Field write `stats+0x068` for absolute mirroring** is the low-risk mechanism (no VM, no
2DA re-entry), analogous to the gold field poke. Use `SetExperience 0x005AF480` only if a
verified `this=stats` is convenient — it does the same store plus internal bookkeeping the
raw poke skips (**NEEDS-LIVE** whether that bookkeeping matters for the level-up prompt).
Avoid `AddExperience`/`DistributeExperience` for sync: they are *additive* and re-run the
per-instance award you are trying to deduplicate.

---

## 3. Key-item / inventory (feasibility only, not deep)

Enough to judge later item-grant sync:

| store | field | notes |
| --- | --- | --- |
| creature equipped slots | `CSWSCreature+0x0A2C` → `CSWInventory*` | vtable at `+0`, then `head/torso/hands/right_weapon/left_weapon/...` object ids at `+0x04..` (**VERIFIED** DB) |
| creature backpack | `CSWSCreature+0x0A30` → `CItemRepository*` | the loose-items container |
| **party shared loot** | `CSWPartyTable+0x118` (`pt_item_repo`) → `CItemRepository*` | the shared inventory both players' UIs read |
| repository contents | `CItemRepository+0x0C` `items_list` (ptr to id array), `+0x10` `item_count` | size `0x18` (**VERIFIED**) |

VM helpers exist if item-grant sync is pursued: `ExecuteCommandGetItemInInventory 0x00549B50`,
`ExecuteCommandGetHasInventory 0x005392D0`, plus `CSWPartyTable::TransferInventory 0x005641E0`
/ `UpdateInventory 0x00564030`. **Feasibility: YES, later.** An item is a full `CSWSItem`
object (its own blueprint/resref + stack count), so item sync is a create-from-template +
add-to-repository problem, materially heavier than the two scalar fields above and out of
scope here. Don't build on the repository internals until they're traced.

---

## 4. Recommended host-authoritative design

Model this exactly like the existing party/enemy mirror channels: **host state is truth;
clients overwrite their local copy each tick; clients never additively apply progression.**

**What the host publishes** (new `ProgressionSnapshot`, one per tick or on-change):
- `gold` = `*(u32*)(partyTable+0xFC)`.
- `pcXp` = `*(u32*)(stats+0x068)` for the host PC (key by the PC, which is always
  `is_pc==1` / id `0x7FFFFFFF`; re-resolve each frame — PC death reassigns "the player",
  per party-recon.md §Death guard).
- optionally `xpPool` = `partyTable+0xF8`, and per-companion XP keyed by **NPC slot index**
  (0..8), never object id — same keying rule the party channel already uses.

**What clients apply** (field writes only, on the main/frame thread — never a worker thread,
never mid-script; same guards as `spawn.md`):
- `*(u32*)(partyTable+0xFC) = min(host.gold, 0x3B9AC9FF)`.
- `*(u32*)(stats+0x068) = host.pcXp` for the client's own PC creature (and per-slot for
  companions).
- Idempotent by construction: writing the same absolute value repeatedly is a no-op, so a
  dropped snapshot self-heals on the next tick — the same property the position mirror relies
  on.

This degenerates cleanly to "host is truth, client mirrors" and needs **no engine calls** in
the steady state — only two or three scalar field pokes, which is why it is low-risk.

### Desync / double-count risks (ordered by how likely to bite)

1. **Both instances award the same kill's XP (the big one).** Every `DistributeExperience`
   call site fires locally on each machine (section 2). If you *additively* combined XP you
   would roughly double every gain; if combat outcomes differ (different RNG/timing) the two
   `+0x068` values diverge outright. **Mitigation: absolute mirror, single authority.** Host
   value overwrites the client's locally-awarded value each tick, so the client's redundant
   local award is harmlessly discarded. Do **not** additively grant.
2. **Same for gold.** Loot piles, quest rewards, and merchant sales all run
   `AddGold`/`GiveGoldToCreature` locally on both sides. Absolute mirror of `partyTable+0xFC`
   makes the client's independent awards moot — correct, because both worlds picked up the
   same credits. **Never** ship gold *deltas*; ship the absolute wallet.
3. **Client-side gold/XP mutation between snapshots is briefly visible** (client spends
   credits at a store, then the next host snapshot overwrites). Acceptable for a mirror;
   if it flickers, additionally suppress client-initiated store/quest mutation, or raise
   snapshot rate on gold-change events. **NEEDS-LIVE.**
4. **Level-up is player-local and manual.** Mirroring `+0x068` lights the prompt on both
   sides but each player levels their own PC independently; feats/skills chosen can diverge.
   That is arguably desirable (each player builds their own character) but means "same XP" ≠
   "same sheet". Decide policy explicitly. **NEEDS-LIVE.**
5. **Max-gold clamp.** Any write must clamp to `0x3B9AC9FF`; an un-clamped value the engine
   later feeds to `AddGold` is compared but the stored value was never sanitised. Low
   severity, trivially mitigated.
6. **GUI refresh.** Field writes may not repaint an already-open Credits/XP readout until the
   screen reopens (section 1/2 NEEDS-LIVE). If it must repaint live, fall back to the VM
   `GiveGoldToCreature` feedback path (`0x0062EEB0`) for gold — at the cost of re-entering
   that GUI code.

### Size

**SMALL.** Two u32 field reads on host, two/three u32 field writes on client, plus optional
per-slot companion XP reuse of the existing slot-keyed party channel. Shared-block gains a
`ProgressionSnapshot` (gold, pcXp, optional xpPool + companion XP[9]). **No new engine call
is required** for the steady-state mirror — unlike the party/spawn work, this is pure field
I/O. The only genuinely new work is the snapshot plumbing and the death-guard interaction
already solved for the party channel.

---

## 5. Load-bearing constants (this build, VERIFIED unless noted)

| | |
| --- | --- |
| `CSWPartyTable::pt_gold` | `partyTable+0x0FC` (u32) |
| `CSWPartyTable::pt_xp_pool` | `partyTable+0x0F8` (i32) |
| `CSWPartyTable::pt_item_repo` | `partyTable+0x118` (`CItemRepository*`) |
| party table base | `CServerExoAppInternal+0x1B770`; `GetPartyTable 0x004AEE70` |
| gold clamp (max) | `0x3B9AC9FF` = 999,999,999 |
| `creature->in_party` flag | `creature+0xA88` (selects wallet vs private gold) |
| `creature->private_gold` | `creature+0x9D0` (non-party only) |
| `creature->creature_stats` | `creature+0x0A74` (`CSWSCreatureStats*`) |
| `CSWSCreatureStats::experience` | `stats+0x068` (u32) |
| `CSWSCreatureStats::is_pc` | `stats+0x06C` |
| `creature->inventory` | `creature+0x0A2C` (`CSWInventory*`, equip slots) |
| `creature->item_repo` | `creature+0x0A30` (`CItemRepository*`, backpack) |
| `CItemRepository.items_list / item_count` | `+0x0C` / `+0x10`, size `0x18` |
| `GetGold` / `SetGold` / `AddGold` / `RemoveGold` | `0x004EDD60` / `0x004EDD90` / `0x004F3DB0` / `0x004F3ED0` |
| `GiveGoldToCreature` VM / feedback GUI | `0x0053E690` / `0x0062EEB0` |
| `SetExperience` / `AddExperience` | `0x005AF480` / `0x005AF6A0` |
| `ReceiveExperience` | `0x004EF930` |
| `DistributeExperience` | `0x005653A0` |
| `ApplyDeathExperience` / `CalculateDeathExperience` | `0x004FB1E0` / `0x004F19E0` |
| `GetExpNeededForLevelUp` / `CalculateExpNeededForNextLevel` | `0x005A6610` / `0x005A5490` |
| `GiveXP` / `GivePlotXP` / `SetXP` / `GetXP` VM | `0x0053E750` / `0x00545E20` / `0x005437B0` / `0x0053E600` |

### Known unresolved (NEEDS-LIVE)
- Does a bare `partyTable+0xFC` / `stats+0x068` write repaint an open Credits/XP GUI, or only
  on next screen open?
- Does writing `+0x068` past a threshold light the level-up indicator without engine help?
- Policy: mirror host PC XP absolutely (identical levels) vs. let each player level
  independently (divergent sheets from the same XP number)?
- Companion XP: verify each companion's `stats+0x068` tracks the pool as expected before
  keying a per-slot companion-XP mirror. **Done — see companion-state-sync.md §6.**

## Live-verified 2026-08-27 — party gold convergence

`progression_sync` is default ON and had never been run against a real two-instance rig
before this round (no prior mention in `docs/live-verification.md`). A `grantgold <amount>`
dev verb was built to reach it without a real vendor purchase (no GUI click this rig can
reliably deliver): it calls `KotorGameBridge.TryReadProgression`/`TryWriteProgression`
directly, agent-side — the exact `partyTable+0xFC` field read/write this doc's own "safest
write" recommendation already describes, with no plugin/cheat-channel round trip needed. See
`docs/CONSOLE.md`.

Two-instance rig (`tools/demo.ps1`), `end_m01ab`, both starting at 0 gold:

1. `grantgold 500` on the CLIENT → client reads 0 → 500. Within 5 s, the HOST independently
   read 500 → 500 — converged.
2. `grantgold 250` on the HOST (now 500 → 750) → within 5 s, the CLIENT read 750 → 750 —
   converged, symmetric with step 1.

**PASS, both directions, no code changes needed** — `PumpProgression`'s existing ledger merge
(host spends every peer's outstanding contribution against the party table, publishes the
result; a client reports its own contribution then mirrors the host) worked exactly as
designed on the first real attempt. This also stands in for a real vendor-purchase scenario
per the task's own framing: a `grantgold`-simulated trade is the same shape of local-gold
change `ObserveLocalGold` infers from a real purchase (a level change with no engine "gold
changed" event to hook), so the actual mechanism under test — does the gold LEDGER converge —
is exercised identically whether the change came from a merchant GUI or this dev verb.

## What ships for XP max-merge (2026-09-02)

Roadmap item 3 (`docs/roadmap-seamless.md`) still frames XP sync as a claim to be
live-verified. It has now been live-verified: on 2026-09-02 a client's kill raised the shared
PC's XP `150 -> 300` on **both** sides, with no double-count. This section pins down exactly
what code produced that, so the claim doesn't rest on the live observation alone.

### What is published, by whom

- **PC XP is a single absolute `uint`, not a ledger — unlike gold.** The host reads its own
  `stats+0x068` each tick (`bridge.TryReadProgression`, called from
  `Pumps/PartyPump.cs:181`), merges it against every live peer report, and broadcasts the
  merged absolute value every tick via `CoopHost.PublishProgression` →
  `BroadcastProgression` (`src/KotorCoop.Session/CoopHost.Progression.cs:16-24` and
  `:107-137`), wrapped in `ProgressionSnapshotPacket`.
- **A client never publishes its own XP as truth.** `CoopClient.PublishProgression`
  (`src/KotorCoop.Session/CoopClient.Progression.cs:17-19`) is an empty stub — "only the host
  is authoritative for progression; a client's own view is dropped" (doc comment on that
  file). Instead a client sends a `ProgressionReportPacket` every tick via
  `CoopClient.ReportProgression` (`src/KotorCoop.Session/CoopClient.Progression.cs:31-49`),
  carrying its own locally-read `experience` alongside the gold-ledger fields (which travel
  differently — see §1/§4 above).
- The host keeps the latest report **per peer id**, discarding anything older than
  `ProgressionReportFreshMs = 15_000` ms (`src/KotorCoop.Session/CoopHost.Progression.cs:50`,
  `GetProgressionReports` at `:57-79`). This freshness window only controls how long a
  departed peer's report stays *eligible for the max*; it never causes a value to be
  subtracted or rolled back once it has been folded in (see "rejoin" below).

### How a peer reconciles: max, not sum, not host-only-authoritative for the value's origin

The merge rule is **max-merge**, applied on **both** ends, exactly as
`Pumps/PartyPump.cs`'s own doc comment (lines 162-170) states and as the code implements:

- **Host side** — `PumpProgressionHost` (`src/KotorCoop.Session/Pumps/PartyPump.cs:212-265`):
  `mergedExperience` starts at the host's own freshly-read `experience` (line 215), then for
  every fresh `PeerProgression` report, `mergedExperience = Math.Max(mergedExperience,
  report.Experience)` (line 219) — a plain per-tick fold, no ledger, no per-peer XP state
  retained across ticks. If the merged value differs from what the host's engine currently
  holds, the host writes it straight back into its own party table
  (`bridge.TryWriteProgression`, line 257) *before* publishing (line 264). That write-back is
  what makes the host's own next read (line 181, next tick) start from the elevated value
  rather than from whatever a single kill locally granted — this is the mechanism that
  prevents a later, lower/stale report from ever pulling the merged value back down (see
  "rejoin" below).
- **Client side** — `PumpProgression`, client branch (`Pumps/PartyPump.cs:194-205`): a client
  reports its own reading (line 195), then applies `Math.Max(hostExperience, experience)`
  (line 201) — "never below our own XP: a max on both ends means neither instance can pull
  the other backwards while a report is in flight" (comment at lines 199-200). So a client's
  own locally-granted XP (e.g. its own kill) is never stamped down by an in-flight host
  snapshot that hasn't caught up yet, and the host's higher value is never refused either.
- There is **no separate "who owns this delta" bookkeeping for XP**, unlike gold's
  `_appliedProgression`/generation ledger (`Pumps/PartyPump.cs:148-150`, `:221-247`). That
  ledger machinery exists *only* for gold, which can go down (spending) and therefore needs
  cumulative earned/spent accounting to avoid double-applying a report. XP only ever goes up
  in this design, so a stateless per-tick max is sufficient and is all that's implemented.

**Why no double-count, precisely:** both engines already grant kill XP independently through
the real engine pipeline — a client's mirrored copy of a shared enemy dies through
`KillCreature`/`ApplyDeathExperience` exactly as the host's own copy does (doc comment,
`Pumps/PartyPump.cs:164-170`; the underlying engine hooks are `ApplyDeathExperience
0x004FB1E0` / `DistributeExperience 0x005653A0`, §2 above, **VERIFIED** from disassembly,
**INFERRED** that both sides' mirrored copy of a synced enemy actually triggers this
independently — not separately re-confirmed live this round). The network layer is not
*granting* XP at all; it is only ever taking the max of two numbers that a local kill already
produced on each side. Forwarding a **delta** instead (as gold does) would have summed two
independent full-XP grants for the same kill and double-counted it — which is exactly why
`Pumps/PartyPump.cs`'s doc comment calls this out as the reason gold and XP "do not travel the
same way" (lines 157-170). The 2026-09-02 observation (`150 -> 300` on both sides, not
`150 -> 450`) is consistent with a single grant reaching both sides via the max-merge, not a
summed double-grant.

### Rejoin / reload: does a stale lower value ever win?

**No — verified in code, and now pinned by a unit test on each side of the channel.** The
mechanism is structural, not a special case:

- On the **host**, a peer report is combined via `Math.Max` against the host's *own current
  reading* (`Pumps/PartyPump.cs:215,219`), and that own reading already reflects any earlier
  merge because the host wrote the merged value back into its own party table
  (`bridge.TryWriteProgression`, line 257) on the tick that produced it. A peer that
  reconnects — or simply reports again — with a lower value (an older save, a stale local
  cache, a reordered datagram) therefore cannot regress the host's merged truth: `Math.Max`
  against an already-higher local reading just keeps the higher one. Pinned by
  `tests/KotorCoop.Tests/ProgressionPumpTests.cs::AStaleLowerReportNeverPullsTheHostsExperienceBackward`.
- On a **client**, the identical guarantee runs the other way: `Math.Max(hostExperience,
  experience)` (`Pumps/PartyPump.cs:201`) means a client rejoining with a stale lower local
  reading is pulled *up* to the host's already-elevated broadcast value, never held at its own
  lower number. Pinned by the pre-existing
  `ProgressionPumpTests::AClientTakesTheHostsHigherExperience`.
- **INFERRED, not live-verified this round:** that the real game engine's `stats+0x068` field
  write is durably read back on the next frame exactly as written (no clamp, no engine-side
  reset). The design assumes this — it is the same durability the gold field write already
  relies on and that *was* live-verified for gold on 2026-08-27 (see the section above) — but
  a rejoin/reload scenario specifically for XP (disconnect a peer, lower its save, reconnect,
  confirm the party does not regress) has not itself been run against the live two-instance
  rig. **NEEDS-LIVE.**

### Companion XP vs PC XP

Companion XP is a **separate channel**, `companion_xp_sync` (`AgentSettings.SyncCompanionXp`,
`src/KotorCoop.Session/AgentSettings.cs:278`, default **ON**), keyed **per NPC roster slot**
rather than a single scalar:

- Merge primitive: `CompanionExperienceMerge.MergeInto`
  (`src/KotorCoop.Session/CompanionExperienceMerge.cs:24-38`) — pure, slot-independent max:
  "a slot absent from BOTH inputs stays absent; a slot present in only one is adopted exactly
  as reported; a slot present in both takes the higher value" (doc comment, lines 13-14).
  Covered by `CompanionStateSyncTests::MergeIntoTakesTheHigherValueWhenBothSidesHaveASlot`,
  `::MergeIntoAdoptsASlotPresentOnlyInTheCandidate`, `::MergeIntoNeverInventsASlotAbsentFromTheCandidate`,
  `::MergeIntoKeepsEachSlotIndependent`.
- Pump shape mirrors the PC's exactly: host merges its own reading plus every peer's report
  (`PumpCompanionExperienceHost`, `Pumps/PartyPump.cs:363-380`), writes the merged result back
  into its own party table (line 376) before publishing (line 379); a client reports its own
  reading then merges the host's snapshot on top of its own local reading
  (`PumpCompanionExperience`, client branch, `Pumps/PartyPump.cs:336-355`).
- Gated on **both** `SessionCapabilities.Progression` and `SessionCapabilities.Party` (line
  318-319), unlike the PC's single scalar which needs only `Progression` — the roster-slot
  keying needs both instances to have loaded the same save (`party-reconciliation.md`).
  Covered by `CompanionStateSyncTests::CompanionXpNeedsBothTheProgressionAndPartyCapabilities`.
- Same rejoin/never-lowers guarantee as the PC's XP, now pinned by
  `CompanionStateSyncTests::ThreePeersConvergeToTheHighestCompanionXp` and
  `::AStaleLowerCompanionXpReportNeverPullsTheHostsMergedValueBackward` (added 2026-09-02,
  alongside the write-through fix to that test file's `CompanionBridge` double so
  `TryWriteCompanionExperience` feeds `TryReadCompanionExperience` on the next tick, matching
  the real engine's read-your-own-write behaviour instead of silently forgetting it).
- Kill XP's "both engines already grant it independently" claim was previously marked
  INFERRED for companion XP specifically (no primitive existed to trigger a real combat kill)
  — see `docs/roadmap-seamless.md` item 12. That remains true; this section does not change
  companion-XP live-verification status, only the PC-XP one (item 3).

### A 3-peer session

Both channels fold **every** live report into one `Math.Max` pass per tick — there is nothing
in the merge that assumes exactly one remote peer, unlike `SharedBlock.SoleRemotePhantomId()`
on the damage/creator-attribution side (see `docs/research/peer-damage-ledger.md` §8.6/§8.8).
`GetProgressionReports()` / `GetCompanionExperienceReports()`
(`src/KotorCoop.Session/CoopHost.Progression.cs:57-79`,
`CoopHost.CompanionProgression.cs:58-77`) return one entry per **connected peer id**, and the
host's loop (`Pumps/PartyPump.cs:217-219` for PC XP, `:369-371` for companion XP) visits all of
them regardless of count — three, four, or more peers converge to the single highest reading
in one host tick, then to everyone via the next broadcast. Pinned by two new tests added
2026-09-02: `ProgressionPumpTests::ThreePeersConvergeToTheHighestExperience` and
`CompanionStateSyncTests::ThreePeersConvergeToTheHighestCompanionXp`, each seeding three
distinct peer ids with different XP values and asserting the host converges on the highest one
and publishes it. **NEEDS-LIVE:** a real 3+ peer rig run was not exercised this round (still
covered only at the pump-unit level, as gold's own `TwoClientsBothContributeToOnePurse` test
already was for two peers).

## A gold read above the ceiling is a wrapped overdraft, not a fortune (2026-09-03)

Live 2026-09-03 01:06 (store-sync.md): a store purchase spent from the panel's stale gold
snapshot and the engine wrote `100 − 110` into `partyTable+0xFC` unsigned — 4294967286. The
host pump read that as a legitimate balance, clamped it to its 999,999,999 ceiling and
broadcast it; both purses became infinite.

`GoldReadingRule` (Session): `Max = 999_999_999` (the engine's own display ceiling),
`IsWrapped(raw) => raw > Max`, `Sanitise(raw) => IsWrapped ? 0 : raw`. `PartyPump.PumpProgression`
sanitises every read (logging `purse: party gold read {raw} - a wrapped overdraft, treating it
as 0`), the host clamps its merged purse to `Max` and compares against the RAW read so a
sanitised wrap is written back as 0, and the client baseline is `Math.Min(hostGold, Max)` so a
mirror above the ceiling is not reported as a phantom spend. Pinned by `GoldReadingRuleTests`
and two `ProgressionPumpTests` cases. The plugin-side cause (the store cache) is closed
separately; this is the belt for anything else that can drive the table below zero.

## XP baseline: a first report never raises the host (2026-09-18)

`ExperienceMerge` replaces the bare `Math.Max(host, report.Experience)` fold in
`PumpProgressionHost`. A peer's first report after join (or a new agent generation) is only a
baseline; afterwards the candidate is `hostBase + (peerNow - peerBase)`, still folded by max, so a
veteran 5,000 XP character joining a 500 XP save no longer lifts the host, while conversation XP
only the peer received still carries. A peer at or below the host re-baselines. Two consecutive lower
reports (a reload, which keeps peer id and generation) re-baseline; one is a reordered datagram and
is ignored. The publish, host write-back and three-peer folds above are unchanged. Done and covered by
`ExperienceMergeTests` and `ProgressionPumpTests`. NEEDS-LIVE: the rejoin case (reload a save
mid-session and confirm no regression or jump).
