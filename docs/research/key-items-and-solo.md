# Key items, credits and solo play after disconnect (research, 2026-09-25)

What happens to a purchased/found key item, and to party credits, across the four ways a
co-op session and solo play can interleave: a joiner buys something in the host's world and
disconnects, the host buys something and the joiner disconnects, a joiner plays solo and then
rejoins, and the ordinary flow of credits earned/spent in each mode. No code was assumed —
every claim below is traced to the actual sync design (`docs/research/shared-inventory-v2.md`,
`docs/research/party-pool-sync.md`, `docs/research/store-sync.md`, `docs/research/
progression-sync.md`), the actual join/carry mechanism (`docs/research/world-adoption.md`,
`docs/research/session-save.md`), and the actual source
(`src/KotorCoop.Memory/PlayerIfo.cs`, `src/KotorCoop.Session/ClientCharacterPersistence.cs`,
`src/KotorCoop.Session/CoopConfig.cs`, `src/KotorCoop.Plugin/Odyssey.Store.cs`).

Tag legend, matching every other doc in this tree: **VERIFIED** = read out of the codebase,
the retail binary's disassembly (already done by a cited prior doc), or asserted by a passing
test. **INFERRED** = a reasoned deduction from evidence that was read, not independently
re-derived from scratch this pass. **NEEDS-LIVE** = reasoned but never watched happen on a
running two-instance rig. Nothing in this document was run against a live game — it is a
static-research pass plus one small, comment-only config fix (§5).

---

## 0. Current shipped defaults (VERIFIED, `src/KotorCoop.Session/CoopConfig.cs`)

| Setting | Default | Where |
| --- | --- | --- |
| `join_mode` | `auto` — first join goes through chargen; the resulting character (`saves\kotorcoop-character.pifo/.json`) is the co-op character for every later join, never a single-player save's PC unless `join_mode = save` is set | `CoopConfig.cs:73`, `docs/research/world-adoption.md` "join_mode = auto" section |
| `auto_adopt_world` | `1` (on) | `CoopConfig.cs:86` |
| `party_pool_sync` | **`2`** (host-authoritative redesign) — **not** the `0`/off the top-of-file example comment claimed before this pass (§5 fixes the comment) | `CoopConfig.cs:721,745`, live-verified since 2026-08-29, `docs/research/shared-inventory-v2.md` |
| `store_sync` | **`2`** (host-authoritative redesign, same shape) | `CoopConfig.cs:781,796`, live-verified since 2026-08-29, further fixes through 2026-09-24, `docs/research/store-sync.md` |
| `client_load_rejoin` / `host_load_readopt` | `1` / `1` (on) | `CoopConfig.cs:490,505` |

**This matters for everything below**: the shared item pool and shared merchant stock are ON
by default today, at the *second-generation* design (host publishes an absolute snapshot,
clients only ever send intents — see §1). The older, bidirectional op-log design
(`party-pool-sync.md`/`store-sync.md` mode 1) is still in the codebase but OFF by default and
superseded; this document reasons about mode 2, the one that actually ships.

---

## 1. The one engine fact everything else in this document rests on

**VERIFIED**, `src/KotorCoop.Plugin/Odyssey.Store.cs` lines 976-1000 (full disassembly of
`CSWSCreature::GetItemRepository`, already done by this codebase for an unrelated store-sell
bug): **a live, in-party PC's own "backpack" IS the party's shared repository, not a private
per-creature one.**

`GetItemRepository(creature, arg=1)` — the accessor every retail UI path (inventory screens,
the buy/sell handlers) and, by strong inference, `GetItemPossessedBy` itself resolve through —
checks `creature+0xA88` (the in-party flag). If set — true for any real, live in-party PC — it
does **not** read the creature's own `+0xA30` backpack field at all; it tail-calls
`CSWPartyTable::GetPartyItems` (`CSWPartyTable+0x118`), the exact same repository
`party-pool-sync.md`/`shared-inventory-v2.md` already sync. The private per-creature `+0xA30`
field is only reached as a last-resort fallback for a creature that is **not** the live
in-party PC. Gold works identically: `CSWSCreature::GetGold`/`SetGold` gate on the same
`+0xA88` flag and read/write `CSWPartyTable+0xFC` instead of a per-creature field
(`docs/research/progression-sync.md` §1, VERIFIED disassembly). The `Odyssey.Store.cs` comment
states it in its own words: *"KOTOR 1's own inventory model is party-shared, not
per-creature... the party is one shared wallet/backpack."*

**Consequence for a plot gate.** `GetItemPossessedBy(oCreature, sTag)` is well known in KOTOR
modding circles to return true if *any* party member holds the item, not literally only the
named creature — a documented engine quirk consistent with the repository-resolution fact
above (**INFERRED**: this pass did not independently disassemble `GetItemPossessedBy` itself,
but the mechanism it would have to route through is the one just verified). A store purchase
lands directly in the buyer's own copy of `CSWPartyTable+0x118` the instant it happens — there
is no separate "drag it into the shared stash" step in vanilla KOTOR at all, because for a live
PC the backpack *already is* the stash. Since `party_pool_sync=2` mirrors that repository
between the host's and every client's own engine (§0), **a host-side plot script's
`GetItemPossessedBy(GetFirstPC(), tag)` genuinely CAN be satisfied by an item only the joiner
bought**, the moment the mirror has landed. This is the intended co-op behaviour (a truly
shared economy while connected), not a leak — the crux question the parent task asked
("can the host's story ever be gated-satisfied by an item only the joiner holds") is answered
**YES, by design, while both sides are connected and the sync channel is healthy.**

**The risk shifts to what a SAVE FILE captures, not to whether the live mirror works.** A
save's `Mod_PlayerList[0]` entry (`module.ifo`, what `PlayerIfo.BuildPifo` copies for the
join/rejoin carry) is a **static, at-rest** representation — the live engine's
`CSWPartyTable+0x118`/`+0xFC` are a **separate resource, `PARTYTABLE.res`**, at the save-archive
top level (`docs/research/world-adoption.md`'s own save-size breakdown: `SAVEGAME.sav` 584 KB,
`PARTYTABLE.res` 11 KB, `GLOBALVARS.res` 58 KB, as separate members). `PlayerIfo.TryExtractPlayer`
(`src/KotorCoop.Memory/PlayerIfo.cs:74-141`) opens `PARTYTABLE.res` **only** to read
`PT_CONTROLLED_NP` (the Leviathan-rescue guard, lines 83-94) and discards everything else in
it — the shared item pool and party gold **never travel through the pifo/sidecar mechanism at
all.** Only `Mod_PlayerList[0]` — equipped-slot item ids, class/level/stats/feats/alignment,
appearance, the personal Pazaak deck (`PersonalPazaak.Capture`) — is copied byte-for-byte
(`BuildPifo`, lines 154-183; `Min1HP` is the one field explicitly stripped, an unrelated
pre-existing fix). This single fact — **loose/backpack items and gold live in `PARTYTABLE.res`,
which the carry mechanism never touches; only equipped gear and character stats live in
`Mod_PlayerList[0]`, which it copies whole** — is the key to every scenario below.

---

## 2. `world-adoption.md`'s own "the trade" already says this, in different words

`docs/research/world-adoption.md`, "Headline" section:

> **The trade, stated plainly:** the joiner gives up the world they were playing, not their
> character. Party, inventory, companions, globals and journal come from the host's save,
> because in co-op those are shared state. Their own save on disk is never written — only the
> autosave slot and `Temp\` — so leaving the session and loading their own game costs nothing.

Read literally, "inventory... come[s] from the host's save" on **every** (re)join — the
project's own design already states that a joiner's own solo inventory is discarded and
replaced by whatever the host's world's shared pool currently holds. §1's engine-level finding
is *why* that is structurally true (inventory and gold are `PARTYTABLE.res` fields, which the
carry mechanism was never built to move), not a new design decision.

**"Their own save on disk is never written" was true when this was written (2026-08-13) and is
still true for a joiner who never manually saves** (VERIFIED — nothing found this pass changed
it). It stopped being the *whole* story on 2026-08-21, when `docs/research/session-save.md`
shipped `ClientCharacterPersistence` (the "resume tomorrow" sidecar) plus, as its recommended
trigger, **an ordinary player-driven in-game Save while adopted into the host's world** — which
*does* write a real, engine-produced save into the joiner's own `saves\` directory. That save
is the one place the two halves of this document collide: it is written by the retail engine,
so it faithfully captures `PARTYTABLE.res` (the current shared pool + shared gold) exactly as
`Mod_PlayerList[0]` from an in-session live engine, but the *world* it captures is the host's
current module/globals/journal, not the joiner's own story. See §3 below for exactly what that
means per scenario.

---

## 3. Scenario tracing

### (a) Joiner buys a key item from a host-world store, then disconnects and loads own solo save

- **If the joiner never manually saved during the session**: nothing on the joiner's disk
  changes at all (VERIFIED, world-adoption.md's own guarantee, unmodified since 2026-08-13).
  Loading any pre-existing solo save shows exactly what it always did. The item is genuinely
  gone from the joiner's perspective, not merely hidden — it was never written to a save file
  the solo game will ever open. **This is the documented, intended trade, not a bug** — though
  it is easy for a player to be surprised that a purchase "vanishes" with no on-screen warning.
- **If the joiner DID save in-game before disconnecting**: the item — already in the joiner's
  own live copy of `CSWPartyTable+0x118` at the moment of purchase (§1), and there whether or
  not `party_pool_sync` ever needed to mirror anything, since the purchase happened on the
  joiner's own engine — is captured into that save's `PARTYTABLE.res` and does reappear when
  that specific save is loaded solo. But (§4) that save is a graft of the HOST's world/journal/
  globals onto the joiner's own character, not a continuation of the joiner's own story.
- **Store depletion** (`store_sync=2`, default ON): the purchase is applied to the HOST's own
  engine too — mode 2's host is the single source of truth and applies a client's buy *intent*
  to its own engine via the same apply primitive (`docs/research/shared-inventory-v2.md` §2.1/
  §2.7; `docs/research/store-sync.md` §2.4). The merchant's stock is permanently reduced on the
  host's own subsequent save regardless of what the joiner does afterward — correct and
  intended (one shared economy while connected), **provided `party_pool_sync` also
  successfully mirrored the physical item into the host's own pool**, which mode 2 is designed
  to do automatically (§1 — a purchase already sits in the buyer's own party-shared repository
  the instant it happens, no separate deposit step). Both mode-2 channels have extensive
  documented histories of transient live bugs before landing (`shared-inventory-v2.md` §§5-10,
  `store-sync.md`'s many live-pass sections) — they are currently shipped ON after real
  live-testing through 2026-09-24, but a **fresh regression in either channel specifically
  would produce "the host's story loses the merchant's stock permanently, and the joiner who
  paid for it has nothing to show for it once they disconnect without saving"** — worth a
  periodic live recheck (see §6), not a currently-open defect this pass found.

### (b) Host buys it, joiner disconnects and plays solo

Symmetric to (a), **not** asymmetric — mode 2's snapshot is host → every client, so a host
purchase reaches the joiner's own live engine the same way a joiner's purchase reaches the
host's. If the joiner never saves, nothing persists to their own solo save (same guarantee).
If the joiner does save before disconnecting, their save now also contains the host-bought
item and the current merged gold total — the identical §4 "graft" risk, just contributed from
the other direction. **"Nothing joiner-side is affected" is only true if the joiner never
saves during the session** — which `session-save.md`'s own design explicitly wants to
encourage (its whole point is enabling "resume tomorrow").

### (c) Joiner plays solo, buys/finds the item, then rejoins the host

Per §1/§2: **loose/backpack items from the joiner's solo save are discarded on rejoin, not
duplicated.** `PlayerIfo.TryExtractPlayer`/`BuildPifo` never reads or copies `PARTYTABLE.res`'s
item repository — only `Mod_PlayerList[0]` (equipped slots + stats). A solo-bought/found LOOSE
item is simply left behind, silently, with no on-screen warning that this will happen — a
real, if narrow, UX/data-loss gap, but **not** a duplication risk.

**The one narrower exception: an EQUIPPED item.** `Equip_ItemList` genuinely lives on
`Mod_PlayerList[0]` (per-creature, always — this is not a shared-pool field even for a live
PC), so it **does** travel via the pifo. If a joiner solo-equips a unique-tagged key item and
then rejoins, that specific item arrives in the host's world attached to the joiner's carried
character, and could in principle coexist with an independent copy of the same tag already
present somewhere in the host's own, independently-progressed world/story state — a genuine,
narrow duplication/uniqueness risk. In practice most K1 "key" plot props are non-equippable
plot tokens (datapads, disks, plot-flagged quest items), which fall under the "loose item,
discarded" case instead — see §7's item census for which named items are actually equippable.

### (d) Credits spent in co-op vs. the joiner's own solo credits

Two genuinely separate storage locations that only ever meet through a live wire mirror, never
through a saved-file reconciliation. The shared purse **is** `partyTable+0xFC` on each
process's own live engine, kept converged by `PumpProgressionHost`/`PumpProgression`'s
per-peer contribution-ledger merge (`docs/research/progression-sync.md`, live-verified
2026-08-27 and again 2026-09-03 for the wrapped-overdraft fix). It is never additively
re-derived from a save file and is never itself written to a save file except through the same
mechanism as items: an ordinary in-game Save while connected writes whatever
`partyTable+0xFC` currently reads into that save's `PARTYTABLE.res`.

- If the joiner never saves: the shared-session credit total evaporates the instant they
  disconnect; their own solo saves' credit fields are completely untouched — symmetric with
  items.
- If the joiner does save mid-session: their own subsequent "solo" credits become whatever the
  shared purse happened to read at that instant — potentially a large windfall from the host's
  own earnings, or a near-zero balance if the party had just spent it down — the same §4 "graft"
  risk, applied to a number instead of an item.
- On the HOST's side, nothing about the joiner disconnecting changes anything: the host's next
  save captures `partyTable+0xFC` as it stands, which correctly and permanently includes every
  credit either player earned or spent while connected. This half of the design is intended and
  works as shipped.

---

## 4. The biggest concrete risk: a client session-save is secretly the host's world

Not previously named this way in any doc this pass found. `docs/research/session-save.md`'s
own design (§2.3) states session-save is meant to be "character-only... does NOT attempt to
make the client's own save directory a faithful copy of the shared WORLD." The **shipped
implementation** achieves the character carry-over by letting the retail engine write a real,
complete save of the CURRENT (host-staged) world, and only reading the character back out of
it for the *next* adopt-in (`session-save.md` "Phase 1 shipped", item 5). Nothing stops a
player from instead picking that exact save from the main menu's Load Game list directly.

**Consequence**: a save a joiner made mid-session, if loaded outside a co-op session, drops the
player into the HOST's module/journal/globals — quests the joiner may never have personally
experienced, companions recruited on the host's own timeline, world flags set by the host's own
choices — with the joiner's own character's stats/gear/level grafted onto it. This is not
"corruption" (nothing crashes, nothing is malformed), but it is a genuine story-consistency
surprise: the save-list UI gives no indication that a particular slot is "really the host's
game," and a player who saves out of habit during co-op (which the mod's own design wants to
encourage, for crash-safety and "resume tomorrow") can end up treating it as their own solo
continuation without realising what it actually contains.

This is the concrete shape of the parent task's "keeps a host plot item that makes its solo
story inconsistent" concern: it is not that a specific item leaks in isolation — it's that the
**entire save**, including any co-op-acquired items/credits, is a snapshot of the host's world
that happens to look exactly like an ordinary solo save in the load list.

---

## 4a. Not every key item is covered by a sync channel — a documented, still-open gap

§1's "a purchase already lands in the shared pool, and `party_pool_sync=2` mirrors it" is
specifically about **store purchases and manual shared-stash deposits**. It does not cover
every way K1 hands the player a key item. Two purpose-built channels exist for the rest:

- **`dialogue_item_sync`** (`AgentSettings.cs:138-140`, default ON) — diffs the local PC's
  inventory only *while this instance owns a conversation*, catching a dialogue node's own
  `CreateItemOnObject`/`GiveItem` reward (`docs/research/dialogue-consequences.md` §3, live
  pass 2026-08-24/27).
- **`script_item_sync`** (`AgentSettings.cs:142-158`, default ON since 2026-08-27) — the
  explicit complement: diffs the local PC's inventory *whenever this instance is NOT in a
  conversation*, meant to catch "an OnEnter trigger, an OnDeath script, a cutscene, a heartbeat
  script" grant (`AgentSettings.cs:144-145`), live-verified across three controls
  (`dialogue-consequences.md`'s script-item-sync section).

Despite both shipping ON well before it was written, **`docs/research/manaan-hrakert-rift.md`
(2026-09-18)** documents a still-open, live-reasoned gap for exactly this class of item: the
Hrakert Station envirosuit (`man28_envirosuit`, `BaseItem 64` "Plot_Useable_Items", `Plot 1`)
is granted by `k_pman_suituse`, an `OnUsed` placeable script — `GetItemPossessedBy(member,
"man28_envirosuit")` gates the airlock dialogue that lets a player through. That doc's own
finding: *"Only the engine that used it gets the item... any `GetItemPossessedBy(..,
"man28_envirosuit")` gate on its engine (`k_pman_airlock01`) fails"* for whichever
player did not personally click the suit locker. The doc does not cross-reference
`script_item_sync` at all (it reasons only about the dialogue-specific `ItemOpKind`/
`DialogueGrantSender` channel), so it is genuinely unclear from the written record whether
`ScriptItemGrantPump`'s generic "not in a conversation" diff would actually catch an `OnUsed`
placeable grant too, or whether something about a `Plot 1`-flagged item, or the specific timing
of a placeable interaction, defeats it. **NEEDS-LIVE, flagged rather than resolved here** — this
is outside the present task's scope (which is about purchased/bought key items, not every grant
mechanism in the game), but it means a reader should not assume every "key item" this document's
§7 census finds is automatically safe just because `party_pool_sync`/`dialogue_item_sync`/
`script_item_sync` are all ON by default. The shipped mitigation for the envirosuit specifically
is architectural, not a sync fix: the non-driving player is kept out of the Hrakert
Station/sea-floor/Rift modules entirely and spectates (`manaan-hrakert-rift.md` §4, "Option A").

---

## 5. Fixes made this pass (small, safe, doc/comment-only — no behaviour change)

`src/KotorCoop.Session/CoopConfig.cs`'s top-of-file `<summary>` example block (the first place
a developer or a config-writing script would look) had drifted out of date: it still showed
`party_pool_sync = 0 ; ... (NEEDS-LIVE, default off)` and never mentioned `store_sync` at all,
while the ACTUAL defaults (the per-property doc comments a few hundred lines down, and the
parse code itself) have been `party_pool_sync = 2` / `store_sync = 2`, both ON, since
2026-08-29. This is exactly the "config default drift" hazard already named in this project's
own conventions (multiple readers of one default must agree) — here the drift was between two
COMMENTS in the same file, not between two independent readers, but it is the same class of
trap: the top-of-file example is the one a human is most likely to trust at a glance. Fixed to
read:

```
party_pool_sync = 2        ; sync the shared party item pool: 0 off, 1 the legacy op-log
                            ; (docs/research/party-pool-sync.md), 2 the host-authoritative
                            ; redesign (default, live-verified since 2026-08-29) -
                            ; docs/research/shared-inventory-v2.md
store_sync = 2              ; sync merchant store stock: same 0/1/2 modes as party_pool_sync,
                            ; 2 is the default (live-verified since 2026-08-29) -
                            ; docs/research/store-sync.md and docs/research/shared-inventory-v2.md
```

No functional/config-parsing code was touched — `CoopConfig.ParseSyncMode`/the property
initializers already had the correct `2`/`2` defaults (§0); only the misleading example text
changed. `dotnet build KotorCoop.slnx` clean (comment-only change, no plugin touched, so
`build.ps1` was not needed). No new test needed — nothing observable by a test changed.

No other fix was made this pass. The session-save "graft" risk (§4) and the equipped-item
duplication risk (§3c) are both real but are **design-level** gaps, not small/local bugs — a
fix would mean either warning the player in the save-list UI, marking a session-save
specially so it cannot silently masquerade as an ordinary solo save, or teaching the rejoin
path to warn/refuse when an equipped item's tag might collide with the host's own world state.
Each is a real feature, not a "clearly-safe small fix," and is left for the roadmap (§6).

---

## 6. Live recipes (NOT run this pass — static research only)

Two real GOG installs, a live co-op session already running (host + one client, both loaded
into the same module, `party_pool_sync`/`store_sync` at their shipped defaults).

1. **(a) Joiner buys, disconnects without saving.** Client opens any store, buys an item whose
   tag is not already in the party pool, confirms via `items`/an inspect verb that it is in
   the shared pool. `disconnect` from the console (no in-game Save first). From the main menu,
   load any of the client's OWN pre-existing saves. Expect: the item is absent, exactly as it
   was before the session — confirms §3(a)'s "own save never written" claim still holds with
   `party_pool_sync=2`/`store_sync=2` at their current defaults.
2. **(a, continued) Joiner buys, saves, disconnects.** Repeat the buy, then use the in-game
   Save menu (a new/overwritten slot), THEN disconnect. Load that specific save from the main
   menu. Expect: the item is present, but the module/journal/NPCs match the HOST's world at the
   moment of the buy, not the joiner's own prior solo progress — confirms §4's "graft" claim
   directly, and is the single most important scenario to watch for anything surprising (a
   crash, a broken quest reference, a companion the joiner never recruited standing in their
   party).
3. **(b) Host buys, joiner disconnects.** Reverse roles of #1 — confirms §3(b)'s symmetry claim
   (the item reaches the joiner's own live pool via the mirror while connected, but never
   touches their own save unless they explicitly save).
4. **(c) Joiner solo-buys/finds a LOOSE item, then rejoins.** Disconnect the joiner first,
   have them buy/find an item solo and save, then rejoin the host (`agent adopt` /
   auto-rejoin). Expect: the item is NOT present in the joiner's inventory once adopted into
   the host's world — confirms §3(c)'s "loose items are discarded, not duplicated" claim.
5. **(c, continued) Joiner solo-EQUIPS a distinctively-tagged item, then rejoins.** Same as #4
   but equip the item (weapon/armor/whatever slot) before saving and rejoining. Expect: the
   item IS present, equipped, on the joiner's carried character once adopted — confirms the
   narrower equipped-item exception, and is worth checking specifically with one of the K1 key
   items identified in §7 that happens to be equippable, if any were found.
6. **(d) Credits.** `grantgold <amount>` on one side (the existing dev verb,
   `docs/research/progression-sync.md`'s own live-verified mechanism) while connected, confirm
   convergence on both sides within the documented ~5 s window, then disconnect the joiner
   WITHOUT saving and confirm their own solo save's credits are completely unaffected; repeat
   WITH a save before disconnecting and confirm the joiner's own save now shows the shared
   total, not their own pre-session balance.

---

## 7. K1 key/plot item census and merchant availability

### 7.1 Method (VERIFIED against game data, 2026-09-25)

- `nwscript.nss` (extracted with `tools/kotorfiles.py`) declares `object
  GetItemPossessedBy(object oCreature, string sItemTag);` at routine **30**. K1 has no `HasItem`;
  this is the only "does X hold tag Y" routine.
- Every `.ncs` was walked with `tools/localvaraudit.py`'s abstract stack interpreter (imported,
  unmodified; a scratch subclass recorded each ACTION 30 with its creature and tag arguments):
  all 1,784 BIF-global scripts plus every script in all 117 `modules\` stems. Result: 689 call
  sites in 443 scripts, **156 distinct literal tags**.
- Every store template (`.utm`, 30 templates in 20 modules; none are global) was read for its
  `ItemList` and joined on tag. **34 of the 156 tags are sold somewhere; 122 are not.**
- **Gaps.** 32 scripts exceeded the interpreter's step budget and yielded no hits (mostly large
  combat-escalation and area-dispatch scripts: `k_ai_master`, `k_pdan_14a/14b/14d_area`,
  `k_ptat_*dlg`, `k_psta_hostscn2/3`, ...); 4 more are partial. A call inside them would be
  missed. 31 call sites pass a variable tag; all are inside the computer-spike/repair-part
  minigame helpers and resolve to `K_COMPUTER_SPIKE`/`K_REPAIR_PART`. `"-"` (`temp_at02`) and
  `w_crystal` (`test18`) are dev leftovers, not gates.
- **Creature argument.** Almost every call passes `GetFirstPC()` or `GetPCSpeaker()`, never a
  party loop (exceptions: `k_pman_suituse`, `k_pkor_open_door`, `k38a_init_wardrd` iterate
  `GetPartyMemberByIndex`). Per §1 this still resolves through the party-wide repository, so
  what matters in co-op is whether the item is in **that engine's** pool — not which body.

### 7.2 Plot gates that can be bought (the only ones §3(a)'s "buys" scenario applies to)

| Tag | Gate | Where checked | Sold by |
| --- | --- | --- | --- |
| `rakghoulserum` | Taris rakghoul-cure sidequest (Zelka / Gendar) | `k_ptar_gendarser`, `k_ptar_hasserum`, `k_ptar_noserum`, `k_ptar_takeserum`; `tar_m02ab`..`tar_m10ac` | `zelkastore001/002/004/005`, `tar_m02ac` |
| `ptar_permacrete` | Taris apartments feud (Matrik) | `k_ptar_matrikcs0`, `k_ptar_matrikdet`; `tar_m03*`, `tar_m10*` | `keblastore`, `tar_m02ac` |
| `tat20_banthafod` | Tatooine bantha-fodder / Sand People ambush chain | `k_ptat_hasfodder`, `k_ptat_gotfodder`, `k_ptat_banthdlg`, `k_ptat_ambushed` ...; `tat_m18aa/ac`, `tat_m20aa` | `kas_twostore`, `tat_m17ab` |
| `g_i_belt006/007/008/010`, (`geno_stealth` not sold) | Korriban academy stealth shortcut (optional); `g_i_belt010` also `k_pend_carth14` on the Endar Spire | `k_pkor_open_door`, `k38a_init_wardrd` | general stores on Taris, Tatooine, Kashyyyk, Korriban |

Everything else sold and checked (medpacs, stims, grenades, `k_computer_spike`,
`k_repair_part`, `gizkapoison`, a few weapons) is a "do you have spare gear" cost check, not a
story gate.

### 7.3 Plot items no store sells (quest-given or found only)

Main quest: `kor_starpad` (Korriban star-map datapad), `dan_map1/2` (Dantooine ruins),
`lev40_spacesuit` and `tar09_icebreaker` (Leviathan escape), `ptar_sitharmor` +
`ptar_sithpapers` (Taris Sith-base disguise), `bast_temp_saber`, `sta_temp_saber`/`sta_light1`/
`sta_shortlight1` (Leviathan/Star Forge scripted sabers), `g_shipparts` (Ebon Hawk parts,
Unknown World), `unk42_key`, `punk_holocron`, `ancienttome`, `g_head`, `unk_researchpad`,
`holocron`. Manaan: the Sunry trial evidence (`w_*`, `g_mandat1/2/3`, `g_manaanvis`,
`g_puzdat`) and the Hrakert set (`man28_envirosuit`/`sonic`/`toxic`/`environment`, §4a).
Korriban academy: `k33_itm_sithmdln`, `k35_itm_*`, `k36_itm_holocron`, `k37_itm_freed*`,
`k39_itm_*`, `kor37_datapad01/02`, `kor_itm_sithtabl`. Kashyyyk: `kas22_resouceid`,
`kas23_baccasword`, `kas24_*` (incl. `kas24_joleeamule`), `kas25_*`. Taris: `ptar_*journal`,
`ptar_sbpasscrd`, `ptar_shieldcodes`, `tar05_synthodor`, `tar08_comppass`, `tar10_*` (swoop),
`tar_spice`. Tatooine: `tat17_*`, `tat18_*`, `tat20_gaffi*`, `tat20aa_westmap`. Dantooine:
`dan13_plotcrys`, `dan14_*`, `dan14ae_blk/whtcrystal`, `dan16_key`.

These reach the player through dialogue rewards, script grants or container/corpse loot, so
§3(a)'s buy scenario does not apply to them. What does apply is §3(c) (a solo-acquired copy is
left behind on rejoin unless equipped — none of these are equippable except the scripted sabers,
which the engine hands out and takes back itself) and §4a (whether the other engine receives the
grant at all).

### 7.4 What this means per scenario, for the items that matter

- **(a)/(b) with one of the four buyable gates.** While connected, a buy on either side lands
  in both pools (§1), so either player's Zelka/Matrik/fodder conversation passes. After the
  joiner leaves without saving, the host keeps the item and the shrunk stock; the joiner's own
  saves never had it, and their own Taris/Tatooine run is untouched. If the joiner saved first,
  that save is the §4 graft.
- **(c) with a buyable gate item.** A joiner who bought `rakghoulserum` solo and rejoins arrives
  without it (loose items do not travel). No duplicate. The host's quest state is the host's.
- **The one real inconsistency surface is §4**, and the census does not change its shape: the
  graft save carries the host's `GLOBALVARS.res`, so a Taris-quest global the host set sits
  beside whatever serum/permacrete/fodder is in that save's pool — consistent with the host's
  story, not the joiner's.
