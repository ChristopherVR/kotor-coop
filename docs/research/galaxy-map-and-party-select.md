# Galaxy map and pre-departure party-select in co-op (research + design, 2026-08-27)

Roadmap Tier 2 item 18: *"Galaxy map / pre-departure party-select have zero co-op awareness;
each instance runs the vanilla UI locally."* This doc answers, with evidence, how big that gap
actually is, what a co-op-aware design would need to do, and what story-forced solo segments
(Leviathan capture, Taris swoop, the turret sequences, the endgame) still leave uncovered once
it does. No code shipped this pass — this is RE + design, per the task brief; a few read-only
probe ideas are named but nothing is wired.

Tag key, same as every other doc here: **VERIFIED** = read directly out of the GOG v1.0.3
binary/symbol DB/game files/source in this pass, or already true of the shipped code · **INFERRED**
= a reasoned deduction from VERIFIED facts, not itself read · **NEEDS-LIVE** = the one thing that
would settle an INFERRED claim, unresolved because this pass had no rig access.

Evidence sources: `tools/kotor1_0_3.db` (symbol DB, queried with `tools/q.py`), `tools/disasm.py`/
`tools/xrefs.py` against the GOG install's `swkotor.exe`, and `tools/kotorfiles.py` reading
`chitin.key`/`data/scripts.bif` directly out of `C:\GOG Games\Star Wars KOTOR (GOG)` — KOTOR 1
ships the original NWScript **source** (`.nss`) alongside the compiled `.ncs` for its own game
scripts, so several of the claims below are read straight out of BioWare's own script text, not
reverse-engineered bytecode.

---

## 1. The galaxy map

### 1.1 The panel and its scripts — VERIFIED

`CSWGuiInGameGalaxyMap` (symbol DB: size `0x2550`, vtable `0x754910`) is the native panel,
reached off **`CGuiInGame+0x080 = in_game_galaxy_map`** (VERIFIED, `q.py offsets CGuiInGame`) —
non-null exactly while the map is on screen, the identical detection shape the plugin already
uses for the dialogue panel (`CGuiInGame+0x3C`, `Odyssey.Dialogue.Follow.cs`) and, not
coincidentally, for the party-select panel below. Relevant offsets (`q.py offsets
CSWGuiInGameGalaxyMap`):

| Offset | Field | Type |
| --- | --- | --- |
| `0x064` | `planet_buttons` | `CSWGuiButton[16]` |
| `0x1F24` | `accept_button` | `CSWGuiButton` |
| `0x20E8` | `back_button` | `CSWGuiButton` |
| `0x23CC` | `planets` | `GuiInGameGalaxyMapPlanet[16]` |
| `0x254C` | `current_planet` | `int` |

Native functions on the class: `OnPlanetClicked 0x006935A0`, `NextPlanet`/`PrevPlanet`,
`DisplayPlanet`, `Draw`, `HandleInputEvent`. `CSWPartyTable` itself carries the map's data model
(`q.py offsets CSWPartyTable`): `available_planets int[16]` at `+0x060`, `selectable_planets
int[16]` at `+0x0A0`, `galaxy_map_selected_planet int` at `+0x0E0` — read/written by the NWScript
VM commands `GetGalaxyMapPlanetAvailable/Selectable`, `SetGalaxyMapPlanetAvailable/Selectable`,
`GetSelectedPlanet` (`CSWVirtualMachineCommands::ExecuteCommand*`, all VERIFIED in the symbol
DB).

**Opening the map** — `k_pebn_galaxy.nss` (VERIFIED, full source read via `kotorfiles.py extract
k_pebn_galaxy.nss`): sets `SetPlanetAvailable`/`SetPlanetSelectable` per the story flag
`K_KOTOR_MASTER`, then calls `ShowGalaxyMap(nPlanet)` — NWScript action → `CSWVirtualMachineCommands
::ExecuteCommandShowGalaxyMap 0x005467B0` → `CGuiInGame::ShowGalaxyMapGui 0x0062D040`.

**Confirming a destination** — `k_sup_galaxymap.nss` (VERIFIED, full source read; there is also
an older sibling `k_sup_galaxmap.nss`, same header, simpler body — read too). Its own header calls
it *"Universal Script that fires when the galaxy map is used"*; it reads `GetSelectedPlanet()`,
re-locks the just-confirmed planet, sets `K_FUTURE_PLANET`, applies a few story-specific
Bastila-availability rules, and then dispatches to exactly one of several `ST_Play*` functions in
`k_inc_stunt.nss` (also VERIFIED, full source, 774 lines) depending on which story beat is active.
Every one of those functions ends in a native NWScript **`StartNewModule(module, waypoint,
movie1..movie6)`** call. The ordinary case (`ST_PlayPlanetToPlanet`):

```
StartNewModule("ebo_m12aa", "", ST_GetTakeOffRender(), "08", ST_GetLandingRender());
```

`ebo_m12aa` is the Ebon Hawk's normal module — **VERIFIED**, `module.ifo`'s `Mod_Name` reads
`"Ebon Hawk"` for `ebo_m12aa` (and again for `ebo_m41aa`, a later-story Ebon Hawk instance;
`ebo_m40aa`, the Leviathan-era instance, has no localized name). `ST_GetTakeOffRender()`/
`ST_GetLandingRender()` pick the take-off/landing `.bik` resref **at runtime** from a `K_CURRENT_
PLANET`-keyed table (`k_inc_stunt.nss` lines ~444–579) — this is the "runtime-selected movie
name" case `cinematics.md` §3.2 already flags as unfindable by any static string scan, and it is
exactly why that doc's design reads the engine's own queue rather than predicting names.

**This is the same `StartNewModule` the mod's own travel machinery already targets.**
`CServerExoAppInternal::StartNewModule 0x004BA920` (called via `ExecuteCommandStartNewModule
0x00544390` for the NWScript case above, or via the engine's own pending-flag poll for the
plugin's `Odyssey.TravelToModule`/`TravelController.Execute` path — VERIFIED, read
`src/KotorCoop.Plugin/Odyssey.Travel.cs:94-126`: it sets `move_to_module_pending`/the module and
waypoint strings and lets `MainLoop` run the transition, "because this is exactly what the
shipped `warp` cheat calls") is the one function every module change in this game funnels
through. `cinematics.md` states this outright: *"the co-op warp path lands here too."*

### 1.2 What is ALREADY covered for free, and what is not — VERIFIED by reading, not assumed

**The movies are already covered.** `CinematicWatch.OnFrame` (`src/KotorCoop.Plugin/
CinematicWatch.cs:139-220`) polls `Odyssey.QueuedTransitionMovieCount()` **every tick**, on
`MainLoopHook` — not gated on a door, not gated on `trans_pending`, not gated on any particular
trigger. The instant the count GROWS (any `StartNewModule` call queuing movies, from any source —
a door, `k_sup_galaxymap`'s `ST_Play*` dispatch, a stunt sequence), it reads the new names off
`move_to_module_movies` and calls `block.PublishCinematic(...)` — the write side of the
`TransitionMovies` wire packet (`PacketType.TransitionMovies = 40`) a follower's own warp already
carries forward (`TravelController.cs:221-226`). **A galaxy-map departure's take-off/landing
movies are therefore already captured and relayed to the follower today, by the existing,
already-ON-by-default `TransitionMovies` channel — VERIFIED by reading the poll, no new code
needed for this half.**

**The EARLY announce is NOT covered, and this is the real gap.** `TransitionAnnounceWatch.
OnTransitionBegan` — the hook that cuts the ~5.6-5.8s "wait for the host's published module to
catch up" latency down to ~180ms (`transition-smoothness.md` §8.2, live-measured) — fires on
exactly two edges, neither of which a galaxy-map departure trips:

1. The rising edge of `Odyssey.IsAreaTransitionPending()` (`CSWSArea::trans_pending` at
   `area+0x2C4`) — **door-transition-specific.** `cinematics.md` states plainly that this flag
   "cannot appear on a co-op warp at all," and by the same reasoning it cannot appear on ANY
   NWScript-triggered `StartNewModule` call either — `SetTransitionPending` is written only by
   `CSWSDoor::EventHandler`'s own two phases (`0x0058C230`/`0x0058C185`), not by
   `CServerExoAppInternal::StartNewModule` or its NWScript entry point.
2. `TravelController.Execute`, for the plugin's OWN `Odyssey.TravelToModule` calls (pulling a
   follower along, or the `moviewarp`/`warp` debug verbs) — **mod-initiated only.**

A galaxy-map departure (or any other story-script `StartNewModule` — a stunt chain, the Leviathan
capture, the endgame) is neither. **INFERRED, not yet live-measured this pass**: without a fix,
the follower falls back to `PumpTravel`'s slow, module-diff-based path, with its full
`ModuleSettleMs` (5,000 ms) debounce — i.e. every interplanetary/story transition still pays
close to the FULL latency the announce feature was built to eliminate for doors, even though the
door case is already fixed.

**The fix is cheap and reuses code that already exists.** `TransitionAnnounceWatch` already has
everything it needs except a third trigger edge: `CinematicWatch`'s own "the queue just grew"
poll is the exact edge to watch (it already runs every tick, already survives the one-frame
window before a queued movie can stall the thread). Wiring `TransitionAnnounceWatch.
OnTransitionBegan`'s door-sweep call to ALSO fire on that edge — with no linked door nearby, it
would fall through to the existing "no linked door found... the fallback module-change detection
will still carry the follow" log path, which is exactly correct: there is no door for the
Ebon Hawk's own case, only a destination the plugin can still resolve from the pending
`move_to_module_string`/`move_to_module_start_waypoint` fields the engine has ALREADY written by
the time the queue-growth edge fires (same fields `Odyssey.TravelToModule` itself writes, just
read instead of written). **This is Q4's #1 recommendation.**

### 1.3 What the client should do while the host is on the map — INFERRED, NEEDS-LIVE

Opening the galaxy map is an in-world GUI overlay, not a menu/load-screen state — the player's
`CSWSObject` stays live in the current area (the Ebon Hawk), unlike the main menu, where the
world genuinely does not exist. `AwayRule.IsAway` (`src/KotorCoop.Session/AwayRule.cs`) triggers
off **consecutive read failures**, and nothing about `CGuiInGame+0x080` being non-null should
cause a read failure — the player creature, position and module all keep resolving normally.
**INFERRED**: a peer watching a host on the galaxy map should see exactly what is already true —
a stationary phantom, standing wherever the host's PC stood when they opened the map — with no
new "park" mechanism needed, the same way a peer already sees a stationary phantom for any other
menu the host has open (inventory, character sheet) without anything special happening.
**NEEDS-LIVE**: confirm the read chain genuinely stays healthy with the map open (no field goes
stale/poisoned the way a load screen's does) — cheap to check with `pluginstate.ps1`/
`partystate.ps1` against a live session with one side sitting on the map.

**Should the CLIENT'S own map be blocked while the host is on theirs?** Nothing forces this: the
map is a personal-instance GUI panel with no shared-world side effect until `ConfirmPartySelection`
actually calls `StartNewModule` (§2.1). A client opening their own map concurrently costs nothing
and resolves nothing — it can only ever cause a problem at the SAME point party-select already
needs a co-op rule (§2.3): whose confirmed destination wins if both open the map. **Recommendation**:
do not block the client's own map from opening; gate co-op behavior at the confirm/`ConfirmPartySelection`
edge instead (§2.3, §4), the same single choke point the design already needs for party-select.

---

## 2. The pre-departure party-select screen

### 2.1 The panel, and what it writes — VERIFIED

`CSWGuiPartySelection` (size `0x3A80`, vtable `0x756D28`), reached off **`CGuiInGame+0x078 =
party_selection`** — the exact offset the mod's own dialogue-follow code once misread as the
dialogue panel and had to fix (`Odyssey.Dialogue.Follow.cs`'s own doc comment: *"This was 0x78,
which is the PARTY SELECTION panel, and the mistake was silent and expensive... The symbol
database agrees — it names +0x78 party_selection"*) — so this exact detection point is not a
guess, it is a previously-paid-for correction. Relevant offsets:

| Offset | Field | Type |
| --- | --- | --- |
| `0x074` | `exit_script` | `CExoString` — the script run after confirm |
| `0x07C` | `party_data[9]` | `CSWGuiPartySelectionData[9]` — one row per companion slot |
| `0x2770` | `count_label` | how many are currently picked |
| `0x28B0` | `done_button` | |
| `0x38B8` | `accept_button` | |

`CSWGuiPartySelectionData` (`0x454` each): `npc_button` (a `CSWGuiPartySelectionButton`,
toggle-select), `portrait_label`, `character_label`, `npc_index int` — pure UI decoration over
the underlying `CSWPartyTable` state, not a second copy of the roster.

**The panel does NOT write the roster directly. It writes through the same two calls a
dialogue-driven recruit already uses.** `CSWGuiPartySelection::ConfirmPartySelection 0x006BEC90`
is the confirm handler — VERIFIED as the button's bound entry point by its own call graph:
`xrefs.py 0x006BE560` (AcceptParty) shows a caller at `0x006BECAE`, 30 bytes into
`ConfirmPartySelection`'s own body, and `ConfirmPartySelection`'s own address appears as a
literal at `0x006BF55F` — the shape of an `AddEvent`-bound button handler, the identical pattern
`native-browser-panel.md` already verified end-to-end for a different panel (`AddEvent
0x0041AB20(control, eventId, owner, fn)`, same-id-replaces). **INFERRED** (not itself
disassembled this pass) that `done_button`/`accept_button` are the two controls bound this way —
consistent with the field names but not traced instruction-by-instruction.

`ConfirmPartySelection` calls `AcceptParty 0x006BE560`, which — per `party-reconciliation.md`'s
own already-verified disassembly — calls `CSWPartyTable::SpawnNPC` then `CSWPartyTable::AddMember
0x00565620` per chosen companion. `AddMember`'s own refusal order (already documented,
re-confirmed here): refuses if `+0xF0 != -1` (a script has one companion under direct control —
the roster-freeze case), refuses if `pt_num_members (+0x000) == 2` (**two companions is the
engine's own ceiling** — see §3), refuses `nNPC` outside 0..8, refuses if `+0x30+4*nNPC == 0`
(not recruited/available). On success it writes `pt_member_ids[+0x004+4*count] = nNPC` and
`pt_controlled_npc-adjacent object slot [+0x0C+4*nNPC] = oCreature`. This is **the exact same
write** the roster reconciliation channel already watches.

After `AcceptParty`, `ConfirmPartySelection` runs the panel's `exit_script` (`+0x074`) — confirmed
live in the actual game data: `k_pebn_loc01.nss` (VERIFIED source) calls
`ShowPartySelectionGUI("k_pebn_swoop")`, and `k_pebn_swoop.nss` (VERIFIED source, 245 lines)
reads `K_CURRENT_PLANET` and dispatches to a `StartNewModule(module, waypoint)` call picked from
a hard-coded table of intra-planet destinations (9 entries for Taris alone, matching its own
9 swoop-registration triggers).

### 2.2 A correction to the task's own framing — VERIFIED, worth stating plainly

**`ShowPartySelectionGUI` was NOT found in any of the interplanetary galaxy-map departure paths
read this pass.** `k_sup_galaxymap.nss`/`k_sup_galaxmap.nss` (the galaxy map's own confirm
script) and every `ST_Play*` function in `k_inc_stunt.nss` it can reach go straight to
`StartNewModule` — none of them call `ShowPartySelectionGUI`. The only confirmed caller found is
`k_pebn_loc01.nss` (and, by naming pattern, its eight siblings `k_pebn_loc02`..`09`), whose own
header says *"Sets the location for the drop off and brings up the party selection screen"* — this
is the **Taris swoop-taxi dispatch** (the same system `minigames.md`'s multi-round swoop-race
investigation already covers extensively), not a screen shown on every planet arrival. So: the
task brief's framing ("party-select... when leaving the Hawk, story-forced") is **broader than
what this pass's source reading actually confirms**. What IS confirmed is narrower and still real:
party-select is a genuine, reachable screen (used at least 9 times on Taris), built on a fully
understood panel/roster-write mechanism (§2.1) that generalizes to wherever else it fires. This
pass could not exhaustively prove or disprove other callers elsewhere in the game — only the ~40
`k_pebn_*`/`k_inc_stunt`/`k_inc_ebonhawk` scripts with shipped `.nss` source were read; the
remaining game scripts exist only as compiled `.ncs` and were not decompiled. **NEEDS-LIVE/
further static work**: a broader sweep (the `NcsReader` disassembler `minigames.md` §13 already
built for the swoop-race investigation could, in principle, be pointed at every script calling
NWScript action 543 — `ShowPartySelectionGUI`'s own VM index — to enumerate every real caller
without needing a live game). Whatever the true frequency, the co-op design in §2.3 works
identically regardless: it hooks the panel/its confirm call, not any particular script.

### 2.3 The co-op rule — design, built on §2.1's verified mechanism

**Host picks; the client's own screen is suppressed and auto-confirmed with the host's choice.**
Read-only-display was considered and rejected: it would need the client to render the HOST's
`CSWGuiPartySelectionData` state inside the client's own local panel instance, which means either
faking input events into the client's own `CSWGuiPartySelectionButton` toggles (fragile — the
panel has real per-slot state this pass did not trace) or building a whole second read-only
rendering path — much more surface than intercepting one button.

Two building blocks, both already proven live for other panels in this codebase:

1. **Detection**: `CGuiInGame+0x078` non-null (§2.1) is the trivial per-frame check for "the
   local party-select panel is open" — no new engine reads needed, the same cost class the
   dialogue-follow detection already pays.
2. **Interception**: `AddEvent 0x0041AB20(control, eventId, owner, fn)` same-id-**replaces** an
   existing binding — `native-browser-panel.md` verified this exact ABI end-to-end (disassembled,
   not just inferred) for `CSWGuiSaveLoad`'s buttons, and the same primitive applies to any
   `CSWGuiControl`, `CSWGuiPartySelectionButton`/`done_button`/`accept_button` included.

The design: on a CLIENT, the moment `CGuiInGame+0x078` goes non-null, rebind (or immediately
fire, bypassing the human) `ConfirmPartySelection`'s own call with the roster ALREADY implied by
the wire — because of the point made in §2.4 below, the client does not even need to read the
panel's own toggle state; it can drive `AddMember`/roster-matching calls directly from the
already-synced `party_roster_sync` masks and simply close the panel. **NEEDS-LIVE**: whether
suppressing the human's own toggles this way (rather than letting them click and overriding on
confirm) reads as "the screen didn't respond" versus "the screen closed itself" — a one-line
chat/on-screen notice ("host is choosing the party") is the cheap mitigation, same shape as
`transition_announce`'s own on-screen line.

### 2.4 The roster-seat sync ALREADY WORKS for this, for free — VERIFIED

**This is the single most important finding in this section.** `PartyRosterController`
(`party-reconciliation.md`, already shipped, `party_roster_sync=1` ON by default in
`profiles/coop.ini.example`) publishes a 9-bit "in-party" mask **every tick** and reconciles a
peer's own roster against it **every 30 frames**, unconditionally — it does not care WHY the
host's roster changed. Whether a companion enters the party via ordinary dialogue recruitment or
via `CSWGuiPartySelection::AcceptParty`'s own `SpawnNPC`+`AddMember` pair, the write lands in the
exact same `CSWPartyTable` fields (`pt_member_ids`, the count) this channel already watches.
**So the moment the host confirms a party-select screen, the client's own roster reconciles to
match within one 30-frame pass — with zero new code, riding the channel that already ships ON.**
The only genuinely new work this section needs is §2.3's screen suppression (so the client is not
ALSO shown a redundant, potentially-conflicting confirm prompt of their own) — the DATA sync is
already solved.

`dialogue_recruit_sync=1` (also ON by default) is the sibling half: it already handles the
AVAILABILITY bit (`pt_avail_npcs+0x30+slot*4`, the flag `GetIsNPCAvailable` reads and the one a
party-select screen's own roster is built from) the same way, via `Odyssey.AddAvailableNpcByTemplate`
— so even a companion that only just became selectable (a `SetNPCSelectability` story gate lifting)
crosses the wire before a party-select screen showing it would ever need to.

### 2.5 The "two PCs + N companions" math — VERIFIED, and it resolves cleanly

`AddMember`'s own refusal (`+0x00 == 2` → refuse, §2.1) confirms **the engine's own cap is TWO
companions**, tracked in `pt_member_ids`/`pt_num_members` — a value that does NOT include the
controlling PC. A phantom (a remote peer's on-screen body) is **not a party member at all**:
`PhantomController.TrySpawnOne` (`src/KotorCoop.Plugin/PhantomController.Lifecycle.cs:129-262`)
spawns it via `Odyssey.SpawnCreature` — a bare `CreateObject`-style blueprint spawn positioned at
the peer's own coordinates, then `Odyssey.ClaimCreature`/`Odyssey.MarkAsRemotePlayer` — **at no
point does phantom creation touch `CSWPartyTable` in any way.** It is a free-standing creature the
engine has no concept of as a party member.

**This settles the math cleanly, and corrects the task brief's own tentative framing.** The
client's phantom on the host's screen does **not** occupy one of the host's two companion slots.
The host still gets the full, ordinary choice of 2 companions on a party-select screen; the
client's phantom is a THIRD, extra body the party-select screen's own model has no awareness of
and no interaction with. On the host's own instance: PC + 2 chosen companions + 1 client phantom
= 4 visible bodies. On the client's own instance (mirrored/suppressed per §2.3): client's own PC
+ 2 mirrored companions (driven per `companion_local_combat`'s existing mirror machinery) +
1 host phantom = 4 visible bodies. Symmetric, and nothing about it strains the engine's 2-companion
ceiling because the second human was never asking that ceiling for anything.

---

## 3. Story-forced solo segments

Every module/script name below was read directly out of `k_inc_stunt.nss`/`k_inc_ebonhawk.nss`
(VERIFIED source) or the module files themselves (VERIFIED via `kotorfiles.py gff`), unless
marked otherwise.

### 3.1 Leviathan capture — `ST_PlayLevCaptureStunt`/`02`, `ebo_m40aa`

```
ST_PlayLevCaptureStunt():   StartNewModule("STUNT_16", "", sRender, "08")
ST_PlayLevCaptureStunt02(): StartNewModule("ebo_m40aa", "", "17")
```

A `STUNT_*` module is exactly the case `cinematics.md` §4.3 already names and the mod already
handles: **`CoopAgent`'s follow logic deliberately refuses to follow a peer into a `stunt*`
module**, and instead **accumulates** every movie queued while declining, handing the whole set
to whatever real transition it finally does follow into (VERIFIED — this is shipped, tested
behavior, `cinematics.md` §9.4). So the partner does not watch the capture cinematic personally,
but does NOT desync or get stuck either: their own eventual warp into `ebo_m40aa` carries the
same movie names, and — per §1.2 above — the `TransitionMovies` channel that carries them needs
no new code for a script-driven `StartNewModule`. **What is NOT covered**: `party-reconciliation.md`'s
already-documented roster-freeze guard (`pt_controlled_npc +0xF0 != -1`, "the Leviathan brig, the
Rakatan temple, any forced-solo beat") stops a MIRRORING instance's companion-drive code from
fighting a script that has taken one companion under direct control — but that protects the
LOCAL engine's own companion body from double-driving, it does nothing for the OTHER human
player's EXPERIENCE of the sequence. If the capture happens on the HOST's engine while the
partner is elsewhere in the same module (not a stunt-module case, an in-place scripted sequence),
the partner has no dedicated spectate/notice channel today beyond the generic phantom mirror —
**Missing**, not built, not designed this pass.

### 3.2 Taris swoop system — `k_pebn_loc01`..`09`, `k_pebn_swoop`, `tar_m02ab`/`tar_m02ac`/`tar_m03aa`/`tar_m04aa`

This is the SAME system §2.1/§2.2 already traced: 9 swoop-registration triggers around Taris, each
calling `ShowPartySelectionGUI("k_pebn_swoop")`, then `k_pebn_swoop.nss` reading which
`UT_GetPlotBooleanFlag` was set and dispatching `StartNewModule` to one of 9 destinations. `§2.3`'s
"host picks, client suppressed/auto-confirmed" design covers the party-select half. The actual
SWOOP MINIGAME (racing) is a **separate, already extensively investigated subject**
(`minigames.md`, 2800 lines, roadmap Tier 3 item 21): **confirmed live** (§9.1 of that doc) to be
an **in-place mode switch inside the SAME module**, not a module transition — `CSWCArea::mini_game`
going non-null starts `CSWMiniGame::Update` running inside the current area's own frame loop, with
no `travel:`/`epoch: module changed` anywhere near it. That means co-visible swoop racing is **not
structurally precluded** the way the turret sequence is (§3.3) — but `minigames.md`'s own repeated,
multi-round conclusion is that the actual race TRIGGER (`k_ptar_bekrace.ncs`'s own delayed
module-transition action) still does not reliably fire live, for reasons several rounds of static
disassembly narrowed but did not close (§16-20 of that doc) — an existing, unresolved, unrelated
bug this design does not need to fix or work around, since it blocks single-player too. Today's
shipped behavior — hide/park the peer for the duration (`MinigameController`, roadmap item 9) — is
the safe default and is unaffected by anything in this doc.

### 3.3 Turret sequences — `m12ab` ("Ebon Hawk Turret"), reached from `ST_PlayStuntTurret_07_3`/`07_4`, `ST_PlayLeviathanEscape01`, `ST_PlayPostTurret`

**Correction to the task brief: there is no Manaan/Tatooine turret sequence in KOTOR 1.**
`chitin.key`'s own script/model list (VERIFIED, `kotorfiles.py key --find turret`) shows exactly
one turret-gunnery module, `m12ab` — `module.ifo`'s `Mod_Name` reads `"Ebon Hawk Turret"`
(VERIFIED) — reached only from Ebon Hawk stunt sequences (the Taris departure and the Leviathan
escape, `k_inc_stunt.nss` lines 124-131/193-196). The only planet-specific turret-flavored script
found is `k_ren_unkturret` (Unknown World, i.e. the Rakatan planet approaching the Star Forge) —
not decompiled this pass, named here as a lead, not a confirmed second turret module.

`minigames.md` §9.2 already answers this case **live, definitively, not inferred**: `m12ab`'s
own `Mod_OnModLoad = k_pebo_mgload` auto-starts the minigame the instant the module finishes
loading, with no interaction required — confirmed by watching BOTH instances independently log
`minigame: entered` the moment each one's own engine finished loading the module. **Spectate/park
is not a design choice here, it is the only possible outcome**: because merely walking into
`m12ab` puts a player into minigame mode on their OWN engine, there is no state a peer could
occupy that is "present but not playing." The existing park mechanism (hide the peer's phantom,
confirmed live to engage within 20ms, degrade cleanly even through a hard process crash) already
covers this correctly and needs no change.

### 3.4 Star Forge finale — `ST_PlayStarForgeApproach` (`STUNT_34`), `ST_PlayBastilaTorture` (`STUNT_18`), `ST_PlayUnknownWorldTakeOff`/`Landing` (`STUNT_44`/`STUNT_42`, `ebo_m41aa`), `ST_PlayStarForgeLanding` (`ebo_m12aa`)

Every one of these is, mechanically, the same shape as §3.1: a `STUNT_*` module reached via
`StartNewModule`, already covered by the existing stunt-gate + movie-accumulation mechanism
(§1.2/§3.1) for the CINEMATIC half. What this pass did not find or investigate (out of scope for
a static read, and the task named it without a specific mechanism to check) is whether the actual
Star Forge DUNGEON and the Malak confrontation itself force any party-composition change beyond
what `party-reconciliation.md`'s already-documented roster-freeze guard covers — no `ShowPartySelectionGUI`
call was found inside `k_inc_stunt.nss` on this path, so if the finale changes the party, it does
so through ordinary `AddNPC`/`RemoveNPC`/dialogue-recruit calls the existing §2.4 channel already
carries, not through a party-select screen. **Missing/NEEDS-LIVE**: whether the finale forces
`pt_solomode` (`+0x190`) at any point — `party-reconciliation.md` documents that field is agent-
owned (`KotorGameBridge.TryWritePartyMembers`/`ReleasePartyFreeze`) and re-asserted every pump,
independent of this doc's subject, so if it fires here it is already the same solo-mode machinery
the rest of the game uses, not a new case.

---

## 4. Ranked implementation plan

| # | Item | Size | Rig? | What needs live verification |
| --- | --- | --- | --- | --- |
| 1 | **Extend `TransitionAnnounceWatch` to fire on `CinematicWatch`'s "queue grew" edge, not just the door's `trans_pending` edge.** Closes the real gap in §1.2: every script-triggered `StartNewModule` (galaxy map, stunt chains, the endgame) currently gets the SLOW 5s-debounced follow instead of the ~180ms announced one doors already have. Reuses `CinematicWatch`'s existing per-tick poll and `TransitionAnnounceWatch`'s existing "no door found, fall back to module-change detection" path — mostly wiring, not new mechanism. | S | 🎮 (measure the latency drop the same way `transition-announce-measure.ps1` already does for doors) | Whether the announce publish can safely read `move_to_module_string`/`move_to_module_start_waypoint` on the SAME edge `CinematicWatch` fires on (both are engine fields already written by the time the queue grows) — one live galaxy-map departure with both instances' logs |
| 2 | **Party-select suppression on the client (§2.3)**: detect `CGuiInGame+0x078` non-null, and either rebind `ConfirmPartySelection`'s bound button (the `AddEvent`-replace technique `native-browser-panel.md` already proved) or drive the roster directly from the already-synced masks and close the panel. The DATA sync (§2.4) needs no new code at all — this item is purely "don't show the client a redundant/conflicting prompt." | M | 🎮 (needs a live Taris swoop-registration trigger; §2.2's `NcsReader`-based sweep can find a second call site to test against without a live game) | Whether the human's own already-clicked toggles on the client conflict with the auto-drive; whether the on-screen "host is choosing" line reads cleanly against the panel closing itself |
| 3 | **A short, static `ShowPartySelectionGUI` caller sweep** using the `NcsReader` disassembler `minigames.md` §13 already built (point it at NWScript action index for `ShowPartySelectionGUI`, scan every shipped `.ncs`) to settle §2.2's open question — how many places in the real game actually show this screen, beyond the 9 Taris triggers found by source-reading alone. Pure static work, no rig. | S | No | N/A — this IS the verification step for an existing INFERRED gap |
| 4 | **Galaxy-map "your partner chose X" notice** — once item 1 ships, the announce already carries the destination early; add the same on-screen line `transition_announce`'s door case already has ("following `<host>` to `<module>`"), reusing `ModuleDisplayName`. Trivial once item 1 lands. | S | 🎮 (cosmetic only) | Wording/timing against the map's own 0.5s+ confirm flow (no fade window here the way a door has one, so timing may read differently) |
| 5 | **Leviathan-capture / in-place-sequence partner notice (§3.1)** — when a `pt_controlled_npc` roster-freeze fires WITHOUT an accompanying stunt-module transition (i.e. the capture cutscene plays in the SAME module the partner might still be standing in), give the partner some indication beyond the generic phantom mirror. Smallest-scoped version: a chat line, same shape as the minigame "partner is in a minigame — waiting" notice already shipped. | S | 🎮 | Whether this case (in-place, non-stunt-module roster freeze) is actually reachable, or whether every real forced-solo beat in this game happens to route through a stunt module already (making this item unnecessary) — needs a live walkthrough of the actual Leviathan brig sequence, not a synthetic trigger |

Nothing in this plan touches an offset this pass did not already read out of the symbol DB or
disassembly; items 1 and 2 are the only ones that write new engine-facing code, and both reuse
call shapes (`AddEvent`-replace, the pending-move-to-module fields) already verified live for
other panels/paths in this codebase.

---

## Summary

The galaxy map and party-select screens both fire through the SAME engine mechanism the mod's
existing travel/movie/roster channels already target — `CServerExoAppInternal::StartNewModule`
for travel, `CSWPartyTable::AddMember` for roster changes — which means most of the "co-op
awareness" this item asks for is **already there for free**: the `TransitionMovies` channel
already relays a galaxy-map departure's take-off/landing movies (verified by reading its
trigger-agnostic poll), and the `party_roster_sync`/`dialogue_recruit_sync` channels already
propagate a party-select confirm's roster change to a peer within one reconciliation pass. The
two real gaps are narrower than the roadmap line suggested: (1) the fast `TransitionAnnounce`
path — the thing that makes doors feel instant — simply never got wired to the non-door
`StartNewModule` trigger a galaxy-map/story departure uses, so those transitions still pay close
to the full pre-optimization latency; and (2) nothing suppresses the CLIENT's own redundant
party-select prompt, so today two players would each get their own, independently-clickable copy
of the screen with no coordination between them. Story-forced solo segments mostly resolve
through mechanisms already built and live-verified for other reasons (the stunt-module follow
refusal + movie accumulation, the turret module's unavoidable auto-park) — the one place with a
real, unbuilt gap is an in-place (non-stunt-module) forced-solo beat, if one exists, which needs
a live walkthrough to even confirm is reachable.

Also worth flagging plainly: this pass found the task's own framing overstated in two places —
"Manaan/Tatooine turrets" do not exist in KOTOR 1 (only the Ebon Hawk's own `m12ab`), and
party-select is not shown "when leaving the Hawk" on every departure — the only confirmed
trigger is the Taris swoop-taxi system. Both corrections are evidence-backed (§2.2, §3.3), not
guesses, and neither changes the design in §2.3/§4: it hooks the panel and the roster-write
mechanism directly, so it works correctly regardless of how often the screen actually fires.

### Top 3 recommended next steps

1. **Ship item 1** (extend the announce hook to the non-door `StartNewModule` edge) — smallest,
   highest-value, and the live-test harness (`transition-announce-measure.ps1`) already exists to
   measure it.
2. **Run item 3** (the static `ShowPartySelectionGUI` caller sweep) before building item 2's
   suppression UI — it is cheap, needs no rig, and settles exactly how much of the game item 2's
   design actually has to cover.
3. **Take item 2 to the rig** once item 3 narrows the target list, using the Taris swoop triggers
   as the first live test case since they are already confirmed reachable and exercised
   extensively by `minigames.md`'s own navigation tooling (`walkto`, the tp-hop pattern).
