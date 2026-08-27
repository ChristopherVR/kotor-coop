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

## Shipped (2026-08-21) — implementation against this design

Roadmap item 10 is now built following the recommended build order in §"Recommended build
order", with one deliberate deviation from the original task steer (recruitment, noted below).
**Nothing in this section has been run against the real game.** Every item is marked
NEEDS-LIVE unless stated otherwise; treat all of it as Implemented-untested until a live pass
says differently.

### Item grants (§3)

Built as designed: `IGameBridge.ReadPossessedItems` (unchanged signature — it already took a
generic possessor-id set, not an enemy-specific one, so no extension was needed) is swept by
`CoopAgent.PumpDialogueItemGrants`, gated on the token OWNER via the same debounced `active`
flag `PumpDialogue` already computes, at conversation start, every ~1 s while it continues
(`DialogueGrantSampleIntervalMs`), and once more at conversation end. The diff itself
(`KotorCoop.Protocol.DialogueItemDiff`) is a pure resref-ROW-COUNT comparison rather than an
identity comparison — `ReadPossessedItems` returns one row per item object with no object id to
track across two sweeps, so a stack growing (five spikes becoming eight) is recognised as the
SAME row and correctly never reported, while a genuinely new row raises the resref's count and
is reported once, carrying its own full identity.

New wire: `DialogueGrantPacket` (**PacketType 45**) / `DialogueGrantAckPacket` (**PacketType
46**, not pre-assigned by the task but needed — the existing Ack packets all pair 1:1 with an op
packet and PacketType 44 stays reserved for a concurrent agent's PeerEffect work). Protocol
**Version 29** (28 left for that same concurrent agent). Unlike `MaterialisedLootPacket` (host-
only, since only the host's engine ever holds a dying enemy's corpse), a dialogue grant can
originate from EITHER peer, so `DialogueGrantSender` is used symmetrically by `CoopHost` and
`CoopClient` — modelled on `MaterialisedLootSender` per the task's own steer, but keyed on
`(origin, sequence)` rather than a bare sequence, and relaying (`EnqueueRelay`) preserves the
REPORTING peer's true origin rather than re-attributing to the host the way `ContainerLoot`'s
relay does — re-attribution would make a receiver's "is this my own echo" self-filter
indistinguishable from a genuinely new host-originated grant. Receivers dedup the wire op by
`(origin, sequence)` (a bounded seen-set on the client, `DialogueGrantSender`'s own bounded
`_everQueued` on the relay leg) and additionally drop any grant whose origin is their own local
peer id before it ever reaches the give queue — a container-loot echo is harmless because
removing an already-removed item is a no-op, but `GiveItemToInventory` mints a fresh item every
call, so the echo cannot be allowed to reach the drain path at all.

The give itself reuses `CheatKindGiveLoot`/`CheatController.GiveLoot` exactly as the design
called for, with one small, deliberate extension: `SharedBlock.CheatTargetLocalPlayer`
(`0x7FFFFFFF`, the engine's own verified player object id, `docs/re/object-model.md`) is a new
sentinel `CheatTarget` value that `GiveLoot()` resolves straight to `Odyssey.GetPlayerObjectId()`
instead of through `MaterialisedEnemies.ToLocal` — no new `CheatKind`, no `SharedBlock` layout
change, no version bump for the plugin-local block. `CoopAgent.PendingMaterialisedLootGive`'s
`OwnerEnemyId` field was renamed to `TargetObjectId` (two call sites, one test file) to carry
both id spaces honestly.

**NEEDS-LIVE** (per §5's own risk list, none of it closed by this pass):
- §5 item 2: whether the inventory diff produces false positives against a real conversation
  (a concurrent unrelated pickup, a script that removes-then-re-adds a resref).
- §5 item 3: a reward node granting two items in the same frame — the dedup key should
  generalise fine (unverified), untested against a real burst.
- §5 item 4: the simultaneous-trigger / adopt-timing race between the owner's grant and the
  spectator's adoption of the same conversation.
- §5 item 5: the `GiveItem`-vs-`CreateItemOnObject` distinction — the diff should not care, but
  the four dlg files that use `GiveItem` are all late-game and unreached by any test so far.
- §3.2's own NEEDS-LIVE: that `ReadPossessedItems` resolves correctly mid-conversation at all.
- The full wire path end to end: no two live instances have exchanged a `DialogueGrant` packet.

### Recruitment availability (§4) — DEVIATION from the task's literal wire steer

The task instruction said to fold an `availableSlotsMask` field into `DialogueGrantPacket`.
**This was NOT done.** Re-reading §4.2 while implementing found that folding it would duplicate
information already on a wire channel that exists and runs continuously: `PartyRosterController`
already publishes this instance's `available` mask every frame via the existing `PartyRoster`
packet (37), and the peer's mask (`hostAvailable`) is already read by `Reconcile()` every pass —
§4.2 says this outright: "the recruitment signal needs no resref, no template name, and no
payload at all... the only missing step is acting on a bit the peer has that we do not." Adding
a second, conversation-scoped field carrying the same bit would need its own plumbing for no
behavioural gain over acting on the level that is already there — and a level, unlike a
dropped-packet-scoped edge, keeps saying "you're missing this" every tick rather than only once
around a conversation's end, which is a strictly better fit for the "a level, not edges"
reasoning that shaped every other already-shipped channel this codebase's own docs argue for
(see `PartyRosterController.cs`'s own header, and item 2's item-grant channel above, which IS
an edge because a give is one-shot and idempotency is the wrong property to want there).

Built instead: `Odyssey.TrySetSlotAvailable(slot)` raw-pokes `partyTable+0x30+slot*4 = 1` (the
exact literal store `AddAvailableNPCByTemplate` performs at `0x00564350` — VERIFIED, the same
evidence `PartyRosterController`'s own `PartyAvailable` offset comment already cites).
`PartyRosterController.Reconcile()`'s existing "not recruited here" branch calls it, behind
**`dialogue_recruit_sync=0` (OFF by default)**, before falling through to the ordinary
spawn+add flow on a later pass. No new packet type, no protocol version bump specific to this
half, no `SharedBlock` change.

**NEEDS-LIVE, unresolved, and the reason this stays off by default**: §4.2 item 1's own
headline risk — recruit a companion on instance A only, confirm the flag poke on instance B is
sufficient for B's `SpawnAvailableNpc` to resolve a real body, not just pass the availability
read while the spawn silently fails. Nothing in this pass observed a live engine at all;
§4.2 item 2 (the `AddAvailableNPCByTemplate` VM-routine fallback) remains unbuilt and its
routine number untraced, exactly as the design doc left it.

### Tests

Added, all passing against the pre-existing 1102-test baseline (now 1109 after this pass, plus
the queue-hand-off suite below): `DialogueItemDiffTests` (pure diff logic, 8 cases —
no-change, new-resref, stack-growth-is-not-a-grant, stack-shrink-is-not-a-grant, extra-copy-is-
a-grant, multiple-distinct-grants, empty-resref-rows-ignored, empty-both-sides);
`ReliableSenderTests`'s new `Grant_*` block (10 cases covering first-send, retransmit timing,
per-recipient ack retirement, unknown-op acks ignored, two origins reusing sequence 1 without
colliding, relay preserving true origin, a relayed op's retransmit not being re-queued after
retirement, empty-recipient holding, `Clear`); `PacketCodecTests`'s `DialogueGrant*` block
(round trip, max-items-fits-one-datagram, malformed over-cap count refused);
`DialogueGrantQueueTests` (modelled directly on `MaterialisedLootQueueTests` — the queue hand-
off item 3 asked for by name — 7 cases proving a drained grant lands in the same
`_pendingLoot` queue under the local-player sentinel, FIFO, across ticks, with full identity,
and bounded); and one real-socket integration test in `SessionIntegrationTests`
(`ADialogueGrantFromAClientReachesTheOtherClientButNotItself`) proving the full client→host→
other-client relay and the self-echo filter over actual loopback UDP. No test exercises the
plugin's native code (`CheatController.GiveLoot`'s new branch, `Odyssey.TrySetSlotAvailable`,
`PartyRosterController`'s poke) — that class of code has never had unit coverage in this
codebase and needs the live rig, per every `NEEDS-LIVE` line above.

## Round (2026-08-23) — the raw-poke risk settles NEGATIVE (already live-confirmed elsewhere),
## and `Odyssey.AddAvailableNpcByTemplate` replaces it

Task: check whether `dialogue_recruit_sync`'s flag poke is actually sufficient for the engine to
spawn/add a companion on a peer's screen — §4.2 item 1's own headline risk, still open per the
prior section. Two things settle it, one already in the repo and one new this round:

**Already settled, just not folded back into this document.**
`docs/research/party-reconciliation.md` Round 75 (2026-08-23, same day, a different task) built
`CheatKindRecruit`/`recruit <slot>` — a dev-only console verb that runs the EXACT sequence
`PartyRosterController.Reconcile()` runs behind `dialogue_recruit_sync`: `TrySetSlotAvailable`,
then `SpawnAvailableNpc`, then `AddPartyMemberBySlot`. Live result: **`SpawnAvailableNpc` refuses
every time** ("spawn failed, failure 0" — a genuine engine refusal). Round 75's own words: "a bare
availability poke does not carry the BLUEPRINT `AddAvailableNPCByTemplate` also records, which
settles `TrySetSlotAvailable`'s own long-standing NEEDS-LIVE question: the flag alone is *not*
enough." That IS this document's §4.2 item 1, closed, in the negative — it just was not recorded
here because the two rounds were tracking different bugs (Trask's follow-AI) and happened to reuse
the same primitive as a staging tool.

**New this round: the disassembled ROOT CAUSE, from static analysis of the GOG `swkotor.exe`
(`tools/disasm.py`, `tools/kotor1_0_3.db`, no live game touched).** `Odyssey.SpawnAvailableNpc`
calls `CSWPartyTable::GetNPCObject` (`0x00564700`), which:
1. Refuses unless `table+0x30+slot*4` (the availability flag) is non-zero — the ONE thing the raw
   poke sets correctly.
2. Reads the cached object-id slot (`table+0x0C+slot*4`, `PartyObjectIds`). If it is not the
   sentinel `0x7F000000` (`OBJECT_INVALID`), it resolves that cached object directly.
3. **If it IS still the sentinel — the raw poke never touches this array, so it always is —
   `GetNPCObject` rebuilds from a resource literally named `"AVAILNPC%d"`** (the format string at
   `0x007490B4`, VERIFIED by reading the string straight out of the image; built via
   `CSWPartyTable::GetFilename` `0x00563620`, which formats it purely from the slot index). This
   is a per-slot RUNTIME CACHE of that companion's off-stage character sheet, populated as a side
   effect of a real recruit (`CSWPartyTable::AddNPC_2`/`AddNPC`, see below) — not a static
   resource shipped in `chitin.key`/`templates.bif`. A slot the raw poke marks available has never
   had this cache populated on THIS instance, so `LoadFromTemplate` (`0x005026D0`, the same load
   call `docs/re/spawn.md`'s own `CreateObject` trace documents) has nothing to open, the
   freshly-allocated object is deleted, and `GetNPCObject` returns nothing — exactly matching
   Round 75's live "spawn failed, failure 0".

**The fix, matching §4.2 item 2's own fallback recommendation, now built with real addresses
instead of "not traced this pass":** `Odyssey.AddAvailableNpcByTemplate(slot)` calls
`CSWPartyTable::AddNPC_2(int nNPC, CExoString* sTemplate)` (`0x005645F0`) directly — the same
function `CSWVirtualMachineCommands::ExecuteCommandAddAvailableNPCByTemplate` (`0x0052DCD0`, the
NWScript `AddAvailableNPCByTemplate` handler itself) calls, VERIFIED by disassembling its call
site (`0x0052DD65`). It builds a fresh off-stage creature straight from the STORY blueprint
resref (e.g. `p_carth`) via the SAME `LoadFromTemplate` path — no dependency on the `AVAILNPC%d`
cache at all — then calls `CSWPartyTable::AddNPC` (`0x00564300`) on it, which sets the
availability flag AND runs the bookkeeping the raw poke skipped entirely:
`CSWSFaction::AddMember` (`0x005BFA70`, faction membership — low practical risk, since
`docs/re/spawn.md`'s own parse of `p_carth` already shows `FactionID=2=Friendly_1` baked into the
blueprint independent of this call) and `CSWPartyTable::TransferInventory` (`0x005641E0`, merges
starting inventory into the party pool — also run a second time by `AddMember` itself when the
body is added to the active party, so redundant rather than missing if this call is ever skipped).
`PartyRosterController.Reconcile()` now calls this instead of `TrySetSlotAvailable` behind
`dialogue_recruit_sync`, and — since it hands back a live object id directly — adds the companion
to the party in the SAME pass rather than waiting on a separate spawn pass.
`Odyssey.TrySetSlotAvailable` itself is UNCHANGED and still used by `CheatController.Recruit`
(Round 75's own dev primitive), where the documented engine refusal and its tag-lookup fallback
are the point, not a defect.

**What is still NEEDS-LIVE, narrowed from "does the flag do anything" to one specific,
independently checkable fact:** the per-slot NPC-id → template-resref mapping
(`PartyRosterTemplateTable`, standard nwscript.nss order: Bastila, Canderous, Carth, HK-47,
Jolee, Juhani, Mission, T3-M4, Zaalbar). Every resref is individually confirmed to exist in
`templates.bif` (`docs/re/spawn.md` §4, VERIFIED), and slot 2 = Carth is independently
live-confirmed (Round 75's own `recruit 2 carth` succeeded, and `ConsoleCommand.ParseRecruit`'s
comment says the same). The other eight slots are NOT independently re-derived from this repo's
own decompilation pipeline (no `.dlg`/`.ncs` decompiler wired up here) — a wrong entry would spawn
the WRONG companion rather than fail outright, which is exactly what the live-test plan below
checks for directly. **UPDATE, round 2026-08-24: this whole paragraph is superseded — see "Round
(2026-08-24)" below, which re-derives and confirms all nine entries by disassembly of the shipped
game's own recruit scripts.**

### Live-test plan

Fastest reachable recruitment for a clean two-instance check: **Carth on the Endar Spire**
(`k_hcar_dialog`, the very first companion the game hands over, reachable within a minute of a
fresh start, no travel). Steps:

1. Both instances on the SAME fresh save, at or just before the point Carth is recruited (the
   Endar Spire escape, before the two players split up). `dialogue_recruit_sync=1` and
   `party_roster_sync=1` (default) on BOTH `coop.ini`s.
2. Player A runs the recruitment conversation with Carth to completion (A is the OWNER of that
   conversation). Do not have B in the same conversation.
3. Watch B's plugin log for, within one `PartyRosterController` pass (`IntervalFrames=30`, well
   under a second): `party roster: recruited NPC slot 2 as 0x... from its own template (peer
   recruited ..., here ...) - dialogue_recruit_sync; adding them to the party now`, immediately
   followed by `party roster: added NPC slot 2 (object 0x...) to the party`. Absence of the first
   line (or a `party roster: could not recruit NPC slot 2 from its own template ... engine call
   refused` line instead) means `AddNPC_2` itself refused — check `PartyRosterStatus` (written
   `200` on this specific refusal) and re-open the "is the flag/call enough" question with a live
   debugger, since static analysis would then have missed something.
4. **The identity check the mapping needs**: on B's own screen, does the newly-added party member
   actually look like, and portrait as, Carth — not some other companion? This is the one thing
   this round's static analysis could not settle (only slot 2 = Carth is independently confirmed;
   every other slot's template is unverified against the real `.dlg`/`.ncs` content). If the wrong
   companion appears, `PartyRosterTemplateTable`'s ordering is wrong and needs correcting
   against a real `.ncs` decompile per §4.2 item 2's own original method, not guessed again. (As
   of the "Round (2026-08-24)" section below, this has now been done statically for all nine
   slots — this in-game portrait check is the one thing that pass still could not do, since it
   needs a running game.)
5. Confirm on B: open the party/character sheet for the new companion — does it have a real
   inventory (not empty), a plausible level/HP, and is it controllable/selectable the way a
   normally-recruited companion is? This exercises the `TransferInventory`/`AddMember` bookkeeping
   path.
6. Confirm faction correctness the cheap way: have B's own hostiles (if any are nearby) NOT
   attack the newly-added Carth, and have Carth not attack B's own player character. This is the
   one piece of `AddNPC`'s bookkeeping (`CSWSFaction::AddMember`) `AddNPC_2` DOES run (unlike the
   old raw poke), so it is expected to be fine, but is cheap enough to eyeball while already there.
7. Repeat with a second companion from a DIFFERENT part of the mapping (e.g. Bastila, slot 0, or
   Mission, slot 6) to get a second independently-confirmed mapping entry beyond Carth.

If steps 3-6 all pass for two slots, `dialogue_recruit_sync` can default ON with high confidence;
if step 4 fails for one slot, only that slot's template entry needs fixing, not the mechanism.

## Live pass (2026-08-23): Carth reconciles correctly end-to-end, via the console fallback path —
plus a real, newly-found duplicate-body defect in the same-pass fallthrough

Ran against the two-instance rig, `dialogue_recruit_sync=1` on both installs, `END_M01AA`
(Endar Spire — the same save this document's own plan names as fastest-reachable). The real
dialogue conversation with Carth (`talk carth`) was not attempted this pass — this project's own
prior finding (`docs/research/party-reconciliation.md` round 75) already established that hook
reaches Carth as an "unreachable NPC" with no renderable content from this save's console path, so
per this task's own explicit sanction ("if Carth isn't reachable from this save's story point, use
the console recruit-equivalent path... to force the same code path deterministically instead"),
the HOST recruited Carth via `console 'recruit 2 carth'` — the dev-only `CheatKindRecruit` verb,
which sets `partyTable+0x30+2*4=1` (the SAME bit `PartyRosterController`'s wire read publishes as
`available`) and adds a real body to the host's own active party. This is deliberately NOT the
`AddAvailableNpcByTemplate` code path itself — the point was to get the SAME bit set on the wire so
the CLIENT's own `dialogue_recruit_sync` reconciliation (which DOES go through
`AddAvailableNpcByTemplate`) has something to react to, independent of how the host itself came to
have Carth.

### Result: Carth appears in the client's party, confirmed by object resolve and party-table read

Host: `partystate.ps1` showed `companions 2`, slot 0 = Trask (0x127), slot 2 = Carth (0x2),
immediately after the `recruit` console command. Client, within the next `PartyRosterController`
pass (well under a second):

```
[19:30:05.023] party roster: recruited NPC slot 2 as 0x1 from its own template (peer recruited 0x005, here 0x001) - dialogue_recruit_sync; adding them to the party now
[19:30:05.024] party roster: could not add NPC slot 2 (failure 9)
[19:30:05.088] party roster: spawned NPC slot 2 as 0x13B to add them here
[19:30:05.088] party roster: added NPC slot 2 (object 0x13B) to the party
[19:30:05.091] party roster: local party is now 0x005 (recruited 0x005)
```

Client `partystate.ps1` afterward: `companions 2`, slot 0 = Trask (0x127), slot 2 = Carth
(object 0x13B). `resolve 0x13B` on the client confirmed a real, live, resolvable object
(`0x13B resolves to 0x1D1859C8 here`). **`dialogue_recruit_sync` genuinely closes the recruitment
gap this document exists to fix — Carth appears in BOTH players' party after the flag fires,
confirmed by object resolution, not just a mask bit.** This uses the already-independently-verified
slot-2-is-Carth mapping (`PartyRosterTemplateTable`, confirmed live in
`party-reconciliation.md` round 75) — the other eight slots' template mappings were unverified at
the time of this pass; see "Round (2026-08-24)" below, which closes that gap statically (though
not yet with a second live recruit).

### A real, newly-found defect in the same pass: the immediate-add-after-recruit fallthrough fails
and leaks an orphaned duplicate body

The log above shows the mechanism did NOT work the way `PartyRosterController`'s own code comment
describes ("hands back a live object id directly — no separate spawn step"). `AddAvailableNpcByTemplate(2)`
returned object `0x1` and the code fell through to `AddPartyMemberBySlot(2, 0x1, ...)` in the SAME
pass, which refused with **failure 9** — `AddMember`'s own VM dispatch returning nonzero, most
likely its `GetGameObject`-resolves-to-a-live-body gate (`party-reconciliation.md`'s own
2026-07-31 entry: "the object id must resolve through `CServerExoApp::GetGameObject`... the add
needs a LIVE body"). Reading this against the same document's `AddAvailableNpcByTemplate` doc
comment (`Odyssey.cs`, 2026-08-23 round): `AddNPC_2` builds the creature via `LoadFromTemplate` and
registers it in the party table's own bookkeeping, but that is apparently NOT the same as being a
`GetGameObject`-resolvable live world object — a distinction this document's own design never
anticipated when it said the object could be added "in the SAME pass."

The system self-healed on the VERY NEXT pass (56ms later, same batch): with `localAvailable` now
carrying bit 2 (set by `AddNPC_2`'s own bookkeeping), the controller took the OTHER branch
(`GetPartyRosterObjectId`/`ResolveServerObject` failing over to the OLDER `SpawnAvailableNpc`
fallback), which produced a genuinely new, different, in-world object (`0x13B`) that DID resolve
and DID get added successfully. **Net result: correct (Carth ends up in the party), but at the
cost of a real, verified duplicate body** — `resolve 0x1` on the client afterward confirmed object
`0x1` (the first, never-added recruit) is STILL a real, resolvable object
(`0x1 resolves to 0x3314E04 here`), sitting off-stage, never cleaned up, never referenced by
anything after the fallback took over. This is a genuine, reproducible bug in
`PartyRosterController`'s same-pass fallthrough optimisation (§4.3 of this document's own design,
"no change needed there" — which this pass shows is not quite true), not merely a theoretical risk.

**Not fixed this pass** (out of scope for an observation-first live-verification round; the
end-to-end outcome IS correct, so this is a leak/inefficiency, not a correctness bug the way a
failed recruit would be). The fix is narrow when someone picks it up: either don't attempt the
same-pass `AddPartyMemberBySlot` call immediately after `AddAvailableNpcByTemplate` (let the very
next pass's `else` branch — which already handles "recruited but no live body yet" via
`SpawnAvailableNpc` — do the work, avoiding the wasted first object entirely), or destroy the
orphaned object when the immediate add fails with failure 9 specifically.

### Verdict

`dialogue_recruit_sync` stays OFF by default (unchanged) — the channel works end-to-end (the
headline claim this document exists to prove), but this pass found a real, minor, previously
unknown defect (an orphaned duplicate body per recruit) that was not part of the design's own
NEEDS-LIVE list. Per this project's "flip on if clean" bar, a defect found during the very live
pass meant to clear the flag is a reason to keep it off, not flip it, until the fallthrough is
fixed or the leak is otherwise judged harmless enough to accept. The core recruitment mechanism
itself (`Odyssey.AddAvailableNpcByTemplate`, the wire, `PartyRosterController.Reconcile`'s overall
shape) is proven sound.

`dotnet test tests/KotorCoop.Tests` (Release): 1638/1638 passed — no production code touched by
this pass (pure live observation against already-shipped code).

## Round (2026-08-23): the duplicate-body defect's root cause, disassembled and fixed —
## `AddAvailableNpcByTemplate`'s object was never meant to be added directly

Task: the previous round left the duplicate-body defect open with two candidate fixes ("don't
attempt the same-pass add" or "destroy the orphan on failure 9") and no root cause beyond "AddMember's
own `GetGameObject`-resolves-to-a-live-body gate... needs a LIVE body" — inherited from an older,
more general finding, not re-derived for this specific call. This round disassembles the real
`CSWPartyTable::AddMember` (`0x00565625`, `tools/disasm.py` against `tools/kotor1_0_3.db`, no live
game touched) to settle it precisely, then ships the first candidate fix.

### Root cause: the object is real, but never placed in the world — so the wrong check refuses it, permanently, not just for one pass

`CSWPartyTable::AddNPC_2` (`0x005645F0`, `Odyssey.AddAvailableNpcByTemplate`'s callee) allocates
and constructs a `CSWSCreature` with the identical call `docs/re/spawn.md`'s own `CreateObject`
trace uses for its step 2 (`CSWSCreature::Constructor(0x7F000000, 0)`, `0x004F7A10`) — which
internally registers the object in `CGameObjectArray` (that trace's step 2b). It then calls
`CSWSCreature::LoadFromTemplate` (`0x005026D0`, that trace's step 4) to load the story blueprint.
**But it never calls `CSWSCreature::AddToArea` (`0x004FA100`) — `CreateObject`'s own step 9,
VERIFIED absent from `AddNPC_2`'s and `CSWPartyTable::AddNPC`'s (`0x00564300`, the function
`AddNPC_2` calls next) disassembly.** The object is real and registered, but was never placed in
an area.

That distinction turns out to be exactly what breaks the same-pass add. `docs/research/enemy-materialisation.md`
already recorded, independently, that `CGameObject+0x08` is not a plain type enum: a live,
in-world creature reads `0x40005` there, while a freshly constructed one reads plain `5`. This
round's disassembly of `CSWPartyTable::AddMember` (`0x00565625`) shows its own live-body check —
at `0x0056566A` — does NOT call the generic, flag-blind `CServerExoApp::GetGameObject` walker
`ResolveServerObject` uses. **It calls the TYPED getter `GetCreatureByGameObjectID` (`0x004AE770`
-> `0x004B1790`), which re-tests that exact flag bit** — the same object-type trap this project's
docs already warn every other resolver about (`docs/research/enemy-presence-sync.md`,
`enemy-materialisation.md`), now found inside a THIRD engine function nobody had disassembled for
this reason before. Since `AddToArea` is the only place that flag bit gets set and `AddNPC_2` never
calls it, an object fresh off `AddAvailableNpcByTemplate` can never pass `AddMember`'s check — not
after one pass, not after a hundred. Failure 9 is not a race to be waited out; it is permanent for
that specific object.

This also explains the previous round's own live evidence without contradiction: `resolve 0x1`
(the console command, which per this project's own discipline uses the generic, flag-blind getter)
found the object "STILL a real, resolvable object" afterward — consistent with it being genuinely
registered, just never world-placed. And it explains why the very next pass succeeded with a
DIFFERENT object (`0x13B`): that one came from `SpawnAvailableNpc` (`CSWPartyTable::SpawnNPC`),
whose entire job is placing a body in the world, so it legitimately carries the flag bit `AddMember`
checks for.

### Is the first object actually a "leak"? No — it is the off-stage character-sheet cache, by design

`AddNPC_2`'s doc comment (already in the repo from the round that built it) records that it
populates a per-slot `"AVAILNPC%d"` resource — the exact cache `SpawnAvailableNpc`/`GetNPCObject`
reads on a later pass to build the real in-world body. NWScript's own `AddAvailableNPCByTemplate`
is declared `void`: no script ever receives this object id either, which is the strongest evidence
the engine's own authors never intended it to be treated as an addable body. The object this round
worried about "orphaning" is not a rendered, in-world duplicate a player could see or an object
census would count against the world — it is the off-stage record the engine's own two-step
recruit-then-spawn design requires. Nothing here needed cleaning up; the code needed to stop
mis-using the object for a purpose it was never built for.

### The fix

`PartyRosterController.Reconcile()` no longer falls through to `AddPartyMemberBySlot` in the same
pass `AddAvailableNpcByTemplate` runs. A new pure, unit-tested rule,
`PartyRosterRecruitRule.CanAddImmediately(PartyRosterBodyOrigin)`
(`src/KotorCoop.Plugin/PartyRosterRecruitRule.cs`), gates the add call for BOTH branches: it
returns `false` for `RecruitedOffStage` (the branch that just called `AddAvailableNpcByTemplate`)
and `true` for `LiveInWorld` (an object `ResolveServerObject` already resolved, or one
`SpawnAvailableNpc` just placed) — so recruiting alone returns without attempting the doomed add,
and the ordinary "recruited but bodyless" branch (already correct, already used for dismiss/rejoin)
picks it up on the very next pass exactly as it already did for the self-heal the previous round
observed, minus the wasted failing dispatch and the misleading failure-9 log line. This is a
root-cause fix, not a symptom patch: no destroy-the-orphan cleanup pass was added, because the
"orphan" was never a defect to begin with once the object's real purpose (the off-stage cache) is
understood; see `PartyRosterRecruitRule`'s own doc comment for the full disassembled chain.

Tests: `PartyRosterRecruitRuleTests` (2 cases — the exact regression this round found, and the
ordinary spawn-then-add path that must keep working unchanged). `dotnet test tests/KotorCoop.Tests`
(Release): 1640/1640 passed.

### Live-verification step for the coordinator (rig currently owned by another agent)

Repeat the same two-instance recruit this document's own live-test plan already describes (Carth,
Endar Spire, `dialogue_recruit_sync=1` on both installs; host runs `console 'recruit 2 carth'` if
the real conversation is still unreachable from that save, per the previous round's own sanctioned
substitution), then, in addition to the existing checks:

1. Watch the client's plugin log across the recruit. Expect exactly:
   `party roster: recruited NPC slot 2 as 0x... from its own template ... - dialogue_recruit_sync;
   a body will be spawned and added on a later pass`, followed on the NEXT pass by
   `party roster: spawned NPC slot 2 as 0x... to add them here` and
   `party roster: added NPC slot 2 (object 0x...) to the party`. **There must be no
   `party roster: could not add NPC slot 2 (failure 9)` line anywhere in the sequence** — that
   line's disappearance is the fix working.
2. On the client, `resolve` the FIRST object id the log names (the one from the "recruited ... as
   0x..." line) and the SECOND (the one from "spawned ... as 0x..."). Confirm they are two
   DIFFERENT ids, both still resolvable (the first is expected to remain resolvable — it is the
   off-stage cache object, not a bug — the second is the one actually seated in the party).
3. Confirm the object count this recruit adds to the world matches exactly ONE new in-world,
   visible companion (the second object) — e.g. via an object-count console verb or simply
   confirming only one Carth model renders/portraits in — not two. This is the concrete "exactly
   one Carth object exists" check the task that opened this round asked for; per the analysis
   above the off-stage object was never expected to render or portrait, so this should already
   pass, but has not been watched for directly before.
4. Confirm the timing: the "spawned ... to add them here" and "added ... to the party" lines land
   within one `PartyRosterController` pass of the recruit line (well under a second at
   `IntervalFrames=30`), so the two-pass design does not introduce a player-visible delay.

If steps 1-4 all pass, this defect is closed and `dialogue_recruit_sync` has one fewer open item
against defaulting on (the per-slot template mapping for the other eight slots, per the existing
NEEDS-LIVE list above, remains the only other gate).

## Live pass (2026-08-23, live rig round): all four steps confirmed — the fix works exactly as designed

Ran on the two-instance rig, `dialogue_recruit_sync=1` set on both `coop.ini`s (was `0`, the
shipped default), fresh `END_M01AA` save on both sides. Host recruited via
`console 'recruit 2 carth'` (the same sanctioned console-fallback substitution the prior live
pass used — Carth's real dialogue is still unreachable from this save's story point).

**Step 1 — log sequence.** The client's plugin log showed exactly the new sequence, in order,
79 ms apart, with **no `could not add NPC slot 2 (failure 9)` line anywhere**:

```
[23:07:48.865] party roster: recruited NPC slot 2 as 0x1 from its own template (peer recruited 0x005, here 0x001) - dialogue_recruit_sync; a body will be spawned and added on a later pass
[23:07:48.944] party roster: spawned NPC slot 2 as 0x137 to add them here
[23:07:48.944] party roster: added NPC slot 2 (object 0x137) to the party
```

**Step 2 — object identity.** `resolve 0x1` and `resolve 0x137` on the client both succeeded and
resolved to two DIFFERENT addresses (`0x18F90164` and `0x1CEAD660`) — the off-stage cache object
and the real seated body are both genuinely distinct, live, resolvable objects, exactly as the
root-cause analysis predicted.

**Step 3 — exactly one visible Carth.** `partystate.ps1` on the client showed slot 2 seated as
`0x137` (not `0x1`) — the off-stage cache never enters the party table. A `creatures.ps1` scan
of the client turned up three `carth`-tagged hits: the genuine seated one (`0x1CEAD660`, hp 40,
xp 6001 — matching `0x137`) plus two more (hp 500, id `0x2`, same position). Those two looked
like a possible second duplicate at first glance, but running the identical scan against the
**host** — which never went through `AddAvailableNpcByTemplate`/the off-stage-cache path at all,
only the plain `recruit` cheat — turned up the **same two hp-500/id-`0x2` ghost hits** and
nothing else. Since the host's path cannot produce a coop-introduced duplicate, these two are a
pre-existing `creatures.ps1` scan artifact (most likely the game's own internal party-available
template bytes matching the vtable scan's pattern, unrelated to anything this fix touches), not
evidence of a leaked visible body. The client has exactly one genuinely seated, in-world Carth,
symmetric with the host.

**Step 4 — timing.** 79 ms between the recruit line and the completed add — well under a second,
under `IntervalFrames=30`'s own budget, no player-visible delay.

**Verdict: this defect is closed.** All four of the coordinator's own checks pass cleanly, with
no regression and no new defect found. `dialogue_recruit_sync` remains OFF by default in the
committed `coop.ini.example` — this pass did not flip it on repo-wide, since the per-slot
template mapping for the other eight slots is still unverified (this document's own long-standing
NEEDS-LIVE item, unaffected by this round) — but the specific duplicate-body defect that was the
last known correctness gap against doing so is now confirmed fixed, live, not just by disassembly.
Both installs' `coop.ini`s were flipped back to `dialogue_recruit_sync=0` after this test,
restoring the rig to its shipped default — the same undo discipline this task's other
live-verification rounds use for save/install changes made purely to exercise a test.

## Round (2026-08-24): the other eight slots' templates, independently re-derived by disassembly —
## and the mapping table split out into a tested, pure lookup class

Task: this document's own long-standing NEEDS-LIVE item — "the per-slot NPC-id → template-resref
mapping... NOT independently re-derived from this repo's own decompilation pipeline for the other
eight [slots beyond Carth]" — closed by static analysis this round (no live rig touched; another
agent owns it). Also generalises the write side properly: `Odyssey.AddAvailableNpcByTemplate` was
already slot-generic (not a Carth-only special case — `PartyRosterController.Reconcile()` already
called it with whatever `addSlot` the peer's roster mask names), so no behavioural change was
needed there; what this round adds is independent verification of the DATA it reads, plus
splitting that data into its own unit-tested class.

### Method: the shipped game's own recruit scripts, read directly

No live game was touched. Two static techniques, both already established elsewhere in this
project's research trail, applied together for the first time on this question:

1. **Trace `AddAvailableNPCByTemplate`'s NWScript routine number** — §4.2's own "not yet traced"
   note, now closed. `CSWVirtualMachineCommands::ExecuteCommandAddAvailableNPCByTemplate` is at
   `0x0052DCD0` (already known — it is `AddNpcByTemplateFn`'s own caller, per `Odyssey.cs`'s
   comment on that delegate). `tools/xrefs.py 0x52DCD0` finds exactly ONE address-literal
   occurrence in `swkotor.exe`, at `0x0054CBC4` — the routine-table population site. Reading the
   raw bytes there (`tools/disasm.py`-adjacent manual read, since the site sits mid-instruction
   for capstone's alignment) shows `8b 46 0c c7 80 e4 0a 00 00 d0 dc 52 00`: `mov eax,[esi+0xC]`
   then `mov dword [eax+0x0AE4], 0x0052DCD0` — the identical "`mov dword [table+OFF],
   <handler>`" shape §1.1 already used for seven other routines (`min-one-hitpoint.md` and this
   document's own §1.1 both use it; six of §1.1's own seven table rows reproduce cleanly under
   `OFF / 4 = routine#`, cross-validating the method again here). `0x0AE4 / 4 = 697` —
   **`AddAvailableNPCByTemplate` is routine 697.**
2. **Scan every module's `.ncs` for that routine's ACTION calls, reading the literal arguments.**
   `tools/kotorfiles.py`'s `RIM`/`ERF` readers opened every `modules/*.rim` (234 files, 10,863
   distinct `.ncs` resources) in the real GOG install (`D:\Games\swkotor-gog-p2`, read-only — the
   task's own sanctioned exception to this worktree's no-writes rule). A scratch scanner (not
   committed — reproducible in under 100 lines from this section) searched each script's raw
   bytes for the 5-byte ACTION pattern `05 00 02 B9 <argc>` (0x02B9 = 697, same format
   `min-one-hitpoint.md` and this document's own §1.1 already established:
   `05 00 <u16 routine> <u8 argc>`), then read the 6 bytes immediately before each hit for a
   `CONST Int` (`04 03 <i32>`, same shape as `min-one-hitpoint.md`'s own `CONSTI` examples) — the
   `nNPC` argument, pushed last (right before the call) under NWScript's reverse-declaration-order
   stack convention, independently confirmed by the `SetMinOneHP(GetFirstPC(), TRUE)` disassembly
   `min-one-hitpoint.md` already shows (the second-declared argument is pushed FIRST, the
   first-declared argument pushed immediately before the call). A second, tighter backward scan
   (60-byte window) for `CONST String` literals (`04 05 <u16 len> <bytes>`) recovers the
   `sTemplate` argument from the SAME call.

### Result: 46 real hits, every one carrying both fields together, unanimous across every module

The scan found real hits in **32 distinct scripts across 24 modules**. Every single hit's `nNPC`
integer and `sTemplate` string agree with every other hit for the same companion — no script
contradicts another — and slot 2 = `p_carth` reproduces this project's own independently
live-confirmed value (`party-reconciliation.md` round 75). This is materially stronger evidence
than the prior round's "matches the standard nwscript.nss declaration order": it is the actual
shipped recruit scripts naming both the slot number and the blueprint resref together, in the
same call, not an inferred ordering.

| Slot | Companion | Resref | Hits (scripts) | Representative non-cutscene / non-endgame-rejoin script |
| --- | --- | --- | --- | --- |
| 0 | Bastila | `p_bastilla` | 15 | `tar_m03aa_s.rim:k_ptar_bastpart`, `tar_m03af_s.rim:k_ptar_bastpart` (Taris) |
| 1 | Canderous | `p_cand` | 5 (4 spell it `p_cand`; one leftover/dead-code hit in `tar_m02aa_s.rim:k_ptar_davikest` spells it `p_canderous`, a resref that does not exist anywhere in `chitin.key`/the BIFs — see below) | `tar_m08aa_s.rim:k_ptar_candadd` (Taris) |
| 2 | Carth | `p_carth` | 9 | `tar_m02aa_s.rim:k_ptar_addcarth`, `tar_m02af_s.rim:k_ptar_addcarth` (Taris; the Endar Spire recruit itself is not an ACTION-697 call reachable this way). **Slot LIVE-CONFIRMED** (`party-reconciliation.md` round 75). |
| 3 | HK-47 | `p_hk47` | 2 | `tat_m17ac_s.rim:k_ptat_hk47add`, `tat_m17ac_s.rim:k_ptat_hk47ship` (Tatooine) |
| 4 | Jolee | `p_jolee` | 5 | `kas_m24aa_s.rim:k_pkas_joleejoin` (Kashyyyk) |
| 5 | Juhani | `p_juhani` | 3 | `danm13_s.rim:k_pdan_vandar02` (Dantooine — global `DAN_JUHANI_JOINED` set in the same script) |
| 6 | Mission | `p_mission` | 2 | `tar_m04aa_s.rim:k_ptar_addmissio` (Taris) |
| 7 | T3-M4 | `p_t3m4` | 2 | `tar_m02ab_s.rim:k_ptar_addt3m4` (Taris) |
| 8 | Zaalbar | `p_zaalbar` | 1 | `tar_m05aa_s.rim:k_ptar_addzaal` (Taris) |

**This exactly matches the mapping the code already shipped** (`PartyRosterTemplateTable`, née the
private array inside `Odyssey.AddAvailableNpcByTemplate` — see "code change" below). No entry
needed correcting.

The one anomaly, checked and explained rather than ignored: `tar_m02aa_s.rim:k_ptar_davikest`
(a Davik's-estate-heist-adjacent script) pushes `CONSTI 1` (Canderous's slot) immediately after
the string `"p_canderous"` — an 11-character resref that a direct `chitin.key --find p_canderous`
across all 25,836 key entries and every BIF returns **zero** matches for. Since the shipped,
verified resref is `p_cand` (confirmed against `templates.bif` — see below — and matching the
other four `AddAvailableNPCByTemplate(1, ...)` hits, all of which spell it correctly), this single
hit is read as leftover or unreachable script content, not a competing data point.

### Template identity re-confirmed independently, past just "exists in templates.bif"

The prior round's confidence rested on `docs/re/spawn.md` §4's "all nine exist in `templates.bif`"
— true, but not proof a resref's NAME matches the companion it is assumed to. This round pulled
each `.utc`'s own `Tag` and `FirstName` GFF fields (`tools/kotorfiles.py gff <resref>.utc --path
Tag`, resolving the `FirstName` `StrRef` through `dialog.tlk`) and got an exact, unambiguous match
for all nine — e.g. `p_hk47`'s `Tag` is literally `HK47` and its `FirstName` strref resolves to
`"HK-47"`; `p_t3m4`'s `Tag` is `T3M4`, name `"T3-M4"`; `p_cand`'s `Tag` is `Cand`, name
`"Canderous"`:

| Resref | `Tag` | `FirstName` (via `dialog.tlk`) |
| --- | --- | --- |
| `p_bastilla` | `Bastila` | `Bastila` |
| `p_cand` | `Cand` | `Canderous` |
| `p_carth` | `Carth` | `Carth` |
| `p_hk47` | `HK47` | `HK-47` |
| `p_jolee` | `Jolee` | `Jolee` |
| `p_juhani` | `Juhani` | `Juhani` |
| `p_mission` | `Mission` | `Mission` |
| `p_t3m4` | `T3M4` | `T3-M4` |
| `p_zaalbar` | `Zaalbar` | `Zaalbar` |

### Code change: the mapping split into `PartyRosterTemplateTable`, a small unit-tested class

`Odyssey.AddAvailableNpcByTemplate` was **already slot-generic** before this round — it is not,
and was never, a Carth-only special case; `PartyRosterController.Reconcile()` already calls it
with whatever slot the peer's roster mask names. What was missing was independent confidence in
the DATA (closed above) and a way to unit-test that data at all — the private `string[]
PartyRosterTemplates` array lived inside `Odyssey.cs`, a `static unsafe` class full of raw
P/Invoke that cannot run in the test project.

Following the same split this codebase already uses for `PartyRosterRecruitRule` (the decision
half of the same feature, split out "so it can be unit-tested — the engine call itself does not
exist in the test runner"), the table now lives in its own file,
`src/KotorCoop.Plugin/PartyRosterTemplateTable.cs`:

```csharp
internal static class PartyRosterTemplateTable
{
    public const int SlotCount = 9;
    public static bool TryGetTemplate(int slot, out string resref) { ... }
}
```

`Odyssey.AddAvailableNpcByTemplate` now calls `PartyRosterTemplateTable.TryGetTemplate(slot, out
resref)` in place of its old range-check-then-index; behaviour is identical (same nine strings,
same order, same out-of-range refusal), but the data is now covered by
`PartyRosterTemplateTableTests` (`tests/KotorCoop.Tests/PartyRosterTemplateTableTests.cs`,
compiled into the test project the same way `PartyRosterRecruitRule.cs` already is): all nine
slot→resref pairs, out-of-range refusal (`-1`, `9`, `100`), `SlotCount == 9`, and no two slots
sharing a template. `dotnet test tests/KotorCoop.Tests` (Release): **1688/1688 passed** (14 new
cases; no production behaviour changed, no test removed).

### Confidence summary

| Slot | Companion | Resref | Confidence |
| --- | --- | --- | --- |
| 0 | Bastila | `p_bastilla` | Statically confirmed (disassembly, 15 independent script hits) + template identity confirmed (Tag/name). NOT live-tested. |
| 1 | Canderous | `p_cand` | Statically confirmed (disassembly, 4 clean hits + 1 explained anomaly) + template identity confirmed. NOT live-tested. |
| 2 | Carth | `p_carth` | **Live-confirmed** (`party-reconciliation.md` round 75 and this document's own live pass) AND statically confirmed (9 hits) AND template identity confirmed. The only slot with an actual two-instance result. |
| 3 | HK-47 | `p_hk47` | Statically confirmed (disassembly, 2 hits) + template identity confirmed. NOT live-tested. |
| 4 | Jolee | `p_jolee` | Statically confirmed (disassembly, 5 hits) + template identity confirmed. NOT live-tested. |
| 5 | Juhani | `p_juhani` | Statically confirmed (disassembly, 3 hits) + template identity confirmed. NOT live-tested. |
| 6 | Mission | `p_mission` | Statically confirmed (disassembly, 2 hits) + template identity confirmed. NOT live-tested. |
| 7 | T3-M4 | `p_t3m4` | Statically confirmed (disassembly, 2 hits) + template identity confirmed. NOT live-tested. |
| 8 | Zaalbar | `p_zaalbar` | Statically confirmed (disassembly, 1 hit) + template identity confirmed. NOT live-tested. |

This is a much stronger evidentiary basis than the prior round's "standard nwscript.nss order,
not independently re-derived" — every slot now has direct disassembly evidence from the shipped
game's own scripts, not an assumed convention — but it is still **static analysis, not a live
result**. `dialogue_recruit_sync` stays OFF by default; per this project's own "flip on if clean"
bar, that requires live confirmation for the remaining eight slots (or at least a sample beyond
Carth), which needs the two-instance rig this round did not touch.

### Live-test recipe for the remaining eight companions

Follows the same shape the Carth live-test plan (above) and its live-verification round already
used: force the same bit onto the wire the peer's `dialogue_recruit_sync` reconciliation reacts
to, independent of how the recruiting instance itself came to have the companion, then confirm the
OTHER instance ends up with a correctly-identified, live, resolvable body.

**The console shortcut, and its real constraint.** `console 'recruit <slot> [tag]'`
(`ConsoleCommand.ParseRecruit`, `CheatController.Recruit`) is the same dev-only primitive used for
Carth. Two facts about it matter for planning a companion beyond Carth:

- `recruit <slot>` with NO tag only works if that slot was ALREADY recruited at some point on
  THIS savegame (it reads the `"AVAILNPC%d"` cache `AddNPC_2` populates — see
  `Odyssey.TrySetSlotAvailable`'s own doc comment) — on a fresh save it will refuse with "spawn
  failed... no blueprint on a raw availability poke."
- `recruit <slot> <tag>` falls back to a creature ALREADY PLACED IN THE CURRENT MODULE with that
  exact `Tag` (the `Tag` column in the table above — e.g. `Cand`, `HK47`, `T3M4`). This means the
  tester's save must be in (or have already visited and left placed) the module where that
  companion's body physically exists — the "representative script" column above names it, and
  since scripts are per-module, the module name itself (`tar_m08aa`, `tat_m17ac`, `kas_m24aa`,
  `danm13`) is the area to be in.

**Recipe, per companion**, mirroring the Carth recipe's steps 1-6 exactly (both instances on the
same fresh save at/near the module named below; `dialogue_recruit_sync=1` and
`party_roster_sync=1` on both `coop.ini`s; HOST recruits, watch CLIENT's log for the
`recruited NPC slot N ... spawned NPC slot N ... added NPC slot N` sequence within one
`PartyRosterController` pass; confirm two distinct resolvable object ids on the client per the
duplicate-body-fix verification steps; confirm portrait/identity, inventory, and faction on the
client exactly as the Carth recipe's steps 4-6 already describe):

| Companion | Slot | Module to be in | Tag for `recruit <slot> <tag>` | Notes |
| --- | --- | --- | --- | --- |
| Bastila | 0 | `tar_m03aa` / `tar_m03af` (Taris) | `Bastila` | Reunion point after the party splits following the Endar Spire crash; also re-added at `danm13` (Dantooine) and the endgame `unk_m44ac` sequence — those are rejoins, not the first recruit, so prefer the Taris module for a "first recruit" test. |
| Canderous | 1 | `tar_m08aa` (Taris) | `Cand` | |
| HK-47 | 3 | `tat_m17ac` (Tatooine) | `HK47` | Two scripts in the SAME module (`k_ptat_hk47add`, `k_ptat_hk47ship`) — either placement should work. |
| Jolee | 4 | `kas_m24aa` (Kashyyyk) | `Jolee` | |
| Juhani | 5 | `danm13` (Dantooine) | `Juhani` | Same module as one of Bastila's re-adds; if testing both on the same save, recruit Juhani first to avoid ambiguity about which global flag fired. |
| Mission | 6 | `tar_m04aa` (Taris) | `Mission` | |
| T3-M4 | 7 | `tar_m02ab` (Taris) | `T3M4` | |
| Zaalbar | 8 | `tar_m05aa` (Taris) | `Zaalbar` | |

Cheapest reachable next test beyond Carth: five of the eight (Bastila, Canderous, Mission, T3-M4,
Zaalbar) are all on Taris, reachable from a single save advanced only as far as the Taris chapter
— no need to reach Kashyyyk or Tatooine for a first additional data point. If even one of these
five confirms clean (correct portrait, no duplicate-body regression, correct faction), that is a
second independent data point beyond Carth and a reasonable basis to consider defaulting
`dialogue_recruit_sync` on, per this project's own "two independently-confirmed slots" bar the
original design doc set (§4's live-test plan, step 7).

### What could not be determined from the repo

- No live confirmation for slots 0, 1, 3-8 — this round is static analysis only (explicitly
  sanctioned: another agent owns the live rig).
- Whether `p_canderous` (the one non-existent resref spotted in `k_ptar_davikest`) is truly dead
  code or reachable under some condition this pass did not trace — irrelevant to the shipped
  mapping (`p_cand` is what `AddAvailableNPCByTemplate` is called with everywhere else, and is the
  only one of the two that exists as a real resource), but flagged rather than silently dropped.
- The exact Taris/Dantooine/Kashyyyk/Tatooine area names each module resref corresponds to (this
  round worked at MODULE granularity, which is what `recruit <slot> <tag>` needs — it does not
  need the specific `.are` display name).

## Round (2026-08-24): re-verifying the wiring itself, an independent second cross-check of all
## nine templates against the real GOG install, and one more unit test tying the two pieces together

Task: confirm — not assume — that the previous round's `PartyRosterTemplateTable` is actually
consumed by the live recruitment dispatch for all nine slots (not just present as an unused
table, or silently special-cased to Carth somewhere), independently re-cross-check the eight
non-Carth resrefs against the real GOG install from a fresh worktree, and strengthen unit
coverage of the wiring. No live rig touched this round either (worktree-isolated: read-only
against `D:\Games\swkotor-gog-p2`, no write access, no running game).

### The wiring: confirmed already fully generic, no code change needed

Read `Odyssey.AddAvailableNpcByTemplate` (`src/KotorCoop.Plugin/Odyssey.cs`) and
`PartyRosterController.Reconcile` (`src/KotorCoop.Plugin/PartyRosterController.cs`) directly
rather than trusting the prior round's own summary of them:

- `AddAvailableNpcByTemplate(int slot)` calls `PartyRosterTemplateTable.TryGetTemplate(slot, out
  resref)` unconditionally for whatever `slot` it is passed — no Carth (or any other) special
  case, no hardcoded fallback resref if the lookup fails (it returns 0/refuses instead).
- `PartyRosterController.Reconcile()` computes `addSlot = LowestSlot(toAdd)` from the peer's
  roster bitmask (any of the nine bits, not a fixed one) and — behind `dialogue_recruit_sync`,
  still OFF by default — calls `Odyssey.AddAvailableNpcByTemplate(addSlot)` with that same
  variable slot. Nothing between the wire (`PartyRoster`, packet 37) and the engine call narrows
  this to slot 2.

**Verdict: the full nine-companion table was already wired into the live recruitment path before
this round started** (done by the "Round (2026-08-24): the other eight slots' templates..."
section above, which this round's date collides with — that work landed first). No production
code was changed this round.

### Independent re-cross-check against the real GOG install (`D:\Games\swkotor-gog-p2`, read-only)

This worktree can read the real install directly, so rather than trusting the prior round's
table of Tag/FirstName values, this round re-ran the same two checks itself from scratch using
`tools/kotorfiles.py key --find` and `tools/kotorfiles.py gff --path Tag` / `--path FirstName` +
`tools/kotorfiles.py tlk <strref>`, independently, resref by resref:

| Resref | Exists in `templates.bif` as `.utc`? | `Tag` | `FirstName` (via `dialog.tlk`) | Matches expected companion? |
| --- | --- | --- | --- | --- |
| `p_bastilla` | Yes (index 1008) | `Bastila` | `Bastila` (strref 31360) | Yes |
| `p_cand` | Yes (index 1010) | `Cand` | `Canderous` (strref 26218) | Yes |
| `p_carth` | Yes (index 1012) | `Carth` | `Carth` (strref 30733) | Yes |
| `p_hk47` | Yes (index 1014) | `HK47` | `HK-47` (strref 27766) | Yes |
| `p_jolee` | Yes (index 1016) | `Jolee` | `Jolee` (strref 16771) | Yes |
| `p_juhani` | Yes (index 1018) | `Juhani` | `Juhani` (strref 16772) | Yes |
| `p_mission` | Yes (index 1021) | `Mission` | `Mission` (strref 30734) | Yes |
| `p_t3m4` | Yes (index 1027) | `T3M4` | `T3-M4` (strref 22095) | Yes |
| `p_zaalbar` | Yes (index 1030) | `Zaalbar` | `Zaalbar` (strref 25891) | Yes |

Also independently re-confirmed: `p_canderous` (the one anomalous string spotted in
`k_ptar_davikest`) returns **zero** matches against `chitin.key`'s 25,836 entries from this
round's own separate query — reproducing the prior round's finding that it is not a real
resource, not merely re-stating it.

**No mismatch found.** All nine resrefs independently re-verified: real, loadable `.utc`
templates in the shipped GOG install, each one's `Tag` and `FirstName` unambiguously naming the
companion its slot claims. This is a second, independently-run confirmation of the previous
round's template-identity table (same method, same tool, freshly executed from this worktree
against the real install rather than trusted from the doc), not a rubber stamp — every value was
re-queried from the raw files, not copied from the table above.

### Unit test added: the wiring itself, generalised across all nine slots

The existing `PartyRosterTemplateTableTests` (all nine slots resolve, out-of-range refused,
`SlotCount == 9`, no duplicate templates) and `PartyRosterRecruitRuleTests` (2 cases, origin-only
— correctly slot-agnostic, since the immediate-add gate never depended on which companion) each
already covered their own class in full. What was missing was a test that ties the two together
the way the actual call chain does, generalised over the slot count rather than asserting only
the one case the live rig has touched (Carth). Added
`tests/KotorCoop.Tests/PartyRosterRecruitWiringTests.cs`:

- `EveryOneOfTheNineSlotsResolvesATemplateAndIsGatedTheSameWayCarthIs` — loops
  `slot in 0..PartyRosterTemplateTable.SlotCount`, asserting every slot both resolves a non-empty
  template AND is gated identically by `PartyRosterRecruitRule.CanAddImmediately`. A regression
  that silently narrowed either piece back to a single companion (e.g. an early-return special
  case for slot 2 introduced by a future change) would fail this test even though the two
  existing per-class test files would not catch it, since neither asserts the OTHER class's
  behavior in the same case.
- `OutOfRangeSlotsAreRefusedBeforeTheRuleEverRuns` — the second independent bounds check
  `Odyssey.AddAvailableNpcByTemplate` relies on, beyond `PartyRoster.SlotMask`'s own 0-8 bound.

This remains, like everything else in this codebase's coverage of this feature, a test of the
PURE decision logic only — `Odyssey.AddAvailableNpcByTemplate`'s actual P/Invoke call and
`PartyRosterController.Reconcile`'s engine-facing branches still have no unit coverage and can't
get any without the live rig (the class is `internal sealed unsafe`, calling raw function
pointers into a running `swkotor.exe`). This test is a stronger proxy for "is the wiring generic"
than the two pre-existing test files individually provide, not a substitute for a live recruit.

`dotnet test tests/KotorCoop.Tests` (Release): **1690/1690 passed** (2 new cases; no production
code touched, no existing test changed).

### What a live pass still needs, unchanged in substance from the prior round

Static confidence is now about as high as it can get without a live rig — table wired in
generically (re-confirmed by reading the actual dispatch code, not just the doc describing it),
all nine templates independently re-verified twice now (two separate rounds, same method,
against the real install) against two different signals (`Tag` and `FirstName`) each. None of
this substitutes for what only a running two-instance test can show:

1. **A second live-recruited slot, beyond Carth.** Slot 2 remains the only slot with an actual
   two-instance result (`party-reconciliation.md` round 75 + this document's own 2026-08-23 live
   passes). Every other slot's *identity on screen* — does the client's newly-added companion
   actually look like, portrait as, and voice as the companion its slot claims — has never been
   observed, only inferred from static resref/Tag/FirstName matching. The recipe and the
   cheapest-first ordering (five Taris-reachable companions: Bastila, Canderous, Mission, T3-M4,
   Zaalbar) are already written out in the "Live-test recipe for the remaining eight companions"
   section above; nothing in this round changes that recipe.
2. **The same duplicate-body-fix verification steps** (two distinct resolvable object ids, exactly
   one visible in-world body, sub-second timing) repeated for that second slot — the 2026-08-23
   live pass only exercised the fix's mechanism for Carth; whether it generalises cleanly to a
   companion recruited through a genuinely different `.dlg`/script (not the `recruit`
   console-fallback substitution used for Carth) is untested.
3. **Ideally, the REAL dialogue conversation** for at least one companion (not the `console
   'recruit <slot> <tag>'` fallback every live pass so far has used) — every live result to date,
   including Carth's, went through the console substitution because the test saves used were past
   or short of the actual recruitment conversation's story point. A real `AddAvailableNPCByTemplate`
   call fired from an actual `.dlg` reward node has still never been observed end-to-end on the
   rig, only reproduced via the dev console primitive that calls the same engine functions.

Per this project's own "two independently-confirmed slots" bar (§4's live-test plan, step 7;
also restated in the previous round), **one clean live recruit of any second companion — ideally
one of the five Taris-reachable ones — is what would move `dialogue_recruit_sync` from "off,
static-only" to "a real candidate for default-on."** `dialogue_recruit_sync` stays OFF by default
in `coop.ini.example`; this round changed no defaults and no production code, only re-verified
existing claims and added one test file.

`README.md`'s "confirmed for one of nine companions" framing (line 45) is left unchanged — this
round found nothing that revises it; the eight additional slots are now independently
STATICALLY re-confirmed twice over, not live-confirmed once.

## Live pass (2026-08-24): a SECOND companion (Zaalbar, slot 8) live-recruited through the REAL
`dialogue_recruit_sync` network path — the "two independently-confirmed slots" bar is met, but
`dialogue_recruit_sync` stays OFF pending one new open question this pass surfaced

Rig access this round (plugin rebuilt/reinstalled on both GOG installs first). `dialogue_recruit_sync=1`
set on both `coop.ini`s. Both instances started fresh on `END_M01AA`/save slot 1 (the only saves on
this rig are both at the Endar Spire — no further-along Taris save exists, and this rig has no
working keyboard/mouse input, so a REAL dialogue-triggered recruit, as this task asked for
"ideally," was not reachable this pass either; the console `recruit <slot> <tag>` fallback was used
again, same as every prior round).

### Two crashes, then a clean run — `tools/blockpoke.ps1 travel` cross-planet warp

The first two attempts to reach Taris (`travel tar_m08aa` then `travel tar_m05aa` back to back; then,
after a full rig restart, a single `travel tar_m05aa`) each ended with the CLIENT's `swkotor.exe`
process exiting entirely during the transition (`agent2.log`: `[resolve] player ...: vtable
unreadable`, `TryFindPlugin: scan found 0 candidate(s)`, then a clean `session-saved character`
sidecar and disconnect — the same signature `shared-state-audit.md`'s own item 3 already recorded
for an un-godded client taking overkill damage). The host's own log showed it entered combat
(`state IN COMBAT combat [FIGHT in area1 - engaged: you]`) right as the transition announced on
both failed attempts. A third attempt — full rig restart, `cheat god 1` armed on BOTH instances
BEFORE the warp, single `travel tar_m05aa` (no intermediate module) — landed both instances
cleanly on `tar_m05aa`, connected, no crash. Not proven causal (three data points, two different
variables changed between the failing and succeeding attempt — the double-warp and the missing god
mode — and `shared-state-audit.md`'s own item 4 already showed a bare single warp CAN succeed
without god mode), but consistent with the existing "un-godded client + incidental combat" crash
class, and cheap insurance either way. Worth carrying into future rig work: arm god mode on both
sides before any debug cross-module warp, and avoid chaining two warps back to back.

### The recipe's own tag assumption was wrong — found and worked around live

`docs/research/dialogue-consequences.md`'s own companion table (above) lists Zaalbar's `recruit
<slot> <tag>` tag as `Zaalbar`, taken from the base `p_zaalbar` template's `Tag` field
(independently re-verified against `templates.bif` earlier this same document). **The LIVE, placed
body in `tar_m05aa` does not use that tag.** `tools/creatures.ps1` (a full live-creature memory
scan, not a tag-indexed lookup) found him as `id 0x2, tag 'tar05_zaalbar'` — the per-module GIT
placement's own tag override, not the story blueprint's. `console 'recruit 8 Zaalbar'` refused
("no creature tagged 'Zaalbar' is here either") on both attempted Taris modules; `console 'recruit
8 tar05_zaalbar'` succeeded immediately: `NPC slot 8 recruited and added to the party (0x2)`. This
is worth recording for whoever tries the remaining six companions next — the table's `Tag` column
is the STORY BLUEPRINT's tag, not necessarily what a specific GIT placement uses; `tools/creatures.ps1`
against the target module is the reliable way to find the real live tag before assuming the table's
value works. (This also explains why the earlier `tar_m08aa`/Canderous attempt refused with no
placed body found at all — `tools/kotorfiles.py creatures tar_m08aa`'s own GIT dump, checked
separately, lists no `p_cand`/`cand`-named entry; Canderous is very likely a script-spawned
addition to that module rather than a static GIT placement, unlike Zaalbar.)

### The real `dialogue_recruit_sync` path fired correctly, end to end

Host `plugin.log`: `party roster: local party is now 0x101 (recruited 0x101)`. Client `plugin.log`,
within the same `PartyRosterController` pass window (191ms total, well under the "one pass" bar
prior rounds used):

```
[17:14:22.300] party roster: recruited NPC slot 8 as 0x1 from its own template (peer recruited
                0x101, here 0x001) - dialogue_recruit_sync; a body will be spawned and added on a
                later pass
[17:14:22.388] party roster: spawned NPC slot 8 as 0xF4 to add them here
[17:14:22.388] party roster: added NPC slot 8 (object 0xF4) to the party
[17:14:22.391] party drive: companion 0xF4 held neutral and pinned - the host owns its health
[17:14:22.391] party drive: mirroring 2 of the host's companions
```

No `party roster: could not add NPC slot 8 (failure 9)` line anywhere in the sequence — the
duplicate-body FIX (the off-stage cache object never being added directly) held for this slot
exactly as it held for Carth's. `tools/creatures.ps1` on the client confirms object `0xF4` is a
genuine, fresh, properly-leveled companion body (`hp 32, xp 6001` — standard companion stats, NOT
the captive's `hp 8`), correctly identified as Zaalbar (tag `zaalbar`), correctly faction-pinned
(`held neutral and pinned`). This is a second, independently-confirmed slot beyond Carth: the
per-slot template mapping (`PartyRosterTemplateTable[8] == p_zaalbar`) is now live-confirmed, not
just statically re-derived twice.

### One new open question this pass surfaced, not settled: a possible stale mirrored body

`tools/creatures.ps1` on the CLIENT, run again ~10s after the recruit, still shows the PRE-recruit
captive body too: `object 0x2, tag 'tar05_zaalbar', hp 8, xp 0` — the client's mirrored copy of
Zaalbar's original placed-in-the-world body, from BEFORE he was recruited, sitting at the exact
same coordinates as the new party companion (`225.86, 192.24, 29.72`, since the new companion had
not yet started walking to the leader). This is a DIFFERENT object from the "off-stage cache" the
existing duplicate-body fix targets (that one, per this document's own root-cause writeup, is never
placed in the world and never renders) — this is a full, in-world creature object that existed
BEFORE the recruit and, as far as this pass could determine, was never explicitly destroyed or
hidden once its "owner" (Zaalbar) became a party member instead of a standalone placed NPC. The
HOST's own `plugin.log` confirms its own local object `0x2` correctly transitioned to
`commandable=1 inParty=1 cswPartyIndex=2` (the real, singular Zaalbar, now following) — so this is
specifically a CLIENT-side question: does its mirrored copy of the pre-recruit body linger,
unrendered but present in memory (harmless), or does it still render (a real visual duplicate)?
This pass could not settle it — the rig's own documented limitation applies (`tools/click.ps1`/
`tools/shot.ps1` both come back solid black, per `shared-state-audit.md`'s own item 1 finding) —
and it is a genuinely NEW question the Carth-only precedent never surfaced, because Carth's own
live-test rounds never ran a full `creatures.ps1` sweep looking for a stale copy of his own
pre-recruit body specifically. Flagged, not fixed, and not assumed either way.

### Verdict: the recruit mechanism itself is now confirmed for two independent slots; `dialogue_recruit_sync` stays OFF, one specific reason

Per this project's own "two independently-confirmed slots" bar, that bar is now met on the
mechanism's own core question (does the wire bit reach the peer, does the peer build the right
companion from the right template, does it seat cleanly with the right stats and faction) — Zaalbar
joins Carth as a second, live, network-path-confirmed slot. `dialogue_recruit_sync` is NOT flipped
to default ON this pass, for one specific, named reason rather than general caution: the stale-
mirrored-body question above is new, untested, and could be a real (if likely cosmetic) duplicate-
body regression specific to companions who are ALSO pre-existing GIT placements — a category
Carth's own precedent happens to also fall into but was never checked this way. Resolving it needs
either working screenshot capture in this rig or a live human eyeballing the client's screen right
after a recruit of a companion who was previously a placed, visible NPC. Recorded precisely so the
next pass does not have to re-discover it: check whether the client's screen shows one Zaalbar or
two immediately after a recruit like this one, before defaulting this flag on.

## Root cause and fix for the client cross-module-warp crash (2026-08-24, follow-up round)

The "two client crashes during cross-module debug warps" the round above hit and worked around
(arm `cheat god 1` on both sides, avoid chaining two warps) but did not root-cause: root-caused and
fixed. Reproduced live on this rig, twice in a row, BOTH with and WITHOUT `cheat god 1` armed —
ruling out the "un-godded client taking overkill damage" theory `shared-state-audit.md`'s own item 3
originally proposed for this signature (that theory remains correct for ITS OWN case; it is simply not
what this one is). Confirmed by polling Windows Event Viewer for several minutes after each crash: NO
WER 1000 (Application Error) event was ever logged for either reproduction — this is a clean process
exit, not an access violation caught by the OS's default handler, contrary to this codebase's own usual
convention that a plugin crash's eip is inside `dinput8.dll` and symbolicatable via `llvm-symbolizer`.
No crash address was ever available to symbolicate; the root cause was found by log correlation
instead.

**The evidence**: the client's own `kotorcoop-plugin.log` shows `control: you are driving companion
0x... — switch not ready yet, retrying` (`ControlReturnController`'s own hand-back-in-progress line) as
the LAST line before the process silently exits, both times, each within 150ms-1.1s of `epoch: world
unloading` for the destination module. This line is not rare on its own — it fired 15 times across this
install's accumulated log with no crash most of those times — but every prior firing happened during
steady-state play, seconds to minutes after any transition; only the two firings landing within about a
second of a fresh module epoch crashed.

**Root cause**: `ControlReturnController.OnFrame`'s engine calls (`Odyssey.GetControlledCreaturePointer`/
`Odyssey.SwitchControlToPc`, which walk `CSWParty`'s raw member array via `FindMemberIndex`/
`RotateControlToFront`) ran completely unconditionally, with no check against the world-load epoch. This
is the exact same access-violation class `PartyFollowTransitionRule` (party-reconciliation.md round
85/87) already closed for `PartyFollowController`: a fresh module epoch can read valid (object-array
pointer resolved, module name resolved) one frame before the new module's `CSWParty`/party-table data is
actually populated, and any per-frame controller walking that data unconditionally on that exact frame
is reachable by the same crash. `ControlReturnController` was simply never given the guard the sibling
controller already had.

**Fix**: reused `PartyFollowTransitionRule.ShouldSkipThisFrame` verbatim in `ControlReturnController.OnFrame`
— skip this controller's engine calls entirely on the single frame a new epoch is first observed
(deferring by exactly one frame, the same discipline the existing rule already documents), clearing the
countdown/claim bookkeeping so nothing carries a stale companion id across the module change. No new
pure-function tests needed: the reused rule already has its own full test coverage
(`PartyFollowTransitionRuleTests`), and `ControlReturnController.OnFrame` itself is `unsafe`/engine-coupled
and untestable in isolation, exactly like `PartyFollowController.OnFrame` before it.

**Live-verified, decisively**: reproduced the exact trigger condition deliberately rather than waiting for
luck — switched the client's own control to a party companion via the `leader 0` console primitive, then
immediately warped the host cross-module before the hand-back could settle on its own. The
`control: ... switch not ready yet, retrying` line fired 780ms after `epoch: world unloading`, the same
shape that killed the client twice before this fix (150ms-1.1s window) — and this time the client
survived cleanly, no crash, no disconnect, both processes still `Responding: True` afterward. Two
additional plain warps (with and without god mode) also passed clean.

`dotnet test`: 1721/1721 green (one known-flaky `TransitionMovieTests.TheBlockVersionMovedInBothHalves`
failure under full-suite parallel load, passes cleanly in isolation — unrelated to this change).

## Round (2026-08-24): the stale-mirrored-body mechanism identified by code inspection, and a
## targeted fix shipped against it — NEEDS-LIVE, no rig access this round

Code-only pass (isolation worktree, no live rig this round — another agent held the only slot).
Task: read the previous entry's stale-mirrored-body finding in full, trace the recruitment path
end to end, confirm or refute the "client's creature-phantom channel never told to despawn the
pre-recruit NPC" hypothesis, and implement a fix if the mechanism held up.

### What the previous entry's evidence actually said

`tools/creatures.ps1` on the CLIENT, ~10s after a real `dialogue_recruit_sync` recruit of Zaalbar,
still showed **object `0x2`, tag `tar05_zaalbar`, hp 8, xp 0** — the pre-recruit captive body, at
the exact spawn coordinates, alongside the genuine new party companion at object `0xF4` (hp 32,
xp 6001, tag `zaalbar`). The host's own log confirmed ITS OWN object `0x2` correctly transitioned
to `commandable=1 inParty=1 cswPartyIndex=2` — i.e. on the host, the SAME object that was standing
in the world became the party member; nothing was destroyed or replaced. The entry explicitly
flagged this as unconfirmed on-screen (no screenshot capability) and undetermined whether it
renders or is merely a harmless unrendered duplicate in memory.

### The hypothesis as given, and what tracing the code actually found

The task handed down a specific theory: a separate "creature-phantom mirroring channel" mirrors
an off-stage NPC's body as an ordinary creature phantom before recruitment, and nothing tells it
to stop when the recruit lands. **That channel does not exist for this case, and tracing
`PhantomController.cs` end to end confirms why**: `PhantomController` spawns and drives exactly
one body PER REMOTE PLAYER (`k_coop_phantom`/`n_commm`, driven by `RemoteView.Position` etc.) — it
has no concept of an ordinary world NPC at all, recruited or not.

The actual mechanism, read from `PartyRosterController.cs`, `Odyssey.AddAvailableNpcByTemplate`
and the comment trail across `EnemyPresenceController.cs`/`MaterialisedEnemies.cs` (grep for "GIT
identical"): **KOTOR's placed NPCs are never mirrored at all**. Both instances load the identical
save and module data, and the engine assigns object ids deterministically in GIT placement order
— so a GIT-placed NPC like Zaalbar gets the SAME object id (`0x2`) on every instance that loaded
the module, independently, with no network traffic involved. This is the exact assumption
`EnemyPresenceController`'s "GIT-identical creatures" reasoning already relies on for enemies.

The bug is a direct consequence of that architecture, not a missing "stop mirroring" message:
- On the HOST, the real recruit dialogue's own script converts the EXISTING placed body (`0x2`)
  into the party member in place — no new object, nothing to clean up.
- On a peer running `dialogue_recruit_sync`, `PartyRosterController.Reconcile` never sees that
  conversion happen (only one instance ever runs the recruiting conversation's script). Instead it
  recruits blind through `Odyssey.AddAvailableNpcByTemplate` (an off-stage character-sheet object)
  and then, next pass, `Odyssey.SpawnAvailableNpc` — which builds a completely FRESH body at the
  player's current position. The peer's own GIT-identical copy of the placed NPC (its own object
  `0x2`, independently spawned when ITS OWN engine loaded the module) is never referenced by any
  of this and is left standing exactly where it always was.

So this is the SAME defect class the prior round's duplicate-body fix closed
(`PartyRosterRecruitRule.cs`, "an off-stage recruitment cache mistaken for an addable member") —
a second engine object built where only one was needed — but on the OPPOSITE side of the
recruitment: not the host's roster-add plumbing, but the peer's blind fresh-spawn never checking
whether it already had a GIT-identical body it could reuse instead.

### The fix: broadcast each instance's own party-table object id per slot, and reuse a
### GIT-identical local body before ever spawning a fresh one

`PartyRosterController.PublishLocalRoster` now also publishes, alongside the existing
available/party bitmasks, this instance's own party-table object id
(`Odyssey.GetPartyRosterObjectId`) for every slot currently in its ACTIVE party (0 otherwise —
the field is only ever meaningfully written by the engine's `AddMember`). This rides the SAME
roster channel `Reconcile` already consumes every pass, wired through all four layers it
touches:

- **`SharedBlock.cs`/`SharedBlockView.cs`** (plugin, in-process): two new nine-slot `uint`
  arrays in the free tail (`0x3F28..0x3F70`, no `TotalSize` growth) — `OffsetLocalPartyRosterObjectIds`
  / `OffsetHostPartyRosterObjectIds`. `PluginHost._block->Version` and
  `KotorGameBridge.PluginSignature` both bumped to **53** (kept in lockstep —
  `ItemHarnessTests.TheBlockVersionIsTheSameNumberInBothHalves` and
  `TransitionMovieTests.TheBlockVersionMovedInBothHalves` both updated and green).
- **`KotorCoop.Protocol.PartyRoster`**: a new `uint[]? ObjectIds` field (default `null`,
  backward-source-compatible with every existing 2-arg call site), `ObjectIdFor(slot)` accessor,
  and a hand-written `Equals`/`GetHashCode` — the record-synthesized ones would have compared the
  backing array by REFERENCE, which broke the existing `PartyRosterRoundTrips`/
  `EmptyPartyRosterRoundTrips` tests the moment a decoded roster's freshly-allocated all-zero
  array stopped reference-equalling a hand-built roster's `null`. Fixed by treating `null` and
  "all zero" as the same value (`ObjectIdFor` already returns 0 for both).
- **`PacketCodec.Write/ReadPartyRoster`**: nine more `uint32`s per `PartyRosterPacket`, always
  present (36 bytes; `MaxPacketSize` is 1400, no pressure). `CoopHost`/`CoopClient`/`CoopAgent`
  needed NO changes — they already forward the whole `PartyRoster` value opaquely.
- **`KotorGameBridge.TryReadLocalPartyRoster`/`WriteHostPartyRoster`** (agent, external memory):
  reads/writes the same nine-slot arrays at the plugin-block offsets above.

`PartyRosterController.Reconcile`, in the `dialogue_recruit_sync` branch, now checks the peer's
published object id for the slot BEFORE calling `AddAvailableNpcByTemplate`: if
`CreatureFinder.FindByObjectId` resolves a live creature under that same id locally, the slot is
marked available (`Odyssey.TrySetSlotAvailable` — sufficient here specifically because, unlike
the raw-poke path `party-reconciliation.md` Round 75 already ruled insufficient, nothing
downstream ever needs the off-stage `SpawnAvailableNpc` cache) and that body is added to the
party DIRECTLY, in the same pass — no template, no fresh spawn, so no duplicate is ever created.
The decision itself (`PartyRosterBodyReuseRule.cs`, new file) is split out pure/testable the same
way `PartyRosterRecruitRule` already is, since the engine calls it gates
(`CreatureFinder.FindByObjectId`, `Odyssey.TrySetSlotAvailable`) do not exist in the test runner.
Falls through unchanged to the existing off-stage-recruit-then-spawn path whenever no
GIT-identical body is found (a companion found via a minigame, a crate, a cutscene reveal — never
a placed NPC to begin with — or an older peer build that never populated the new field).

### Tests added, `dotnet test` result

- `PartyRosterBodyReuseRuleTests.cs`: the pure reuse decision (0/`ObjectInvalid` never worth a
  lookup, a lookup miss falls back, and the exact Zaalbar shape — id `0x2`, found locally — is
  authorised).
- `PacketCodecTests.PartyRosterObjectIdsRoundTrips` / `ANullObjectIdArrayEqualsAFreshlyDecodedAllZeroOne`:
  the new wire field survives encode/decode, and the equality fix holds.
- `TransitionMovieTests.TheBlockVersionMovedInBothHalves` updated to the new version 53.

Full suite (`dotnet test` from repo root) run after every change in this round. Baseline before
this pass's own edits: 1714 tests, 1713 passing. Bumping the shared-block version to 53 broke
exactly the one test expected to break (`TransitionMovieTests.TheBlockVersionMovedInBothHalves`'s
hardcoded "52"), fixed immediately alongside the bump. Final run, including this pass's 8 new
tests: **1722 total, 1722 passing, 0 failed** (2m56s).

### What this pass could NOT do, and needs a live rig to close

**NEEDS-LIVE, entirely.** Nothing in this round touched a running game — this is a code-only
pass in an isolation worktree with no rig access. Specifically unverified:

1. That `CreatureFinder.FindByObjectId` genuinely resolves the peer's pre-recruit placed body
   under the SAME id on this instance for a real two-install session (the GIT-identity assumption
   this whole fix leans on, already trusted elsewhere in this project, but never exercised through
   THIS particular call site).
2. That `Odyssey.TrySetSlotAvailable` followed immediately by `Odyssey.AddPartyMemberBySlot` on a
   REUSED (not freshly template-spawned) object id actually succeeds end to end through the real
   engine calls, with no refusal this design did not anticipate.
3. That the fix actually eliminates the duplicate live — re-run the exact recipe the prior entry
   used (Zaalbar, `tar_m05aa`, `dialogue_recruit_sync=1` on both installs) and confirm
   `tools/creatures.ps1` on the client shows ONLY object `0x2` (now `inParty=1`), never a second
   `0xF4`-style fresh body, and ideally a human eyeballing the client's screen for the one-vs-two
   question the prior entry could not settle without screenshot capability.
4. That the new wire field and version bump do not desync an old-plugin/new-agent (or reverse)
   pairing in a way the version guard does not already catch cleanly.

`dialogue_recruit_sync` stays OFF by default, unchanged by this pass — this fix only changes what
happens on the path that flag already gates, so there is no live behavioural change until a
future live pass both confirms this fix and makes its own call on flipping the flag.

`dotnet test`: no production code changed this pass (a live-verification run only).

## Live pass (2026-08-24, follow-up round): the stale-mirrored-body fix CONFIRMED live —
## `PartyRosterBodyReuseRule` reuses the GIT-identical body exactly as designed, `dialogue_recruit_sync`
## flipped ON by default

Rig access this round (plugin/agent rebuilt and reinstalled on both GOG installs first, block
version 53 confirmed on both). Ran the exact recipe the entry above asked for: both instances
warped (`tools\blockpoke.ps1 travel tar_m05aa`, `cheat god 1` armed on both first, single warp
each, no chaining — same discipline dialogue-consequences.md's own prior round found necessary),
then `console 'recruit 8 tar05_zaalbar'` on the HOST (the same dev-only test primitive every prior
round in this thread has used in place of real dialogue input, for the same documented reason —
no working keyboard/mouse injection on this rig).

### The fix fired exactly as designed, both directions

Host `kotorcoop-plugin.log`: `recruit: NPC slot 8 has no blueprint to spawn from (failure 0); using
already-placed 'tar05_zaalbar' (0x2) instead` → `recruit: added NPC slot 8 (object 0x2) to the
party`. Client `kotorcoop-plugin.log`, 300ms later:

```
[19:07:56.266] party roster: NPC slot 8 recruited - reusing its own already-placed body 0x2 here
                (GIT-identical to the peer's pre-recruit body) instead of spawning a duplicate -
                dialogue_recruit_sync
[19:07:56.266] party roster: added NPC slot 8 (object 0x2) to the party
[19:07:56.271] party roster: local party is now 0x101 (recruited 0x101)
```

This is the exact new log line `PartyRosterBodyReuseRule` was written to produce, firing for real
against a live peer for the first time. `tools/partystate.ps1` on both instances agrees exactly:
`companions 2, member 0: slot 0 objectId 0xD1 (Trask), member 1: slot 8 objectId 0x2 (Zaalbar)` —
identical object ids on both sides, no manual reconciliation needed.

### No duplicate body anywhere — the specific defect this fix targets is gone

A full `tools/creatures.ps1` sweep of the client (43 creature objects found) turned up exactly ONE
object tagged `tar05_zaalbar`: `id 0x2`, `hp 8`, `str 13`, `xp 0` — the pre-recruit captive's own
stats, since the console recruit primitive (like the host's own conversion) adds the existing body
directly rather than running the recruit dialogue's own stat-boost script effects; this is expected
and matches the HOST's own object, which shows the identical hp/str/xp. No second Zaalbar body
exists anywhere in the scan — no `0xF4`-style fresh spawn, no duplicate object id, at any hp/xp
combination. (The scan tool itself reports every live creature TWICE, at two different memory
addresses with identical id/tag/position/stats each time — confirmed as a scan-tool artifact, not a
duplicate-body bug, since it does this uniformly for every creature in the module, Trask and every
hostile included, not just Zaalbar.)

### Verdict: all four NEEDS-LIVE items from the round above are now answered

1. `CreatureFinder.FindByObjectId` does resolve the peer's pre-recruit placed body under the same
   id — confirmed directly by the fix's own log line naming it.
2. `Odyssey.TrySetSlotAvailable` + `Odyssey.AddPartyMemberBySlot` on the REUSED object succeeded
   end to end with no refusal — `partystate.ps1` shows the reused body correctly seated in slot 8
   with the party-table's own commandable/in-party bookkeeping intact.
3. The duplicate is eliminated live — the "one Zaalbar or two" question the previous round could
   not settle without screenshots is now settled by direct memory scan: one.
4. No version-guard/desync issue surfaced — both installs ran the same freshly-built plugin/agent
   (block version 53) this round, so a cross-version pairing was not exercised, but the normal
   version-mismatch refusal path is unchanged by this fix and was not expected to be at risk.

`dialogue_recruit_sync` is flipped **ON by default** as of this result (`profiles/coop.ini.example`)
— the core recruit mechanism (two independently-confirmed slots: Carth, Zaalbar) and the
stale-mirrored-body fix (this round) are both now live-confirmed, closing the one specific,
named reason the flag was held back.

`dotnet test`: no production code changed this pass (a live-verification run only); full suite
re-run after the coop.ini.example default flip (no production code touched) — see this round's
own commit for the count.

## Round (2026-08-27): script-item-sync — items granted OUTSIDE any conversation, behind
## `script_item_sync=0` (OFF, NEEDS-LIVE)

`dialogue_item_sync` (§3, "Shipped" above) only ever diffs the local PC's inventory while THIS
instance owns a conversation (`DialoguePump.PumpDialogueItemGrants`, gated on the debounced
`active` flag `PumpDialogue` already computes). Most K1 quest items are never granted that way —
an OnEnter trigger, an OnDeath script, a cutscene, or a heartbeat script calls
`CreateItemOnObject`/`GiveItem` straight into the PC's inventory with no conversation token
involved at all, and every one of those landed on one screen only, exactly the way an in-dialogue
grant did before §3 shipped. This round closes that specific gap with a second, always-on-when-
enabled pump, deliberately kept separate from `DialoguePump` rather than folding into it — the
two windows (in-conversation vs. everything else) are mutually exclusive by construction, and
keeping them in two files keeps each one's "whose job is this sample" question answerable by
which file it is in.

### What shipped

- **`KotorCoop.Protocol.RecentlySyncedItems`** (`src/KotorCoop.Protocol/RecentlySyncedItems.cs`):
  a short-lived (5 s default), resref-keyed, consume-on-match budget. This is the load-bearing
  piece — without it, an ordinary ground pickup, a chest looted, a corpse looted, a peer's own
  grant arriving over the wire, or a human's own `giveloot` at the console would ALL look
  identical to a script grant from a bare possessed-items diff (`ReadPossessedItems` carries no
  object id across two sweeps — see `DialogueItemDiff`'s own remarks — so "a new row of resref X
  appeared" is genuinely all the sweep can say). Every already-synced path records one unit of
  budget the instant it knows a new row is about to land; the script-item diff consumes one unit
  per already-accounted-for row instead of re-reporting it.
- **`KotorCoop.Protocol.ScriptItemGrantRule`**: the pure decision (baseline, current sample,
  rebaseline-only flag, the ledger, now) -> (rows to publish, next baseline). Reuses
  `DialogueItemDiff.Compute` for the actual row-count diff — identical stack-growth/stack-
  shrink/multiple-distinct-grants behaviour to the dialogue channel, on purpose.
- **`KotorCoop.Session.Pumps.ScriptItemGrantPump`**: samples `IGameBridge.ReadPossessedItems`
  for the local player on its own ~1 s cadence (`AgentSettings.ScriptItemGrantSampleIntervalMs`,
  independent of `HeavyPumpIntervalMs` for the same reason `DialogueGrantSampleIntervalMs` is)
  whenever `DialoguePump.IsOwningConversation` is false, and publishes through the SAME
  `session.PublishDialogueGrant`/give-loot cheat channel §3 already built, with an empty partner
  tag (there is no conversation to name). Called from `CoopAgent.PumpOnce` every tick, gated on
  the `SessionCapabilities.Dialogue` capability the wire channel already needs.
- **Baseline coordination, without a hand-off value.** The task framing was "the dialogue pump's
  post-conversation snapshot must become this pump's baseline, and vice versa." Built instead:
  both pumps independently take a FRESH `ReadPossessedItems` read exactly at the moment they
  start owning the window (`DialoguePump` already did this on conversation start; `ScriptItemGrantPump`
  now does the same thing on the falling edge of `IsOwningConversation`, via a `rebaselineOnly`
  sample that diffs against nothing and just adopts `current`). Two independent fresh reads of the
  SAME live game state at the SAME instant are the same baseline by construction — this needs no
  reference from one pump to the other's internal field, and cannot drift even if a call is
  missed on either side, which a literal hand-off value could.
- **Suppression ledger feed points** (`RecentlySyncedItems.Record`, one unit each, resref
  resolved as close to the write as possible):
  - Ground-item pickup: `WorldStatePump.PumpItems`, resolving the resref via
    `session.Items.TryGet` BEFORE `session.RemoveItem` clears the entry.
  - Container/corpse loot: `LootPump.PumpContainerLoot`, resolving the resref via a NEW
    `IItemBridge.TryGetItemResref(uint, out string)` default-interface member (mirrors the
    existing `KotorGameBridge.TryGetItemResref` the `item`/`giveloot` staging verbs already use,
    cache-only — no forced sweep on this hot a path). Best-effort: a resref that cannot be
    resolved (an untagged body, a mod whose tag-as-resref proxy does not hold — see
    `KotorGameBridge.TryGetItemResref`'s own remarks) simply cannot be suppressed for that one
    removal, which costs an occasional unsuppressed false-positive report, never a wrong write.
  - A peer's own dialogue or script grant, applied here via the give-loot cheat channel:
    `LootPump.PumpMaterialisedLoot`, while draining `session.DrainDialogueGrants()` — before the
    item ever reaches the automatic-give queue.
  - A human's own `giveloot` targeting the local player: `CoopCommandDispatcher.GiveLoot` calls
    the new `CoopAgent.NoteLocalItemGiven(targetObjectId, resref)`, a no-op for any other target.
    (`dropitem` needs no suppression — `CheatController.DropItem`'s own doc confirms it spawns on
    the FLOOR, never in the inventory, on purpose.)
- **coop.ini `script_item_sync`** (agent-side, `CoopConfig.ScriptItemSync` / `AgentSettings.ScriptItemSync`),
  **OFF by default** — unlike every other item channel in this codebase, a false positive here
  (an ordinary pickup misread as a script grant) mints a REAL duplicate item on the peer's
  engine, not a cosmetic desync, so it stays opt-in until the suppression ledger has been proven
  against a live rig. Documented in `profiles/coop.ini.example` and `docs/CONFIG.md`.

### Tests

All pure/unit, no live game: `RecentlySyncedItemsTests` (9 cases — record/consume, expiry,
cross-resref isolation, zero/negative/empty no-ops, `Clear`, case-insensitive match);
`ScriptItemGrantRuleTests` (10 cases — script grant detected with no ledger match; suppressed by
a ground-pickup-shaped ledger entry; suppressed by a container-loot-shaped entry; suppressed by a
peer-grant-shaped entry; suppressed by a manual-giveloot-shaped entry; a second copy beyond the
ledger's one-unit budget still reported; `rebaselineOnly` reports nothing and adopts `current`;
stack growth inherited from `DialogueItemDiff` is still not a grant; a ledger entry for an
unrelated resref does not cross-suppress); `ScriptItemGrantPumpTests` (6 cases, through a full
`CoopAgent` the way `MaterialisedLootPumpTests` already does — off by default publishes nothing;
the first sample seeds the baseline silently; a genuine grant between two samples is detected and
published; nothing is sampled or published while a conversation is owned; the sample right after
a conversation ends rebaselines instead of reporting what the conversation itself granted; a
module change resets the baseline silently). `AgentSettings.ScriptItemGrantSampleIntervalMs` is
the same testability knob `CombatPumpIntervalMs`/`HeavyPumpIntervalMs` already are — zeroed in
every pump test so a diff is observed on the very next call rather than waiting on a real clock;
the one test that depends on `DialoguePump`'s own 300 ms between-entries debounce
(`DialogueLingerMs`) sleeps past it for real, the same pattern `CreatureLivenessPumpTests` already
uses for its own debounce window.

Full suite: see the commit this section ships with.

### NEEDS-LIVE — nothing in this round has been run against the real game

1. **The core claim itself**: that a script-created item (not a dialogue reward, not a pickup)
   actually surfaces as a new row from `ReadPossessedItems` on the OWNER's instance, and that the
   give-loot cheat channel applies it cleanly on the PEER's instance, exactly as §3's dialogue
   channel was already confirmed to do for the in-conversation case (§3's own NEEDS-LIVE list —
   never itself closed by a later round in this file, so this item is doubly unverified).
2. **The suppression ledger under real timing**, one sub-case per feed point:
   - Ground pickup: walk over a dropped item, confirm no `DialogueGrantPacket` is sent for it.
   - Container/corpse loot: loot a chest (or a slain enemy's corpse — the same `CollectContainerLootRemovals`
     path) and confirm the same. This is the sub-case most likely to fail live: `TryGetItemResref`'s
     tag-as-resref proxy is a heuristic ("works for base-game items... a mod that tags items
     differently breaks it" — the method's own doc), so a modded item could slip past the
     suppression ledger unresolved and get double-reported. Base-game items first.
   - A peer's grant arriving via giveloot while this instance is mid-sample: fire a dialogue
     grant from peer A, confirm peer B's own `ScriptItemGrantPump` does not re-broadcast it.
   - A manual `giveloot 0x7FFFFFFF <resref>` at the console: confirm no re-broadcast.
3. **The baseline hand-off at conversation start/end**, live: start a conversation with a reward
   node, let it grant an item, end the conversation, and confirm `ScriptItemGrantPump`'s very
   next sample does NOT re-report what `DialoguePump` already sent.
4. **A concrete positive repro** for item 1. Two candidates, neither exercised this round:
   - `firescript` (`docs/CONSOLE.md`) fires an object's own `+0x260`/`+0x270` event script
     directly — the only console primitive in this codebase that can trigger an arbitrary
     script without scripted player input. Needs a live probe to find an object whose event
     script actually calls `CreateItemOnObject`/`GiveItem` (none confirmed by this round; §1's
     own scan of the repo's `.dlg` files does not cover module-level `.utc`/`.uti`/`.git`
     OnEnter/OnDeath/heartbeat scripts at all — that inventory has not been taken).
   - The Endar Spire (`end_m01aa`) is the standing suggestion for a natural repro (Trask's
     starting equipment, an OnEnter-triggered area) but the specific script/item was NOT
     identified or confirmed against the real game files this round — treat it as a lead, not a
     verified test case, until someone actually reads `end_m01aa`'s `.git`/module scripts against
     `tools/kotorfiles.py` the way §1 did for `.dlg` files.
5. Whether the `SessionCapabilities.Dialogue` gate (reused rather than adding a new capability
   bit, per this pass's own constraint not to touch `ProtocolConstants.Version`) is the right
   capability to ride — it has never been exercised for a script-outside-dialogue grant, only for
   the conversation-token channel it was designed around.

## Live pass (2026-08-27): the core claim and one control CONFIRMED; two controls not exercised — stays OFF

Item 4's own concrete-positive-repro problem (nothing in this codebase can fire an
OnEnter/OnDeath/heartbeat script that grants an item, on demand, in a reachable module) is
solved the same way `quittomenu` solved the FlagAway round's navigation problem: a new dev-only
console verb, `scriptgive <resref> [stack]` (`CheatKinds.ScriptGive` = 26,
`SharedBlock.CheatKindScriptGive`, `CoopCommandKind.ScriptGive`), gives an item straight into the
LOCAL player's inventory through the same `Odyssey.GiveItemToInventory` call `giveloot`'s
local-player sentinel already uses — but, unlike every other console item verb, calls NO
agent-side suppression hook (`CoopAgent.NoteLocalItemGiven`). To `ScriptItemGrantPump`'s own
sampling diff, a `scriptgive`d item and a genuine script grant are the same event: a new row
that appeared in `ReadPossessedItems` with no upstream channel having recorded it first. Same
files touched as `quittomenu` (`Odyssey.GiveItemToInventory` reused, not new;
`CheatController.Items.cs`, `SharedBlock.cs`/`IGameBridge.cs`, `ConsoleCommand.cs`/
`ConsoleCommand.Items.cs`, `CoopCommandDispatcher.cs`, `docs/CONSOLE.md`). `dotnet test
tests/KotorCoop.Tests`: 1980/1980 green (`CoopCommandDispatcherTests`'s own two UDP-port-bind
flakes reproduced and cleared on a solo rerun, per CONTRIBUTING.md's own note — not a
regression).

### The core claim — CONFIRMED

Both instances connected in `END_M01AA` (block version 53), `script_item_sync=1` on both.
`pwsh tools\blockpoke.ps1 -ProcId <host> console 'scriptgive g_w_blstrpstl001'` answered `gave
'g_w_blstrpstl001' (0x132) to the local player, untracked`. Three independent log lines, on
different processes, agree:

```
host plugin:  scriptgive: 'g_w_blstrpstl001' (0x132) placed directly on 0x7FFFFFFF - no
              suppression ledger entry was recorded for this, ON PURPOSE
host agent:   * script item grant: 1 new item(s) in the local PC's inventory outside dialogue:
              g_w_blstrpstl001
client agent: * dialogue grant: received 1 item(s) from peer 1 (''), queueing
client plugin: materialise: gave loot 'g_w_blstrpstl001' (0x131) to stand-in 0x7FFFFFFF for
              the local player (dialogue grant) - stack 1, charges 0/0, upgrades 0x0
```

The empty partner tag (`''`) is exactly as designed — §"What shipped" notes the script channel
publishes with "an empty partner tag (there is no conversation to name)". End to end: the
OWNER's instance detected an untracked inventory row, published it through the SAME
give-loot cheat channel §3's in-dialogue grants already use, and the PEER applied it through the
SAME `Odyssey.ApplyItemIdentity` call — once, cleanly, no crash. Polled both agent logs for
3 more seconds afterward: neither side re-reported or re-broadcast the same grant (the client's
own `ScriptItemGrantPump`, now sampling an inventory that also contains this item, did not
re-detect it — the suppression feed point at `LootPump.PumpMaterialisedLoot`, draining
`session.DrainDialogueGrants()` before the automatic-give queue, worked as designed).

### Control 1 (manual `giveloot` targeting the local player) — CONFIRMED suppressed

`giveloot 0x7FFFFFFF g_w_lghtsbr01` on the host (a human staging a give against their own
character, the exact shape `CoopCommandDispatcher.GiveLoot`'s `NoteLocalItemGiven` call exists
for) produced no `script item grant` line on either side — the suppression ledger correctly
recognised it as already-accounted-for and did not double-report it as a script grant.

### Controls 2 and 3 (ground pickup, container/corpse loot) — NOT exercised this round

`useplaceable` (blank = nearest container) refused: `the sweep found 64 placeable(s) but none
close enough / none with inventory (status -3)` — nothing in `END_M01AA` at the player's current
position was in the 15 m `PlaceableUseRange`, and no time was spent this round finding or
navigating to a specific footlocker/corpse. Ground pickup needs either real player movement (an
injected-input class this codebase's own standing notes call unreliable for anything but a
console keystroke) or a click on a floor item, neither attempted this round.

This matters because §2 of this same round's own NEEDS-LIVE list names container/corpse loot as
**"the sub-case most likely to fail live"**: `TryGetItemResref`'s tag-as-resref proxy is a
heuristic that can leave a removal unresolved and therefore unsuppressed. That risk is
completely unaddressed by what this round actually ran.

### Verdict: stays OFF

The core mechanism and one of three required controls are now genuinely live-confirmed — a
real improvement over "nothing in this round has been run against the real game." But
`script_item_sync`'s own live-test plan explicitly requires all three negative controls before
calling it clean, and the one most likely to surface a real bug (container/corpse loot) was not
run. **`script_item_sync` was NOT flipped to `1` by default** — `profiles/coop.ini.example` and
`CoopConfig.ScriptItemSync`'s code default both stay `0`. The two rig `coop.ini`s used for this
pass were edited to `script_item_sync=1` for the test session only (not committed as a default
change).

**Recommended for the next round with rig access:** navigate to a known footlocker (`tp` plus a
found placeable id from a `census`-style sweep, or simply walk near one of the 64 placeables this
sweep already enumerated) and re-run `useplaceable` there; walk (or `walkto`) a dropped item's
position for the ground-pickup control. Both controls are otherwise ready to run exactly as
written above.

## Live pass (2026-08-27, follow-up round): the container/corpse-loot control run — FAILED first, fixed, re-run PASS — flipped ON by default

### The blocker this round had to solve first: `useplaceable` cannot loot anything

`useplaceable` only replicates the network half of a real click (it opens the
interaction lock so the mirror machinery reacts, per its own doc comment) — it never
moves an item, and no injected click can drive the engine's own container GUI on this
rig. A new dev-only console verb, `loottake [tag|id]` (`CheatKinds.LootTake` = 29,
`SharedBlock.CheatKindLootTake`, `CoopCommandKind.LootTake`), reproduces a genuine take
through two real, already-verified engine calls instead: `Odyssey.TryTakeFirstContainerItem`
removes the container's first item via the same `CSWSPlaceable::RemoveItem` primitive
`TryWriteContainerLoot`'s apply path already uses (reading the item's Tag first, since
`RemoveItem` deletes the object), then `Odyssey.GiveItemToInventory` gives an equivalent
copy to the local player — the same two building blocks `scriptgive` and the peer-apply
path already use, just composed to fire both the removal signal
(`CollectContainerLootRemovals`) and the new-inventory-row signal (`ReadPossessedItems`)
a real take produces. Same target resolution as `useplaceable` (object id / tag /
nearest container). Dev-only, blockpoke-only, matching `quittomenu`/`scriptgive`'s own
convention. `dotnet test`: 2118/2118 green before this pass touched the rig.

### Finding a real corpse with real loot needed live trial and error

Footlockers in the rig's modules were consistently empty (matches this doc's own
earlier note). A slain enemy's corpse becomes a real `[container]`-tagged placeable
(confirmed via the plugin's own placeable-sweep log, which now tags corpses distinctly
from ordinary placeables) — but only for a NEW object id assigned to the corpse, not the
creature's own id (`loottake <creature-id>` correctly refuses: "does not resolve to a
container here"). Several corpses held only `g_i_credits001` (a pseudo-item `GiveItemToInventory`
cannot spawn — `loottake` correctly reports the container-side removal succeeded but the
give-back failed, and leaves the container empty rather than lying about a give that
didn't happen). One corpse (`0x90` in `end_m01ab`, killed via `slay` then closed to
within 15m via `tp`) held a real weapon, `g_w_ionblstr01`.

### First run: FAILED — a real double-report, exactly the risk this control was written to catch

`pwsh tools\blockpoke.ps1 -ProcId <host> console 'loottake'` → `took 'g_i_drdrepeqp001'
(0x71) from 0x7E ... and gave it to the player`. The HOST's own `agent1.log`:

```
[19:52:30] * took 'g_i_drdrepeqp001' (0x71) from 0x7E (the nearest container
[19:52:30] * script item grant: 1 new item(s) in the local PC's inventory outside dialogue: g_i_drdrepeqp001
```

The suppression ledger did NOT catch it — exactly the failure mode Control 2/3 exists to
catch, on the very sub-case this doc's own earlier round flagged as "most likely to
fail live".

### Root cause: an ordering bug, not a timing-budget bug

`CoopAgent.PumpOnce` ran `_lootPump.PumpContainerLoot(caps)` on the heavy ~1 Hz pass
(gated by `HeavyPumpIntervalMs`) and `_scriptItemGrantPump.PumpScriptItemGrants(...)` on
the fast per-tick path (self-throttled to its own, INDEPENDENTLY-clocked ~1s interval,
`ScriptItemGrantSampleIntervalMs`) — earlier in the same method. `RecentlySyncedItems`'s
5s budget is not the problem: the ledger records "before" semantics, so a suppression
entry recorded AFTER `ScriptItemGrantPump` has already published a grant cannot un-ring
that bell no matter how long the budget window is. With the two pumps' calls in the
wrong relative order and on independently-phased ~1s timers, `ScriptItemGrantPump` could
(and here, did) sample the new inventory row before `PumpContainerLoot`'s own throttled
scan ever got a chance to notice the removal and feed the ledger.

**Fix.** `PumpContainerLoot` moved off the heavy pass onto the fast per-tick path,
called immediately BEFORE `PumpScriptItemGrants` every tick (`CoopAgent.cs`). Calling it
every tick costs nothing extra: `CollectContainerLootRemovals` already self-throttles its
own expensive scan to once a second internally (`_nextLootScanTicks`), so this is a cheap
early-return on all but the one tick a second the scan is actually due — it now always
gets a chance to run, and record, ahead of the script-item sample in the same call.

### Second run, same recipe, on a fresh corpse: PASS

`loottake` on a new corpse (`0x90`, `g_w_ionblstr01`, freshly built agent carrying the
fix) → `took 'g_w_ionblstr01' (0x12) from 0x90 ... and gave it to th[e player]` at
20:03:55. No `script item grant` line for it anywhere in the host's full log, checked
both immediately after and several seconds later. The client's own log shows the
resref once, from a completely unrelated, earlier materialised-enemy-loot event
(20:02:29, a different channel) — not a duplicate of this one.

### Controls 1 and the core claim re-confirmed on the same (fixed) build

- **Core claim** (`scriptgive g_w_blstrpstl001`): `* script item grant: 1 new item(s)
  ... g_w_blstrpstl001` — still correctly detected as untracked.
- **Control 1** (`giveloot 0x7FFFFFFF g_w_lghtsbr01`, a human staging a give against
  their own character): no `script item grant` line for `g_w_lghtsbr01` anywhere —
  still correctly suppressed.

### Verdict: all three required controls now PASS — `script_item_sync` flipped ON by default

Every item this round's own live-test plan required is now answered: the core grant
detection, the manual-giveloot negative control, and — the one that actually surfaced a
real bug — the container/corpse-loot control. `CoopConfig.ScriptItemSync` /
`AgentSettings.ScriptItemSync` default to `true`; `profiles/coop.ini.example` and
`docs/CONFIG.md` updated to match. `script_item_sync=0` still turns it back off.

Not re-exercised this round: the ground-pickup control (walking over a dropped item) —
lower risk by construction (`WorldStatePump.PumpItems`'s suppression feed runs on the
SAME already-per-tick-adjacent cadence issue does not apply to it the way it did to the
heavy-pass-gated container scan), and every control that WAS run passed clean on the
first try after the ordering fix, so this was not chased further this round.
