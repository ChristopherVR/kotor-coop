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
static-research pass plus the small fixes in §5 (one behaviour fix with tests, doc corrections).

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
join/rejoin carry) holds only what belongs to that one creature. The party-wide state lives
elsewhere in the save, **VERIFIED by reading every save on the rig install** (7 saves, 20
module entries, scratch reader over `tools/kotorfiles.py`):

| What | Where in the save | Seen on disk |
| --- | --- | --- |
| loose items (the party stash, `CSWPartyTable+0x118` live) | `SAVEGAME.sav` → **`INVENTORY.res`** (`INV `, top-level `ItemList`) | 1-4 entries per save |
| party credits (`CSWPartyTable+0xFC` live) | **`PARTYTABLE.res` → `PT_GOLD`** | present in every save |
| the PC's own `ItemList` | `<module>.sav` → `module.ifo` → `Mod_PlayerList[0].ItemList` | **0 entries in all 20** |
| the PC's worn gear | `Mod_PlayerList[0].Equip_ItemList` | 2 entries in all 20 |
| a `Gold` field on the PC | `Mod_PlayerList[0].Gold` | 0 in all 20 |

So the at-rest layout matches §1's live one: the engine writes the stash and the credits to
party-level resources and leaves the PC's own item list empty. `PlayerIfo.TryExtractPlayer`
(`src/KotorCoop.Memory/PlayerIfo.cs:74-141`) reads `Mod_PlayerList[0]` only (it opens
`PARTYTABLE.res` just for the `PT_CONTROLLED_NP` Leviathan guard, lines 83-94, and never touches
`INVENTORY.res`), and `BuildPifo` (lines 154-183) copies that struct byte-for-byte: worn gear,
class/level/stats/feats/alignment, appearance, the personal Pazaak deck
(`PersonalPazaak.Capture`), minus `Min1HP`. **Loose items and credits therefore never travel
through the pifo/sidecar carry at all; worn gear and character stats always do.** Every
scenario below follows from this.

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
is *why* that is structurally true (loose items live in `INVENTORY.res` and credits in
`PARTYTABLE.res`, neither of which the carry mechanism reads), not a new design decision.

**"Their own save on disk is never written" is no longer true, and has not been since
2026-09-18** (VERIFIED in code). Two slots on the joiner's own install are written while they
are connected:

- `saves\000001 - AUTOSAVE` — the staged copy of the host's world (adoption, as designed).
- `saves\000000 - QUICKSAVE` — **the joiner's own quicksave slot.** `CoopCommandDispatcher.
  MaintainPeriodicCapture` (`src/KotorCoop.Agent/CoopCommandDispatcher.SaveLoad.cs:128-218`)
  sends `CheatKinds.QuickSave` every `character_capture_interval_min` minutes (default **10**,
  `CoopConfig.cs:521`) while idle, and `CaptureCharacterOnDisconnect` (`:1801-1861`) sends one
  more on every clean disconnect. The plugin's `QuickSave` is `CClientExoAppInternal::DoQuickSave`
  `0x005F4B50`, whose directory is `SAVES:000000 - QUICKSAVE` (`docs/research/world-adoption.md`,
  "The save primitive"). The purpose is sound (a crash no longer loses everything since the
  last door, `docs/review-seamless-2026-09-18.md` §2); the side effect is that the joiner's
  single-player quicksave is overwritten with a save of the host's world within ten minutes of
  joining, and again on leaving.

Any save the joiner makes by hand while connected lands the same way. Every one of these is a
real engine save of the live session, so it holds that session's `INVENTORY.res` (the shared
pool), `PARTYTABLE.res` (the shared purse) and `Mod_PlayerList[0]` (the joiner's character) —
and the host's module, `GLOBALVARS.res` and journal. Before this pass the player-facing docs
(`README.md`, `docs/limitations.md`, `tools/package-README.txt`) still promised "your own save
files are never changed"; §5 corrects them.

> **⚠ Superseded for the quicksave slot, 2026-09-25 — see §4.** Everything below in §2/§3 that
> says the joiner's `000000 - QUICKSAVE` gets overwritten with the host's world was true when
> written and is the bug §4 now records as fixed: the mod no longer writes that slot at all,
> for any reason. What §2/§3 say about `000001 - AUTOSAVE` (the mechanical staging target) is
> still current — see §4's own "what this does NOT do." Left unedited below rather than rewritten,
> the same way this tree layers a correction on top of a still-useful trace rather than erasing it.

---

## 3. Scenario tracing

### (a) Joiner buys a key item from a host-world store, then disconnects and loads own solo save

- **Named solo saves** (anything but slots 000000/000001): untouched (VERIFIED — no code path
  writes them). Loading one shows what it always did; the co-op purchase is not in it.
- **The quicksave slot** (and the autosave slot): now holds the host's world with the bought
  item in its `INVENTORY.res` and the shared purse in `PT_GOLD`. Quick Load, or loading that
  slot in single player, puts the joiner in the host's story holding the co-op item (§4). This
  is automatic — the joiner does not have to save anything for it to happen.
- **Store depletion** (`store_sync=2`, default ON): the purchase is applied to the host's own
  engine as well — the mode-2 host is the single source of truth and applies a client's buy
  intent to its own engine (`docs/research/shared-inventory-v2.md` §2.1/§2.7,
  `docs/research/store-sync.md` §2.4). The item itself is already in the joiner's party pool
  the instant it is bought (§1) and `party_pool_sync=2` mirrors it to the host. After the joiner
  leaves, the host keeps both the item and the shrunk stock, and its next save records them —
  the host's story continues coherently. Both channels have long histories of transient live
  bugs (`shared-inventory-v2.md` §§5-10); a fresh regression in the pool channel alone would
  leave the host with the stock gone and no item. Worth the periodic recheck in §6, not an open
  defect this pass found.

### (b) Host buys it, joiner disconnects and plays solo

Symmetric: the mode-2 snapshot is host → every client, so the host's purchase is in the
joiner's live pool too, and therefore in the joiner's quicksave (§2) the next time the periodic
or pre-disconnect quicksave lands. Named solo saves are untouched. The host's own story is
consistent (the host bought it in the host's world).

### (c) Joiner plays solo, buys/finds the item, then rejoins the host

**Loose items from the solo game do not come into co-op, and are not duplicated.**
`PlayerIfo.TryExtractPlayer`/`BuildPifo` never read the solo save's `INVENTORY.res`, and the
world the joiner adopts brings the host's `INVENTORY.res`. The solo item stays in the solo save
and is absent in co-op, silently — a UX gap, not a duplication risk.

**Which character comes in.** With `join_mode = auto` (the default) the carried character is
the co-op sidecar, not a solo save, so solo play is not meant to reach co-op at all. It could,
through one path, fixed this pass (§5): the disconnect/periodic capture falls back to "the
newest own save" when its quicksave is refused (combat, a conversation — `GetIsAllowedToSave`),
and a solo save made between sessions is newer than the sidecar, so the rollback guard let it
through. The co-op character then became the single-player PC — a different character
entirely if the player runs two — with its worn gear, level and alignment. With
`join_mode = save`, carrying the newest own save is the documented behaviour.

**Worn gear is the one inventory a carried character brings.** `Equip_ItemList` lives on
`Mod_PlayerList[0]` and travels. Under `join_mode = save` a solo-equipped unique item therefore
arrives in the host's world and can coexist with a copy the host's story holds. §7 shows the
K1 gate items are almost all non-equippable plot props; the equippable ones (the scripted
sabers) are handed out and taken back by the engine itself, so the practical exposure is small.

### (d) Credits spent in co-op vs. the joiner's own solo credits

Separate storage that meets only through the live mirror. The shared purse is
`partyTable+0xFC` on each engine, converged by `PumpProgressionHost`/`PumpProgression`'s
contribution ledger (`docs/research/progression-sync.md`, live-verified 2026-08-27 and
2026-09-03). On disk it is `PARTYTABLE.res → PT_GOLD` (VERIFIED, §1 table); the carry never
reads it.

- Named solo saves keep their own `PT_GOLD`; nothing in a session changes it.
- The joiner's quicksave (and any save they make while connected) records the shared purse
  at that moment — possibly a windfall from the host's earnings, possibly near zero.
- A rejoin does not bring solo credits in: the adopted world's `PARTYTABLE.res` is the host's.
- The host's next save records the purse as it stands, including everything either player
  earned or spent. Intended, and working.

---

## 4. The main inconsistency: the joiner's quicksave becomes the host's game

`docs/research/session-save.md` §2.3 describes session save as "character-only". The shipped
mechanism gets the character by letting the engine write a complete save of the live (host)
world and reading the character back out of it. Since 2026-09-18 that save is made
automatically, every ten minutes and on leaving, into the slot a single-player habit reaches
for first — Quick Load.

Loaded outside a session, it puts the joiner in the host's module, journal and globals —
quests they never started, companions the host recruited, flags set by the host's choices —
with their co-op character and the co-op items and purse. Nothing is malformed, but the story
is the host's, and the load list shows it as an ordinary quicksave. This is the concrete shape
of "keeps a host plot item that makes its solo story inconsistent": not one item leaking, but a
whole save of the host's world sitting in a single-player slot. It also destroys whatever
single-player quicksave was there.

**FIXED, 2026-09-25** (`docs/research/session-save.md`'s "A dedicated co-op save slot" entry —
read it for the full design and the engine facts it rests on). The mechanism that shipped is NOT
the `SendSaveGameRequest`-with-a-redirected-number idea this paragraph originally proposed:
`DoQuickSave` cannot be redirected at all (it is hard-coded to slot 0), and the one native call
that DOES take an arbitrary slot only re-issues a request `SaveFlushHook` already intercepted, a
mechanism still marked NEEDS-LIVE by its own header — tying a correctness requirement to it was
the wrong trade. What shipped instead needs no new native call at all: `KotorCoop.Memory.
CoopSaveRedirect` moves `000000 - QUICKSAVE` aside before a mod-driven save request, lets the
engine write its fresh (unavoidably slot-0) capture exactly as before, then moves THAT to a
dedicated `"Co-op"` save slot (`KotorCoop.Memory.CoopSaveSlot`) and restores what was held back —
ordinary directory renames, the same primitive class `PluginMenuLoader.
TryStageSaveAsAutosaveFromDirectory` already uses. Wired into `NewCharacterCaptureController`'s
chargen capture, `MaintainPeriodicCapture` and `CaptureCharacterOnDisconnect` — the three
mechanisms this section named above. `000000 - QUICKSAVE`'s own mtime is unchanged across a
whole co-op session (a same-volume rename preserves file timestamps), which is now the live
recipe's own check. `000001 - AUTOSAVE` still gets staged into (an engine requirement —
`CSWGuiSaveLoad::LoadPCAutoSave` is hard-coded to that literal name and `MenuLoadController`'s
whole load replay is built around it) but is now ALSO mirrored into the same dedicated slot right
after staging, so the player has a normal, Load-Game-menu-visible "Co-op" save to return to
instead of reading the ambient autosave slot. NEEDS-LIVE: the recipe at the foot of that
session-save.md entry has not been run this pass.

---

## 4a. Not every key item is covered by a sync channel — a documented, still-open gap

§1's "a purchase already lands in the shared pool, and `party_pool_sync=2` mirrors it" is
about **store purchases and stash deposits**. The rest of K1's grants have their own channels:

- **`dialogue_item_sync`** (`AgentSettings.cs:138-140`, default ON) — diffs the local PC's
  inventory while this instance owns a conversation (`docs/research/dialogue-consequences.md`
  §3).
- **`script_item_sync`** (`AgentSettings.cs:142-158`, default ON since 2026-08-27) — diffs it
  whenever this instance is not in a conversation, for "an OnEnter trigger, an OnDeath script,
  a cutscene, a heartbeat script".

`docs/research/manaan-hrakert-rift.md` (2026-09-18) nevertheless records a gap for an `OnUsed`
placeable grant: `man28_envirosuit` (`BaseItem 64`, `Plot 1`) is created by `k_pman_suituse` and
gates `k_pman_airlock01`; "only the engine that used it gets the item". That doc reasons only
about the dialogue channel and never mentions `script_item_sync`, so whether
`ScriptItemGrantPump` catches this grant is unknown from the record. **NEEDS-LIVE.** The shipped
mitigation is architectural: the non-driving player stays out of the Hrakert sea floor and Rift
and spectates (`manaan-hrakert-rift.md` §4, Option A).

---

## 5. Fixes made this pass

1. **The capture fallback no longer lifts a single-player save into the co-op character**
   (§3c). `ClientCharacterPersistence.Capture` gained a `connectedSinceUtc` floor: a source whose
   `SAVEGAME.sav` was written before the current connection began is refused (`Kept`, the
   sidecar is left as it is). `SessionSaveWatch` records the floor at its first observation
   after `Reset` (the dispatcher resets it on every tick with no session, so this is the first
   tick of the connection) and passes it to every capture entry point (`ObserveClient`,
   `CaptureOnDisconnect`/`CaptureNow`, `CaptureNowWritten`). Nothing that should be captured is
   lost: every save the session itself makes is captured as it lands, and the own-load path
   (`TryCaptureVerifiedLoadSource`) does not go through `Capture`. Tests:
   `ClientCharacterPersistenceTests.CaptureNeverLiftsASinglePlayerSaveWrittenBeforeThisConnection`,
   `SessionSaveWatchTests.DisconnectCaptureRefusesASaveWrittenBeforeTheConnectionBegan`;
   `ClientCaptureFiresOnlyOnASaveIdentityChange`'s fixture now writes its "save happened" during
   the connection, as a real one is. NEEDS-LIVE: recipe 4 below.
2. **Player docs no longer promise the joiner's saves are untouched.** `README.md`,
   `docs/limitations.md` ("Bringing your own character") and `tools/package-README.txt` now say
   the autosave and quicksave slots hold the host's world while connected, and to keep
   single-player progress in a named save.
3. **`CoopConfig.cs`'s top-of-file example** said `party_pool_sync = 0 ... default off` and
   omitted `store_sync`; both default to `2`, ON, since 2026-08-29 (the property doc comments
   and the parser already said so). Comment only.

Not fixed (design-level, left for the roadmap): the quicksave slot itself (§4's proposed
rule), the silent loss of solo loose items on (re)join (§3c — a one-line on-screen note at join
would do), and the equipped-item uniqueness edge under `join_mode = save`.

---

## 6. Live recipes for the main session (NOT run this pass)

Two GOG installs, shipped defaults (`join_mode = auto`, `party_pool_sync`/`store_sync` = 2,
`character_capture_interval_min` = 10), host + one joiner in the same module. Before starting,
on the joiner's install copy `saves\000000 - QUICKSAVE` aside and make one named solo save.
Rebuild and `-Install` first so the §5 fix is on the rig.

1. **(a) Joiner buys, disconnects.** Joiner opens a Taris store (`zelkastore*` or `keblastore`,
   `tar_m02ac`, which sell the gate items `rakghoulserum`/`ptar_permacrete`, §7) and buys one.
   On the host the item appears in its pool (`party pool:` lines, or `items`) and the stock
   drops. `disconnect` on the joiner; its agent log shows `character capture: pre-disconnect
   quicksave landed`. Then on the joiner, from the main menu:
   - load the named solo save → no serum, own credits (named saves untouched);
   - load `000000 - QUICKSAVE` → the host's module/journal, serum present, shared credits (§4).
   `python tools/saveinspect.py` on both; compare `INVENTORY.res` and `PT_GOLD`.
2. **(a), host side.** The host saves after the joiner left and reloads: serum in the pool,
   stock still reduced, Zelka's conversation takes the has-serum branch (`k_ptar_hasserum`).
3. **(b) Host buys, joiner leaves.** Reverse of #1: the host buys `tat20_banthafod`
   (`kas_twostore`, `tat_m17ab`); it reaches the joiner's pool; disconnect; the joiner's
   quicksave has it, the named save does not.
4. **(c) Solo between sessions — the §5 fix.** On the joiner, load the named solo save and
   make a NEW named save (newer than the sidecar). Rejoin. Within 10 minutes start a fight and
   `disconnect` mid-combat (the quicksave is refused). Expect `pre-disconnect quicksave did not
   land`, then `... predates this co-op connection ... kept the sidecar`; the next join carries
   the co-op character, not the solo PC. Before the fix the sidecar took the solo PC.
5. **(c) Loose item left behind.** In the solo game buy a stim or `g_i_belt010`, save, rejoin:
   absent in co-op, still in the solo save.
6. **(d) Credits.** `grantgold 500` on either side; both converge within ~5 s; disconnect the
   joiner: the named save's `PT_GOLD` is unchanged, the quicksave's `PT_GOLD` is the shared
   purse.

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
  joiner leaves, the host keeps the item and the shrunk stock; the joiner's named saves never
  had it and their own Taris/Tatooine run is untouched, but their quicksave slot now holds the
  host's world with the item in it (§4).
- **(c) with a buyable gate item.** A joiner who bought `rakghoulserum` solo and rejoins arrives
  without it (loose items do not travel). No duplicate. The host's quest state is the host's.
- **The one real inconsistency surface is §4**, and the census does not change its shape: the
  graft save carries the host's `GLOBALVARS.res`, so a Taris-quest global the host set sits
  beside whatever serum/permacrete/fodder is in that save's pool — consistent with the host's
  story, not the joiner's.
