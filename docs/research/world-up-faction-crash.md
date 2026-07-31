# World-up faction crash — `GetSingletonParty(null)` in the object-update writer (research 2026-07-31)

Why the plugin-driven main-menu load crashes ~0.4 s after "menu: the world is up" at
`0x5BF694`, what the retail load path does that our replay does not, and how to fix it.
Verified against `swkotor.exe` (GOG retail 1.0.3; disk == memory, no ASLR, base `0x400000`)
by disassembly (`tools/disasm.py`), caller scans (`tools/xrefs.py`) and the sha256-matched
symbol DB. Nothing was modified.

Builds on Handoff.md's engine-side load path and on `PartyFactionGuard.cs` (the fix for the
earlier crash at `0x5272D8` — which this doc shows was the **same underlying state** seen
through a different window).

Tag key: **VERIFIED** = read from the binary here (or measured live already) · **INFERRED** =
deduced, not yet run · **NEEDS-LIVE** = must be confirmed on the running game.

## Headline

The crash is not about a missing "faction singleton" — the faction manager and the player
faction **do** exist by world-up. The player's **creature** is simply not a member of any
faction: its `CSWSCreatureStats.faction_id` (`stats+0x78`) is the constructor default `-1`
(set at `0x5ACEDA`), so `CSWSCreature::GetFaction 0x513FC0` → `CFactionManager::GetFaction
0x52B3B0` returns null, and the first code path that uses the result without a null check —
the party block of `CSWSMessage::WriteGameObjUpdate_UpdateObject`, reached as soon as the
loopback player reaches update state 1/2 — dereferences it. **The earlier `0x5272D8` party
crash was the identical state** hit via `HandlePlayerToServerParty`; the PartyFactionGuard
merely moved the first dereference 0.1 s later into the update writer.

`RequestStatus` is a red herring: it is a 3-byte server-status query (`70 01 00`) that
creates nothing — and the first crash **happened with RequestStatus sent**, so sending it is
not the fix. The engine call that retail relies on to give the PC its faction is
`CSWSFaction::AddMember 0x5BFA70` on the **player faction at `[CFactionManager+0x18]`**,
made by `CSWSCreature::PostProcess 0x4F1C40` — and PostProcess's PC branch **silently skips
it** when the player-faction slot is null (the NPC branch has a fallback; the PC branch does
not). The recommended fix is to make that exact call ourselves at world-up when the player
creature's faction resolves to null. One `AddMember` repairs every crash window at once
(there are at least seven unguarded faction dereferences downstream); a byte-patch guard
would have to cover `GetSingletonParty` **and** `GetLeader` **and** more.

## 1. The crash path, instruction by instruction (Q1)

### Fault site — VERIFIED

`CSWSFaction::GetSingletonParty 0x5BF690`:

```
0x5BF690  push ebx
0x5BF691  push esi
0x5BF692  mov  esi, ecx          ; this = the CSWSFaction
0x5BF694  mov  eax, [esi+4]      ; member count -> faults, this == null (reads 0x4)
```

Semantics (whole function read): walk the faction's member-id array (`[this+0]`, count
`[this+4]`), count members whose `[creature+0x9D4]` (the is-PC flag, copied from
`stats.is_pc` by PostProcess at `0x4F1D0C`) is non-zero; return **1** if the faction holds
at most one PC ("the party is a singleton"), 0 otherwise. It is a BOOL for the update
message, **not** a singleton accessor.

### The caller that faults — VERIFIED

The faulting call is at `0x57595A`, inside `CSWSMessage::WriteGameObjUpdate_UpdateObject
0x574A10`, in the block gated on update-flag bit `0x4000` (`test ah,0x40` at `0x575923`):

```
0x57592C  mov  edx, [ebx+0x9D4]  ; ebx = the game object being written (a creature)
0x575932  push edx               ; WriteBOOL(is-PC)
0x575951  mov  ecx, ebx
0x575953  call 0x513FC0          ; CSWSCreature::GetFaction  -> returns NULL here
0x575958  mov  ecx, eax
0x57595A  call 0x5BF690          ; CSWSFaction::GetSingletonParty(this=NULL)  ** CRASH **
0x57595F  push eax               ; (the recorded return address)
0x57596A  mov  ecx, ebx
0x57596C  call 0x513FC0          ; GetFaction AGAIN
0x575971  mov  ecx, eax
0x575973  call 0x5BF5C0          ; CSWSFaction::GetLeader -> derefs [this+0xC]: would ALSO crash
```

The stack values `0x4ED687` (`UpdateAppearanceForEquippedItems+0x67`) and `0x567129`
(`ComputeAppearanceUpdateRequired+0x29`) are **stale frames** left by the earlier
`TestObjectUpdateDifferences 0x577FA0` → `ComputeAppearanceUpdateRequired 0x567100` →
`UpdateAppearanceForEquippedItems 0x4ED620` pass over the same object (all VERIFIED: neither
of those two functions calls `GetSingletonParty`). The true chain is:

`CServerExoAppInternal::UpdateClientGameObjects 0x4B6950` →
`UpdateClientGameObjectsForPlayer 0x4B3EC0` (ret `0x4B3F7B`) →
`SendServerToPlayerGameObjUpdate_2 0x578520` → `SendServerToPlayerGameObjUpdate 0x578200` →
`ComputeGameObjectUpdateForObject 0x578060` → `WriteGameObjUpdate_UpdateObject 0x574A10` →
`GetFaction` → **`GetSingletonParty(null)`**.

### Where the null comes from — VERIFIED

`CSWSCreature::GetFaction 0x513FC0`:

```
edx = [creature+0xA74]          ; CSWSCreatureStats*
eax = [edx+0x78]                ; stats.faction_id
ecx = [serverInternal+0x10054]  ; CFactionManager (offsets DB: faction_manager)
CFactionManager::GetFaction(eax)
```

`CFactionManager::GetFaction 0x52B3B0` returns null iff `id < 0` **or**
`id >= factions_list.count` (`[fm+0x24]` array, bound check at `0x52B3B6/0x52B3BE`) —
it does not crash itself; it cleanly hands back null.

`stats.faction_id` is only ever made valid two ways:

1. **`CSWSFaction::AddMember 0x5BFA70`** writes `stats.faction_id = [faction+0xC]` at
   `0x5BFAD2–0x5BFADB` (the faction's own id, assigned when the faction self-registers into
   `factions_list` in its constructor `0x5BF860`, at `0x5BF8E6–0x5BF8F5`).
2. **`CSWSCreatureStats::ReadStatsFromGff 0x5AFCE0`** reads GFF field `"FactionID"`
   (`0x5B063D`, `ReadFieldWORD`) — **with the current value as the default**, i.e. if the
   field is absent the constructor's `-1` (`mov dword [esi+0x78], 0xFFFFFFFF` at
   `0x5ACEDA`) survives (as `0xFFFF` after the word round-trip; out of range either way).

So the crashing creature is one that was never `AddMember`ed and whose GFF source did not
carry a resolvable `FactionID`.

### The hole that permits it — VERIFIED

`CSWSCreature::PostProcess 0x4F1C40` (called by **every** load-time creature path, see §3):

* PC branch (`[creature+0x9D4] == 1`, `0x4F1D28`): fetch `CFactionManager` (`0x4AEFC0`),
  take **`player_faction = [fm+0x18]`** (`0x4F1D33`) and `AddMember(creatureId, 0)` —
  **but if `fm` or `[fm+0x18]` is null it just falls through** (`je 0x4F1D46`), leaving
  `faction_id` untouched. No fallback, no log.
* NPC branch: reads the saved id, sets `+0x78 = -1`, `GetFaction(id)`; on failure falls
  back to `GetFaction(1)` (`push 1` at `0x4F1DB4`) and AddMembers that — NPCs always end up
  in *some* faction.

The PC branch is the only load path in the binary that can complete "successfully" and
leave a creature with an unresolvable faction id.

## 2. `UpdateClientGameObjectsForPlayer 0x4B3EC0` — who it iterates, when it is safe (Q2)

VERIFIED. `UpdateClientGameObjects 0x4B6950` walks `player_list`
(`[serverInternal+0x10064]`, `CExoLinkedList<CSWSClient>`) and calls
`UpdateClientGameObjectsForPlayer(player, time)` per entry. Gate per player, on
`al = [player+0x24]` (the login state byte):

* **state 2** — jump straight to the send (`0x4B3F4C`). State 2 is written by
  `HandlePlayerToServerAreaMessage` at `0x524BB0`: the client's **"area loaded" ack**.
* **state 1** — set by `LoadCharacterFinish 0x4B5C50` (`mov byte [esi+0x24],1` at
  `0x4B5CC8`, alongside `SendServerToPlayerLogin_Confirm 0x56FDE0`). Requires
  `[player+0x7C] == 1`, `CSWSPlayer::GetGameObject 0x561080` non-null, vtable `+0x30`
  (as-creature) non-null, and `[creature+0x310] != 0x7F000000` (an object-id field checked
  against OBJECT_INVALID — area binding, INFERRED name) before sending.
* anything else — skip to `0x4B4023` (no updates).

Then `SendServerToPlayerGameObjUpdate_2([serverInternal+0x10010], player)` at `0x4B3F76`
walks the player's last-update set and writes an update **per visible object** — the party
block (bit `0x4000`) included. So: the crash fires only once **our loopback player is
logged in (state ≥ 1) with a valid creature**, and the object being written when it faults
is a creature in that player's update set. Crash #1 (`HandlePlayerToServerParty` doing
`GetGameObject → GetFaction → null`) proved the **player's own creature** is the one with
the bad faction, and the first object update naturally covers it. The safe precondition
this path assumes is exactly "every creature reachable from a logged-in player resolves its
faction" — the invariant our load breaks.

## 3. Retail vs. replay — what actually initialises the PC's faction (Q3)

### What RequestStatus really is — VERIFIED

`CSWCMessage::RequestStatus 0x6775E0` sends the 3-byte message `70 01 00` (marker `0x70`,
major 1, minor 0) — a server-status query. Its senders are every game-start GUI flow
(`OnModulePicked 0x67BABA`, `CSWGuiSaveLoad::LoadGame 0x6CB3CE/0x6CB5E5`,
`BeginPlayGame 0x6DBF07`). Server-side it produces a status reply
(`SendServerToPlayerServerStatus_Status 0x568310` from
`HandlePlayerToServerServerStatusMessage 0x524C40` — routing INFERRED, handler VERIFIED).
**It creates no player, no faction, no party.** Decisive measured fact: the first crash
(`0x5272D8`) occurred in a run where RequestStatus *was* sent — the bad faction state
arises with or without it. Skipping it is not what broke the faction, and re-sending it
will not repair the faction.

### The full retail from-menu sequence — VERIFIED (each arrow is a read call site)

1. **Connect** completes → `CServerExoAppInternal::PlayerListChange 0x4B78F0` constructs the
   `CSWSPlayer` (`0x562C70` at `0x4B79FE`) and appends it to `player_list` — **connection
   alone creates the player**, no message needed. Ctor defaults: state `+0x24 = 0`, game
   object id `+0x28 = INVALID`, save-slot index `+0x44 = -1`, char index `+0x80 = 0`.
2. `LoadPCAutoSave 0x6CA250` (which we call): extracts the save's player-IFO to
   **`TEMP:pifo`** (string `0x74554C "TEMP:pifo"`, restype `0x7DE`, at `0x6CA604–0x6CA634`),
   calls `SetDoingStartNewModule(1) 0x4AF090` (writes `[serverInternal+0x1007C] = 1`), then
   `ExecuteLoadModule`.
3. Module load runs to completion — `CSWSModule::LoadModuleStart 0x4C9050` loads the
   **factions first thing on the module-load frame**: from the savegame table
   (`LoadFactionsFromSaveGame 0x52B5C0` at `0x4CA557`; `ResolveFactionName 0x52B2A0` maps
   the saved name `"player"` (string `0x748608`) onto **`[fm+0x18]`**), else
   `LoadFactions 0x52B490` at `0x4CA5BD`, else `CreateDefaultFactions 0x52BCE0` at
   `0x4CA5CF` (which allocates the player faction **first** and stores it at `[fm+0x18]`,
   `0x52BD28`). Only after the whole load does `MainLoop` set the server mode word
   `[serverInternal+0x10008] = 1` (`0x4BB193`) — so **factions are always loaded before any
   login can be serviced**.
4. `RunModule 0x4B6270` (mode 1→2): because `[+0x1007C] == 1` it **skips
   `LoadPrimaryPlayer`** (`je 0x4B6373`) and instead broadcasts
   `SendServerToPlayerModule_EndStartNewModule(1) 0x56CC50` and clears the flag.
   (`LoadPrimaryPlayer 0x4B5F50` is the *other* flow — module transitions during play.)
5. Client `HandleServerToPlayerModule_EndStartNewModul 0x652860`: reads the BOOL, and sends
   **`SendPlayerToServerLogin_LoginIFOCharacte 0x678030`** = message `70 02 0F`.
6. Server `HandlePlayerToServerLoginMessage 0x5244E0`, minor `0xF`: `[player+0x80] != -1`
   (default 0 → passes) → `LoadCharacterStart 0x4B7470` type `0xF` →
   `CSWPartyTable::CreateParty 0x565760` + **`LoadCharacterFromIFO(-1, 1, 1) 0x561E30`**:
   index `-1` selects **`TEMP:pifo`** (`0x561E91`), reads `Mod_PlayerList[[player+0x80]]`,
   constructs the creature (`0x4F7A10`), `SetGameObject 0x561C40`,
   `ReadStatsFromGff 0x5AFCE0` (restores `FactionID` if present), and — because arg3 is 1 —
   **`PostProcess` at `0x5622CC`** → PC branch → `AddMember([fm+0x18], id, 0)` → faction
   valid. Then `LoadCharacterFinish(player, 0)` → state 1, Login_Confirm.
7. Client acks the area (`HandlePlayerToServerAreaMessage`) → state 2 → full updates flow.

Every load-time creature constructor path (`LoadCreature 0x5005E8`, `LoadFromTemplate
0x502A8B`, `LoadCreatureData 0x56104D`, `LoadCharacterFromIFO 0x5622CC`) calls PostProcess;
with factions loaded, all of them produce a valid faction. **Statically, our replay drives
the same steps 1–7** — the deliberate omissions (RequestStatus, AddPanel/PopModalPanel) are
not on the faction path. The one *structural* difference is timing: our replay inserts a
1 s serviced gap between connect and load, and skips the whole client-side status/GUI state
machine, so the loopback client's handlers run against client state retail never has at
this point. Which concrete branch produced the factionless creature therefore **needs one
live probe** (below) — but the violated invariant, and therefore the fix, is unambiguous
either way: *the player creature is not in the player faction and its `faction_id` does not
resolve*, precisely the case PostProcess's PC branch drops on the floor (§1).

**Is the fix "send RequestStatus after all"? No** — measured: crash #1 had it sent and the
faction was still null. **Is a faction/party object missing? No** — LoadModuleStart always
leaves a populated `CFactionManager` with `[fm+0x18]` set (all three branches, VERIFIED).
**What is missing is the membership call** — `CSWSFaction::AddMember` on the player faction
for the player's creature — and it can be made directly.

## 4. Ranked fixes (Q4)

### Fix 1 — RECOMMENDED: make the missing engine call (`FactionFixup`) — class (a)

At world-up (the existing phase-3 `Odyssey.HasPlayer` ack in `MenuLoadController` — before
the ack is written is ideal, and it precedes both measured crash windows by ~0.4 s), run:

```
creature = CServerExoApp::GetPlayerCreature 0x4B2820(server)        // already bound in Odyssey.cs
if creature == 0: nothing to fix yet (retry next frame while pumping)
faction  = CSWSCreature::GetFaction 0x513FC0(creature)              // thiscall
if faction != 0: done (state is healthy)
fm = CServerExoApp::GetFactionManager 0x4AEFC0(server)              // thiscall, server = [[0x7A39FC]+8]
pf = *(fm + 0x18)                                                   // player faction
if fm == 0 || pf == 0: log loudly and stop (world loaded without factions — new information)
CSWSFaction::AddMember 0x5BFA70(this=pf, objectId=*(creature+4), bSilent=0)
```

Why this is the right level: it is byte-for-byte the call `PostProcess` makes for a PC
(`0x4F1D3A–0x4F1D41`), on the engine's own object, with all derived state rebuilt by the
engine (per the project's engine-write rules): AddMember dedups membership (`0x5BFAA4`
loop), removes the creature from any stale faction first (`0x5BF920`), appends to the
member array, **broadcasts `SendFactionUpdateAdd 0x5BE9C0`** because `bSilent=0`, and
writes `stats.faction_id = [pf+0xC]` (`0x5BFAD2`). Its only early-out is
`GetCreatureByGameObjectID(id) == null` (`0x5BFA8B`) — impossible here since we obtained
the creature from that same lookup. One call fixes **all seven** known crash windows
(the six `GetSingletonParty` callers — `0x526D9C`, `0x567DE6`, `0x56BE48`, `0x56EB1E`,
`0x57595A`, `0x576155` — plus `GetLeader` at `0x575973`) and any faction consumer we have
not enumerated. Risk: **low**. It also lets `PartyFactionGuard`'s patched path go back to
never firing.

### Fix 2 — worth flying alongside: keep the diagnostics, add the live probe — class (a)

At the same world-up hook, log once: `stats.faction_id` (`[[creature+0xA74]]+0x78`), `fm`,
`[fm+0x18]`, and `[[fm+0x24]+4]` (faction count). This turns the one INFERRED link (which
branch created the factionless creature) into a measurement for the doc, and confirms the
fixup fired. Cost: nil.

### Fix 3 — belt-and-braces byte-patch, only if a second crash window appears — class (b)

Entry guards in the `PartyFactionGuard` template, at the **callee** (one patch covers all
callers), for the two functions the party block calls back-to-back:

* `CSWSFaction::GetSingletonParty 0x5BF690` — original first 10 bytes
  `53 56 8B F1 8B 46 04 57 33 FF` (`push ebx; push esi; mov esi,ecx; mov eax,[esi+4];
  push edi; xor edi,edi`). 5-byte `jmp` over `53 56 8B F1 8B` → stub:
  `test ecx,ecx; jnz cont; mov eax,1; ret;`
  `cont: push ebx; push esi; mov esi,ecx; mov eax,[esi+4]; jmp 0x5BF697`.
  Returning **1** ("the party is a singleton") is the semantically correct answer for a
  faction that does not exist — it matches the function's own empty-faction return
  (`jle 0x5BF6CF` → `eax=1`).
* `CSWSFaction::GetLeader 0x5BF5C0` — original first 5 bytes `51 A1 FC 39 7A 00` (start:
  `push ecx; mov eax,[0x7A39FC]`; take 6 and pad). Stub:
  `test ecx,ecx; jnz cont; mov eax,0x7F000000; ret;`
  `cont: push ecx; mov eax,[0x7A39FC]; jmp 0x5BF5C6`. `0x7F000000` = OBJECT_INVALID is the
  function's own no-server answer (`mov ebx,0x7F000000` at `0x5BF5D3`, returned on the
  `je 0x5BF646` paths).

Not recommended as the primary fix: it is whack-a-mole (`GetFactionMember 0x5BF6E0` and the
reputation paths also deref an unchecked faction elsewhere), and it leaves the player
faction-less — cures the symptom, ships the disease.

### Fix 4 — suppress the update path until login completes — class (c), REJECTED

Holding `[player+0x24]` at 0 (or patching the state gate at `0x4B3F01`) stops
`UpdateClientGameObjectsForPlayer`, but crash #1 proved the same state is reachable through
`HandlePlayerToServerParty` (client-initiated), and the client cannot finish entering the
world without the updates this would block. Suppression hides the broken invariant instead
of restoring it.

## 5. Is the "faction singleton" supposed to exist at all? (Q5)

Yes — and it does. `GetSingletonParty` is misleadingly named: it is a per-faction query
("does this faction contain ≤ 1 PC"), not a global singleton accessor. The objects that
must exist are:

* **`CFactionManager`** at `[serverInternal+0x10054]` — created with the server internals
  (`Constructor 0x52B8E0`); VERIFIED present at crash time (GetFaction executed and
  returned null *cleanly*, which requires a valid manager and factions array).
* **The player faction** at `[fm+0x18]` — populated by `LoadModuleStart` in *all three*
  branches (save table via `ResolveFactionName("player")` `0x52B2BC`; 2DA; or
  `CreateDefaultFactions` `0x52BD28`), i.e. guaranteed after any successful module load.
  INFERRED non-null at our crash (no static path loads a module without it); the Fix-2
  probe confirms it live.

So `GetSingletonParty` returning garbage does **not** indicate a missing singleton
initialisation — it indicates a missing **membership** call, and that initialiser is
directly callable: `CSWSFaction::AddMember 0x5BFA70` (Fix 1), the exact call
`CSWSCreature::PostProcess` performs at `0x4F1D41` when the player faction is available.

## Crash-risk register

| Risk | Assessment |
| --- | --- |
| AddMember on a creature already in a faction | Safe — dedup at `0x5BFAA4`, removal from old faction first (VERIFIED). |
| AddMember before the creature is registered | Early-outs harmlessly (`0x5BFA8B`); cannot happen when the creature came from `GetPlayerCreature` (same lookup). |
| `[fm+0x18]` null at world-up | No static path produces it after a module load; fixup logs and aborts rather than passing null (NEEDS-LIVE confirmation via probe). |
| `SendFactionUpdateAdd` to a state-0/1 player | Retail sends it from PostProcess in the same window (`bSilent=0` at `0x4F1D3D`) — same exposure as retail (VERIFIED call shape). |
| Entry-guard patches (Fix 3) | Original bytes given above must verify before patching; both stubs resume at instruction boundaries (`0x5BF697`, `0x5BF5C6`); return values mirror the functions' own degenerate cases. |
| Doing nothing after PartyFactionGuard | Guaranteed crash: six `GetSingletonParty` call sites plus `GetLeader` at `0x575973` all pass an unchecked `GetFaction` result (VERIFIED). |

## What still needs a live pass (NEEDS-LIVE)

1. The Fix-2 probe values at world-up: `stats.faction_id`, `fm`, `[fm+0x18]`, faction
   count — pins which branch created the factionless creature (the one link static
   analysis could not close: every enumerated path *should* have produced a valid faction
   once factions are loaded, yet measurement says otherwise; prime suspects are a client
   state divergence in the `EndStartNewModule → LoginIFOCharacter` round-trip, or a
   `LoadCharacterFromIFO` failure leaving an earlier half-built creature in place).
2. That `AddMember` at world-up stops both measured crashes (party handler window and the
   update-writer window) with `PartyFactionGuard` left installed as a canary — its stub
   firing again would mean a second factionless creature source.
3. Whether the staged save's `TEMP:pifo` `Mod_PlayerList[0]` entry carries `FactionID`
   (inspectable offline with the C# game-file readers against the staged autosave).

## Key addresses

| Address | Symbol / meaning |
| --- | --- |
| `0x5BF690` | `CSWSFaction::GetSingletonParty` — faults at `+4` on null `this`; returns 1 for ≤1-PC faction |
| `0x5BF5C0` | `CSWSFaction::GetLeader` — next unguarded deref in the same block (`0x575973`) |
| `0x5BFA70` | `CSWSFaction::AddMember(objId, bSilent)` — **the fix call**; writes `stats.faction_id=[faction+0xC]` at `0x5BFAD2` |
| `0x57595A` | faulting call site in `WriteGameObjUpdate_UpdateObject 0x574A10` (bit-`0x4000` party block, `0x575923`) |
| `0x513FC0` | `CSWSCreature::GetFaction` — `stats+0x78` id → `CFactionManager::GetFaction` |
| `0x52B3B0` | `CFactionManager::GetFaction(id)` — null iff id out of `[fm+0x24]` range |
| `0x5ACEDA` | stats constructor: `faction_id = -1` (the default that survives to the crash) |
| `0x5B063D` | `ReadStatsFromGff` "FactionID" read — default = current value |
| `0x4F1C40` | `CSWSCreature::PostProcess` — PC branch `0x4F1D2D–0x4F1D46` silently skips AddMember if `[fm+0x18]` null |
| `fm+0x18` | player faction slot; set by `0x52B2BC` (save names), `0x52BD28` (defaults) |
| `0x10054` | `CServerExoAppInternal` → `CFactionManager*` |
| `0x4B3EC0` | `UpdateClientGameObjectsForPlayer` — state gate at `0x4B3F01` (`[player+0x24]`: 1 login-done, 2 area-acked) |
| `0x524BB0` | state 2 write (client "area loaded" ack, `HandlePlayerToServerAreaMessage`) |
| `0x4B5CC8` | state 1 write (`LoadCharacterFinish`) |
| `0x4B78F0` | `PlayerListChange` — CSWSPlayer created on connect (ctor `0x562C70`: state 0, `+0x80`=0, `+0x44`=-1) |
| `0x1007C` | `DoingStartNewModule` flag (`SetDoingStartNewModule 0x4AF090`); makes `RunModule` broadcast EndStartNewModule and skip LoadPrimaryPlayer |
| `0x56CC50` / `0x652860` / `0x678030` | EndStartNewModule send / client handler / client's `LoginIFOCharacte` (`70 02 0F`) reply |
| `0x5244E0` / `0x4B7470` / `0x561E30` | login dispatch (minor `0xF` needs `[player+0x80]!=-1`) / LoadCharacterStart / LoadCharacterFromIFO (index -1 = `TEMP:pifo`, staged by `LoadPCAutoSave` at `0x6CA604`) |
| `0x6775E0` | `RequestStatus` = `70 01 00` server-status query — **not** on the faction path |
| `0x4B2820` | `CServerExoApp::GetPlayerCreature` (already bound in `Odyssey.cs`) |
