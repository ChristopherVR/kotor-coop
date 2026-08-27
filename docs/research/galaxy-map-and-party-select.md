# Galaxy map & pre-departure party select — research + design (2026-08-27)

Roadmap `Tier 2` item 18: *"Galaxy map / pre-departure party-select have zero co-op awareness;
each instance runs the vanilla UI locally."* This doc answers, with evidence pulled from the
real GOG v1.0.3 install (`C:\GOG Games\Star Wars KOTOR (GOG)`) and the symbol database
(`tools/kotor1_0_3.db` via `tools/q.py`), how both screens actually work, what the mod's
existing channels already give it for free, and what a co-op design should build. No code was
written; this is a research + design pass only.

**Methodology note**: this doc reconciles two independent readings of the same game files,
run in parallel and cross-checked against each other where they diverged. Both passes read
real shipped NWScript **source** (`.nss`, not just compiled `.ncs` — KOTOR 1 ships both for
its own scripts) via `tools/kotorfiles.py extract`. One divergence was a real error (a
commented-out `StartNewModule` line quoted as if live) and is corrected in §1.3 with the note
explaining what the mistake was; the rest were complementary half-pictures that combine into
one complete, cross-verified chain — noted inline where relevant. Treat the fact that two
independent passes converged on the same offsets/function addresses (`CGuiInGame+0x78`/`+0x80`,
`AcceptParty 0x6BE560`, etc.) as itself corroborating evidence.

Tag key, matching every other doc in `docs/research/`: **VERIFIED** = read from disassembly,
the symbol database, or actual game files this pass; **INFERRED** = a reasoned deduction from
verified facts, not itself read directly; **NEEDS-LIVE** = the claim only a running two-instance
session can settle.

## Headline

**Both screens are ordinary `CGuiInGame` panels** (`+0x080 in_game_galaxy_map` →
`CSWGuiInGameGalaxyMap`, `+0x078 party_selection` → `CSWGuiPartySelection`), opened by plain
NWScript calls the same way every other in-game panel opens — not a menu-level state change,
not a load, not a pause. World state stays readable the whole time a peer has one open, so
`AwayRule`'s read-failure detector never fires for either screen; a new, cheap wire flag
(mirroring the shipped `PlayerState.FlagInMinigame` pattern) is the right tool.

**The full Ebon Hawk departure chain is now traced end to end, in two legs, both VERIFIED by
reading real shipped script source**: confirming a planet on the galaxy map (`k_sup_galaxymap.nss`)
never transitions the player directly to a planet's surface — **every single dispatch path in
`k_inc_stunt.nss` ends back inside a fresh `ebo_m12aa`/`ebo_m40aa`/`ebo_m41aa` (an Ebon Hawk
instance), or a `STUNT_*` cutscene module that itself eventually returns to one** — with
`K_CURRENT_PLANET` updated to match the choice. Only afterward, walking through the ship to its
exit corridor (`ebn12_ebonexit`→`k_pebn_leavhawk`, then `ebn12_ebonleav`→`k_pebn_exithawk`),
does a second, separate `StartNewModule()` dispatch — keyed off the now-updated
`K_CURRENT_PLANET` — send the player to the real planet-surface module. **Neither leg is a door
crossing.** Both are script-driven, invisible to the mod's existing door-only announce hook.
That is this doc's single most actionable finding: extending the hook to the engine's own
universal transition flag (`CServerExoAppInternal+0x10080`, already read/written by the mod's
own `TravelToModule`) is a small, high-leverage fix that covers *both* legs — and every other
non-door `StartNewModule()` transition in the game (Leviathan capture, Star Forge stage
transitions, any custom mod content) — with one change.

**The take-off/landing movies riding leg 1 are already relayed today, verified by reading the
poll, no new code needed.** `CinematicWatch.OnFrame` polls the engine's own queued-movie count
every tick, trigger-agnostically — it does not care whether a door, a galaxy-map confirm, or a
stunt chain grew the queue.

**Party-select needs no new engine mechanism at all.** Its GUI writes through the exact
`CSWPartyTable::SpawnNPC`/`AddMember` pair `PartyRosterController` already replicates for
recruitment, via a **poll**, not a hook — so a party-select-driven swap is, to the existing
roster-mirroring channel, indistinguishable from any other roster change. **Its only confirmed
trigger this pass found in shipped source is the Taris swoop-taxi system** (`k_pebn_loc01`..`09`
→ `ShowPartySelectionGUI("k_pebn_swoop")`), not every interplanetary departure — a correction to
this doc's own working assumption partway through, kept here because the design in §2.4 works
identically regardless of how often the screen actually fires. The two-humans math holds: a
client's PC is a phantom (`Odyssey.SpawnCreature`, a raw blueprint spawn), never a
`CSWPartyTable`/`CSWParty` member, so it costs the host's own instance zero party slots.

## 1. Galaxy map

### 1.1 The panel and its host class — VERIFIED

`CSWGuiInGameGalaxyMap` (symbol DB: size `0x2550`, vtable `0x754910`), reached from
`CGuiInGame+0x080` (`in_game_galaxy_map`) — non-null exactly while the map is on screen, the
identical detection shape the plugin already uses for the dialogue panel
(`CGuiInGame+0x3C`, `Odyssey.Dialogue.Follow.cs`). Offsets (`tools/q.py offsets
CSWGuiInGameGalaxyMap`):

| Offset | Field | Type |
| --- | --- | --- |
| `0x000` | `galaxymap_panel` | `CSWGuiPanel` (base) |
| `0x064` | `planet_buttons` | `CSWGuiButton[16]` |
| `0x1CA4`/`0x1DE4` | `planetname_label`/`description_label` | `CSWGuiLabel` |
| `0x1F24` | `accept_button` | `CSWGuiButton` |
| `0x20E8` | `back_button` | `CSWGuiButton` |
| `0x22AC`/`0x233C` | `planetdisplay_3d`/`planetmodel_3d` | `CSWGui3DSceneView` |
| `0x23CC` | `planets` | `GuiInGameGalaxyMapPlanet[16]` (`name_ref`/`description_ref`/`model_ref`) |
| `0x254C` | `current_planet` | `int` |

Functions: `Constructor 0x695180`, `Destructor 0x6958A0`/`0x6958C0`, `OnPanelAdded 0x694B40`,
`OnPanelRemoved 0x6927D0`, `DisplayPlanet 0x6933A0`, `OnPlanetClicked 0x6935A0`,
`Draw 0x693610`, `NextPlanet 0x694BF0`, `PrevPlanet 0x694CA0`, `HandleInputEvent 0x695980`.
`CGuiInGame::ShowGalaxyMapGui 0x62D040` / `HideGalaxyMapGui 0x62D1D0` are the show/hide entry
points; `ingame-gui-transition.md` §1.1 independently lists "dialog/store/galaxy-map/solo-mode
screens" among `SetSWGuiStatus`'s 14 callers, consistent with the galaxy map being a normal
`CGuiInGame` panel that toggles HUD status like every other one, **not** a special engine mode.

`CSWPartyTable` itself carries the map's data model (`q.py offsets CSWPartyTable`):
`available_planets int[16]` at `+0x060`, `selectable_planets int[16]` at `+0x0A0`,
`galaxy_map_selected_planet int` at `+0x0E0` — read/written by the matched NWScript VM
command pair `CSWPartyTable::SetSelectedPlanet 0x00563B60` /
`ExecuteCommandGetSelectedPlanet 0x00546970` (VERIFIED present in the symbol DB; the
`+0x60`/`+0xA0`/`+0xE0` offsets themselves were not independently re-disassembled this pass —
a generic-offset byte scan of `+0xE0` returned 57 hits across the whole binary, too many to
disambiguate by class without more targeted work — treat as VERIFIED-by-paired-accessor-name,
confirm live before depending on it).

### 1.2 Opening the map — VERIFIED, full source read

`ebo_m12aa`'s console placeable, `.utp` resref `infoterm001` — **`Tag = "GalaxyMap"`**,
distinct from the nearby `kiosk004` placeable (`Tag = "Supplies"`, an easy misidentification
from the git dump alone) — carries **`OnUsed = k_pebn_galaxy`**. Full source
(`tools/kotorfiles.py extract k_pebn_galaxy.nss`):

```c
void main()
{
    int nPlanet = EBO_GetCurrentPlanet();     // maps K_CURRENT_PLANET (5/10/15…50) to PLANET_*
    int nMain = GetGlobalNumber("K_KOTOR_MASTER");    // game-progress stage gate
    if (nMain == 10) { SetPlanetAvailable(PLANET_DANTOOINE, TRUE); SetPlanetSelectable(...); }
    if (nMain == 15) { /* unlocks Kashyyyk/Manaan/Korriban/Tatooine + live-content planets */ }
    // ... nMain == 20/30/50/60: further story-gated unlock/lock steps ...
    ShowGalaxyMap(nPlanet);
}
```

`ShowGalaxyMap(int)` is `CSWVirtualMachineCommands::ExecuteCommandShowGalaxyMap 0x005467B0`
(VERIFIED in the symbol DB), which constructs/shows `CSWGuiInGameGalaxyMap`.
`SetPlanetAvailable`/`SetPlanetSelectable`/`GetPlanetAvailable`/`GetPlanetSelectable` are
`0x5467F0`/`0x5468B0`/`0x546850`/`0x546910` — matching `docs/re/spawn.md`'s prior citation.
`planetary.2da` (17 rows: `label,name,description,icon,model,guitag`) supplies only display
metadata — no module resref column, confirming the destination is not looked up from this
table (see §1.3).

### 1.3 Confirm → destination — VERIFIED, two legs, both traced end to end this pass

**Leg 1: confirm → back into a fresh Ebon Hawk, never straight to a planet's surface.**
`k_sup_galaxymap.nss` (full source read; an older sibling `k_sup_galaxmap.nss` also exists,
same header, simpler body, not read in detail) is *"Universal Script that fires when the
galaxy map is used"* — this is the Accept confirm handler, not `k_pebn_galaxy` (which only
*opens* the map). It reads `GetSelectedPlanet()`, maps it through `EBO_GetPlanetFrom2DA`,
writes `K_FUTURE_PLANET`, applies a couple of story-specific availability rules (Bastila
becomes unselectable once Korriban is chosen or after the Leviathan), then dispatches to
exactly one `k_inc_stunt.nss` `ST_Play*` function chosen by which story flags are set. Every
branch's own comment and code was read (`k_inc_stunt.nss`, full source, ~760 lines); **every
single one ends in a `StartNewModule()` call whose destination is either a `STUNT_*` cutscene
module or an Ebon Hawk instance — never a planet-surface module**:

```c
void ST_PlayPlanetToPlanet()          // the ordinary case
{
    if (ST_VisionPlayed() == TRUE) {
        if (d100() > 50) {
            // 50% random Sith-fighter ambush
            StartNewModule("m12ab", "", ST_GetTakeOffRender(), "11a");     // → turret minigame
        } else {
            StartNewModule("ebo_m12aa", "", ST_GetTakeOffRender(), "08", ST_GetLandingRender());
            SetGlobalNumber("K_CURRENT_PLANET", GetGlobalNumber("K_FUTURE_PLANET"));  // ← the commit
        }
    } else {
        ST_PlayVisionStunt02();       // an unseen-planet vision plays first, THEN this same landing
    }
}
```

(`ST_GetTakeOffRender()`/`ST_GetLandingRender()` pick the `.bik` resref **at runtime** from a
`K_CURRENT_PLANET`-keyed table — the "runtime-selected movie name" case `cinematics.md` §3.2/§4.4
already documents as unfindable by any static string scan, and exactly why that doc's own
design reads the engine's queue rather than predicting names.) Every other dispatch branch
(`ST_PlayDantooineTakeOff`→`STUNT_12`, `ST_PlayLevCaptureStunt`→`STUNT_16`→(`02`)→`ebo_m40aa`,
`ST_PlayStarForgeApproach`→`STUNT_34`, `ST_PlayUnknownWorldTakeOff`→`STUNT_44`/`STUNT_42`,
`ST_PlayStarForgeLanding`→`ebo_m12aa`, `ST_PlayGenericLanding`→`ebo_m12aa`) follows the identical
shape — a `STUNT_*` cutscene, or a direct return to an Ebon Hawk instance, with
`K_CURRENT_PLANET` set to the newly chosen planet either immediately or once the stunt chain
resolves (`cinematics.md`'s own stunt-module-refuses-to-be-followed behaviour, §4.3, is exactly
what a co-op peer sees during whichever of these plays).

⚠ **A corrected error, kept here as a worked example of why cross-checking matters.** An
earlier reading of this same chain (a parallel pass on this repo) quoted
`StartNewModule("ebo_m12aa", "", ST_GetTakeOffRender(), "08", ST_GetLandingRender());` as
appearing *inside* `k_sup_galaxymap.nss` itself, presented as "the ordinary case." That exact
line **does** appear in `k_sup_galaxymap.nss` — but commented out
(`//StartNewModule("ebo_m12aa", ST_GetTakeOffRender(), ST_GetLandingRender());`, dead code
left over from an earlier draft), immediately below the LIVE line that actually runs there:
`ST_PlayPlanetToPlanet();`. The live call is a function reference, not the commented-out raw
statement; tracing that function (above) into `k_inc_stunt.nss` turned up the real, executing
`StartNewModule` — which happens to target the identical resref and arguments the dead comment
named, which is presumably why the comment was never deleted. The conclusion the other pass
drew from it ("confirming a planet targets `ebo_m12aa`, not the planet's surface") turned out
to be correct, just for the wrong textual reason — worth stating plainly since a future reader
grepping the same file would hit the same trap.

**Leg 2: the Ebon Hawk exit corridor → the real per-planet destination.** Walking out of the
reloaded Ebon Hawk crosses two nested trigger volumes in `ebo_m12aa` (read directly from the
module's GFF this pass):

| Trigger tag | `ScriptOnEnter` | Constant-pool contents |
| --- | --- | --- |
| `ebn12_ebonexit` | `k_pebn_leavhawk` | `K_CURRENT_PLANET` (×2), `G_FinalChoice`, `k_pebn_exithawk` (×2) |
| `ebn12_ebonleav` | `k_pebn_exithawk` | `K_CURRENT_PLANET`, `"On Planet "`, then 11 `k_<planet>_ebon_hawk_transition` gate globals, each immediately followed by a destination module resref |
| `ebn12_ebonexi002` (tag `ebn12_basttalk`) | `k_pebn_basttalk` | unrelated — a Bastila-conversation trigger sharing the corridor |

`k_pebn_exithawk`'s per-planet dispatch (module-local `.ncs` only, no `.nss` shipped — a
constant-pool strings scan, not a bytecode disassembly of the branch logic itself, since no
NCS decoder exists in this repo's tooling):

| Planet | Gate global | Destination module |
| --- | --- | --- |
| Taris | `k_tar_ebon_hawk_transition` | `tar_m02aa` |
| Dantooine | `k_dan_ebon_hawk_transition` | `danm13` |
| Kashyyyk | `k_kas_ebon_hawk_transition` | `kas_m22aa` |
| Manaan | `k_man_ebon_hawk_transition` | `manm26ad` |
| Korriban | `k_kor_ebon_hawk_transition` | `korr_m33aa` |
| Tatooine | `k_tat_ebon_hawk_transition` | `tat_m17ab` |
| Leviathan | `k_lev_ebon_hawk_transition` | `lev_40ac` |
| Unknown World | `k_unk_ebon_hawk_transition` | `unk_m41aa` |
| Star Forge | `k_sta_ebon_hawk_transition` | `sta_m45aa` |
| Live content 1–6 | `k_live1..6_ebon_hawk_transition` | `liv_m99aa`..`liv_m99af` |

This now closes the loop cleanly and consistently with leg 1: `k_pebn_exithawk` reads
`K_CURRENT_PLANET` — which `ST_PlayPlanetToPlanet` (and every sibling `ST_Play*` function)
already updated to the newly chosen planet by the time the player can walk anywhere in the
reloaded ship — and dispatches to the real surface module. The resref order and grouping
matches `k_inc_ebonhawk.nss`'s own canonical planet numbering exactly (**VERIFIED** pairing);
the specific `if/StartNewModule` branch shape inside `k_pebn_exithawk` remains **INFERRED**
(not disassembled — see the NEEDS-LIVE/tooling note in §4 item 7).

### 1.4 Is this the same `TravelToModule` path the mod already announces? — VERIFIED, the key finding

**Every module transition in KOTOR 1 — door, NWScript `StartNewModule()` call (either leg
above), or the mod's own plugin-initiated warp — converges on exactly one engine funnel**,
confirmed this pass by disassembly and cross-reference with the mod's own shipped code:

* `CServerExoAppInternal+0x10080` = `move_to_module_pending` (plain dword), `+0x10084` =
  destination module `CExoString`, `+0x1008C` = start-waypoint `CExoString`. The mod's own
  `Odyssey.TravelToModule` (`src/KotorCoop.Plugin/Odyssey.Travel.cs:94`) already writes all
  three, live-verified across dozens of transitions in `transition-smoothness.md`'s live
  passes — the exact mechanism the shipped `warp` cheat and `TravelController.Execute` use.
* `CServerExoAppInternal::StartNewModule 0x004BA920` — the function that drains those three
  fields and performs the actual transition — has **exactly one call site in the whole
  binary** (`tools/xrefs.py 0x4BA920` → `0x004BAD93`, VERIFIED this pass): one poll loop in the
  server's own `MainLoop`. There is no second path — `cinematics.md` states this outright,
  "the co-op warp path lands here too."

**By contrast, `trans_pending`** (area `+0x2C4`, `SetTransitionPending 0x00506B30`) — the field
`TransitionAnnounceWatch` currently hooks — is written from 37 sites across the binary
(`fieldwrites.py 0x2C4`), and the specific rising edge the announce channel keys on is
**door-specific**: `CSWSDoor::EventHandler 0x0058C185`. Neither leg 1 nor leg 2 above is a door
crossing (a placeable console and two trigger volumes, respectively) — **neither raises
`trans_pending` at all**, and nothing in the shipped code reads `move_to_module_pending` back
as a detection signal (only `SetMoveToModulePendingFn` is declared; no matching getter exists
anywhere in `src/KotorCoop.Plugin`).

**The movies riding leg 1 are already covered, independent of the announce gap.**
`CinematicWatch.OnFrame` (`src/KotorCoop.Plugin/CinematicWatch.cs:139-220`) polls
`Odyssey.QueuedTransitionMovieCount()` **every tick**, unconditionally — not gated on a door,
not gated on `trans_pending`, not gated on any particular trigger. The instant the count grows
(any `StartNewModule` call queuing movies, from any source), it reads the new names and
publishes them on the pre-existing `TransitionMovies` wire packet a follower's own warp already
carries forward. **A galaxy-map departure's take-off/landing movies are therefore already
relayed today, verified by reading the poll — no new code needed for the movie half.** This
resolves what would otherwise be an open question about which of two candidate movie-firing
shapes is authoritative (a separate, apparently-unused `k_inc_ebonhawk.nss` pair,
`EBO_PlayTakeOff`/`EBO_PlayLanding`, calls `PlayMovie()` **in place** rather than through the
queued shape `k_inc_stunt.nss` actually uses live — likely dead/legacy code, consistent with
`cinematics.md` §4.4's own note about several movie names referenced with no live path to
them): it does not matter which shape fires, because `CinematicWatch`'s poll is
trigger-and-shape-agnostic.

**Conclusion: today, a follower is told nothing about EITHER leg of a galaxy-map departure.**
The follower only finds out once the host's *published* module actually changes — the full
pre-announce latency `transition-smoothness.md` §1 measured for a bare door (5.6–5.8 s
minimum, and TWO such transitions happen back to back here, each potentially carrying a
multi-second movie).

**The fix, and why it should watch `move_to_module_pending` directly rather than
`CinematicWatch`'s queue-growth edge.** Both are valid candidate hooks and both are cheap;
`move_to_module_pending` is the more complete one. `CinematicWatch`'s edge only fires when a
transition actually queues a movie — but §1.3's leg 2 (`k_pebn_exithawk`'s per-planet dispatch
into `tar_m02aa`/`danm13`/etc.) shows **no movie names in its constant pool at all**: the
take-off/landing movies already played during leg 1's stunt/Ebon-Hawk-reload sequence, so leg
2's own `StartNewModule` call is very likely a **bare** transition. Hooking only the
movie-queue-growth edge would announce leg 1 but miss leg 2 — the actual arrival at the
planet's surface — entirely. Watching `move_to_module_pending`'s rising edge instead catches
both legs (and any other bare, non-door `StartNewModule()` in the game) uniformly, with the
`+0x10084` destination string already populated to read directly, no door sweep needed. See
§4 item 1.

### 1.5 What a client should do while the host is on the map screen

**Not `Away`.** `AwayRule` fires on sustained read failure — modeling "stuck at a menu or
disconnected," not "readable, but doing something else." The galaxy map is an ordinary
in-world `CGuiInGame` panel; the player's own object, position and party table stay live and
readable throughout (§1.1: it toggles HUD status like dialogue and the store screen do, per
`ingame-gui-transition.md`'s `SetSWGuiStatus` caller list — not a load, not a pause).

**Reuse the shipped minigame-park pattern.** `src/KotorCoop.Plugin/MinigameController.cs`
already reads a pointer chain every frame, purely locally, and publishes
`PlayerState.FlagInMinigame` (`src/KotorCoop.Protocol/PlayerState.cs:75`) so a peer's instance
parks the phantom and suppresses forwarded damage/census — see `MinigameNoticeController.cs`,
`MinigameNoticeRule.cs`, `src/KotorCoop.Agent/MinigameAnnouncer.cs`. A new flag of the exact
same shape — read `CGuiInGame+0x080`/`+0x078` non-null **and registered**
(`CSWGuiManager::PanelExists`, not a bare non-null test — `native-browser-panel.md`'s own
lesson: a panel can be allocated but sitting in the delete/removed `+0x44` state) each frame —
costs nothing new on the wire beyond one bit, reuses `MinigameRule`'s park/hide decision shape,
and gives the correct answer: park the peer's phantom, post
`"<peer> is choosing a destination — waiting"`, exactly as the shipped minigame notice already
does. This is §4 item 2.

**The client's own galaxy map is left alone (not blocked).** Nothing about the retail screen
or `K_KOTOR_MASTER`/planet-availability state is per-peer or contended — a client opening
their own map concurrently costs nothing and resolves nothing, since the only shared-world
side effect happens at confirm (leg 1), which is a personal-instance NWScript call with no
lock on it today either way. The co-op question is entirely about what happens *after* a
confirmed departure (§1.4/§4), not about gating the screen itself.

## 2. Party-select screen (`CSWGuiPartySelection`)

### 2.1 The panel — VERIFIED, cross-checked twice, converging on identical offsets

`CSWGuiPartySelection` (size `0x3A80`, vtable `0x756D28`), reached from `CGuiInGame+0x078`
(`party_selection`). This offset has a documented history already: `Odyssey.Dialogue.Follow.cs`
carries a postmortem comment about a prior bug where `+0x78` was mistakenly read as the
*dialogue* panel (it is `+0x3C`), which silently broke follower dialogue-reply replay until
traced back to this exact offset. Offsets (`tools/q.py offsets CSWGuiPartySelection`):

| Offset | Field | Type |
| --- | --- | --- |
| `0x000` | `partyselection_panel` | `CSWGuiPanel` (base) |
| `0x074` | `exit_script` | `CExoString` — the script run after confirm |
| `0x07C` | `party_data` | `CSWGuiPartySelectionData[9]` (`npc_button`, `portrait_label`, `character_label`, `npc_index`) |
| `0x2770` | `count_label` | how many are currently picked |
| `0x28B0` | `done_button` | `CSWGuiButton` |
| `0x2A74`/`0x2BB4`/`0x2CF4` | `npc_name_label`/`npc_level_label`/`model_3d_label` | `CSWGuiLabel` |
| `0x2E34` | `labels` | `CSWGuiLabel[7]` |
| `0x36F4` | `back_button` | `CSWGuiButton` |
| `0x38B8` | `accept_button` | `CSWGuiButton` |

Functions: `SetForcedNPC 0x6BE310`, `OnPanelRemoved 0x6BE490`, `UpdateCount 0x6BE4E0`,
`AcceptParty 0x6BE560`, `ConfirmPartySelection 0x6BEC90`, `SetAddRemoveLabel 0x6BECC0`,
`HandleInputEvent 0x6BEDE0`, `OnPanelAdded 0x6BEEB0`, `OnToggled 0x6BF2A0`, `OnDone 0x6BF3B0`,
`OnEnter 0x6BF5B0`, `Constructor 0x6BFA40`, `Destructor 0x6BFF90`/`0x6BFFB0`.
`CSWGuiPartySelectionButton::SetSelected 0x6BE370` toggles one roster row's state.
`ConfirmPartySelection` is the confirm handler that calls `AcceptParty` (a caller/xref check
this pass found `AcceptParty`'s address referenced ~30 bytes into `ConfirmPartySelection`'s own
body, and `ConfirmPartySelection`'s own address appears as a literal elsewhere — consistent
with, but not a full instruction-by-instruction trace of, an `AddEvent`-bound button handler).

`exit_script` (a live `CExoString` field) is the load-bearing detail: the panel carries **its
own script to run on exit**, independent of whatever opened it. `ExecuteCommandShowPartySelectionGUI
0x005438B0` (disassembled this pass) pops a **string** argument first, then — only if the VM
call carries 3 params — two integers, matching the canonical NWScript signature
`ShowPartySelectionGUI(string sExitScript="", int nForceNPC1=-1, int nForceNPC2=-1)`.

### 2.2 When it appears — VERIFIED trigger found, corrects this doc's own working assumption

**Confirmed, from real shipped source**: `k_pebn_loc01.nss` (full source, 15 lines):

```c
void main()
{
    UT_SetPlotBooleanFlag(OBJECT_SELF, 1, TRUE);
    if (GetGlobalNumber("K_CURRENT_PLANET") != 10)
        DelayCommand(1.5, ShowPartySelectionGUI("k_pebn_swoop"));
    else
        DelayCommand(1.5, ExecuteScript("k_pebn_swoop", OBJECT_SELF));
}
```

— one of nine near-identical `k_pebn_loc01`..`09` scripts (by naming pattern), each a
swoop-taxi drop-off point around Taris. This is `minigames.md`'s own already-extensively-studied
swoop-registration system, not a screen tied to interplanetary departure at all.

**No call to `ShowPartySelectionGUI` was found anywhere in the galaxy-map chain traced in §1** —
neither `k_sup_galaxymap.nss` nor any `ST_Play*` function in `k_inc_stunt.nss` calls it; both
were read in full. This doc's own working assumption while tracing §1.3 (that the screen sits
inside the Ebon-Hawk exit corridor, `ebn12_ebonexit`/`ebn12_ebonleav`) was **not confirmed** —
those two triggers' scripts exist only as `.ncs` with no shipped source, so their exact call
graph could not be read directly, and nothing else in this pass turned up positive evidence
either way for that specific corridor. Only ~40 `k_pebn_*`/`k_inc_stunt`/`k_inc_ebonhawk`
scripts with shipped `.nss` source were read this pass; the remaining game scripts exist only
as compiled `.ncs` and were not decompiled (no NCS reader exists in this repo's tooling — see
§4 item 7). **The honest state**: party-select's only positively-confirmed trigger is the
Taris swoop-taxi system; whether it also fires on some or all interplanetary departures is
open. `CGuiInGame::GetPartyAccessPanelUp 0x62B4D0` — a function name suggesting a general,
player-invoked "is the party-access button available" check, found in the same functions
list as `ShowPartySelection 0x62DD20` — is a plausible lead for a free-standing, non-departure
entry point (an ESC-menu-style party management option) and was not investigated further.

The design in §2.4 does not depend on resolving this: it hooks the panel's own detection
(`CGuiInGame+0x078`) and the roster-write mechanism (§2.3) directly, so it works identically no
matter how many places in the game actually open the screen.

### 2.3 What confirming it writes — VERIFIED, and it is exactly what the mod already mirrors

`CSWGuiPartySelection::AcceptParty 0x006BE560` calls `CSWPartyTable::SpawnNPC 0x006BE90C` then
`CSWPartyTable::AddMember 0x006BEA25` — **the identical pair**
`src/KotorCoop.Plugin/PartyRosterController.cs:469-470` already cites and replicates for
dialogue-driven recruitment sync (`party-reconciliation.md:136-137` names this exact call pair
as "the canonical spawn-then-add pair... the game's own"). It writes through `CSWPartyTable`'s
disassembly-corrected field layout `Odyssey.Party.cs` already uses (its own header comment
flags the symbol DB's raw offsets for this struct as wrong — do not use them directly):

| Offset | Field | Meaning |
| --- | --- | --- |
| `0x00` | `PartyMemberCount` | companions in the active party, 0..2 (`AddMember` refuses at 2) |
| `0x04` | `PartyMemberSlots` | `int[2]`, NPC **slot indices** of the active companions (not object ids) |
| `0x0C` | `PartyObjectIds` | `int[9]`, spawned server object id per NPC slot |
| `0x30` | `PartyAvailable` | `int[9]`, availability — the one field the symbol DB and the corrected map agree on |
| `0xF0` | `PartyControlledNpc` | -1 unless a script/GUI scene is driving one specific member; both `AddMember`/`RemoveMember` refuse while this is set |

`PartyRosterSlots = 9`, `PartyMaxCompanions = 2` (companions only — the player's own PC is not
counted; see §2.5).

### 2.4 The co-op rule, and how to implement it — reuses existing, proven machinery

`PartyRosterController`'s mirroring is **poll-based, not hook-based**: it publishes this
instance's roster every frame and reconciles every 30 frames by reading the raw fields above
directly, then calling the engine's own `AddPartyMember`/`RemovePartyMember`/`SpawnAvailableNPC`
routines to converge — it does not care *how* the table changed. **A party-select-driven swap
is therefore already "just another roster change" to this channel** — no new hook is needed for
the mirroring itself, only for what the *client's own copy of the screen* should do while open.

* **(Recommended, §4 item 3) Suppress the client's copy outright.** Detect `CGuiInGame+0x078`
  opening (registered, per §1.5's `PanelExists` lesson), and force-close it immediately using
  the identical, already-proven idiom `native-browser-panel.md` documents in full for the mod's
  own synthetic server-browser panel: `panel+0x44 = (panel+0x44 & ~0x300) | 0x400`, letting
  `CSWGuiManager::Update`'s sweep (`0x40CE70`) unregister and delete it next frame. Nothing new
  is called into the engine; the client never sees the picker. The host picks normally through
  the unmodified retail screen; `PartyRosterController`'s existing poll then converges the
  client's roster within its normal ~30-frame cadence; §1.5's new flag (or the same shape
  reused for this panel) parks the client's phantom / shows a waiting notice for the brief
  window before suppression lands. Simplest, reuses fully-proven machinery.
  **NEEDS-LIVE**: does force-closing via the sweep (rather than the retail Back/Done path) skip
  running `exit_script`? If the client's own copy of whatever follows (e.g. the swoop-taxi's
  own `k_pebn_swoop` dispatch) depends on `exit_script` firing locally, suppression alone is
  insufficient and the design needs to also fire it synthetically after closing the GUI. This
  is the single biggest open question for this item.
* **(Fallback, §4 item 4) Auto-confirm with the host's picks.** Mirror the host's `party_data[]`
  selections (`CSWGuiPartySelectionButton::SetSelected 0x6BE370` per slot) then synthesize the
  `done_button`/`accept_button` click via `AddEvent`'s proven same-id-replace semantics
  (`native-browser-panel.md` §1.5/§3.f), so the client's own engine performs the identical
  `AcceptParty` sequence the host did. More faithful, more moving parts, and duplicates work
  the polling reconciliation already does for free — build only if a live pass shows the
  suppression approach loses something.

The client never gets a real choice by design — nothing in `CSWPartyTable`/
`CSWGuiPartySelection` is per-peer, there is exactly one roster, and the host is already the
authority for every other roster change in this codebase.

### 2.5 The "two PCs + N companions" math — VERIFIED, confirms the task's framing exactly

`CSWParty` (the separate, client-side control struct) has `party_member_datas[3]` — three
total slots, matching "PC + up to 2 companions." A repo-wide grep of
`src/KotorCoop.Plugin/PhantomController*.cs` for any `CSWPartyTable`/`PartyTable` reference
returns **zero hits**: a peer's phantom is spawned through `Odyssey.SpawnCreature`, a raw
blueprint spawn with no party-table interaction whatsoever. **The second human player's body
occupies zero `CSWPartyTable`/`CSWParty` slots on the host's own instance.**

Concretely: on the **host's** instance the party is `PC(host) + companion + companion` (the
table's normal 3-person cap, full), and the client's PC exists purely as a phantom the host's
engine has never heard of in party terms. On the **client's** instance,
`PartyRosterController`'s mirroring reproduces the host's two companions locally, and the
client sees `phantom(host) + companion(mirrored) + companion(mirrored)` — "2 companions + host
phantom," exactly as the task's own framing states. The host still only ever picks 2
companions through party-select; there is no third slot to negotiate.

## 3. Story-forced solo segments

`AwayRule` applies to **none** of these — every case below leaves the non-solo player's own
game state fully readable. The general fallback, per `cinematics.md`'s own phrasing, is the
**module-diff gate**: "a peer in a different module: no phantom, module-gated channels off,
converge on arrival."

### A. Leviathan capture — `ST_PlayLevCaptureStunt`/`02`

```c
void ST_PlayLevCaptureStunt()   { StartNewModule("STUNT_16", "", sRender, "08"); }     // pulled from hyperspace
void ST_PlayLevCaptureStunt02() { StartNewModule("ebo_m40aa", "", "17"); }              // captured, load the Leviathan-era Hawk
```

(VERIFIED, `k_inc_stunt.nss` lines 106-117.) A `STUNT_*` module is exactly the case
`cinematics.md` §4.3 already names and the mod already handles: `CoopAgent`'s follow logic
deliberately refuses to follow a peer into a `stunt*` module, and instead **accumulates** every
movie queued while declining, handing the whole set to whatever real transition it finally does
follow into (shipped, tested behaviour, `cinematics.md` §9.4). The partner does not watch the
capture personally, but does not desync or stall either — their own eventual warp into
`ebo_m40aa` carries the same movie names, and per §1.4 the `TransitionMovies` channel needs no
new code for a script-driven `StartNewModule`.

**Gap**: `movie_relay`'s own live-verification status is still open (`cinematics.md` §10.6/§10.7,
not closed by a later section). The capture also strips a companion from the active party
(`dialogue-consequences.md:74`) — no repo doc traces what a peer mirroring that companion sees
when it vanishes mid-capture.

### B. Taris swoop system — `k_pebn_loc01`..`09`, `k_pebn_swoop`

This is the SAME system §2.2 traced: 9 swoop-taxi triggers around Taris, each calling
`ShowPartySelectionGUI("k_pebn_swoop")`, then `k_pebn_swoop.nss` dispatching `StartNewModule`
to one of several intra-Taris destinations by which drop-off flag was set. §2.4's party-select
design covers the roster half. The swoop-RACE minigame itself is a separate, already
extensively investigated subject (`minigames.md`, roadmap Tier 3 item 21): confirmed live to
be an **in-place mode switch inside the same module**, not a module transition — so co-visible
racing is not structurally precluded the way the turret sequence is (item C), but the actual
race dispatch has never been observed to reliably fire live, for reasons static disassembly has
narrowed but not closed. An existing, unrelated bug, unaffected by anything in this doc.

### C. Turret sequences — `m12ab` ("Ebon Hawk Turret"), reached from multiple `ST_Play*` chains

```c
void ST_PlayStuntTurret_07_3() { StartNewModule("m12ab", "", "11a"); }
void ST_PlayStuntTurret_07_4() { StartNewModule("m12ab", "", "11a"); }
void ST_PlayLeviathanEscape01(){ StartNewModule("m12ab", "", "17a", "11a"); }
```

(VERIFIED, `k_inc_stunt.nss` lines 124-131, 192-197; `ST_PlayPlanetToPlanet`'s own 50%
"random Sith-fighter ambush" branch, §1.3, also routes here.) `module.ifo`'s `Mod_Name` reads
`"Ebon Hawk Turret"` for `m12ab`, `Mod_OnModLoad = k_pebo_mgload` auto-starts the minigame the
instant the module finishes loading — `minigames.md` §9.2 confirms this **live**: both host and
client independently entered minigame mode moments apart, each auto-parked by construction, not
by a workaround chosen for this case. There is no state a peer could occupy in this module that
is "present but not playing." **A search of `chitin.key` for other turret-flavoured scripts
found only `k_ren_unkturret` (Unknown World) as a further lead, not decompiled this pass — no
separate Manaan or Tatooine turret script or model was found**, tentatively correcting that
half of the task brief's own framing (not exhaustively proven — only a keyword search, not a
full game-content sweep).

**Gap**: if both players physically enter the turret module together (as the existing
travel-together mechanism naturally does), both independently auto-start their own separate
copy of the minigame with no coordination of outcome. The existing "waiting" notice does not
help here (it only fires when a peer is *excluded*, and per `minigames.md` §23.3 cannot even
fire under the shipped `minigame_hide_peer=1` default).

### D. Star Forge finale — `ST_PlayStarForgeApproach`, `ST_PlayBastilaTorture`, `ST_PlayUnknownWorldTakeOff`/`Landing`, `ST_PlayStarForgeLanding`

```c
void ST_PlayStarForgeApproach()   { StartNewModule("STUNT_34", "", "33"); }
void ST_PlayBastilaTorture()      { StartNewModule("STUNT_18", "", ST_GetTakeOffRender(), "08"); }
void ST_PlayUnknownWorldTakeOff() { StartNewModule("STUNT_44"/"STUNT_42", "", "05_8C", "5_9"); }  // dark/light branch
void ST_PlayStarForgeLanding()    { StartNewModule("ebo_m12aa", "", "43"); }
```

(VERIFIED, `k_inc_stunt.nss`.) Every one is mechanically the same shape as item A — a `STUNT_*`
module or an Ebon Hawk return, already covered by the stunt-gate + movie-accumulation mechanism
for the cinematic half. **No `ShowPartySelectionGUI` call was found on this path** — if the
finale changes the party composition, it does so through ordinary `AddNPC`/`RemoveNPC`/
dialogue-recruit calls the existing §2.4 channel already carries, not a party-select screen.

**Missing/NEEDS-LIVE, and the one segment with genuinely no deeper coverage**: whether the
actual Star Forge dungeon and the Malak confrontation split the party across simultaneous
defended locations (the canonical KOTOR 1 finale shape) rather than a sequential scripted
hand-off. No script/module structure for that specific split was traced by this pass — the
`ST_Play*` functions above only cover the cinematic transitions bracketing it, not the dungeon's
own internal structure. If it is truly simultaneous multi-location combat, it has no analog
anywhere else in this codebase's research; every other "solo" case here is one PC alone in one
module at a time, which the module-diff gate already generalizes over. This needs its own
dedicated RE pass before any coverage claim can be made with confidence.

## 4. Ranked implementation plan

| # | Item | Size | Rig? | What needs live verification |
| --- | --- | --- | --- | --- |
| 1 | **Extend `TransitionAnnounceWatch` to a second edge: `CServerExoAppInternal+0x10080` (`move_to_module_pending`)**, reading `+0x10084` for the destination module directly instead of resolving a door. Reuses the shipped `TransitionAnnounce` wire/pump machinery unchanged — a new detection edge, not a new packet. Covers BOTH galaxy-map legs (§1.3/§1.4) and every other non-door `StartNewModule()` transition (Leviathan capture, Star Forge stage changes, future mod content) in one change. Preferred over hooking `CinematicWatch`'s movie-queue-growth edge alone, which would miss leg 2's likely-bare transition (§1.4). | S | No for the logic change; yes to measure | Confirm the read-back doesn't double-announce a door crossing that also sets this flag (the existing serial-based de-dup should handle it) and that the destination string is populated by the time the edge fires |
| 2 | **New `PlayerState` flag for "peer has the galaxy map or party-select panel open"**, read via `CGuiInGame+0x080`/`+0x078` + `CSWGuiManager::PanelExists`. Mirrors `FlagInMinigame`/`MinigameRule` exactly: park the peer's phantom, suppress forwarded damage/census, HUD notice. | S | No for the read; yes for notice text/timing | Confirm `+0x078`/`+0x080` reliably distinguish "on screen" from "allocated but in the delete state" |
| 3 | **Suppress the client's own `CSWGuiPartySelection` on open**, using the `panel+0x44 = (…&~0x300)\|0x400` sweep-delete idiom `native-browser-panel.md` already proves. Let `PartyRosterController`'s existing poll converge the roster. No new engine calls. | S/M | Yes | Whether `exit_script` needs to fire locally on the client (§2.4's open question) |
| 4 | **(Fallback only, build after 3's live pass) Synthesize the client's own Accept**: mirror `party_data[]` selections + fire the real button event via `AddEvent` replace. | M | Yes | Whether item 3's gap actually manifests; if not, skip entirely |
| 5 | **Live-test pass**: walk both instances to `ebo_m12aa`'s `infoterm001`, pick a planet, confirm, walk the exit corridor — measure whether item 1's announce fires before EACH leg's transition, confirm `CinematicWatch` actually relays the take-off/landing movies for a real galaxy-map departure (not just the synthetic `moviewarp` staging `cinematics.md`'s own live passes used), and whether item 3's suppression converges cleanly on a real Taris swoop-taxi trigger (the one confirmed party-select trigger, §2.2). | M | Yes | Everything named NEEDS-LIVE above, in one session |
| 6 | **A small NCS bytecode reader**, comparable in scope to `tools/kotorfiles.py`'s existing GFF/2DA/KEY/BIF readers. Two concrete uses: (a) pin `k_pebn_leavhawk`/`k_pebn_exithawk`'s exact branch shape (§1.3, currently INFERRED from constant-pool strings only); (b) a static sweep for every caller of NWScript action `ShowPartySelectionGUI` across every shipped `.ncs`, settling §2.2's open question (how widely the screen actually fires) without needing a live capture. Not required to ship items 1–5, but would upgrade several INFERRED claims here cheaply. | M | No | — |
| 7 | **Star Forge finale RE pass** (§3.D) — no existing coverage to build on. Out of scope for a design doc; flagged as its own future item. | L | Yes, plus new static RE | Whether the split is simultaneous or sequential — settles which existing mechanism (if any) even applies |

**Offsets/claims most worth a live check, gathered in one place**: `CSWPartyTable+0xE0`
(`galaxy_map_selected_planet`, §1.1 — corroborated only by paired accessor names, not a direct
read); `CSWPartyTable+0x60`/`+0xA0` (`available_planets`/`selectable_planets` — DB-only, never
independently re-derived the way `+0x00`/`+0x04`/`+0x0C`/`+0x30`/`+0xF0` already were);
`CServerExoAppInternal+0x10080` read back as a *detection* signal (§4 item 1 — proven only as a
write path today); `CGuiInGame+0x078`/`+0x080` registration state under
`CSWGuiManager::PanelExists` (§4 item 2); whether `exit_script` needs a local run on
suppression (§4 item 3, the single biggest open question in this whole document); the exact
branch shape inside `k_pebn_leavhawk`/`k_pebn_exithawk` (§4 item 6).

## Summary

The galaxy map and party-select screen are both ordinary `CGuiInGame` panels reached by plain
NWScript calls — not menu-level state, not a load — so `AwayRule` is the wrong tool for either;
a `FlagInMinigame`-shaped wire flag is the right one. This pass fully traced the Ebon Hawk
departure chain in two legs, both verified by reading real shipped script source: confirming a
planet on the galaxy map (`k_sup_galaxymap.nss` → `k_inc_stunt.nss`'s `ST_Play*` family) never
lands the player on the planet's surface directly — it always returns them to a fresh Ebon Hawk
instance (or a `STUNT_*` cutscene that itself does), with `K_CURRENT_PLANET` updated; only the
second leg, walking through the ship to its exit corridor
(`ebn12_ebonexit`/`k_pebn_leavhawk`→`ebn12_ebonleav`/`k_pebn_exithawk`), sends the player to the
real per-planet destination module. Neither leg is a door crossing, so today's door-only
`TransitionAnnounceWatch` sees neither — and the general fix (watch the engine's one universal
`move_to_module_pending` funnel instead of the door-specific `trans_pending` edge) is small,
reuses proven wire/pump machinery, and incidentally covers the Leviathan capture and Star Forge
stage transitions too. The take-off/landing movies are already relayed today, for free, by
`CinematicWatch`'s trigger-agnostic per-tick poll. Party-select needs no new engine mechanism
at all: its writes go through the exact `SpawnNPC`/`AddMember` pair `PartyRosterController`
already mirrors via a poll, so "host picks, client's copy is suppressed" is implementable today
with the mod's own already-proven panel-teardown idiom — though this pass's only positively
confirmed trigger for the screen is the Taris swoop-taxi system, not every interplanetary
departure, a correction to the task's own working assumption that does not change the design.
The two-human-PCs math holds exactly as the roadmap item assumed: a client's phantom occupies
zero party-table slots, so the host still only ever picks two companions. The one segment with
real unfinished business is the Star Forge finale, which has no RE coverage in this codebase at
all and needs its own pass before any coverage claim can be made.

### Top 3 recommended next steps

1. **Ship item 1's `move_to_module_pending` announce edge first.** It is the smallest,
   highest-leverage change here — one new read, reusing the entire shipped `TransitionAnnounce`
   wire and pump — and it fixes both galaxy-map legs and every other non-door
   `StartNewModule()` transition in the game at once.
2. **Build the party-select suppression (item 3) and run the combined live pass (item 5)
   together** — they share the same rig session (galaxy map → exit corridor → a Taris
   swoop-taxi trigger) and item 5 is what resolves item 3's one open question (does
   `exit_script` need to run locally on a suppressed client).
3. **Do not attempt the Star Forge finale (item 7) as part of this work** — it needs its own
   dedicated RE pass first; folding it into this design would mean shipping a claim ("existing
   mechanisms cover it") this pass found no evidence for either way.
