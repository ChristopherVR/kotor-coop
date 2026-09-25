# Personal inventory & credits (design + build, 2026-09-25)

Product decision (the user, this pass): *"Inventory: allow configuring, but let's go with
personal gear with shared quest items."* This document designs and builds `inventory_mode`
(`personal`/`shared`, default **personal**) and `credits_mode` (`personal`/`shared`, default
follows `inventory_mode`) on top of the existing host-authoritative shared-inventory channel
(`docs/research/shared-inventory-v2.md`) and the gold ledger
(`docs/research/progression-sync.md`), without touching either channel's own hard-won
correctness machinery for the content that keeps mirroring.

Tag legend, matching every other doc in this tree: **VERIFIED** = read out of the codebase or
asserted by a passing test. **INFERRED** = a reasoned deduction from evidence read this pass, not
independently re-derived from scratch. **NEEDS-LIVE** = reasoned but never watched happen on a
running two-instance rig — nothing in this pass touched a live process.

---

## 1. The one engine fact this whole design rests on

**VERIFIED**, `docs/research/key-items-and-solo.md` §1 (itself citing a full disassembly of
`CSWSCreature::GetItemRepository`/`GetGold` in `src/KotorCoop.Plugin/Odyssey.Store.cs`): **a
live, in-party PC's own "backpack" is not a private per-creature store — it IS
`CSWPartyTable::GetPartyItems()`, one repository per ENGINE PROCESS.** Gold works identically
(`CSWPartyTable+0xFC`). There is no such thing, inside a single running KOTOR instance, as "my
items" vs. "your items" — a party is one shared stash by construction, always was, in vanilla
single-player too.

**Consequence for what "personal inventory" can even mean in a two-engine co-op session.** Since
each player runs their OWN separate engine process with its OWN separate `CItemRepository`, "your
backpack is your own" cannot mean anything inside one engine (there is only one repository to
have an opinion about) — it can only mean **the cross-engine MIRROR stops copying content between
the two repositories**. `party_pool_sync=2` (`docs/research/shared-inventory-v2.md`) is exactly
that mirror: the host reads its own repository, publishes an absolute snapshot, and every client
reconciles its own repository toward it. "Personal inventory" is therefore not a new storage
model — it is **a filter on what that one existing channel is allowed to move between engines**,
and the entire design in this document follows from restating it that way.

The same argument applies to credits: `party_pool_sync`'s sibling channel for gold
(`Pumps.PartyPump.PumpProgression`, `docs/research/progression-sync.md`) is a single
host-published, contribution-merged purse. "Personal credits" means the same mirror stops
folding a peer's earn/spend into that purse.

---

## 2. Model

### 2.1 Quest-item classification (`QuestItemCatalog`, `KotorCoop.Protocol`)

A quest/plot item is **either** the engine's own Plot flag (`CSWSItem+0x108`, VERIFIED,
`docs/research/item-identity.md` line 76; the UTI field the toolset calls `Plot`) **or** its tag
appearing in a static census built from `docs/research/key-items-and-solo.md` §7 — that
document's own abstract-stack-interpreter walk of every K1 script for
`GetItemPossessedBy(object, string)` (NWScript routine 30) call sites, restricted to the tags it
could name LITERALLY (a handful of call sites resolve a wildcard group the source document itself
could not individually enumerate from a static read alone — e.g. `tat17_*`, `ptar_*journal` —
and those are honestly left out of the static table rather than guessed at; the Plot flag is the
robust, primary signal for exactly this reason). `QuestItemCatalog.Tags` holds ~62 literal tags:
the four buyable gates (`rakghoulserum`, `ptar_permacrete`, `tat20_banthafod`, the Korriban
`g_i_belt0xx` stealth set) plus every quest-given/found-only plot item §7.3 could name across the
whole campaign (Star Maps, the Leviathan escape set, the scripted sabers, the Manaan Sunry/Hrakert
evidence, the Korriban academy items, Kashyyyk, Taris, Tatooine and Dantooine plot props).

**Honest limitation, VERIFIED as a limitation of the DATA, not the code**: `PartyPoolStackKey`
(the party-pool channel's own content key, `docs/research/party-pool-sync.md` §2.1) carries a
resref plus the four non-quantity `ItemIdentity` fields (charges/max-charges/upgrade-mask/
identified-stolen-nonequippable flags) — **it does not carry the item's Plot flag at all**. So the
actual gate the party-pool pump can evaluate is resref-only
(`QuestItemCatalog.IsQuestItemTag`), not the fuller `IsQuestItem(plotFlag, resref)` rule this
class also exposes for a future caller that does have the flag in hand (e.g. a bridge read that
starts carrying it). This is flagged, not hidden: `QuestItemCatalog`'s own doc comment says so,
and §8's risk list repeats it. In practice this matters little — every K1 quest item this pass
could directly confirm (`man28_envirosuit`: `BaseItem 64, Plot 1`,
`docs/research/manaan-hrakert-rift.md`) already carries the flag AND a recognisable tag, so the
census list is redundant defence-in-depth for the common case, not the only thing standing between
a story gate and silent breakage.

### 2.2 What "personal" changes, channel by channel

| Channel | `shared` (today, unchanged) | `personal` (new default) |
| --- | --- | --- |
| Party pool (`party_pool_sync=2`, pickups/purchases/sales/drops/crafting/consumables — everything that moves through `CSWPartyTable::GetPartyItems()`) | Every stack mirrors both ways, unchanged | Only a quest-item stack mirrors. A non-quest stack is invisible to the wire: the host never publishes it, a client never detects or reconciles it (§3.1) |
| Store stock (`store_sync=2`) | Shared stock, unchanged | **Unchanged — orthogonal.** A store's stock is a `CGameObject`'s own repository, not a player's backpack (`docs/research/store-sync.md` §1.2); nothing about `inventory_mode` touches it. Buying/selling already only ever moves the ACTING player's own credits/items (the store-sync channel never touched a non-buyer's backpack) — this is item 5 of the product brief, already true before this pass |
| Dialogue/script item grants (`dialogue_item_sync`/`script_item_sync`) | A reward node's/script's grant is diffed off the granting engine and relayed to the peer, landing in the PEER'S OWN backpack via the give-loot cheat channel (`docs/research/dialogue-consequences.md` §3) | **Unchanged relay mechanism** — each player still gets their own copy, delivered as it always was. The one thing that changes is downstream: once that copy lands in the receiver's own party pool, personal mode's own party-pool filter (row above) simply never re-publishes it onward as if it were a fresh local deposit, for a NON-quest item — see §3.2 for why that matters and why it needed no new code |
| Credits (`Pumps.PartyPump.PumpProgression`) | One merged purse: every peer's earn/spend contribution folds in, host publishes the result, clients overwrite their own gold with it | Nobody's contribution folds in; XP still merges (a separate, unrelated concern — see §4) but gold never does. Each engine's own gold reading is left exactly as its own engine produced it |
| Quest reward gold (`GiveGoldToCreature` inside a dialogue/script) | Reaches the shared purse through the SAME merge above (`docs/research/dialogue-consequences.md` line 72: *"Already merged (gold ledger)"*) | **Open gap, not solved this pass** — see §4 |

### 2.3 Why the filter sits where it sits, and why it does not touch the pump's own arithmetic

`PartyPoolPumpV2` (`src/KotorCoop.Session/Pumps/PartyPoolPumpV2.cs`) is the single most
extensively hardened piece of logic in this codebase for exactly this class of bug — seven
independently-found root causes for the OLDER op-log design (`party-pool-sync.md`) and four more,
subtler ones for the CURRENT host-authoritative design even after that redesign
(`shared-inventory-v2.md` §§5, 7). Every one of those fixes reasons about a SPECIFIC key's
belief/outstanding/expected-reconcile bookkeeping across many ticks. The one change this pass was
not willing to make, under any amount of time pressure, was to let `inventory_mode` interact with
any of that arithmetic.

Instead, the filter is a single early exit, evaluated per key, BEFORE any bookkeeping for that key
is touched:

- **Client** (`PumpClient`'s per-key loop): `if (inventoryPersonalMode &&
  !QuestItemCatalog.IsQuestItemTag(key.Resref)) continue;` — a filtered-out key never enters
  `_belief`, `_outstanding`, `_expectedFromReconcile`, nothing. It behaves, from this pump's own
  point of view, exactly as if that stack did not exist in the local repository at all.
- **Host** (`PumpHost`): the published snapshot is filtered to quest-item keys only
  (`FilterQuestOnly`) before `PublishPartyPoolSnapshotV2`, and an accepted-but-non-quest client
  intent is refused outright (defence in depth for a peer whose own mode has somehow gone out of
  sync with the host's — see §5's negotiation, which is supposed to make that impossible).

A quest-item key's path through the pump is **completely unmodified** by any of this — same
belief-delta detection, same outstanding ledger, same evidence-based retirement, same everything.
`shared` mode (`inventoryPersonalMode: false`, the DEFAULT the `Pump()` method itself falls back
to when the caller passes nothing) short-circuits every one of these checks to "always sync", so
every pre-existing test in `SharedInventoryV2PumpTests.cs`/`SharedInventoryV2LoopbackTests.cs`
keeps passing unchanged — the affirmative proof this document's own task brief asked for that
`shared` mode is byte-for-byte today's behaviour.

---

## 3. Per-channel detail

### 3.1 Party pool — the mechanism in one paragraph

`Pumps/PartyPoolPumpV2.cs` gained a `bool inventoryPersonalMode` parameter on `Pump(...)`
(default `false`), latched into a private field and consulted at three points: the client's
per-key detect/reconcile loop (skip a non-quest key entirely), the host's snapshot publish
(`FilterQuestOnly`), and the host's intent-drain loop (refuse a non-quest intent). `CoopAgent.cs`
passes `session.Overrides.InventoryPersonal ?? _settings.InventoryPersonal` — the identical
"session override, else local coop.ini" idiom every other host-authoritative behaviour flag in
this codebase already uses (`DialoguePump`'s `session.Overrides.DialogueItemSync ??
settings.DialogueItemSync` is the direct precedent copied here).

### 3.2 Why dialogue/script item grants needed zero new code

The product brief's item 3 ("story rewards of non-quest items: each player receives their own
copy... make sure the per-copy delivery goes to the receiver's personal backpack and is not
re-shared") sounds like it needs new logic. It does not, because of §3.1's own shape: a reward
grant already lands in the RECEIVING engine's own party pool via the existing give-loot cheat
relay (`docs/research/dialogue-consequences.md` §3) — that relay has always delivered a REAL,
separate copy per engine, never a reference to the granting engine's item. The only question left
is "does that freshly-landed copy then get treated as a fresh LOCAL deposit and re-published back
onto the party-pool wire" — and per §3.1, for a non-quest item in personal mode, the answer is
simply no: the key never enters the party-pool pump's bookkeeping at all, so there is nothing to
re-publish. The receiver's copy stays exactly where the existing relay put it: their own personal
backpack, mirrored nowhere else. For a QUEST item the grant is (as before) also mirrored through
the party pool the normal way — consistent, not a special case.

### 3.3 The two purchasable plot items

`rakghoulserum` and `ptar_permacrete` (`docs/research/key-items-and-solo.md` §7.2) are both in
`QuestItemCatalog.Tags`. Bought by either player, in personal mode, the purchase still lands in
the buyer's own pool (as any purchase does — §1) and is then mirrored through the party-pool
channel to the other engine exactly as before, because the key IS a quest item — "shared once
bought" falls straight out of the classification rule with no special-casing.

### 3.4 Stores

Unaffected, and deliberately so — see the table row in §2.2. `store_sync` mirrors a store'S OWN
stock (a `CGameObject`, not a player), and buying/selling has always only moved the ACTING
player's own credits and items. `inventory_mode`/`credits_mode` gate the PLAYER-side channels
only.

---

## 4. Credits

### 4.1 What changed

`Pumps/PartyPump.cs`'s `PumpProgression`/`PumpProgressionHost` gained a `mergeCredits` value
(`InventorySyncRule.ShouldMergeCredits(session.Overrides.CreditsPersonal ??
settings.CreditsPersonal)`), consulted at exactly three points:

1. **Host fold loop**: a peer's earned/spent delta is still DRAINED and its baseline still
   ADVANCED (so nothing replays as a backlog if the mode is ever flipped later), but is not added
   into `purse` when `mergeCredits` is false — the host's own purse only ever reflects the host's
   own local gold reading.
2. **Host write-back**: unaffected by the flag directly — `purse` already equals `gold` (the
   host's own reading) when nothing was folded in, so the final `bridge.TryWriteProgression`
   simply re-writes the host's own value (a no-op for gold) while still applying whatever `xp_rate`
   bonus or clamp/sanitisation was due.
3. **Client write-back**: `targetGold = mergeCredits ? hostGold : gold` — a personal-mode client
   writes its OWN current gold back to its own engine (an intentional no-op instead of being
   silently overwritten by the host's purse), while STILL taking
   `Math.Max(hostExperience, experience)` for XP regardless of the credits mode.

**Experience is deliberately never gated by this flag.** XP merging is an unrelated concern
(character progression, not inventory/credits) that both engines already grant independently
through the real combat pipeline (`docs/research/progression-sync.md`'s own reasoning for why XP
merges by max rather than by ledger) — `credits_mode` has nothing to say about it.

### 4.2 The open gap: quest-reward gold, honestly not solved this pass

`GiveGoldToCreature` (a dialogue/script gold reward) has never had its own relay — it has always
reached the OTHER player purely as a side effect of the SAME contribution ledger that also merges
ordinary earn/spend (`docs/research/dialogue-consequences.md` line 72: *"Already merged (gold
ledger), listed for context only"*). Turning that ledger off in `credits_mode = personal` (§4.1)
therefore has a real, product-visible consequence this pass does NOT close: **a credits reward
granted inside a conversation or script currently reaches only the player whose engine is driving
that conversation/script** — the non-driving player's own gold is unaffected, because nothing
folds a contribution into it any more.

**Why this was not fixed this pass, and what the real fix looks like.** The item-grant relay
(`DialogueItemSync`/`ScriptItemSync`) already solves the identical shaped problem for ITEMS: it
diffs the local PC's inventory while this instance owns a conversation/script window and relays
the delta to the peer as a fresh grant (`docs/research/dialogue-consequences.md` §3). The correct
fix for gold is the same shape, one layer over: diff the local PC's GOLD during that same
ownership window and relay the delta as a credit grant to the peer's own engine (a new,
narrow wire message — not a new merge design, and not the SAME contribution ledger, since that
ledger's own shape is what personal mode is turning off). Building and wire-testing that parallel
channel under the schedule for this pass was judged not worth the risk of a rushed, unverified new
packet type sitting next to a brand-new config surface already touching two hardened channels;
it is recorded here explicitly, in `docs/limitations.md` (the "Your credits" row) and in
`docs/review-seamless-2026-09-18.md`'s new row, as the single concrete next-build item for whoever
picks this up.

---

## 5. Settings negotiation

`inventory_mode`/`credits_mode` are HOST-AUTHORITATIVE, negotiated through the exact mechanism
`enemy_health`/`effect_sync`/`dialogue_item_sync` already use
(`docs/research/mod-compatibility.md`'s "Behaviour-flag negotiation", VERIFIED —
`src/KotorCoop.Protocol/BehaviourCompatibility.cs`):

- Two new bits, `BehaviourFlags.InventoryPersonal`/`CreditsPersonal`, added to
  `BehaviourDigest.Flags` (no scheme bump needed — `CurrentScheme` tracks the RECORD's own field
  set, unchanged here; only new bits inside the existing `Flags: uint`).
- `BehaviourCompatibility.Compare` reports a difference on either key with
  `HostAuthoritative: true`.
- `CoopClient.ApplyBehaviourComparison` adopts the host's value into
  `SessionOverrides.InventoryPersonal`/`CreditsPersonal` exactly like every other host-authoritative
  bool key (`Adopt("inventory_mode")`/`Adopt("credits_mode")`).
- Every consultation site reads `session.Overrides.X ?? settings.X` at the point of use, the same
  idiom `PartyPump`/`DialoguePump`/`ScriptItemGrantPump` already follow for their own
  host-authoritative flags — never baked into a constructor once.

**Why host-authoritative, restated in this document's own terms**: the party-pool snapshot is
ONE host-published truth every peer converges toward (§1). If the host and a client disagreed on
which keys that snapshot is even allowed to contain, the two sides would permanently, structurally
disagree about the pool's own contents — not a transient desync a retry fixes, but a standing
architectural mismatch. Adopting the host's value is the only coherent choice, exactly as it is
for `effect_sync`/`trigger_relay`/every other channel the host already drives by design.

`CoopIniExampleDocSyncTests` classifies both keys as PLAYER-FACING (added to
`CoopConfig.PlayerFacingKeys`/`PluginConfig.PlayerFacingKeys` — mirrored per that test suite's own
convention even though the plugin does not itself consume either key) rather than developer
switches, since this is a first-class gameplay choice a player is expected to set, not a tuning
knob.

---

## 6. Persistence — the joiner's personal backpack across disconnect/rejoin

### 6.1 What the product brief asks for, restated precisely

"The joiner's personal backpack + credits must survive disconnect/rejoin and world adoption...
restore into the staged host world on adoption; never duplicate on rejoin; host's own items never
leak into the joiner's sidecar and vice versa." This is the ONE part of this pass's brief that is
design-only this round — see §6.4 for what IS built (the pure core, tested) and §6.5 for why the
live-engine wiring itself is not, and §8 for the concrete follow-up.

### 6.2 Why this is NOT a save-file editing problem (and why that matters)

The obvious-looking approach — read the joiner's `INVENTORY.res`/`PARTYTABLE.res` out of their own
last save and splice it into the staged autosave's copy of the same files — was considered and
rejected. `INVENTORY.res` lives INSIDE the outer `SAVEGAME.sav` ERF archive as a member (VERIFIED,
`docs/research/key-items-and-solo.md` §1's table), and this codebase has no ERF-REBUILDING
primitive anywhere — every existing save manipulation (`PlayerIfo.TryNeutralisePifo`) is a
fixed-size, IN-PLACE byte patch of one 16-byte key cell, deliberately chosen BECAUSE it never has
to resize or relocate anything in the archive. Writing a genuinely new or larger `INVENTORY.res`
member would mean building a real ERF writer from scratch under this pass's own schedule — exactly
the kind of "invent a new, unverified low-level primitive under time pressure" this codebase's own
documented culture (`docs/research/shared-inventory-v2.md`'s whole "why the op-log design cannot
be patched into safety" chapter) warns against.

### 6.3 The design this document recommends instead: replay through the LIVE engine channel

`Odyssey.AddItemToPartyPool`/`RemovePartyPoolStack` (`docs/research/party-pool-sync.md` §2.3-2.4)
already exist, are already verified against the real party-pool repository, and are EXACTLY the
primitive needed: given a resref + `ItemIdentity` + count, spawn it into the local player's pool.
The recommended mechanism, once built:

1. **Capture** (on disconnect, or periodically like `ClientCharacterPersistence` already does for
   the character): read the joiner's OWN live party-pool totals through the EXISTING
   `IPartyPoolBridge.TryScanPartyPoolTotals` bridge read (no new engine surface), filter OUT
   quest items (`!QuestItemCatalog.IsQuestItemTag` — those already travel with the world, not the
   character) and filter out anything the sidecar already knows it carried last time (the dedup
   marker, step 3 below), and write the result as a THIRD sidecar file alongside
   `kotorcoop-character.pifo`/`.json` (e.g. `kotorcoop-character-inventory.json`: a plain
   `(resref, charges, maxCharges, upgrades, flags, count)` list plus a gold integer) — reusing
   `ClientCharacterPersistence`'s existing atomic temp-then-rename write pattern.
2. **Restore** (once the joiner's own engine has finished loading the newly staged world and the
   party-pool bridge reports ready): replay each carried entry as a synthetic LOCAL intent — the
   exact same `CheatKinds.PartyPoolAdd` single-slot cheat channel `party_pool_sync` already
   arbitrates, requesting exactly the primitive the channel was built for. No save file is ever
   touched; the restore is indistinguishable, from the engine's point of view, from the player
   having those items already and the pump discovering them on its very first scan.
3. **Dedup marker**: the sidecar's own metadata (mirroring `SidecarMetadata.CapturedAtUtc`) gains
   a `RestoredIntoWorldEpoch`/generation stamp, set the moment the restore intents are QUEUED (not
   merely captured) for a given staged world's `WorldLoadSerial`
   (`docs/research/session-save.md`'s own generation idiom) — a second adoption of the SAME staged
   world (e.g. a reconnect before the world changed again) sees the stamp already matches and
   skips the replay outright, closing the "never duplicate on rejoin" requirement.

### 6.4 What IS built this pass: the pure carry/merge core

`src/KotorCoop.Protocol/PersonalInventoryCarryRule.cs` (`PersonalInventoryCarryEntry`,
`FilterCarriable`, `ComputeRestoreOps`, `ShouldSkipRestore`) implements and unit-tests
(`PersonalInventoryCarryRuleTests.cs`) exactly the three pure decisions §6.3 above describes,
against `PartyPoolStackKey` directly (no new key type): which scanned stacks are even worth
carrying (quest items excluded — they travel with the world already), how much of a carried
stack still needs to be re-applied once compared against whatever the adopted world's pool
already holds (the excess-only arithmetic that makes a retry or a second look idempotent), and
the world-generation dedup guard that stops a second adoption of the SAME staged world from
replaying a restore already queued for it. This is deliberately the entire "hard part" proven
correct and tested in isolation — a caller with a live bridge/dispatcher only has to (1) scan,
filter and serialise into the sidecar, and (2) once per adopted world, scan again, call
`ComputeRestoreOps`, and enqueue each result through the existing `CheatKinds.PartyPoolAdd`
channel. What is NOT built is exactly that caller — see below.

### 6.5 Why the wiring itself is design-only this pass, not built

Two honest reasons, stated plainly rather than glossed over:

1. **The capture side needs a call site this pass did not have budget to wire correctly.**
   `ClientCharacterPersistence.Capture` (character-only today) is invoked from several places in
   `CoopCommandDispatcher.SaveLoad.cs` (periodic capture, pre-disconnect capture, verified-load
   capture) — extending it to ALSO snapshot the live party pool needs a
   `IPartyPoolBridge` reference threaded to a class that currently only touches
   `PlayerIfo`/GFF file bytes, and needs its own care about capturing at the RIGHT moment (the
   same "must not accidentally capture the STAGED host world's pool instead of the joiner's own"
   class of bug `ClientCharacterPersistence.Capture`'s own three refusals already guard for the
   character — a personal-inventory capture would need the identical three guards independently
   re-derived for a different bridge read).
2. **The restore side's dedup marker interacts with world-epoch/generation bookkeeping
   (`docs/research/same-module-reload-and-rejoin-identity.md`'s own "never restart per-peer
   serials at 1" lesson) that this pass did not have room to design AND unit-test carefully.** A
   half-built restore path that duplicates items on a same-module reload would be strictly worse
   than not building it at all, per this codebase's own house rule (`shared-inventory-v2.md`'s
   entire moral: a plausible-sounding design that has not been proven against a real edge case is
   a liability, not a feature).

**What ships this pass instead, honestly**: the personal-inventory FEATURE (party pool/credits
gating, §§2-4) does not depend on §6.3 at all — it works correctly for the ordinary case (a single
continuous session). §6's own gap is specifically the DISCONNECT/REJOIN edge: today, a joiner who
disconnects and later rejoins (the SAME host or a different one) does NOT carry forward whatever
personal (non-quest) items or credits they held — they simply start again with whatever the
newly-adopted world's own pool holds, the same "loose items do not travel through the pifo/sidecar
carry" fact `docs/research/key-items-and-solo.md` §1 already documents for the OLDER, pre-personal-
mode world. Personal mode does not make this worse than it already was; it just makes the gap more
salient, because a personal player is now expected to accumulate real, non-quest progress of their
own that solo saves already never had a story reason to preserve. No new `docs/limitations.md` row
is needed for it — the existing "Bringing your own character" row already covers gear-and-stats
persistence, and loose items were never part of that promise, before or after this pass — but it
IS the top follow-up item named in this document and in `docs/review-seamless-2026-09-18.md`'s new
row.

---

## 7. Migration — switching a running shared save into personal mode

**Rule** (the simplest one that loses nothing observable): the FIRST session run after
`inventory_mode` flips from `shared` to `personal` treats the world exactly as any other personal
session — the host's own pool (whatever it holds at that moment, unchanged) simply stops being
mirrored for non-quest content from that point forward; nothing is deleted, split or reconciled.
A joiner who connects after the flip starts, in personal mode, with whatever their own engine's
pool already holds (their own residual mirror from the last `shared` session, if any) plus
whatever `party_pool_sync` had already converged before the flip — i.e. **the host's pool stays
with the host, and the joiner's engine keeps whatever it already had**, since nothing subtracts
from either side on a mode change. This is INFERRED, not built as a special-cased migration path:
it falls out for free from §3.1's own mechanism (the filter only ever stops FUTURE synchronisation
of non-quest content; it has no concept of "at the moment of the flip" and needs none, since a mode
change is not something the pump code has to detect or react to specially — it just starts reading
a different `inventoryPersonalMode` value on its very next tick, and neither side's ALREADY-HELD
local items are touched by that read).

The one caveat: the negotiation in §5 makes this an all-or-nothing HOST decision going forward —
existing peers currently connected re-negotiate on their `Welcome`/re-Hello and adopt the change
(NEEDS-LIVE: whether a mid-session flag toggle in `coop.ini` is even picked up without a
reconnect is a pre-existing question for EVERY host-authoritative behaviour flag, not new to this
feature — every one of them is read once per `Hello`/`Welcome` exchange today).

---

## 8. The per-player first lightsaber

The product brief asks this document to note the hook the parallel first-lightsaber work
(`src/KotorCoop.Session/Pumps/FirstLightsaberBuildPump.cs`/`FirstLightsaberColorRule.cs`, being
built concurrently by another pass — this pass did not edit either file) needs from
`inventory_mode`. **No new hook is needed.** Whatever primitive that pump uses to place the
lightsaber into a player's own inventory already lands it in that engine's own party pool (§1 —
there is nowhere else for it to land); once it is there, §3.1's existing party-pool filter governs
it exactly like any other non-quest item: in `personal` mode it simply stays wherever it landed
and is never mirrored to the other player (correct — a Jedi's own first lightsaber is
personal gear by any reasonable reading of the product decision), and in `shared` mode it mirrors
like everything else always has. The only thing worth the other pass double-checking: if their own
design ever needs to know "is inventory_mode personal right now" for a reason of its OWN (not
mirroring), the read is `session.Overrides.InventoryPersonal ?? settings.InventoryPersonal` —
the same expression `CoopAgent.cs`'s own call site uses (§5).

---

## 9. Risks (summary, cross-referenced to where each is discussed above)

1. **Quest-item classification is resref-only at the party-pool layer**, not the fuller
   Plot-flag-or-tag rule `QuestItemCatalog.IsQuestItem` also offers — `PartyPoolStackKey` has no
   Plot bit to give it (§2.1). Mitigated by the census list's own breadth; a genuinely missed,
   non-flagged quest item would simply stop mirroring in personal mode, which is the SAME failure
   shape a story would have shown before `party_pool_sync` existed at all (a per-engine-siloed
   item), not a new crash or corruption class.
2. **Quest-reward credits currently pay only the conversation-driving player in personal
   `credits_mode`** (§4.2) — an explicitly open, named gap, not a silent one.
3. **The joiner's personal backpack/credits do not survive a disconnect/rejoin** (§6) — design
   only this pass; the ordinary continuous-session case is unaffected.
4. **Migration (§7) is inferred, not live-tested** — the mechanism is trivial by construction (a
   pure filter with no state of its own to migrate), but "trivial by construction" has been wrong
   before in this exact family of channel (`shared-inventory-v2.md`'s whole history) and deserves
   the live pass in §10 before being trusted blind.
5. **A mid-session `coop.ini` edit's live effect is unverified** for this flag specifically, though
   it is a pre-existing open question for every host-authoritative behaviour key, not new here.

---

## 10. NEEDS-LIVE recipe (not run this pass)

Two `tools/demo.ps1` instances, shipped defaults (`inventory_mode = personal`,
`credits_mode = personal`), both loaded into the same save/module. Rebuild and `-Install` first.

1. **Non-quest item stays personal.** Host: `pooladd g_i_medeqpmnt01 3` (an ordinary medpac).
   Expect NO `party pool v2: submitting...` line for that resref on either side, and the item
   absent from the joiner's `items`/stash panel. Reverse roles (joiner deposits, host does not see
   it).
2. **Quest item still mirrors.** Host: `pooladd rakghoulserum 1`. Expect the existing
   `party pool v2:` publish/detect/reconcile lines exactly as `shared` mode already produces them,
   and the item present on both sides within one snapshot cycle.
3. **A store purchase of an ordinary item stays personal; a purchase of a buyable plot item still
   mirrors.** On Taris (`tar_m02ac`), buy an ordinary weapon on one side — absent on the other.
   Buy `rakghoulserum` from `zelkastore00x` — present on both within a few seconds (§3.3).
4. **A dialogue/script item reward reaches only the receiver's own backpack, once.** Trigger a
   reward node granting a non-quest item on one player; confirm the OTHER player also receives
   their own copy (the existing `dialogue_item_sync` relay, unaffected by this pass — §3.2), and
   that NEITHER copy is then mirrored a second time onto the other engine (watch for the absence
   of a `party pool v2:` line for that resref following the grant).
5. **Credits diverge and stay diverged.** `grantgold 500` on the host; confirm the joiner's own
   gold is UNCHANGED (contrast with `shared` mode, where §6 of
   `docs/research/key-items-and-solo.md`'s own recipe already expects convergence within ~5s).
   Spend/earn independently on both sides for a few minutes; confirm neither purse ever mirrors
   the other's changes.
6. **`shared` mode is unaffected.** Set `inventory_mode = shared`/`credits_mode = shared` on both;
   re-run recipe 1 and 5 and confirm the OLD behaviour (full mirroring, one purse) — the explicit
   regression check that flipping the config back genuinely restores today's behaviour.
7. **Negotiation.** Host `inventory_mode = personal`, joiner's own coop.ini left at `shared`
   (pre-upgrade file). Expect a `W_CFG inventory_mode host=1 you=0` line and the joiner's own
   console/log to show it adopting personal mode; confirm the joiner's ALREADY-HELD items are not
   retroactively hidden or duplicated by the adoption itself (a live check for §7's own migration
   reasoning).
8. **The open gap, confirmed rather than assumed.** With `credits_mode = personal`, have the
   NON-driving player receive a `GiveGoldToCreature` reward through a conversation the OTHER
   player is running (e.g. a quest turn-in). Confirm the reward currently reaches only the
   conversation owner's purse — the exact, named limitation §4.2 documents, watched happening
   once rather than left as a hypothesis.
