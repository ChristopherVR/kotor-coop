# Dialogue consequences: getting a reward node's effect onto the OTHER instance (design, 2026-08-21)

Roadmap item 10. `docs/research/dialogue-sync.md` gets conversations to LOOK the same on both
instances (Layer 1/2, live-verified — see its "Node sync" and "spectate controller" sections).
It never runs `HandleReply` on the spectating side, so a reward node's SCRIPT executes only on
the instance that owns the token. This document is the design for closing that gap: which
reward kinds actually occur, which of them can legitimately diverge per player, and the wire
for the ones that cannot. **No code was written and no game was run for this document** — it
is deliberately structured so the orchestrator can turn each numbered section into a build task.

Tag key, same as the rest of this research trail: **VERIFIED** (read from the binary, or
measured against the real game files) · **INFERRED** (reasoned, not observed) · **NEEDS-LIVE**
(must be confirmed on the running game before shipping).

## 1. What reward kinds actually occur, and how common each is

### 1.1 Method

`tools/kotorfiles.py` already has GFF and NCS-adjacent readers (`docs/research/mod-compatibility.md`
names it as the source the mod-compat scanner should build on; `docs/research/no-re-features.md`
confirms it "decompiles NCS → NSS pseudocode"). This pass used it against the real GOG install
at `D:\Games\swkotor-gog-p2` — 1,135 `.dlg` files across 117 modules, every planet — rather than
guessing from memory. Two things were computed, both **VERIFIED**:

1. **The VM routine addresses.** `tools/q.py`'s symbol DB (`tools/kotor1_0_3.db`, the same
   sha256-matched DB every other doc in this tree cites) has `functions` rows for
   `ExecuteCommandCreateItemOnObject` (`0x0052F570`), `ExecuteCommandGiveItem` (`0x0053E830`,
   a K1-specific addition distinct from `CreateItemOnObject`), `ExecuteCommandAdjustAlignment`
   (`0x0052DFE0`), `ExecuteCommandAddPartyMember` (`0x0052DE70` — cross-checked against
   `engine-api.md`'s independently-published address for the same symbol), `ExecuteCommandGiveXPToCreature`
   (`0x0053E750`) and `ExecuteCommandGivePlotXP` (`0x00545E20`, both already known from
   `progression-sync.md`), and `ExecuteCommandGiveGoldToCreature` (`0x0053E690`, already known
   from `console-cheats.md`).
2. **Their NWScript routine numbers.** `tools/xrefs.py` found the one place each address is
   stored (the VM command-table population routine, the same shape `min-one-hitpoint.md`
   already used for `SetMinOneHP`=716). `tools/disasm.py` on those sites reads
   `mov dword [table+OFF], <addr>`; `OFF / 4` is the routine index, and this method
   self-validates: `mov dword [table+0x3CC], 0x52f820` at one of the sites reproduces
   `spawn.md`'s independently-verified anchor **`CreateObject = routine 243`**
   (`0x3CC / 4 == 243`, address `0x52f820` matches exactly). The computed indices:

   | Function | Address | Table offset | Routine # |
   | --- | --- | --- | --- |
   | `CreateItemOnObject` | `0x0052F570` | `+0x7C` | 31 |
   | `GiveItem` (K1-only, not standard nwscript) | `0x0053E830` | `+0x43C` | 271 |
   | `AdjustAlignment` | `0x0052DFE0` | `+0x324` | 201 |
   | `AddPartyMember` | `0x0052DE70` | `+0x8F8` | 574 |
   | `GiveXPToCreature` | `0x0053E750` | `+0x624` | 393 |
   | `GivePlotXP` | `0x00545E20` | `+0xB28` | 706 |
   | `GiveGoldToCreature` | `0x0053E690` | `+0x508` | 322 |

A scratch scanner (not committed — it lived at
`%TEMP%\...\scratchpad\dlg_scan.py`, reproducible from the table above in under 100 lines) then
walked every `.dlg`'s `EntryList`/`ReplyList`, read each node's `Script` GFF field, and for each
distinct script referenced, scanned its `.ncs` bytes for the 5-byte `ACTION` opcode pattern
(`0x05 0x00 <u16 be routine> <u8 argc>`, format **VERIFIED** in `min-one-hitpoint.md`) looking
for these seven routines. This is a **heuristic byte scan, not a real decompiler** — it does not
track control flow or argument values, so a routine number appearing in the bytecode is treated
as "this script probably calls this function," not proven. Treat the counts below as informative
order-of-magnitude, not exact.

### 1.2 Results (VERIFIED against the real game files, heuristic scan)

| Total `.dlg` files | Total nodes (entries+replies) | Nodes with a non-empty `Script` | Distinct scripts scanned |
| --- | --- | --- | --- |
| 1,135 | 45,435 | 6,295 (13.9%) | 2,902 |

| Reward kind | ACTION hits | Distinct `.dlg` files | Comment |
| --- | --- | --- | --- |
| `GiveXPToCreature` | 1,047 | 177 | Very common. **Already merged** (max-merge, `shared-state-audit.md` §3) — not a gap this doc needs to close. |
| `CreateItemOnObject` | 123 | 61 | The common item-grant path. Spread across nearly every planet (`danm13`, `danm14`, `end`, etc. all appear). |
| `GiveGoldToCreature` | 68 | 41 | **Already merged** (gold ledger, per the roadmap's own framing). Listed for context only. |
| `AdjustAlignment` | 46 | 24 | Present but far less common than XP or item grants; concentrated in a handful of morally-loaded conversations (Freyyr on Kashyyyk, the Ebon Hawk vision scenes, Shaardan on Korriban). |
| `AddPartyMember` | 23 | 11 | Rare and concentrated — this is recruitment/roster-swap moments, one or two per companion plus the Leviathan/Malak endgame swaps. Matches the expectation of ~9 recruitable companions. |
| `GiveItem` (K1's own function, distinct from `CreateItemOnObject`) | 41 | **4** | Extremely rare and end-game-specific (`lev40_carth`, `lev40_saul403`, `lev40_transform`, `tar02_bastvision` — the Leviathan and the Bastila-vision sequence). Likely used because it hands over an item the engine already holds a reference to (e.g., Bastila's lightsaber) rather than spawning fresh from a blueprint. |
| `GivePlotXP` | **0** | 0 | Confirms an engine fact worth stating plainly: **plot XP in dialogue is NOT delivered by a script call at all.** Every dialogue node's GFF struct carries its own `PlotIndex` (INT) and `PlotXPPercentage` (FLOAT) fields natively (**VERIFIED** — dumped directly off `end_trask01.dlg`'s node structs), and `CSWSDialog` applies plot XP itself when a node with `PlotIndex >= 0` is reached, scaled by that percentage. `GivePlotXP` exists as a VM routine (used from cutscene scripts, not dialogue nodes) but dialogue's plot-XP path bypasses the script layer entirely. This does not change anything about item 10 — plot XP is XP, and XP is already merged — but it means a future NCS-call scanner would never find it in `.dlg` scripts even where plot XP obviously happens, and that is not a bug in the scanner. |

Journal entries follow the same native-field shape as plot XP: the node struct also carries
`Quest` (CExoString) alongside `PlotIndex`, so a journal update from dialogue is very often the
engine reading those two fields directly rather than a `AddJournalQuestEntry` call — consistent
with journal already being merged through `JournalStore` reading the journal object itself
(module-scoped), not by intercepting a script call.

**Net read:** item grants and recruitment are real and happen dozens of times in a playthrough;
alignment shifts are real but rarer than commonly assumed from dialogue nodes specifically
(most alignment shift volume in K1 is believed to come from `k_swg_alignape`-style bark scripts
outside dialogue, which is out of this document's scope); globals, journal and XP were already
correctly identified as merged in the roadmap and this pass found nothing to revise there.

## 2. Per-kind acceptability

| Kind | Per-player divergence acceptable? | Why |
| --- | --- | --- |
| `AdjustAlignment` | **Yes.** | Alignment is per-character stat storage (`CSWSCreatureStats`), read by that character's own dialogue gates, dark-side powers and ending. Two players making different choices in the SAME conversation is exactly what the spectate model already produces (the owner picks; the non-owner watches, per `dialogue-sync.md`'s "Node sync" section) — their characters diverging morally is a *feature* of two people playing one story, not a bug. No channel needed. |
| `CreateItemOnObject` / `GiveItem` to the speaker | **No.** | The task's own framing is the binding case: `GetItemPossessedBy(GetFirstPC())` gates doors, dialogue branches and quest completion on OTHER installs' scripts (every quest-turn-in check, most `k_*` gating scripts). An item that landed only in the owner's inventory means the non-owner's later dialogue/door checks against their own `GetFirstPC()` silently fail — a divergence that looks like a completely unrelated bug three areas later. Needs a channel. |
| Recruitment (`AddPartyMember` + the availability it depends on) | **No.** | `PartyRosterController`'s own header names this exactly: "the party table is the key the whole companion channel resolves by... Diverge the tables and the mirror drives the wrong body, or nothing at all, silently and permanently." Recruitment IS the party-table channel's input; a conversation-only recruit leaves the roster diverged forever, which `PartyRosterController` already both detects (`localAvailable & (1u << addSlot) == 0`) and explicitly declines to fix (its own doc comment: "recruitment does not replicate"). Needs a channel. |
| Globals (`SetGlobal*`) | N/A — already merged | `EventFlagStore`, upward-merge, monotonic. Whatever the reward script sets, both sides converge on the max. |
| `GivePlotXP` / `GiveXPToCreature` | N/A — already merged | Max-merge, `shared-state-audit.md` §3. Also: §1.2 above shows plot XP from dialogue is a native node field, not a script call, so there is no reward-script code path here at all — the merge already covers the whole thing. |
| `AddJournalQuestEntry` | N/A — already merged | `JournalStore`. |
| `GiveGoldToCreature` | N/A — already merged | Ledger, per the roadmap's own framing. |

So the design below only needs to solve two things: **items granted to the speaker**, and
**recruitment**. Everything else in the task's enumerated list is either a non-issue (alignment)
or already solved by an existing channel (XP, journal, globals, gold).

## 3. Channel design: items granted to the speaker

### 3.1 Detection — reuse the item-identity wire, add a diff

Recommended per the task's own steer, and it holds up: `src/KotorCoop.Protocol/Packets.cs`
already has `ItemOpPacket` (`OriginPeerId`, `OpSequence`, `ItemOpKind`, `GroundItem` with its
`ItemIdentity`) and `ReliableItemSender`/`GroundItemStore` for retransmit-until-ack delivery —
built for ground-item drops, but the shape (an item identity, reliably delivered, deduped by
`(origin, sequence)`) is exactly what a reward-item announcement needs. **Do not invent a new
packet type**; add a `Kind` (or a sibling packet reusing the same envelope) meaning "this item
was placed directly into the speaker's inventory," carrying the same `GroundItem`/`ItemIdentity`
fields `item-identity.md` already specifies (resref, stack size, charges, upgrades — everything
`ApplyItemIdentity` already knows how to write). `CheatController.GiveLoot`
(`CheatKindGiveLoot`) and `Odyssey.GiveItemToInventory` already exist and do exactly the "spawn
this blueprint straight into a creature's inventory, no equip" operation the receiving side
needs — reuse both.

### 3.2 Hook point — diff the PC's own inventory across the conversation window

`IGameBridge.ReadPossessedItems(IReadOnlyList<uint> objectIds)` already exists
(`src/KotorCoop.Memory/KotorGameBridge.cs`, consumed today by `CoopAgent.DetectMaterialisedDeaths`
for corpse loot) and returns `(possessorId, resref, ItemIdentity)` tuples for a batch of object
ids in one sweep. The dialogue-owner's agent already has the PC's object id every tick. The
design:

1. On `DialogueSpectateController`'s / the agent's detection of "a local conversation just
   became active" (Layer 1 in `dialogue-sync.md`, already read-only and safe), snapshot
   `ReadPossessedItems([localPcId])` into a resref multiset.
2. On "the conversation just ended" (the same edge the spectate controller already tracks via
   `gui+0xB4` falling back to 0), snapshot again.
3. Any resref present in the AFTER set more times than in the BEFORE set is a gain during this
   conversation. Emit one reward-item packet per net-new copy.
4. **Only the token OWNER runs this diff.** The spectating side never ran `HandleReply`, so its
   own inventory cannot have changed from the conversation — diffing there would just be noise
   (or, worse, would double-detect the OWNER's grant once the spectate/adopt path eventually
   mirrors it, which is exactly what the dedup key in §3.3 exists to prevent regardless).

This needs no new engine hook: it is two calls to an existing bridge method, bracketing a state
machine transition (`Idle`→some active state, and back) that `dialogue-sync.md`'s "explicit
state machine" section already tracks precisely for exactly this purpose (suppression/adoption
timing). **NEEDS-LIVE**: confirm `ReadPossessedItems` resolves correctly mid-conversation (the
PC is not un-spawned or otherwise unusual during a dialog — `dialogue-sync.md` gives no reason
to expect it is, but nothing here has measured it).

A cheaper alternative considered and rejected: hooking `CreateItemOnObject`/`GiveItem` at the VM
dispatch level (the same table `§1.1` located) to catch the call directly rather than diffing.
Rejected because it requires a detour on a routine table entry shared by other things (a bare
call is not host-side attribution the way a diff is, since we would still need to know the call
was for OUR pc as the target) and because the diff needs no native code change or offset trust
at all — pure `ReadProcessMemory`, the lowest-risk category `dialogue-sync.md`'s own layering
scheme prefers. If the diff proves too coarse in live testing (see §5), the VM hook is the
documented fallback, not a redesign.

### 3.3 Dedup key

`(conversationOwnerPeerId, resref, monotonic counter per owner)` — the same shape
`MaterialisedLootPacket`/`ItemOpPacket` already use (`OriginPeerId` + `OpSequence`, deduped via
the existing 4096-entry op history). Do **not** key on the dlg resref or node index: two
different conversations granting the same item resref must not collide, and the per-owner
sequence counter already guarantees uniqueness without needing dialogue-specific identity at
all — reuse `ReliableItemSender`'s existing sequence allocation verbatim.

### 3.4 Receiving side

On receipt: `Odyssey.GiveItemToInventory(localPcObjectId, resref, stackSize, out failure)` — the
exact call `CheatController.GiveLoot` already makes, already MAIN-THREAD-ONLY and already
resolves `CreateItemOnObject`'s routine index at runtime via `FindRoutine(table,
ExpectedCreateItemOnObject)` rather than a hardcoded index (a more robust pattern than the
static table-offset computation in §1.1 — that computation is for *this document's* research
purposes; the shipped write path should keep using `FindRoutine`'s live, self-verifying lookup).
No new native call, no new offset risk.

## 4. Channel design: recruitment

### 4.1 What already exists

`PartyRosterController` already publishes BOTH masks every frame
(`Odyssey.TryReadPartyRoster(out uint available, out uint party)` →
`block.SetLocalPartyRoster(available, party)`), and `Reconcile()` already reads the peer's
`hostAvailable` and gates every add on `localAvailable`. **The gap is narrow**: when
`hostAvailable` has a bit `localAvailable` lacks, the controller today only logs "recruitment
does not replicate" and returns — it never tries to raise the local availability bit at all.

### 4.2 The actual engine field, and why it does not need a wire payload

`Odyssey.TryReadPartyRoster` reads availability off `table + PartyAvailable + slot*4` — a bare
per-slot flag, matching `PartyRosterController`'s own doc comment: "party table +0x30 is a bare
flag, set to the literal 1 at `0x00564350`." Because K1's nine companion slots are a **fixed,
game-defined mapping** (slot index → companion → blueprint is baked into `nwscript.nss`'s
`NPC_*` constants and is identical on both installs, since both run the same executable and
game data) — **the recruitment signal needs no resref, no template name, and no payload at
all.** The party-roster channel already carries the one bit that matters (`availableMask`,
already on the wire); the only missing step is acting on a bit the peer has that we do not.

Two candidate implementations, in order of preference:

1. **Raw field poke — cheapest, matches what the engine itself does.**
   `*(int*)(table + PartyAvailable + slot*4) = 1` mirrors the literal store
   `AddAvailableNPCByTemplate` performs at `0x00564350`. **NEEDS-LIVE, and this is the one real
   open risk in this whole design**: `AddAvailableNPCByTemplate` may ALSO register the
   blueprint association elsewhere in the party table (the thing `SpawnAvailableNpc(slot)` —
   already used by `PartyRosterController` to give a recruited-but-bodyless companion a body —
   reads later to know WHAT to spawn). If the flag alone is set without that association, the
   flag read (`IsAvailableCreature`-equivalent) would pass while `SpawnAvailableNpc` still fails
   to resolve a template. This needs a live check before shipping: recruit a companion on
   INSTANCE A only, poke the flag on INSTANCE B, and confirm `SpawnAvailableNpc` on B still
   produces a real body.
2. **Fallback if (1) fails live: call the real VM routine.** `AddAvailableNPCByTemplate`'s
   routine number was not computed in this pass (not needed for the flag-poke design; see §1.1
   for the method to get it — same `xrefs.py`+`disasm.py` recipe against
   `0x0052DCD0`, the address already resolved from the DB in §1.1's table but not yet traced to
   its table offset here). This needs the companion's blueprint resref, which the OWNER's own
   local conversation already resolved when it called this routine — capturing it would need
   either a live probe of the party table immediately after a local recruit (to see whether the
   resref lands somewhere readable) or a small static per-slot lookup table built once against
   the real game's recruitment scripts (§1.1's method finds all 11 `AddPartyMember`-bearing
   `.dlg` files directly; each names its own NPC constant in its script, decompilable the same
   heuristic way §1.1 did for routine numbers).

Recommend building and shipping (1) first with the live check as the acceptance gate, and only
reaching for (2) if that check fails.

### 4.3 Sequencing with active-party membership

Once availability is granted (either instance), `PartyRosterController.Reconcile()` **already**
handles the rest unmodified — it already gates active-party adds on `localAvailable`, already
spawns a body via `SpawnAvailableNpc` when the recruited companion has none, and already adds
via `AddPartyMemberBySlot`. No change needed there; §4.2 is the only new code.

## 5. Risks and the live-test plan

Ordered by what would invalidate the most of this design if wrong:

1. **The `AddAvailableNPCByTemplate` blueprint-association risk (§4.2 item 1).** The single
   highest-value live check: recruit on A, observe whether a flag-poke on B is enough for B's
   `SpawnAvailableNpc` to work. If it fails, the design falls back to §4.2 item 2 without
   changing anything else in this document.
2. **The inventory-diff hook (§3.2) catches false positives.** A stack-size change (picking up
   more of an item the PC already carried mid-conversation via an unrelated concurrent pickup,
   or a script that removes-then-re-adds the same resref) could misfire as a grant. Live-test by
   watching the diff log across a conversation that grants nothing and one that grants exactly
   one known item, on the SAME character, back to back.
3. **The dedup key (§3.3) assumes `ReliableItemSender`'s existing sequence/history generalizes.**
   It should — the shape is identical to ground-item drops — but has not been tried for a
   same-tick burst (a reward node that grants two items in the same frame). Live-test: a
   conversation node known to grant more than one item, or a synthetic test via
   `blockpoke`/console verb if one exists for triggering the item-op path directly.
4. **Timing: the owner's grant vs. the spectator's adoption of the SAME conversation.**
   `dialogue-sync.md`'s "Adopting" state (both players trigger the same conversation
   independently, the loser's copy is adopted) means the owner side could change mid-adoption.
   If the adopt happens WHILE the diff window is open, the diff might attribute the grant to the
   wrong "owner." Needs a live pass specifically on the simultaneous-trigger case
   `dialogue-sync.md` already describes measuring at ~880 ms.
5. **`GiveItem` (K1's own function, §1.2) vs `CreateItemOnObject`.** Only 4 dlg files use it, all
   end-game (Leviathan, Bastila vision). Both should produce the same net inventory diff from
   §3.2's perspective (an item that is now in the PC's bag), so the detection side does not need
   to distinguish them — only worth flagging because those 4 conversations are late-game and
   thus expensive to reach for a live test; prioritize testing the far more common
   `CreateItemOnObject` path first and treat `GiveItem` as covered by the same diff mechanism
   until proven otherwise.
6. **Alignment and the other already-merged kinds need no live test from this document** — they
   were explicitly ruled either acceptable-to-diverge or already covered by an existing,
   separately-verified channel.

### Recommended build order

1. Recruitment flag-poke (§4.2 item 1) + its one live check — smallest diff, and the live check
   answers the biggest open question in this whole document before any wire work starts.
2. Item-grant diff detection (§3.2), tested in isolation (log-only, no wire) against a known
   single-item conversation and a known zero-item conversation.
3. Wire the diff's output through the existing item-identity envelope (§3.1/§3.3) once detection
   is trusted.
4. Live pass on the simultaneous-trigger / adopt-timing case (§5 item 4) last, since it is the
   narrowest window and the rest of the design does not depend on it working perfectly (a missed
   grant there is a known, documented gap, not a silent one).

## What could not be determined from the repo

- The exact routine number for `AddAvailableNPCByTemplate` (address `0x0052DCD0` is known; its
  table offset/index was not traced — §4.2's design does not need it unless the flag-poke fails
  live).
- Whether the party table's `+0x30` flag array is sufficient on its own for `SpawnAvailableNpc`
  to resolve a template afterward, or whether a second field elsewhere in the table also needs
  writing. This is the one NEEDS-LIVE that gates the whole recruitment design (§4.2, §5 item 1).
- Per-companion blueprint resrefs (needed only for the §4.2 item 2 fallback) — not looked up in
  this pass; §4.2 names the method to get them if needed.
- Whether `k_swg_alignape`-style bark scripts (alignment shifts outside dialogue nodes) are in
  scope for a future pass — flagged in §1.2 but this document is scoped to dialogue-node
  consequences specifically, matching the task's own framing.
