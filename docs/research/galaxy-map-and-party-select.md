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

## Shipped 2026-08-27: party-select suppression (§4 item 3) and galaxy-map gating (part of §4)

Both halves of the roadmap item's remaining recommendations #2-4 that do not need a live rig
to ship a first cut. Item 1 (`move_to_module_pending` announce) shipped separately behind
`transition_announce_scripted`, see `transition-smoothness.md` §11.

### Party-select suppression (`party_select_host_only`, plugin key, default ON, NEEDS-LIVE)

Ships §2.4's **recommended** design exactly: suppress the client's own copy of
`CSWGuiPartySelection` outright with the same panel-teardown idiom `native-browser-panel.md`
proves for the native server-browser panel, and let `PartyRosterController`'s existing poll
converge the roster. Nothing calls into the engine beyond the one `+0x44` state write; nothing
new is added to the wire.

**Why no new `SharedBlock` field, no session-side role computation, and no `ITravelBridge`-style
arm channel were needed** (unlike `client_door_refusal`'s `ClientDoorRefusalReason` channel,
which this doc's own §4 item 3 originally assumed this would need too): the plugin already has
a purely local, already-proven stand-in for "am I a client in a live session right now" —
`SharedBlockView.TryGetHostPartyRoster`'s own valid bit, which `PartyRosterController.
SyncClientPartyTogetherState`'s own doc comment already calls "the cleanest... signal this
plugin has - it is only ever written for a client." Reusing it means this feature is entirely
plugin-side: `PartySelectSuppressionRule.ShouldSuppress(enabled, isClientInLiveSession)` (pure,
4 tests, `PartySelectSuppressionRuleTests.cs`) and `PartySelectSuppressionController` (the
per-frame engine-facing half). No `ProtocolConstants.Version` bump, no `SharedBlock` region —
the block instructions for this task ("0x40C0..0x40E0 are taken, take 0x40E0.. if you need a
field") did not apply because no field was needed.

**Addresses used, verification status:**

| Address / offset | What | Status |
| --- | --- | --- |
| `CGuiInGame+0x078` | `CSWGuiPartySelection*` while the panel is open | VERIFIED §2.1, cross-checked twice this doc, plus the live postmortem `Odyssey.Dialogue.Follow.cs` already carries about this exact offset |
| `CSWGuiPartySelection` vtable `0x00756D28` | guard that `+0x78` really is this class | VERIFIED (symbol database), used here as a read-only compare, never dereferenced past the vtable slot itself |
| `CSWGuiPanel+0x18` (gui manager pointer) | the manager the panel was constructed with | VERIFIED for `CSWGuiSaveLoad` (`native-browser-panel.md` §1); **INFERRED, not independently re-verified, for `CSWGuiPartySelection`** — assumed to hold because both are `CSWGuiPanel` subclasses sharing the same base constructor; guarded with a heap-pointer range check before use, and a mismatch here latches the whole controller off for the session (logged) rather than writing through a wrong guess |
| `CSWGuiPanel+0x44` (state bits) | `(state & ~0x300) \| 0x400` marks the panel for the manager's next-frame sweep | VERIFIED mechanism for `CSWGuiSaveLoad` (`native-browser-panel.md` §2's full state-machine trace); same INFERRED-by-base-class caveat as `+0x18` |
| `CSWGuiManager::PanelExists 0x0040BD70` | liveness check that never dereferences the panel | VERIFIED (already proven live by `NativeBrowserController`) |
| `CClientExoAppInternal+0x40` (`CGuiInGame*`) | reached via the app-manager → client → client-internal chain | VERIFIED, same chain `Odyssey.Dialogue.Spectate.cs`'s `GetInGameGui` already uses live |

**NEEDS-LIVE, everything named above that says so, plus:**

* Whether the client's copy of `CSWGuiPartySelection` actually opens at all in an ordinary
  session (the doc's own §2.2 finding: the only *positively confirmed* trigger for this screen
  is the Taris swoop-taxi system, `k_pebn_loc01`..`09` → `ShowPartySelectionGUI("k_pebn_swoop")`
  — not confirmed on any interplanetary departure leg).
* §2.4's own open question, unchanged by this pass: does suppressing via the sweep (rather than
  the retail Back/Done path) skip running the panel's own `exit_script` field? If the Taris
  swoop-taxi's own dispatch (`k_pebn_swoop`) depends on that script firing locally on the
  suppressed client, this ships incomplete for that one trigger and needs the §2.4 item 4
  fallback (mirror the host's picks and synthesize a real Accept click) built next.
* `CSWGuiPanel+0x18`/`+0x44` for `CSWGuiPartySelection` specifically (INFERRED-by-base-class,
  see the table above).

**Roster convergence verified by reading the code, no gap found.** The task asked to confirm
that a roster change on the host while the client's panel was suppressed is still applied, and
to add a unit test on the pure roster-reconcile rule if a gap showed. `PartyRosterController.
Reconcile` (`src/KotorCoop.Plugin/PartyRosterController.cs`) is driven entirely by
`block.TryGetHostPartyRoster` (the agent's published masks) against a fresh local
`Odyssey.TryReadPartyRoster` every ~30 frames — it has **no dependency on GUI/panel state at
all**, the same "poll, not hook" property §2.4 already documents. Suppressing the client's copy
of the screen cannot desync this: whatever the host's screen writes through `SpawnNPC`/
`AddMember` reaches the roster masks the poll already reads regardless of what, if anything, the
client's own (now-closed) panel would have shown. No isolable "pure roster-reconcile rule"
exists to add a test to — `Reconcile` itself is an engine-calling method, not a pure function —
so no new test was added here; the existing `PartyRosterController` design already covers this
case by construction, and that reasoning is what is recorded above rather than a speculative
test of the plugin-agnostic parts.

## Live re-verification (2026-08-28): the write-storm fix confirmed clean, and the STUNT_00 crash
## confirmed NOT reproducible across three independent fresh sessions

Task: confirm the dangling-pointer write-storm fix (`PartySelectSuppressionRule.IsAlreadyHandled`,
shipped the same day) holds on a fresh session, and re-run the STUNT_00 escape-pod leg three
times, per this doc's own recipe above.

**Write storm: PASS.** A fresh `demo.ps1 -Save 1` session's client plugin log carried exactly
ONE `party-select: client copy suppressed`/`swept away` pair across the whole session (roughly
15 minutes of wall-clock, spanning several module transitions) — not the 43,436 pairs in under 4
minutes ("roughly one pair per frame, non-stop") the original bug report measured. The single
pair fired once, for the real party-select panel this session's own module load constructed,
matching the fix's own "touch each panel once per module" design exactly.

**STUNT_00 escape-pod leg: PASS, 3/3, no crash.** Three independent fresh rig sessions (each a
clean `demo.ps1 -Stop` / clear logs / `demo.ps1 -Save 1` cycle, no state carried over), the exact
recipe run each time: host-only `warp end_m01ab`, `tp 85.29 41.13`, `useplaceable end_pod`,
`selectreply 0` (needed every time this round — the always-present entry conversation blocking
dialogue, as this doc's own prior round already found), ride the `.\movies\01c.bik` transition
into `STUNT_00`, then the module hand-off's own forced dialogue (`node -1 -> 2`,
`forced=True` twice, `forced=False` once — the exact flag varied, the crash risk this doc
previously pinned did not care which). All three runs sailed past the previously-documented
~11.7s post-dialogue-node crash window with no access violation, and all three landed the HOST
cleanly at `tar_m02af` (Taris) — once even with the CLIENT following all the way to the same
module and coordinates, both at full hp, contrary to this doc's own earlier assumption that a
client never follows a `stunt*`-adjacent chain to its real destination (it does, once the chain
reaches a genuine non-`stunt*` module again). `Get-Process swkotor` confirmed `Responding=True`
on both sides throughout every run; no minidump, no Windows Application-log crash entry for
`swkotor.exe` at any point during these three runs specifically (a LATER, unrelated crash this
same overall session — see `store-sync.md`'s 2026-08-28 entry — is not this crash: different
module, different address, different trigger).

**Verdict: this doc's own "STUNT_00 crash not reproduced" finding from the earlier fix entry is
now independently confirmed a third time (fourth, counting the fix entry's own two runs), by a
separate session with no prior manual state manipulation** — exactly the confirmation that
entry's own "Open" note asked for. The regression is CLOSED.

**A new diagnostic verb built this same session, unrelated to this item but reachable from the
same rig**: `faction <id|me> [vsId|me]` (`SharedBlock.CheatKindFaction = 38`,
`IGameBridge.CheatKinds.Faction = 38`; `src/KotorCoop.Plugin/CheatController.Faction.cs`, console
verb in `ConsoleCommand.Debug.cs`/`CoopCommandDispatcher.Diagnostics.cs`) — read-only, reports
`Odyssey.GetAIStateReputation` both directions plus each side's raw `CSWSCreatureStats::faction_id`
(reusing `Odyssey.GetCreatureFaction`, already shipped in `Odyssey.Faction.cs` for the phantom
faction-flip feature — no duplicate engine reader was added). Built for
`docs/research/combat-participation.md` §10's own follow-up list item 1; see that doc's own
2026-08-28 entry for what it found. Live-confirmed working (correct, non-truncated detail in the
plugin log; the 96-byte console-reply channel truncates the same as every other cheat's answer,
by design). `dotnet test`: 2339/2339 (`ConsoleCommandVerbTableCoverageTests` gained the new verb's
sample line).

Build: `dotnet build -warnaserror` clean, native plugin `-WarnAsError` clean.

### Galaxy-map gating (part of §4, task 2)

**Pure rule + tests only, no live plugin wiring.** `GalaxyMapConfirmRefusalRule.ShouldRefuse
(isClient, mode)` (`src/KotorCoop.Session/GalaxyMapConfirmRefusalRule.cs`, 3 tests,
`GalaxyMapConfirmRefusalRuleTests.cs`) mirrors `ClientDoorRefusalRule`'s `follow_lead=host`
case exactly (`isClient && mode == FollowLeadMode.Host`) but — by design, see the rule's own
doc comment — does **not** reach for `ClientDoorRefusalRule`'s extra "is the host busy right
now" gate: confirming a planet is not the moment the host would have to interrupt anything, it
only starts the same announce-then-follow chain (§1.4) an ordinary door crossing already uses,
so the host's own busy-ness is checked once, at the point it would actually follow
(`LeadFollowRule.DecideHostFollow`), not a second time at confirm.

**Why no plugin wiring shipped this pass**: §2.1's own table names `accept_button 0x1F24` on
`CSWGuiInGameGalaxyMap`, but this doc never independently traced *which* function is bound to
that button's click event the way §2.1 traced `CSWGuiPartySelection::ConfirmPartySelection` →
`AcceptParty` for the party-select screen — `k_sup_galaxymap.nss` (§1.3) is the NWScript-side
confirm handler, not the native button dispatch. Wiring a live refusal correctly needs that
address/ABI pinned down and guarded with the same rigor as every other detour in this plugin
(prologue/vtable checks, a registered-panel liveness check) — exactly the kind of researched-
but-unexercised write this project's own engine-write rules hold back behind a live pass. Task
scope for this item was explicitly "pure rule + tests," so that is what shipped; wiring the
plugin side (intercepting the accept button, posting "only the host can choose a destination")
is left as follow-up work, tracked in the roadmap row below.

### Files changed

* `src/KotorCoop.Plugin/PartySelectSuppressionRule.cs` (new) — the pure suppression decision.
* `src/KotorCoop.Plugin/PartySelectSuppressionController.cs` (new) — panel detection + teardown.
* `src/KotorCoop.Plugin/PluginHost.cs`, `PluginHost.Controllers.cs`, `PluginHost.Frame.cs`,
  `PluginHost.SessionLifecycle.cs` — construction, per-frame call (after `_partyRoster`),
  module-invalidation reset.
* `src/KotorCoop.Session/GalaxyMapConfirmRefusalRule.cs` (new) — the pure gating decision.
* `tests/KotorCoop.Tests/PartySelectSuppressionRuleTests.cs` (new, 4 tests),
  `tests/KotorCoop.Tests/GalaxyMapConfirmRefusalRuleTests.cs` (new, 3 tests),
  `KotorCoop.Tests.csproj` linked-source entry for `PartySelectSuppressionRule.cs`.
* `docs/CONFIG.md`, `profiles/coop.ini.example`, `docs/roadmap-seamless.md` — new key
  documented, shipped default, roadmap row updated.

### Live test plan (needs a two-instance rig)

Prerequisite: an Ebon Hawk save, or `warp ebo_m12aa` from the console
(`docs/research/console-overlay.md`) to reach the galaxy map console (`infoterm001`, `Tag =
"GalaxyMap"`); the Taris swoop-taxi party-select trigger needs a save on Taris near one of the
`k_pebn_loc01`..`09` drop-off points (or `warp tar_m03aa` and walking to one).

1. **Party-select suppression, the confirmed trigger.** Host and client both on Taris, host
   free to roam; walk the CLIENT character onto a `k_pebn_loc0N` swoop-taxi trigger. Expect:
   the client's screen never appears (or appears and is torn down within a frame or two), the
   chat line "party is chosen by the host" posts once, and the plugin log shows `party-select:
   client copy suppressed`. Confirm no crash and no leftover input-locked state. Separately,
   trigger the SAME swoop-taxi drop-off with the HOST's own character and confirm the host's
   screen behaves exactly as vanilla (unsuppressed) — `TryGetHostPartyRoster` should read
   invalid for the host, so nothing should fire.
2. **`exit_script` question.** With the client's screen suppressed in test 1, confirm whatever
   `k_pebn_swoop` normally dispatches (the taxi ride itself) still happens correctly for the
   client. If it does not, §2.4 item 4 (synthesize a real Accept click instead of just tearing
   the panel down) needs to be built next.
3. **`CSWGuiPanel+0x18`/`+0x44` for `CSWGuiPartySelection`.** Confirm the guard in
   `PartySelectSuppressionController` (the `panel+0x18` heap-pointer check) does not fire
   `guard refused` in the log during test 1 — if it does, the base-class-inferred offset does
   not hold for this panel class and needs re-derivation.
4. **Galaxy-map departure, both legs, with the existing `transition_announce_scripted`
   announce** (already shipped, separately NEEDS-LIVE per `transition-smoothness.md` §11): walk
   both instances to `ebo_m12aa`'s `infoterm001`, pick a planet, confirm, walk the exit
   corridor — this exercises the pre-existing item 1 announce edge, unrelated to the two new
   pieces above, but worth re-confirming in the same session since it shares the rig setup.
5. **Galaxy-map gating, once the plugin-side wiring above is built** (not this pass): under
   `follow_lead=host`, a client's own galaxy-map confirm should be refused with "only the host
   can choose a destination"; under `follow_lead=any`, the client's confirm should proceed
   normally and become an announce the host follows via the existing chain.

## Live pass (2026-08-28): `partyselect ok` built and run against the real escape pod — a
## new, 100%-reproducible engine crash blocks the leg past `STUNT_00`; a real, pre-existing bug
## found in the suppression detection itself

Task item 2: build a dev verb to confirm the native party-selection screen with its current
selection (this doc's own §4 item 3/4 follow-up), then run the real `end_m01ab` escape-pod
transition end to end. Built `partyselect ok` (`SharedBlock.CheatKindPartySelectOk = 37`,
`CheatKinds.PartySelectOk = 37`; `src/KotorCoop.Plugin/Odyssey.PartySelect.cs`,
`CheatController.PartySelect.cs`; console verb wired in `ConsoleCommand.cs`/
`ConsoleCommand.Cheats.cs`/`CoopCommandDispatcher.cs`; deliberately NOT added to
`FuncInterpHook.cs`'s Tab-completion table, matching `quittomenu`'s own precedent — a dev-only
LIVE-TEST PRIMITIVE, blockpoke-reachable via `console 'partyselect ok'`, not meant for a real
player to type). It resolves `CGuiInGame+0x78` (the same chain and vtable
`PartySelectSuppressionController` already verifies live), then calls
`CSWGuiPartySelection::ConfirmPartySelection` (`0x006BEC90`, thiscall, ret 4 — disassembled fresh
this round with `tools/disasm.py`) directly — the panel's own OK/Done handler, the exact code path
a real click reaches. `ConfirmPartySelection` itself gates on the SAME `CSWGuiMessageBox+0x64` bit
0 precondition `loadgame` already needs poked (`session-save.md`'s "message-box precondition
trap") — disassembly confirmed it resolves the identical `AppManagerPtr -> +4 client -> (a helper
collapsing +4 internal -> +0x40 gui) -> +0x98 messageBox -> +0x64 bit0` chain and only calls
`AcceptParty` (`0x006BE560`) when that bit is set — so `ConfirmPartySelect` pokes it first (a
no-op if already set), the same already-proven-safe trick. 4 test files touched:
`ConsoleCommandVerbTableCoverageTests.cs` (sample-line coverage), no `SharedBlock`/protocol version
bump needed beyond the plain cheat-kind number. `dotnet build -warnaserror` (root + plugin
`-WarnAsError`) and `dotnet test` (2304/2304, +1 from baseline) both clean before going live.

**The verb works and does not crash — called live on the HOST three times across two fresh rig
sessions (`END_M01AA` at game start, and again after a host-only warp to `tar_m10aa`), every time
answering `partyselect ok: confirmed (poked the message-box precondition bit first, same as
loadgame's own workaround)` with no crash and the game still fully responsive afterward.**

**Unexpected, important finding: `CGuiInGame+0x78` reads as a live, vtable-matched
`CSWGuiPartySelection*` from the very first frame after a fresh save load — in `END_M01AA`, well
before any known trigger for this screen, on BOTH host and client, continuously, for the whole
session (43,436 `party-select: client copy suppressed`/`swept away` pairs logged on the client in
under 4 minutes — roughly one pair per frame, non-stop, never settling).** This is not the
"engine-forced conversation on END_M01AB arrival" the original playthrough round attributed it to
— it starts well before that, at the Endar Spire's own opening save point, and it never stops. Two
readings are possible and this round did not have time to settle which: (a) `CGuiInGame+0x78`
genuinely holds a persistent, lazily-constructed-once panel object that the retail engine reuses
via `AddPanel`/`RemovePanel` rather than `new`/`delete` per showing — in which case a raw
non-null-plus-vtable-match test (what `PartySelectSuppressionController.GetPartySelectionPanel`
and this round's own `Odyssey.GetPartySelectPanel`/`ConfirmPartySelect` both do) can **never**
distinguish "on screen" from "constructed but hidden," and the already-shipped suppression feature
has been tearing down (or attempting to) a non-visible object every frame since it shipped; or (b)
our own sweep-teardown (`panel+0x44 = (…&~0x300)|0x400`) is not actually destroying the object the
way `native-browser-panel.md` documents for the different `CSWGuiSaveLoad` class, and what
`PanelExistsFn` reports as "swept away" is immediately followed by a genuinely fresh reconstruction
— which would mean something in the base game itself is re-opening this screen every single frame,
an even stranger finding. Either way, **this means the `party-select: client copy suppressed` log
line is NOT, by itself, evidence that a REAL party-select trigger fired** — it fires identically
whether or not anything actually happened, which undercuts this doc's own §4 item 5 test plan (a
log-line check) as originally written. Flagged here, not root-caused; the next round should read
`CGuiInGame` construction (does `+0x78` get allocated once at GUI-manager construction, independent
of `ShowPartySelectionGUI`?) before trusting this detection at all.

**The escape-pod scripted chain itself works exactly as this doc's §1.3/§1.4 traced it, live,
twice.** Host-only `warp end_m01ab` (landed both players at the identical `50.0,120.0,-0.0`,
matching prior rounds), `tp 85.29 41.13` (the pod), `useplaceable end_pod` (placeable `0x59`) →
`dialogue: engine reached StartDialog` (node `-1 -> 0`, a fresh conversation, confirmed twice) →
the conversation's own single line auto-advances (no `selectreply` needed the second time; the
first time an explicit `selectreply 0` was needed because a DIFFERENT, unrelated conversation —
apparently the same always-present `end_m01ab`-entry dialogue prior rounds attributed to a
"forced conversation" — was already holding the dialogue system and had to be closed first) →
`cinematic: the module transition has queued 1 movie(s): .\movies\01c.bik` →
`StartNewModule("STUNT_00", ...)`, confirmed by `where` reading `module STUNT_00` on the host. The
CLIENT correctly never followed (stayed in `end_m01ab` throughout, per the already-documented
"never follow into a `stunt*` module" rule) — no crash, no desync on the client side either time.
**PASS**, live-confirmed, matching every claim §1.3 made from static reading alone.

**`transition_announce_scripted` did NOT fire for this transition, both times** — no `announce:`
line of any kind in either plugin log across the whole sequence, despite shipping ON by default.
Reading `PluginHost.Frame.cs`'s own scheduling and the STUNT_00 window's own log
(`cinematic: the main thread was gone for 16532 ms ... no frame callback ran, so nothing this
plugin does happened in that window`) explains why: the takeoff movie blocks the main thread for
the movie's own ~16.5s length, and `move_to_module_pending`'s rising edge apparently lands and
clears again entirely inside that unobserved window — the announce watch never gets a frame to
sample it on. **A real, live-confirmed gap in item 1 of this doc's own §4 ranked plan**, specific
to transitions that queue a blocking movie before the module actually swaps (leg 1's own
Ebon-Hawk-return shape from §1.3 would hit the identical gap) — not specific to co-op or to
party-select. Not fixed this round (out of scope — this round's brief was items 2-4 of the escape
pod checklist, not item 1's announce edge); flagged as a NEW addition to this doc's own §4 item 1
follow-up list.

**BLOCKING: a new, 100%-reproducible engine crash fires ~11-12 seconds into the module STUNT_00
hands off to, before any real party-selection screen was observed to open — the escape-pod leg
remains NOT REACHED, now for a different, more precise reason than "needs a human."** Both live
attempts (independent fresh rig sessions) followed the identical path: STUNT_00's own cutscene
completes, hands off into a new module with its own forced dialogue
(`dialogue: engine reached StartDialog (3 so far, forced=False)`, node `-1 -> 2`), and
**`FIRST-CHANCE access violation (0xC0000005) at 0x40D386`** fires on the HOST **11.686s and
11.681s** after that dialogue-node line, respectively — same instruction address, same `esp`
(`0x1AF9B8`), same image-range stack values (`0x745898 0x745898 0x6FD8BD`) both times. Confirmed
via `tools/crashlog.ps1` against the real Windows Application log (`Faulting application:
swkotor.exe ... Exception code: 0xc0000005, Fault offset: 0x0000d386`, twice, matching exactly).
`0x0040D386` is inside `swkotor.exe`'s own image (base `0x400000`), not this mod's plugin DLL — an
engine-internal crash, not (as far as this round could tell) triggered by anything `partyselect
ok` or any other new code in this pass touches (the second reproduction never called `partyselect
ok` at all, and the crash fired identically). The client survives both times (never follows into
`STUNT_00`, as expected) and stays healthy throughout. `tools/q.py`/`tools/disasm.py` could not
name a symbol at this exact address this round (time did not allow deeper RE). **This is the exact
same "escape-pod leg not reached" open issue the original playthrough logged, now reached one step
further and pinned to an exact, reproducible crash address and timing** — genuinely new
information, not a re-confirmation of the old "needs a human for one click" limit, since a human
would hit this SAME crash regardless of who or what confirms the party-select screen.

**Verdict per sub-step:**
- `partyselect ok` dev verb: **BUILT, live-verified safe to call** (no crash across 3 calls).
- Party-select suppression "with the on-screen line": **PASS mechanically** (the line fires), but
  **its evidentiary value is now in question** — see the always-present-panel finding above; this
  round could not confirm the suppression is reacting to anything real versus firing unconditionally.
- Scripted transition announce: **FAIL, live-confirmed gap** — never fires when the transition's
  own movie blocks the main thread for the whole window.
- Client follows into the next module: **NOT REACHED** — blocked by the new `0x40D386` crash before
  either side gets far enough to test it.

### Files changed
* `src/KotorCoop.Plugin/Odyssey.PartySelect.cs` (new), `CheatController.PartySelect.cs` (new),
  `CheatController.cs` (switch case).
* `src/KotorCoop.Plugin/SharedBlock.cs` (`CheatKindPartySelectOk = 37`).
* `src/KotorCoop.Memory/IGameBridge.cs` (`CheatKinds.PartySelectOk = 37`).
* `src/KotorCoop.Session/ConsoleCommand.cs`, `ConsoleCommand.Cheats.cs` (verb + parser).
* `src/KotorCoop.Agent/CoopCommandDispatcher.cs` (dispatch entry).
* `docs/CONSOLE.md` (new row); `tests/KotorCoop.Tests/ConsoleCommandVerbTableCoverageTests.cs`
  (sample line).

### Open issues, ranked
1. **The `0x40D386` access violation** — blocks all further live progress on this specific
   content path. Needs a dedicated RE pass (symbolicate the address, find its caller) before the
   escape-pod leg can be reached at all, co-op or not — this may well be a latent vanilla-engine
   fragility this exact sequence had simply never been exercised against before (the original
   playthrough round never got this far either).
2. **`CGuiInGame+0x78`'s always-non-null behaviour** — undermines confidence in
   `party_select_host_only`'s own detection and this round's `partyselect ok` alike. Needs reading
   `CGuiInGame`'s constructor / `ShowPartySelectionGUI`'s own allocation to settle whether the field
   is genuinely reused, and if so, what the RIGHT liveness test is (visibility bit? registered-panel
   check against a specific instance rather than a bare vtable match?).
3. **`transition_announce_scripted` misses movie-blocked transitions** — add to this doc's §4
   item 1 as a known gap: the announce watch needs to run BEFORE the movie starts blocking frames,
   or read the edge from a source that survives the blocked window, not "the next frame that gets
   to run."

## Fix (2026-08-28): the `CGuiInGame+0x78` always-non-null finding was a real bug — a dangling-pointer write storm, fixed

Task: bisect two regressions between `c9d08bb` and `395f5cc` (the STUNT_00 crash above, and a
separate host-`loadgame`/client-re-adopt regression, `session-save.md`). This entry covers what
the live rig found and fixed for item 2 of the previous section's own ranked list.

**Root cause identified.** The previous round's own finding — `CGuiInGame+0x78` reading as a
live, vtable-matched `CSWGuiPartySelection*` from the first frame after a save load and never
going null again — was flagged but not root-caused. This round confirmed it live is a REAL,
severe bug, not a benign detection quirk: on a fresh two-instance rig, the CLIENT's own
`kotorcoop-general.log` recorded **70,000+ `party-select: client copy suppressed` /
`client copy swept away` line pairs in under 8 minutes** — roughly 500 PER SECOND, far faster
than the render frame rate, proving `PartySelectSuppressionController` was re-detecting and
re-tearing-down the IDENTICAL address every single pass, not a freshly (re)opened screen.
`CGuiInGame+0x78` is never nulled by anything after this controller's own synthetic teardown (the
`panel+0x44 = (…&~0x300)|0x400` sweep-delete idiom `native-browser-panel.md` proves for a
DIFFERENT panel class), so every subsequent frame's raw pointer walk re-reads the same
now-freed-or-never-really-reopened address, finds its vtable header still intact, and writes
`panel+0x44` through it again — a write through a stale pointer, hundreds of times a second,
for the whole life of a client session. This is exactly the kind of bug that can corrupt whatever
heap block has since been reallocated at that address, and a module load — precisely when the
STUNT_00 crash and the host-`loadgame` regression both fire — churns the heap harder than almost
anything else in the game, making a module-transition-timed corruption from this write storm a
very plausible unifying explanation for both regressions.

**The fix**, `src/KotorCoop.Plugin/PartySelectSuppressionRule.cs`
(`IsAlreadyHandled`)/`PartySelectSuppressionController.cs` (`_lastHandledPanel`): touch a given
panel address AT MOST ONCE per module/session. The address is remembered until `Invalidate()`
clears it on a module change or session end — the same boundary every other cached pointer in
this controller already resets on — so a genuinely new panel after a module change still gets
suppressed exactly as before, and the runaway re-detection of the identical stale address does
not. Costs nothing, cannot make the feature worse. 3 new pure tests
(`PartySelectSuppressionRuleTests.cs`).

**Live-verified the fix directly.** Same rig, same client: after the fix, the entire session
produced exactly ONE `suppressed`/`swept away` pair total, not 70,000+. `dotnet test`: 2331/2331
(the two `CoopCommandDispatcherTests` failures on a full-suite run were the documented UDP-port
flake — the class alone was rerun standalone and passed 8/8).

**STUNT_00 crash: not reproduced this round, before OR after the fix.** The exact documented
recipe (host-only `warp end_m01ab`, `tp`/`useplaceable end_pod`, `selectreply 0`, ride STUNT_00
into its destination module, watch the ~11.7s window past the `StartDialog(3, forced=False)`/
`node -1 -> 2` line the previous round pinned the crash to) was run twice — once against the
UNFIXED code, once against the fixed code — and both times the game played on cleanly past the
crash window with no access violation, the dialogue advancing through more nodes than the
previous round's report described before settling into `tar_m10aa`/Taris. Given the write-storm
bug above is real, severe, and now fixed regardless, and the previous round's own crash was only
observed twice with no isolation of WHY it was timing-sensitive, this is recorded as "could not
re-reproduce, root cause plausible but not confirmed" rather than "fixed and verified" — an honest
gap, not a claim.

**Host-`loadgame` -> client-re-adopt: DIRECTLY live-verified working, post-fix.** With the fix
deployed, `warp tar_m10aa` on the host, `partyselect ok` (message-box precondition), then
`loadgame 0` on the host produced the full expected sequence in the AGENT's own log (not the
plugin log — a mistake this round's own first attempts made, costing real time): host
`session-save: host-side load verdict - announcing re-adoption` / `host loaded a save - everyone
re-joins`, and the CLIENT's own agent log showed a clean `host loaded a save - rejoining their
world` -> `session-save: starting rejoin (HostReload)` -> `rejoin: load request N sent to the
plugin` -> `rejoined 'END_M01AA'` with NO stall, NO hang, NO vanish — both `swkotor.exe`
processes stayed `Responding=True` throughout. This matches round 87's own CLEAN pass, not the
regression `session-save.md`'s 2026-08-28 entry recorded. See that doc's own dated section for
the full recipe and this same finding recorded from the session-save side.

**Files changed**: `src/KotorCoop.Plugin/PartySelectSuppressionRule.cs`,
`src/KotorCoop.Plugin/PartySelectSuppressionController.cs`,
`tests/KotorCoop.Tests/PartySelectSuppressionRuleTests.cs`.

**Open**: the STUNT_00 crash's true root cause is still unconfirmed (NEEDS-LIVE — a third
reproduction attempt, ideally on a fresh rig with no prior manual state manipulation, would raise
confidence either way). `ItemTemplateSpawnHook`'s mid-function splice (the task's other prime
suspect) was read closely this round and its register/flag/stack-slot handling all check out
against the disassembly cited in its own class remarks — no bug found there by static review, but
it was not exercised live this round.

## 2026-08-28/29 — the `0x40D386` crash reproduces AGAIN, post-fix, via a recipe that never touches party-select at all

Task: `docs/research/transition-crashes.md`'s own attribution round (module-transition crashes
under shipped defaults). Not looking for this crash specifically — it turned up organically while
soaking plain `warp tar_m02aa` / `warp end_m01aa` / `warp tar_m10aa` / `warp tar_m04aa` cycles, no
galaxy map, no STUNT_00, no `partyselect ok` call anywhere in the recipe.

**Reproduced, symbolicated, and matches the earlier reading exactly.** `FIRST-CHANCE access
violation (0xC0000005) at 0x40D386, eip 0x40D386, esp 0x1AF9B8; image-range stack values:
0x745898 0x745898 0x6FD8BD` — identical `eip`, `esp`, and stack trace to both of this doc's own
"11.686s and 11.681s" STUNT_00 readings above. Independently confirmed via `tools/crashlog.ps1`
against the real Windows Application log (`offset=0x0000d386`). **This walks back the "Host-`
loadgame`... not reproduced this round" section's implied confidence, and directly contradicts
`docs/TROUBLESHOOTING.md`'s player-facing claim that this crash "was... fixed 2026-08-28...
confirmed clean across three independent live re-verifications."** The write-storm fix
(`PartySelectSuppressionRule`/`PartySelectSuppressionController`, above) is still correct and
still worth keeping — the 70,000+ suppress/sweep pairs it eliminated were real — but it evidently
was not `0x40D386`'s actual cause, or was not its only cause. `TROUBLESHOOTING.md` has been
corrected alongside this entry.

**New RE this round, not attempted in either prior pass**: `0x0040D386` is `+0xA6` into
`CExoKeyTable::DestroyTable` (`0x0040D2E0`-`0x0040D490`, per `tools/kotor1_0_3.db`), not any GUI
panel code. The faulting instruction (`cmp word ptr [ecx+4], bx`) reads through `ecx`, which was
just re-derived from `CExoKeyTable`'s own entry-array base (`[esi+0x10]`) after an intervening
call (`0x407230`) that runs between two reads of the identical array index — the same "read,
call something that can invalidate it, re-read without re-validating" shape this doc's own
`CGuiInGame+0x78` bug had, but in the KEY/BIF resource-table subsystem instead of a GUI panel.
Full disassembly and the case for why `tar_m02aa`'s ambient dialogue is a strong (not proven
sole) trigger is in `docs/research/transition-crashes.md`. `0x407230` itself was not disassembled
this round — that is the next step before a real fix can be shipped.

**Attribution: confirmed independent of every coop.ini feature flag.** The full soak this round
included a pass with `party_select_host_only=0`, `solo_segment_spectate=0`,
`transition_announce_scripted=0`, `container_lock_arbitration=0`, `companion_gear_sync=0`,
`script_item_sync=0`, `map_reveal_sync=0`, `pazaak_detect=0`, `store_sync=0`, and
`party_pool_sync=0` all simultaneously OFF, and the crash (same signature, unconfirmed exact
address) still fired. `party_select_host_only` in particular — the flag this doc's own fix
touches most directly — is ruled out as a precondition.

No code change this round; see `docs/research/transition-crashes.md` for the full five-round
soak, the tooling gaps found (no crash-dump admin rights, no debugger available, WER's own
apparent dedup of repeat crashes), and the ranked follow-up list.

## 2026-09-02 — galaxy-map gating, the plugin side (`galaxy_map_host_only`)

Task: wire the "only the host can choose a destination" refusal into the plugin — the follow-up
this doc's own "Galaxy-map gating" section left open ("wiring the plugin side... is left as
follow-up work"). `GalaxyMapConfirmRefusalRule` (§ above) already had the pure decision and its
own tests; what was missing was which native function to splice and how to refuse safely.

### The choke point, RE'd this pass

§2.1 traced the party-select equivalent (`CSWGuiPartySelection::ConfirmPartySelection` →
`AcceptParty`) but this doc never traced the galaxy map's own accept-button dispatch — §2.1's own
table names the FIELD offset (`accept_button 0x1F24`) but not the function its click reaches.
Unlike party-select, `CSWGuiInGameGalaxyMap` has no separate named "confirm"/"accept" method in
the symbol database at all: `tools/q.py func '' CSWGuiInGameGalaxyMap` lists only `Constructor`,
`Destructor`/`Destructor_2`, `DisplayPlanet`, `Draw`, `HandleInputEvent`, `NextPlanet`,
`OnPanelAdded`, `OnPanelRemoved`, `OnPlanetClicked`, `PrevPlanet` — matching §1.1's own function
list exactly. Every button on this panel, accept included, is dispatched from inside
`HandleInputEvent 0x00695980` itself:

```
0x006959AC  test edi, edi
0x006959AE  je 0x695ae2                  ; edi (2nd stack arg) == 0 -> not-handled exit
0x006959B4  lea eax, [ebx - 0x27]        ; ebx = raw GUI event code
0x006959B7  cmp eax, 0xb8
0x006959BC  ja 0x695ae2                  ; out of [0x27,0xDF] -> not-handled exit
0x006959C2  movzx eax, byte ptr [eax + 0x695b0c]   ; byte lookup table -> case index
0x006959C9  jmp dword ptr [eax*4 + 0x695af8]        ; jump table, 4 known targets
```

`tools/disasm.py 0x695980 500` disassembled every case body reachable from the table.
`0x695AAC`/`0x695AD1` call `PrevPlanet 0x694CA0`/`NextPlanet 0x694BF0` (VERIFIED against the same
addresses §1.1 already names) — the arrow buttons. The other two, `0x6959D0` and a sibling at
`0x695A79`, both end by calling `CGuiInGame::HideGalaxyMapGui 0x0062D1D0` (§1.1's own "show/hide
entry points" name) — the two buttons that CLOSE the panel: accept and back.

**`0x6959D0` is the accept branch specifically, confirmed two independent ways:**

1. Only this branch pushes the literal `0x0075497C` before constructing a `CExoString` and
   calling `CVirtualMachine::RunScript 0x005D0FC0` (all four names — `CSWGuiManager::PlayGuiSound
   0x0040A140`, `CExoString::Constructor_2 0x005E5A90`/`Destructor_2 0x005E5C20`,
   `CClientExoApp::GetInGameGui 0x005ED690`, `CVirtualMachine::RunScript 0x005D0FC0`,
   `CGuiInGame::HideGalaxyMapGui 0x0062D1D0` — VERIFIED this pass via `tools/q.py sql "SELECT
   address,class_name,function_name FROM functions WHERE address IN (...)"`). Reading
   `0x0075497C` out of the executable's own `.rdata` (a standalone reuse of `tools/disasm.py`'s
   own PE-section walk) returns the ASCII string `"k_sup_galaxymap\0"` — VERIFIED, byte for byte,
   the exact confirm-handler script name §1.3 already traced end to end from the NWScript side
   ("`k_sup_galaxymap.nss` ... this is the Accept confirm handler"). No other function in this
   class's `HandleInputEvent` reads that string.
2. `tools/xrefs.py 0x6959D0` reports **zero** direct call/jmp instruction sites and **exactly
   one** address-literal occurrence — the jump-table entry at `0x695AF8` itself. The only way
   execution ever reaches `0x6959D0` is through that one computed jump, for whichever raw GUI
   event code(s) the lookup table at `0x695B0C` maps to case index 0. Same "single funnel,
   cross-referenced" argument §1.4 makes for `StartNewModule`.

Full disassembly of the accept case body (`tools/disasm.py 0x6959D0 130`):

```
0x006959D0  8b4e18               mov ecx, dword ptr [esi + 0x18]      ; this (CSWGuiInGameGalaxyMap*)
0x006959D3  6a00                 push 0
0x006959D5  e86647d7ff           call 0x40a140                        ; CSWGuiManager::PlayGuiSound
0x006959DA  a1003a7a00           mov eax, dword ptr [0x7a3a00]
0x006959DF  85c0                 test eax, eax
0x006959E1  746d                 je 0x695a50
0x006959E3  8b0dfc397a00         mov ecx, dword ptr [0x7a39fc]
0x006959E9  8b4904               mov ecx, dword ptr [ecx + 4]
0x006959EC  e89f7cf5ff           call 0x5ed690                        ; CClientExoApp::GetInGameGui
0x006959F1  687c497500           push 0x75497c                        ; "k_sup_galaxymap"
0x006959F6  8d4c2410             lea ecx, [esp + 0x10]
0x006959FA  c780040c000001000000 mov dword ptr [eax + 0xc04], 1
0x00695A04  e88700f5ff           call 0x5e5a90                        ; CExoString::Constructor_2
0x00695A09  8b0d003a7a00         mov ecx, dword ptr [0x7a3a00]
0x00695A0F  6a00                 push 0
0x00695A11  680000007f           push 0x7f000000
0x00695A16  8d542414             lea edx, [esp + 0x14]
0x00695A1A  52                   push edx
0x00695A1B  c744242800000000     mov dword ptr [esp + 0x28], 0
0x00695A23  e898b5f3ff           call 0x5d0fc0                        ; CVirtualMachine::RunScript
0x00695A28  a1fc397a00           mov eax, dword ptr [0x7a39fc]
0x00695A2D  8b4804               mov ecx, dword ptr [eax + 4]
0x00695A30  e85b7cf5ff           call 0x5ed690
0x00695A35  8d4c240c             lea ecx, [esp + 0xc]
0x00695A39  c780040c000000000000 mov dword ptr [eax + 0xc04], 0
0x00695A43  c744241cffffffff     mov dword ptr [esp + 0x1c], 0xffffffff
0x00695A4B  e8d001f5ff           call 0x5e5c20                        ; CExoString::Destructor_2
0x00695A50  8b0dfc397a00         mov ecx, dword ptr [0x7a39fc]
0x00695A56  8b4904               mov ecx, dword ptr [ecx + 4]
0x00695A59  e8327cf5ff           call 0x5ed690
0x00695A5E  8bc8                 mov ecx, eax
0x00695A60  e86b77f9ff           call 0x62d1d0                        ; CGuiInGame::HideGalaxyMapGui
0x00695A65  5f                   pop edi
0x00695A66  5e                   pop esi
0x00695A67  5b                   pop ebx
0x00695A68  8b4c2408             mov ecx, dword ptr [esp + 8]
0x00695A6C  64890d00000000       mov dword ptr fs:[0], ecx
0x00695A73  83c414               add esp, 0x14
0x00695A76  c20800               ret 8
```

And the dispatcher's own shared "not handled" tail, reached both by the pre-jump-table exits
(`edi==0`, out-of-range event code) and by this hook's own refusal:

```
0x00695AE2  8b4c2414             mov ecx, dword ptr [esp + 0x14]
0x00695AE6  5f                   pop edi
0x00695AE7  5e                   pop esi
0x00695AE8  5b                   pop ebx
0x00695AE9  64890d00000000       mov dword ptr fs:[0], ecx
0x00695AF0  83c414               add esp, 0x14
0x00695AF3  c20800               ret 8
```

### The splice

The case handler's own first two instructions are position-independent and exactly 5 bytes —
`mov ecx,[esi+0x18]` (3) / `push 0` (2), the `this` pointer and the argument to the immediately
following `PlayGuiSound` call — so only those 5 bytes are relocated; nothing past them (the sound
call, the string construction, the script dispatch, the hide call) needs touching on the ALLOW
path, which resumes at `0x006959D5` (the untouched original `call 0x40a140`) and runs bit-for-bit
as vanilla from there.

Reached by a `jmp` splice (this is itself a jump-table TARGET, not a callable function — there is
no return address to preserve), so `esi` (`this`), `ebx` (the raw event code) and `edi` (the
caller's second argument, already tested non-zero by `HandleInputEvent`'s own dispatch before the
jump table is even reached) are all live exactly as the vanilla case body expects them, and the
real x86 call stack is untouched — the stub's own `pushad`/`popad` pair is the only thing that
ever moves `esp`, and it is back to its entry value before either the ALLOW or the REFUSE path
runs.

**The refuse path is the function's own no-op exit, not a synthesised one** — the same principle
`ContainerUseHook`'s own remarks establish for its `ret 8`, adapted for a JUMP-TABLE target
instead of a CALLABLE function (there is no return address here to pop; a synthesised `ret` would
corrupt the caller). `0x00695AE2` is `HandleInputEvent`'s own shared "this event was not handled"
tail: it restores the saved SEH handler from `[esp+0x14]`, pops `edi`/`esi`/`ebx` off the real
stack (the SAME values pushed at the function's own entry, untouched by anything this hook does),
and `ret 8`s. Jumping there from the stub is therefore exactly equivalent to the accept click
never having matched a case at all — no galaxy-map/panel state changes, the confirm script never
runs, and the panel is left exactly as it was (still open) for the notice to explain.

### ABI

`HandleInputEvent` itself is `__thiscall(ecx=this, [esp+4]=eventCode, [esp+8]=secondArg), ret 8`
— matches the symbol database's own `CSWGuiPanel::HandleInputEvent` row for the base-class call
this function makes at its own entry (`0x00409E60`, `tools/q.py func '' CSWGuiPanel`). Nothing
about the splice depends on that ABI directly (it neither calls nor returns from the function
itself), only on the two jump targets (`0x006959D5`, `0x00695AE2`) being reached with the same
register/stack state the vanilla dispatch would have left, which the analysis above establishes.

### VERIFIED vs INFERRED

**VERIFIED this pass**, against the sha256-matched GOG v1.0.3 `swkotor.exe`:
* `CSWGuiInGameGalaxyMap::HandleInputEvent 0x00695980`'s full disassembly and jump-table shape.
* `0x006959D0` is the accept-case handler: the `"k_sup_galaxymap"` string match, and
  `tools/xrefs.py`'s "one jump-table literal, zero call/jmp sites" result.
* All six named function addresses (`PlayGuiSound`, `CExoString::Constructor_2`/`Destructor_2`,
  `GetInGameGui`, `RunScript`, `HideGalaxyMapGui`), cross-checked against `tools/q.py`.
* `0x00695AE2`'s own bytes and its two pre-existing callers inside `HandleInputEvent`.
* The 5-byte relocatable prologue (`8B 4E 18 6A 00`) and its resume point (`0x006959D5`).

**NEEDS-LIVE** (never exercised on a running process this pass — see the live test plan below):
* Whether the splice installs cleanly and the ALLOW path truly reproduces vanilla behaviour
  (host confirms a planet, sees the same click sound / script dispatch / panel close as before).
* Whether the REFUSE path (jump to `0x00695AE2`) is actually safe in practice — the byte-level
  argument above is sound, but this is the plugin's first jump-table-target splice (every other
  hook splices a callable function's entry or a mid-function point with a real return address);
  a live pass should confirm no crash, no stuck panel state, no leaked SEH frame.
* Whether the notice text reads correctly and the client's map genuinely stays usable afterward
  (can the player click accept again once the host actually leads the transition, e.g. under
  `follow_lead=any` after the host frees up, or does the panel need to be re-opened).
* The `follow_lead` read itself: the plugin's own direct `PluginConfig.Read("follow_lead")`
  duplicates `CoopConfig.cs`'s parse rather than sharing it (NativeAOT assembly boundary — see
  `GalaxyMapConfirmSuppressionRule`'s own doc comment) — worth confirming live that both sides
  agree on the same session's actual `coop.ini`, since they are two independent readers of the
  same file rather than one value propagated over the wire.

### What shipped

* `src/KotorCoop.Plugin/GalaxyMapConfirmHook.cs` (new) — the detour: verify-bytes-or-refuse
  install, jump-table-target splice, ALLOW/REFUSE stub, the managed callback.
* `src/KotorCoop.Plugin/GalaxyMapConfirmSuppressionRule.cs` (new) — the plugin-side pure
  decision, mirroring `KotorCoop.Session.GalaxyMapConfirmRefusalRule.ShouldRefuse(isClient, mode)`
  exactly in shape (own doc comment explains why it cannot literally reuse that class).
* `src/KotorCoop.Plugin/PluginHost.Controllers.cs` — `GalaxyMapConfirmHook.Bind(_view)`, next to
  `MinimapPeerHook.Bind`.
* `src/KotorCoop.Plugin/PluginHost.Startup.cs` (`InstallHooks`) — installs the hook
  unconditionally (verify-bytes-or-refuse; the coop.ini gate lives inside the hook's own
  callback, same shape as `PartySelectSuppressionController`'s `_enabled`).
* `tests/KotorCoop.Tests/GalaxyMapConfirmSuppressionRuleTests.cs` (new, 4 tests),
  `KotorCoop.Tests.csproj` linked-source entry for `GalaxyMapConfirmSuppressionRule.cs`.
* `docs/CONFIG.md`, `profiles/coop.ini.example` — new key `galaxy_map_host_only`, default ON.
* `docs/limitations.md` — galaxy-map row updated to "Built, NEEDS-LIVE".

Build: `pwsh src/KotorCoop.Plugin/build.ps1 -WarnAsError` clean (0 warnings), `dotnet build
src/KotorCoop.Agent` clean, `dotnet test tests/KotorCoop.Tests --filter
"FullyQualifiedName~GalaxyMap"` 13/13 (the 3 pre-existing `KotorCoop.Session` rule tests plus the
4 new plugin rule tests, plus other matches already in the suite).

### Live test plan (needs a two-instance rig)

Prerequisite: reach the galaxy map console (`infoterm001`, `Tag = "GalaxyMap"`) — an Ebon Hawk
save, or `warp ebo_m12aa` from the console (`docs/research/console-overlay.md`).

1. **Baseline, `galaxy_map_host_only=0` (or single-player)**: confirm the HOST can still open the
   map, pick a planet, click accept, and see the identical vanilla sequence (click sound, the
   take-off sequence begins) as before this change — proves the hook installs without disturbing
   the ALLOW path when it should never refuse.
2. **The refusal, `follow_lead=host` (must be set explicitly — the shipped default is `any`; see the 2026-09-03 correction below)**: with both instances at the galaxy map
   console, the CLIENT opens their own map, picks a planet, clicks accept. Expect: no click
   sound change beyond the vanilla one (`PlayGuiSound` still runs — the refusal fires just after
   it in program order, see the disassembly above; if this is jarring live, the stub order can
   be revisited), the chat line "only the host can choose a destination" posts once, the plugin
   log shows `galaxy-map: confirm refused - only the host can choose a destination
   (follow_lead=host)`, and — the key correctness check — the HOST's own module does NOT change,
   `k_sup_galaxymap` never ran on the client, and the map stays open on the client's screen.
   Separately, confirm the HOST's own accept click on the SAME console proceeds exactly as
   vanilla (the take-off sequence begins, `K_FUTURE_PLANET` gets set, etc.) — this is the "let
   the host's own confirm go through untouched" requirement.
3. **`follow_lead=any`**: repeat step 2 on the client. Expect no refusal — the client's own
   confirm proceeds normally and (per §1.4) becomes an announce (if `transition_announce_scripted`
   is on) the host's own follow-AI can pick up through the existing chain.
4. **Crash/stability check specific to this splice**: since this is the plugin's first
   jump-table-target detour (see NEEDS-LIVE above), repeat step 2 several times back to back
   (open map, pick different planets, accept, repeat) watching for any stuck input, a panel that
   silently stops responding, or a delayed crash a few seconds later — the same class of
   delayed-crash symptom `docs/research/transition-crashes.md` has already found elsewhere in
   this codebase from other causes, so a clean multi-rep run here is worth recording explicitly
   rather than assuming a mid-function/jump-table splice is automatically as safe as this doc's
   reasoning claims.
5. **`RefusedCount`/`AllowedCount` sanity**: these two counters
   (`GalaxyMapConfirmHook.RefusedCount`/`AllowedCount`) are not currently exposed through any dev
   verb — if this live pass adds one (mirroring `partyselect ok`'s own precedent), record it here;
   otherwise cross-check via the plugin log line counts instead.

## 2026-09-03 — `galaxymap open/close/stats`: the console verb the 2026-09-02 pass's own live
## test plan needed, plus fresh disasm of `ShowGalaxyMapGui`/`HideGalaxyMapGui`

Task: the 2026-09-02 section directly above ends with "Baseline"/"the refusal" steps that both
require opening the galaxy map, and item 5's own "not currently exposed through any dev verb"
note for `RefusedCount`/`AllowedCount`. Nothing on the console could open the map at all — the
only route was `k_pebn_galaxy.nss`'s `infoterm001` placeable, walked to in person. This pass adds
`galaxymap open`/`galaxymap close`/`galaxymap stats` so a rig can drive the whole test plan above
without a human clicking, and investigates (but does not ship) a fourth `galaxymap confirm`.

### `ShowGalaxyMapGui`/`HideGalaxyMapGui` — fresh full disassembly this pass, VERIFIED

§1.1 named both addresses from the symbol database only (name + address, no argument count or
calling-convention detail — the same state `ShowStoreGui` was in before the 2026-08-28 store
pass's own correction). Neither had been independently disassembled for its own ABI. Full
`tools/disasm.py 0x62D040 320` / continuation to `0x62D2AD` this pass:

```
0x0062D040  64a100000000  mov eax, fs:[0]              ; SEH prologue (this function has one,
0x0062D046  6aff          push -1                       ; HideGalaxyMapGui below does not)
0x0062D048  6853937200    push 0x729353
0x0062D04D  50            push eax
0x0062D04E  64892500000000 mov fs:[0], esp
0x0062D055  83ec1c        sub esp, 0x1c
0x0062D058  56            push esi
0x0062D059  8bf1          mov esi, ecx                  ; this
0x0062D05B  8b8608010000  mov eax, [esi+0x108]           ; InGameGuiInitializedOffset
0x0062D061  85c0          test eax, eax
0x0062D063  0f8452010000  je 0x62d1bb                    ; not initialised -> shared exit
0x0062D069  8b4630        mov eax, [esi+0x30]             ; InGameGuiBusyOffset
0x0062D06C  85c0          test eax, eax
0x0062D06E  0f8547010000  jne 0x62d1bb                    ; full-screen GUI busy -> shared exit
0x0062D074  a1fc397a00    mov eax, [0x7a39fc]             ; app manager
0x0062D079  8b4804        mov ecx, [eax+4]
0x0062D07C  e80f06fcff    call 0x5ed690                   ; -> gui (CGuiInGame*), same resolve
0x0062D081  8b88b4000000  mov ecx, [eax+0xb4]             ; gui+0xB4 = GuiConversationActive
0x0062D087  85c9          test ecx, ecx
0x0062D089  0f852c010000  jne 0x62d1bb                    ; conversation active -> shared exit
   ... (message-box-adjacent housekeeping calls, unconditional, no further gate) ...
0x0062D185  a1fc397a00    mov eax, [0x7a39fc]
0x0062D18A  8b542430      mov edx, [esp+0x30]             ; the ONE caller stack arg (nPlanet)
0x0062D18E  8b4808        mov ecx, [eax+8]
0x0062D191  52            push edx
0x0062D192  e8d91ce8ff    call 0x4aee70                   ; CServerExoApp::GetPartyTable
0x0062D197  8bc8          mov ecx, eax
0x0062D199  e8c269f3ff    call 0x563b60                   ; CSWPartyTable::SetSelectedPlanet
   ... (construct/AddPanel the CSWGuiInGameGalaxyMap panel at [esi+0x80] if not already present,
        PlayGuiSound) ...
0x0062D1BB  8b4c2420      mov ecx, [esp+0x20]              ; shared exit: SEH epilogue
0x0062D1C7  83c428        add esp, 0x28
0x0062D1CA  c20400        ret 4
```

`0x004AEE70` (`CServerExoApp::GetPartyTable`) is itself a bare 3-instruction accessor —
`mov eax,[ecx+4]; add eax,0x1b770; ret` (no stack args, no cleanup) — so the `push edx` at
`0x0062D191` is never consumed by it; the value sits on the stack across the call and becomes
`SetSelectedPlanet`'s OWN first stack arg instead (confirmed by disassembling `0x563b60`
directly: `mov eax,[esp+4]` is its first instruction). This two-call sequence is exactly the
kind of "argument reused across an intervening call whose own signature makes it look like there
is no argument" shape worth spelling out explicitly, since a naive read of `GetPartyTable`'s own
signature would miss it.

`CSWPartyTable::SetSelectedPlanet 0x00563b60`, full disasm:

```
0x00563B60  8b442404  mov eax, [esp+4]        ; nPlanet
0x00563B64  85c0      test eax, eax
0x00563B66  7c1e      jl 0x563b86              ; nPlanet < 0 -> no-op return
0x00563B68  83f810    cmp eax, 0x10
0x00563B6B  7d19      jge 0x563b86             ; nPlanet >= 16 -> no-op return
0x00563B6D  8b548160  mov edx, [ecx+eax*4+0x60] ; available_planets[nPlanet]
0x00563B71  85d2      test edx, edx
0x00563B73  7411      je 0x563b86              ; not available -> no-op return
0x00563B75  8b9481a0000000 mov edx, [ecx+eax*4+0xa0] ; selectable_planets[nPlanet]
0x00563B7C  85d2      test edx, edx
0x00563B7E  7406      je 0x563b86              ; not selectable -> no-op return
0x00563B80  8981e0000000 mov [ecx+0xe0], eax   ; galaxy_map_selected_planet = nPlanet
```

This is a full VERIFIED disassembly of the bounds check §1.1 could previously only call
"VERIFIED-by-paired-accessor-name" (a generic `+0xE0` byte scan returned 57 hits across the
whole binary, too many to disambiguate without this). All three offsets (`+0x60`, `+0xA0`,
`+0xE0`) read exactly as §1.1 already guessed. **Practical consequence for this verb**: passing
`nPlanet = -1` is a safe, deliberate "leave the current selection alone" sentinel — the `jl`
bounds check rejects it on its very first comparison, before either array read, so it can never
read out of bounds and never writes `galaxy_map_selected_planet`; the map still opens normally
either way, since `SetSelectedPlanet`'s own result is never checked by its caller.

`CGuiInGame::HideGalaxyMapGui 0x0062D1D0`, full disasm — much simpler, no SEH frame at all:

```
0x0062D1D0  56        push esi
0x0062D1D1  8bf1      mov esi, ecx
0x0062D1D3  8b8608010000 mov eax, [esi+0x108]   ; InGameGuiInitializedOffset
0x0062D1D9  85c0      test eax, eax
0x0062D1DB  0f848f000000 je 0x62d270            ; not initialised -> pop esi; ret (no cleanup)
   ... (message-box-adjacent housekeeping calls, unconditional) ...
0x0062D223  8b8680000000 mov eax, [esi+0x80]     ; the panel pointer
0x0062D229  8b4e38    mov ecx, [esi+0x38]
0x0062D22C  50        push eax
0x0062D22D  e8fef5ddff call 0x40c830             ; remove-from-manager-style call
0x0062D232  a170237a00 mov eax, [0x7a2370]
0x0062D237  85c0      test eax, eax
0x0062D239  751a      jne 0x62d255
0x0062D23B  8b8e80000000 mov ecx, [esi+0x80]
0x0062D241  85c9      test ecx, ecx
0x0062D243  7406      je 0x62d24b
0x0062D245  8b11      mov edx, [ecx]
0x0062D247  6a01      push 1
0x0062D249  ff12      call [edx]                 ; vtable slot 0 (destructor(1))
0x0062D24B  c7868000000000000000 mov [esi+0x80], 0   ; the panel pointer cleared
   ... (SetSWGuiStatus(this, 4, 1)-shaped housekeeping) ...
0x0062D270  5e        pop esi
0x0062D271  c3        ret                        ; bare ret - NO stack args, unlike ShowGalaxyMapGui
```

Confirms: `ShowGalaxyMapGui(this, int nPlanet)` — `__thiscall`, ONE stack arg, `ret 4`.
`HideGalaxyMapGui(this)` — `__thiscall`, NO stack args, bare `ret`. Both gate only on
`InGameGuiInitializedOffset` (`+0x108`); only `ShowGalaxyMapGui` additionally gates on
`InGameGuiBusyOffset` (`+0x30`) and `GuiConversationActive` (`gui+0xB4`) — the SAME two extra
gates `Odyssey.Store.cs`'s `OpenStore` already reports by name for `ShowStoreGui`, now confirmed
to apply identically to the galaxy map (`InGameGuiBusyOffset`'s own doc comment already noted
"fresh disasm of ShowStoreGui (0x0062E339) and ShowGalaxyMapGui (0x0062D069)" from the store
pass — this pass traced the REST of the function around that one already-known instruction).
`HideGalaxyMapGui` does **not** gate on the GUI manager's modal-stack count the way
`HideSWInGameGui` does — so unlike `openstore`/`closegui`, no `popmodal`-style companion
primitive is needed for `galaxymap close`.

### `galaxymap open`/`galaxymap close` — what shipped

* `src/KotorCoop.Plugin/Odyssey.GalaxyMap.cs` (new) — `ShowGalaxyMapGuiFn`/`HideGalaxyMapGuiFn`
  declared with the ABI above, `OpenGalaxyMap(out string)`/`CloseGalaxyMap(out string)` following
  `Odyssey.Store.cs`'s `OpenStore`/`CloseFullScreenGui` shape exactly: report each gate by name
  on refusal, verify the panel pointer (`CGuiInGame+0x80`) actually changed rather than trusting
  the call's return (`void`), and treat an already-closed map as APPLIED not refused.
* `src/KotorCoop.Plugin/CheatController.GalaxyMap.cs` (new) — `OpenGalaxyMap`/`CloseGalaxyMap`/
  `GalaxyMapStats` cheat handlers.
* `src/KotorCoop.Plugin/GalaxyMapConfirmHook.cs` — added `IsInstalled` (reads the existing
  private `_installed` field; no behaviour change to the splice itself).
* `src/KotorCoop.Plugin/SharedBlock.cs` — `CheatKindGalaxyMapOpen`/`Close`/`Stats` = 65/66/67
  (next free after `CheatKindDown` = 64).
* `src/KotorCoop.Memory/IGameBridge.cs` — mirrored `CheatKinds.GalaxyMapOpen`/`Close`/`Stats`.
* `src/KotorCoop.Session/GalaxyMapVerbRule.cs` (new) — pure parse for the three subcommands, 10
  tests (`tests/KotorCoop.Tests/GalaxyMapVerbRuleTests.cs`).
* `src/KotorCoop.Session/ConsoleCommand.cs`/`ConsoleCommand.Cheats.cs` — `CoopCommandKind.GalaxyMapOpen`/
  `GalaxyMapClose`/`GalaxyMapStats`, the `galaxymap` verb-table row, `ParseGalaxyMap`.
* `src/KotorCoop.Agent/CoopCommandDispatcher.cs` — the three dispatch entries (`SendCheat`, same
  one-liner shape as `closegui`/`popmodal`).
* `tests/KotorCoop.Tests/ConsoleCommandVerbTableCoverageTests.cs` — sample lines for the three
  new kinds.
* `docs/CONSOLE.md`, `docs/limitations.md` — verb rows / status text (status stays NEEDS-LIVE).

Build: `pwsh src/KotorCoop.Plugin/build.ps1 -WarnAsError` clean (0 warnings). `dotnet build
tests/KotorCoop.Tests -c Debug` clean. `dotnet test tests/KotorCoop.Tests -c Debug --no-build
--filter "FullyQualifiedName~GalaxyMap"` 22/22 (10 new `GalaxyMapVerbRuleTests` plus the
pre-existing `GalaxyMapConfirmRefusalRuleTests`/`GalaxyMapConfirmSuppressionRuleTests`).
`ConsoleCommandVerbTableCoverageTests` 81/81 (was 78 before the three new sample lines).

### `galaxymap confirm` — investigated, deliberately NOT shipped

The task considered a fourth subcommand that would drive the accept-button case handler directly
(`0x006959D0`, the choke point the 2026-09-02 section above already RE'd in full), the way
`partyselect ok` drives `ConfirmPartySelection`. That hook's own class remarks already establish
why calling `0x006959D0` itself is wrong: it is a jump-table TARGET reached only via a computed
`jmp`, not a callable function — there is no return address to preserve, so a synthesised `call`
into it would corrupt the real stack the instant it tries to `ret 8` back through
`HandleInputEvent`'s own shared tail.

The alternative considered — call `CSWGuiInGameGalaxyMap::HandleInputEvent(panel, eventCode,
secondArg)` itself (a genuine callable function, `__thiscall`, `ret 8`, per the 2026-08-28
party-select pass's own precedent for `ConfirmPartySelectionFn`) with an `eventCode` that reaches
case index 0 — is mechanically sound and this pass did the byte-level RE for it. The jump table
at `0x695AF8` and its byte-lookup table at `0x695B0C` (185 entries, `0x695B0C`..`0x695BC4`,
indexed by `eax = ebx - 0x27`, range `[0, 0xB8]`) decode cleanly by reading the raw bytes:

* Jump table (5 entries, `tools/disasm.py`'s own mis-disassembly of this data region as garbage
  x86 was decoded by hand as little-endian dwords): index 0 = `0x006959D0` (accept, matches),
  index 1 = `0x00695A79` (back), index 2 = `0x00695AAC` (prev planet), index 3 = `0x00695AD1`
  (next planet), index 4 = `0x00695AE2` (`NoOpExit`).
* Byte-lookup table: **two** raw `ebx` values map to case index 0 (accept) — `0x27` (table
  index 0) and `0x2D` (table index 6). Two raw values also map to case index 1 (back) — `0x28`
  and `0x2E`. Everything else in range maps to index 4 (`NoOpExit`, i.e. not specially handled).

That two raw event codes reach the SAME case (accept and back each have two) is itself new
information not in the 2026-09-02 section, and is recorded here for whoever picks this up next.
**What stops this pass from shipping `galaxymap confirm`**: nothing in this codebase documents
what these raw GUI event codes actually MEAN (`docs/research/engine-api.md` has no event-code
enum), so it is not established whether `0x27` and `0x2D` are genuinely equivalent for a real
click (mouse-down/mouse-up pair vs. a single click event, a keyboard-equivalent, etc.), nor
whether calling `HandleInputEvent` directly with either one reproduces every side effect a real
click has on the button's own internal state (`CSWGuiButton` is not otherwise touched by this
splice or by anything already verified in this doc) — the RE done for the store/party-select
verbs always had either a named, single-purpose function to call (`ConfirmPartySelectionFn`,
`ShowStoreGuiFn`) or, here, a genuinely ambiguous dispatch table with no documented meaning
behind its raw codes. This is new RE this pass could not verify without either a running process
to trace a real click's event code, or a wider audit of the GUI event-code space this task's
scope did not cover. Per this project's own engine-write rules, this stays a documented gap
rather than a shipped guess: **`galaxymap confirm` does not exist.** A real accept click (mouse,
or the retail engine's own input path) is still required to exercise the refusal end to end.

### VERIFIED vs INFERRED, this pass

**VERIFIED** (fresh disasm this pass, sha256-matched GOG v1.0.3 `swkotor.exe`):
* `ShowGalaxyMapGui(this, int nPlanet)`, `__thiscall`, `ret 4` — full trace, all three gates
  (`+0x108`, `+0x30`, `gui+0xB4`), the panel-pointer write at `+0x80`.
* `HideGalaxyMapGui(this)`, `__thiscall`, bare `ret`, no stack args, no modal-stack gate.
* `CSWPartyTable::SetSelectedPlanet`'s full bounds check and all three field offsets
  (`+0x60`/`+0xA0`/`+0xE0`) — upgrades §1.1's own "-by-paired-accessor-name" caveat to a genuine
  disassembly verification.
* `CServerExoApp::GetPartyTable` is a bare 3-instruction accessor with no stack args of its own
  (explains why `ShowGalaxyMapGui`'s pushed `nPlanet` survives across the call unconsumed).
* The accept/back jump-table's 5 entries and the byte-lookup table's full 185-entry contents
  (decoded by hand from raw bytes, `tools/disasm.py`'s own instruction-level view of this region
  being data, not code).

**INFERRED / NEEDS-LIVE**:
* Everything the 2026-09-02 section above already lists as NEEDS-LIVE for the confirm-refusal
  splice itself is unchanged by this pass — this pass only adds a way to REACH the map and read
  the hook's counters, it does not touch the splice.
* `-1` as `ShowGalaxyMapGui`'s `nPlanet` argument is INFERRED-safe from the disassembly (the
  bounds check rejects it before either array read) but has never been passed to the real
  function on a running process.
* Which raw GUI event code(s) a REAL accept click actually fires, and whether `HandleInputEvent`
  called directly reproduces every side effect of a real click — explicitly NOT verified (see
  "`galaxymap confirm`" above); this is why that subcommand does not exist.
* `OpenGalaxyMap`/`CloseGalaxyMap`'s own gate reporting (the three named refusal reasons) has
  never been exercised live — only the underlying gates it reads were independently confirmed by
  disassembly.

### Live recipe for the next session with the rig

1. Host and client both connected, `galaxy_map_host_only=1` (shipped default),
   `follow_lead=host` (⚠ NOT a default — the shipped default is `any`; set it explicitly in
   the client's `coop.ini` for this recipe, see the 2026-09-03 correction below).
2. Host: `warp ebo_m12aa` (both instances land in the same Ebon Hawk instance the way every other
   two-instance recipe in this doc does).
3. Client: `galaxymap open`. Expect `applied`/`opened (CGuiInGame+0x80 0 -> 0x...)` in the
   console reply and the plugin log; the galaxy map screen appears on the CLIENT's own display.
4. Client (a human, or the rig operator): click a planet, then click Accept on the client's own
   screen — there is no `galaxymap confirm`, this step still needs a real click (see above).
   Expect: the chat line `"only the host can choose a destination"` posts once, the plugin log
   shows `galaxy-map: confirm refused - only the host can choose a destination
   (follow_lead=host)`, the CLIENT's own module does NOT change, and the map stays open on the
   client's screen (the ALLOW/vanilla click sound still plays either way — the refusal fires
   after `PlayGuiSound` in program order, per the 2026-09-02 section's own disassembly).
5. Client: `galaxymap stats`. Expect `installed=True, refused=1, allowed=0` (or `allowed` > 0 if
   any earlier accept clicks on this instance were legitimately allowed, e.g. before this test
   under `follow_lead=any`, or on the host).
6. Host: `galaxymap open`, pick a planet, click Accept on the HOST's own screen. Expect: normal
   vanilla behaviour (the take-off sequence begins), and `galaxymap stats` on the HOST shows
   `allowed` incrementing, `refused` staying at whatever it already was (the hook never refuses a
   host under either `follow_lead` mode).
7. Client: `galaxymap close` if the map is still open (e.g. after a refusal) — expect `applied`,
   `"HideGalaxyMapGui called — CGuiInGame+0x80 0x... -> 0x0"`. A second `galaxymap close` in a
   row should answer `applied`, `"the galaxy map is already closed"` rather than refusing.
8. Repeat step 3-4 several times back to back on the client (open, pick different planets,
   accept) — the crash/stability check the 2026-09-02 section's own live test plan item 4 already
   asks for, now driveable without walking to `infoterm001` each time.

## `galaxymap open`/`close` live (2026-09-03, 02:46–02:54): the verbs work, their success test was wrong, and `+0x80` is not a closed/open flag

Rig: block 66, client pid 42144 (then 42144 after a rebuild), `warp ebo_m12aa` on both halves.

### What the first build reported

    galaxymap open  -> "already open (CGuiInGame+0x80 = 0x1B9161B0)"      (it had just opened it)
    galaxymap close -> "HideGalaxyMapGui called — CGuiInGame+0x80 0x1B9161B0 -> 0x1B9161B0" (status -4, refused)
    galaxymap open  -> passed the +0x30 busy gate and opened again              (so the close HAD worked)

`CGuiInGame+0x80` (the `CSWGuiInGameGalaxyMap*` this file's §1.1 names) is allocated by the
first `ShowGalaxyMapGui` and NEVER nulled by `HideGalaxyMapGui`: the same pointer
(`0x1B9161B0`, later `0x1AC0C1D8` on a fresh process) survived open → close → open. It is a
"has a panel object ever been built" pointer, not a state flag. The "Live recipe" section's
step 7 expectation (`0x... -> 0x0`) was therefore never going to happen.

### What moves, and what the verbs now judge success on

`CGuiInGame+0x30` (`InGameGuiBusyOffset`, the full-screen-GUI field `ShowGalaxyMapGui` itself
gates on): 0 → 1 on open, 1 → 0 on close, every time. The GUI manager's modal stack stayed at 1
throughout on this rig (it is not a galaxy-map indicator either). Rebuilt with both verbs
reading `+0x30`:

    [02:53:40.082] galaxymap open: ShowGalaxyMapGui called — CGuiInGame+0x30 0 -> 1, GUI-manager modal stack 1 -> 1, panel 0x1AC0C1D8 (open)             (status 1)
    [02:53:43.223] galaxymap open: the full-screen in-game GUI is up (...) — close it first (CGuiInGame+0x30 = 1, GUI-manager modal stack = 1)               (status -4, correct: it IS open)
    [02:53:46.371] galaxymap close: HideGalaxyMapGui called — CGuiInGame+0x30 1 -> 0, GUI-manager modal stack 1 -> 1, panel 0x1AC0C1D8 (the panel pointer persists by design) (status 1)
    [02:53:49.519] galaxymap close: no full-screen GUI is up, so the galaxy map is already closed (CGuiInGame+0x30 = 0, GUI-manager modal stack = 1)          (status 1, idempotent)

`galaxymap stats` → `installed=True, refused=0, allowed=0` (the hook is spliced; nothing has
clicked Accept, so nothing has been counted — the refusal itself still needs a real click, and
an `Odyssey.GalaxyMap` synthesised confirm is still deliberately not built, see "`galaxymap
confirm` — investigated, deliberately NOT shipped" above).

### One observation for the next round: `+0x30` reads 1 right after a `warp`

Twice (02:46 and 02:52) the FIRST `galaxymap open` after `warp ebo_m12aa` was refused with
`CGuiInGame+0x30 = 1` although no screen was up; `galaxymap close` (which is
`HideGalaxyMapGui`, gated only on `+0x108`) put it back to 0 and everything after that was
normal. Whatever the transition leaves in `+0x30` (the loading screen, or the party-select
panel the client suppresses), `closegui`/`galaxymap close` clears it. Not chased further.

### Recipe correction

Step 7 above should read: expect `applied`, `"HideGalaxyMapGui called — CGuiInGame+0x30 1 ->
0 ..."`; a second `galaxymap close` answers `applied`, `"no full-screen GUI is up, so the
galaxy map is already closed"`. Never read `+0x80` for state.

## `galaxyaccept` (2026-09-03): the ACCEPT input event, sent without a mouse, plus `galaxymap
## select <n>` — closing the one gap the 2026-09-03 `galaxymap open/close/stats` pass left open

Task: the two sections directly above both end on the same unresolved point. The 2026-09-02
pass's own "Live test plan" step 2 needs a CLIENT to "click Accept" on the galaxy map to
exercise `GalaxyMapConfirmHook`'s splice; the 2026-09-03 `galaxymap open/close/stats` pass
investigated driving that click synthetically (`galaxymap confirm`) and deliberately did NOT
ship it, because it could not tell, from static analysis alone, whether the two raw GUI event
codes its own byte-lookup-table decode found for the accept case (`0x27`/`0x2D`) were a safe,
faithful stand-in for a real click. This pass closes that gap with a DIFFERENT primitive shape
— `galaxyaccept`, calling `HandleInputEvent` itself rather than jumping into its accept-case
body — using the exact precedent `skipkey` already sets for the dialogue panel, and adds
`galaxymap select <n>` so there is a real planet selected for it to confirm.

### Why `galaxyaccept` is a different, safer shape than the `galaxymap confirm` that was rejected

The 2026-09-03 `galaxymap open/close/stats` pass's own class remarks (`GalaxyMapVerbRule.cs`)
give the precise reason `galaxymap confirm` was not shipped: the accept CASE HANDLER
(`0x006959D0`) is reached only through a computed `jmp` from a jump table — it is a JUMP-TABLE
TARGET, not a callable function, so it has no return address on the stack for a synthesised
`call` to preserve. Calling it directly (`call 0x6959D0` from managed code, expecting it to
`ret 8` back) would pop whatever garbage sits where a return address should be and crash or
corrupt the stack. `GalaxyMapConfirmHook`'s own splice (docs section above, "the splice") gets
away with reaching that address only because it arrives via `jmp`, not `call`, exactly
replicating the jump table's own dispatch shape — which is a valid technique for a HOOK
installed once at load time, but not for a live-fired VERB that would need its own `call`.

`HandleInputEvent` itself (`0x00695980`) is a completely different kind of target: a genuine
`__thiscall` function with its own prologue/epilogue and a real `ret 8`, callable safely any
number of times — the exact shape `Odyssey.Dialogue.SkipKey.cs`'s `SendDialogueSkipKey` already
calls live for the dialogue panel's own `HandleInputEvent`, through the panel's OWN vtable slot
rather than a hardcoded address (this repo's own "vtable slots, not addresses" rule). Calling
`HandleInputEvent(panel, eventCode, 1)` runs the SAME dispatch a real click does — including
`GalaxyMapConfirmHook`'s splice, which sits INSIDE that dispatch (spliced onto the accept case's
own body) and therefore still fires exactly as it would for a mouse click. This is why
`galaxyaccept` can ship what `galaxymap confirm` correctly declined to.

### The event-code derivation — independently re-run this pass, not just cited

The 2026-09-03 pass's own byte-lookup-table decode (`0x695B0C`, 185 entries, `eax = ebx - 0x27`,
range `[0,0xB8]`) was re-run from scratch this pass — `tools/disasm.py`'s own `load`/`to_offset`
helpers, reused directly against the same sha256-matched GOG v1.0.3 `swkotor.exe` this repo's
tools already point at (`tools/disasm.py`'s `DEFAULT_EXE`) — rather than trusted from the prior
write-up, per this task's own instruction to derive it fresh:

```python
import struct, sys
sys.path.insert(0, "tools")
import disasm

data, base, sections = disasm.load(disasm.DEFAULT_EXE)

jt_off = disasm.to_offset(base, sections, 0x695AF8)
jt = [struct.unpack_from("<I", data, jt_off + i*4)[0] for i in range(5)]

lut_off = disasm.to_offset(base, sections, 0x695B0C)
lut = data[lut_off:lut_off+0xB9]

accept_ebx = [eax + 0x27 for eax, case in enumerate(lut) if case == 0]
```

Result, VERIFIED this pass:

* Jump table at `0x695AF8` (5 dwords): index 0 `0x006959D0` (accept), index 1 `0x00695A79`
  (back), index 2 `0x00695AAC` (prev planet), index 3 `0x00695AD1` (next planet), index 4
  `0x00695AE2` (`NoOpExit`/not-handled) — byte-for-byte identical to the 2026-09-03 pass's own
  hand-decoded table, confirming that decode was correct.
* Byte-lookup table (`0x695B0C`, 185 entries): **exactly two** raw `ebx` (GUI event code) values
  decode to case index 0 (accept) — **`0x27`** and **`0x2D`**. A histogram of the whole table
  (`case-index histogram: {0: 2, 1: 3, 2: 4, 3: 4, 4: 172}`) also turned up one discrepancy worth
  recording: this pass counted **three** raw values for case index 1 (back) — `0x28`, `0x2E`,
  AND `0xDF` (the table's own last entry, `eax = 0xB8`, the upper boundary of the `[0,0xB8]`
  range) — where the 2026-09-03 pass's write-up says "two". This does not change anything about
  the ACCEPT derivation (still unambiguously `{0x27, 0x2D}`, matching exactly), so it is recorded
  here as a minor correction to the earlier pass's own count rather than chased further; back is
  not this pass's concern.
* Independently re-derived vtable evidence, not present in the 2026-09-02/2026-09-03 sections
  above: `CSWGuiInGameGalaxyMap`'s own vtable (`0x00754910`, symbol database) was dumped directly
  out of `.rdata` this pass. Slot 15 (`+0x3C`) holds `0x00695980` — `HandleInputEvent`'s own
  address, matching the symbol database exactly — and slots 0-26 are real code pointers (slot 27
  onward decodes as ASCII garbage, i.e. past the real vtable, consistent with the class's own
  function count). `+0x3C` is the SAME slot index `Odyssey.Dialogue.SkipKey.cs`'s own
  `PanelHandleInputEventSlot` already uses for both dialogue-panel classes — direct confirmation
  that `CSWGuiPanel`'s virtual layout is one shared shape across every subclass this plugin calls
  through a vtable slot for, not something that has to be re-derived per class.

**Which of the two codes `galaxyaccept` sends, and why**: `0x27`. This is the SAME raw value
`Odyssey.Dialogue.SkipKey.cs`'s own `DialogueSkipEvent` constant already uses for the dialogue
panel's own Enter/click-equivalent case — a second, independent panel class whose accept-shaped
case is ALSO reached by `0x27`. That two unrelated panels both treat `0x27` as their "advance/
confirm" case is the strongest evidence available without a live capture that `0x27` is this
engine's general-purpose Enter/confirm GUI event code, not something galaxy-map-specific — so
`galaxyaccept` sends `0x27`, not `0x2D`. **Still NEEDS-LIVE** (see below): which of the two a
REAL mouse click on the Accept button actually fires has never been captured on a running
process; `0x27` is the best-evidenced choice, not a confirmed one.

### What `galaxymap open`/`nPlanet` requires before an accept can do anything

Re-read this pass, from `Odyssey.GalaxyMap.cs`'s own already-shipped code (2026-09-03,
`OpenGalaxyMap`): `galaxymap open` always calls `ShowGalaxyMapGuiFn(gui, SelectedPlanetNone)`
with `nPlanet = -1` — a deliberate "leave the selection alone" sentinel, not "select nothing".
So after a bare `galaxymap open` on a fresh session, `CSWPartyTable+0xE0`
(`galaxy_map_selected_planet`) holds whatever it last held (0, on a save that has never opened
the map) — not necessarily a planet the story has actually unlocked. `k_sup_galaxymap.nss`'s own
confirm handler (§1.3 above) reads `GetSelectedPlanet()` unconditionally and dispatches
regardless — the accept branch's own disassembly (§ "2026-09-02" above, `0x006959D0`'s full
trace) never checks selection state either; it plays the click sound and runs the script no
matter what is selected. **So `galaxyaccept` alone is already sufficient to exercise
`GalaxyMapConfirmHook`'s REFUSAL** (the hook fires before any of that planet-specific logic
runs) — but a meaningful ALLOW-path test (the host's own accept genuinely starting a departure)
wants a real, story-unlocked planet selected first, which is what `galaxymap select <n>` is for.

`galaxymap select <n>` (new this pass, `Odyssey.GalaxyMap.cs`'s `SetGalaxyMapSelectedPlanet`)
calls `CSWPartyTable::SetSelectedPlanet` (`0x00563B60`, already VERIFIED full disasm in the
2026-09-03 section above) directly through this plugin's own already-proven `GetPartyTable()`
resolve (`Odyssey.Party.cs` — the SAME table `ShowGalaxyMapGuiFn` itself reaches via a different,
also-already-verified this-resolve chain; see that section's "two-call sequence" note), rather
than adding a second address for the same call. It is independent of whether the map screen is
open — `SetSelectedPlanet` touches only the table field, no GUI state — and reports whether the
engine's own bounds/availability check (`available_planets[n]`/`selectable_planets[n]` both
nonzero, `0 <= n < 16`) actually accepted the write, by reading `+0xE0` back rather than trusting
`SetSelectedPlanet`'s own `void` return.

### What shipped

* `src/KotorCoop.Plugin/Odyssey.GalaxyMapAccept.cs` (new) — `SendGalaxyMapAcceptKey`: resolves
  the galaxy-map panel off `CGuiInGame+0x80`, guards its vtable against `CSWGuiInGameGalaxyMap`'s
  own address (`0x00754910`) and that the full-screen GUI is actually up (`CGuiInGame+0x30`,
  NOT `+0x80` — this file inherits the 2026-09-03 pass's own live-pass correction), then calls
  `HandleInputEvent(panel, 0x27, 1)` through the panel's own vtable slot `+0x3C`.
* `src/KotorCoop.Plugin/Odyssey.GalaxyMap.cs` — added `SetSelectedPlanetFn` (0x00563B60),
  `GalaxyMapVtableAddress` (0x00754910, independently re-dumped this pass), `SetGalaxyMapSelectedPlanet`
  (`galaxymap select`), and `TryReadGalaxyMapState` (the read-only open/selected-planet/panel-vtable
  triple `galaxymap stats` now reports).
* `src/KotorCoop.Plugin/CheatController.GalaxyMap.cs` — `GalaxyMapAccept()`, `SelectGalaxyMapPlanet()`,
  and `GalaxyMapStats()` extended to print `open=`/`selectedPlanet=`/`panelIsGalaxyMap=` alongside
  the existing `installed=`/`refused=`/`allowed=` counters (Tier 2 item 18 task 3's own "read the
  panel's state ... only if cheap" ask — three extra reads on an already-firing verb).
* `src/KotorCoop.Plugin/CheatController.cs` — two new switch cases (`CheatKindGalaxyAccept`,
  `CheatKindGalaxyMapSelect`).
* `src/KotorCoop.Plugin/SharedBlock.cs` — `CheatKindGalaxyAccept` = 69 (next free after
  `CheatKindSpawnCreature` = 68), `CheatKindGalaxyMapSelect` = 70.
* `src/KotorCoop.Memory/IGameBridge.cs` — mirrored `CheatKinds.GalaxyAccept`/`GalaxyMapSelect`.
* `src/KotorCoop.Session/GalaxyMapVerbRule.cs` — added `Verb.Select` and a `Parse(args, out int
  planet)` overload (the existing single-argument `Parse` now delegates to it, unchanged for
  every existing caller/test); `galaxymap confirm`'s own rejection note updated to point at
  `galaxyaccept` as the shipped alternative and explain why the two are different shapes.
  `galaxyaccept` itself is registered as its OWN top-level verb (`ConsoleCommand.cs`'s verb
  table), not a `galaxymap` subcommand — it drives `HandleInputEvent` directly, the same
  category of primitive as `skipkey`, not `GalaxyMapVerbRule`'s `ShowGalaxyMapGui`/
  `HideGalaxyMapGui`/`SetSelectedPlanet` trio.
* `src/KotorCoop.Session/ConsoleCommand.cs`/`ConsoleCommand.Cheats.cs` — `CoopCommandKind.GalaxyMapSelect`/
  `GalaxyAccept`, the `galaxyaccept` verb-table row, `ParseGalaxyMap` extended for `select`,
  `ParseGalaxyAccept` (zero-argument, same shape as `ParseSkipKey`).
* `src/KotorCoop.Agent/CoopCommandDispatcher.cs` — the two new dispatch entries (`SendCheat`,
  same one-liner shape as `galaxymap open/close/stats`).
* `tests/KotorCoop.Tests/GalaxyMapVerbRuleTests.cs` — 9 new tests for `Select`'s parse (index
  round-trip, case-insensitivity, out-of-range refusal at both bounds, non-numeric refusal,
  missing-index refusal, and the single-argument overload still discarding the planet correctly).
* `tests/KotorCoop.Tests/ConsoleCommandVerbTableCoverageTests.cs` — sample lines for the two new
  kinds (`galaxymap select 3`, `galaxyaccept`) — the existing `DispatchTableHasARowForEveryCoopCommandKind`
  test (`CoopCommandDispatcherTableCoverageTests.cs`) already covers the dispatcher side via
  reflection over the full enum, so no separate entry was needed there.
* `docs/CONSOLE.md` — `galaxymap select`/`galaxyaccept` rows, `galaxymap stats` row updated for
  the new fields.
* `docs/limitations.md` — the galaxy-map row and item 13 in the NEEDS-LIVE list updated: the
  refusal can now be exercised end to end with zero human clicks (`galaxymap open` + `galaxymap
  select <n>` + `galaxyaccept` + `galaxymap stats`), though it has not yet actually been RUN
  against a live process.

Build: `pwsh src/KotorCoop.Plugin/build.ps1 -WarnAsError` clean (0 warnings). `dotnet build
src/KotorCoop.Agent/KotorCoop.Agent.csproj -warnaserror` clean (0 warnings). `dotnet test
tests/KotorCoop.Tests --filter "FullyQualifiedName~ConsoleCommand|FullyQualifiedName~CoopCommandDispatcher"`
195/195. `dotnet test tests/KotorCoop.Tests --filter "FullyQualifiedName~GalaxyMap"` 31/31 (was
22 before this pass's own 9 new `Select` tests).

### VERIFIED vs INFERRED / NEEDS-LIVE, this pass

**VERIFIED this pass**, against the sha256-matched GOG v1.0.3 `swkotor.exe`:
* The jump table (5 entries) and the FULL 185-entry byte-lookup table at `0x695B0C`, decoded
  programmatically (not by hand, unlike the 2026-09-03 pass) and cross-checked byte-for-byte
  against that pass's own hand-decode — identical results for the accept case, one count
  correction for the back case (see above).
* `CSWGuiInGameGalaxyMap`'s vtable (`0x00754910`), independently re-dumped: slot 15 (`+0x3C`)
  holds `HandleInputEvent`'s own address (`0x00695980`), matching the symbol database and the
  SAME slot index the dialogue panel classes use.
* `CSWPartyTable::SetSelectedPlanet`'s full ABI (`__thiscall(this, int nPlanet)`, `ret 4`) —
  this pass independently re-disassembled it (`tools/disasm.py 0x563B60 60`) rather than only
  citing the 2026-09-03 pass's own transcript, confirming the `ret 4` tail that pass's own
  excerpt did not show.

**NEEDS-LIVE** (never exercised on a running process this pass):
* Everything the 2026-09-02/2026-09-03 sections above already list as NEEDS-LIVE for the splice
  itself is unchanged — this pass only adds a way to FIRE the accept click without a mouse, it
  does not touch the splice's own install/refuse/allow logic.
* Whether a REAL mouse click on the Accept button fires raw event `0x27`, `0x2D`, or something
  this analysis has not considered (a mouse-down/mouse-up pair producing two different codes,
  neither of which is the one a synthesised call sends) — the dialogue-panel cross-check above is
  the best available evidence short of a live capture, not a substitute for one.
* Whether `HandleInputEvent(panel, 0x27, 1)` reproduces every side effect a real click has on the
  `CSWGuiButton` control itself (hover/pressed visual state, any per-button click bookkeeping) —
  this primitive enters the SAME dispatch a real click does, but does not simulate the mouse-down/
  mouse-up sequence that would normally precede it; this is the reason `Odyssey.Dialogue.SkipKey.cs`
  itself already flags for `skipkey`; the same caveat is inherited here rather than re-litigated.
* `galaxymap select <n>`'s own live behaviour: whether `SetSelectedPlanet` genuinely accepts a
  story-unlocked planet index and rejects a locked one exactly as this pass's disassembly-based
  bounds-check reasoning predicts, on a real save with real `K_KOTOR_MASTER`/availability state.
* `galaxymap stats`'s three new fields (`open=`, `selectedPlanet=`, `panelIsGalaxyMap=`) —
  reads only, never exercised live.
* Whether "the panel stays usable" after a REFUSE actually holds under `galaxyaccept` specifically
  — the 2026-09-02 section's own reasoning (jumping to `HandleInputEvent`'s own `NoOpExit` leaves
  the panel exactly as a non-matching event would) should apply identically whether the event was
  sent by a real click or by this primitive, since the splice cannot distinguish the two, but this
  has never been confirmed by actually trying to interact with the panel again afterward.

### Exact live recipe for the next session with the rig

Prerequisite: both instances at the galaxy map console, or `warp ebo_m12aa` on both (the
2026-09-02/2026-09-03 sections' own recipes). `galaxy_map_host_only=1` and `follow_lead=host`
are the shipped defaults — no `coop.ini` edit needed for the baseline case below.

**1. REFUSE case — a CLIENT under `follow_lead=host` (⚠ NOT the shipped default — that is `any`; set it explicitly, see the 2026-09-03 correction addendum):**

```
(client) warp ebo_m12aa
(client) galaxymap open
(client) galaxymap select 5
(client) galaxyaccept
(client) galaxymap stats
```

Expected console/log lines, in order:

* `galaxymap open` → `applied`, `"ShowGalaxyMapGui called — CGuiInGame+0x30 0 -> 1, ... panel
  0x... (open)"` (per the 2026-09-03 section's own live-confirmed shape; if the very first open
  after a `warp` reports refused with `+0x30 = 1` already set, run `galaxymap close` once first —
  the same stale-flag note that section already records).
* `galaxymap select 5` → `applied`, `"SetSelectedPlanet(5) applied — CSWPartyTable+0xE0 <old> ->
  5"` — **IF** planet index 5 is one this save has unlocked; otherwise expect the no-op detail
  naming `available_planets`/`selectable_planets` (try a different index, or a save further into
  the story, or use `galaxymap stats` to read back whatever the map already had selected and
  proceed with that instead — the accept path does not require this step to succeed to exercise
  the REFUSAL, only to prove an ALLOW-path departure for real).
* `galaxyaccept` → `applied`, `"HandleInputEvent(panel, 0x27, 1) sent — CGuiInGame+0x30 1 -> 1,
  GalaxyMapConfirmHook refused 0 -> 1, allowed 0 -> 0"` (busy stays 1: refused, so
  `HideGalaxyMapGui` never ran; the click sound still plays, per the 2026-09-02 section's own
  disassembly, since the refusal fires just after `PlayGuiSound` in program order).
* Plugin log (this instance): `galaxy-map: confirm refused - only the host can choose a
  destination (follow_lead=host)`.
* Chat (both instances, `PostLocalChatLine`): `only the host can choose a destination`.
* `galaxymap stats` → `installed=True, refused=1, allowed=0, open=True, selectedPlanet=5 (or
  whatever select left it at), panelIsGalaxyMap=True`.
* **Key correctness checks**: the CLIENT's own module does NOT change; `k_sup_galaxymap` never
  ran on the client; the map stays open on the client's screen (`open=True` in the stats above);
  a second `galaxyaccept` right after the first should refuse AGAIN identically (`refused 1 -> 2`)
  — proving "the panel stays usable" rather than wedging after one refusal.

**2. ALLOW case — the HOST, same console, same session:**

```
(host) warp ebo_m12aa
(host) galaxymap open
(host) galaxymap select 5
(host) galaxyaccept
(host) galaxymap stats
```

Expected: `galaxyaccept` → `applied`, `"... GalaxyMapConfirmHook refused 0 -> 0, allowed 0 -> 1"`,
busy flips `1 -> 0` (accept closes the map — `HideGalaxyMapGui` runs on this path, per the
2026-09-02 section's own disassembly of `0x00695A50`-`0x00695A60`), the vanilla take-off sequence
begins (movies queue, per §1.4's `CinematicWatch` coverage), and `K_FUTURE_PLANET` gets set to
whatever `galaxymap select` chose. `galaxymap stats` afterward: `allowed` incremented, `refused`
unchanged, `open=False`.

**3. `follow_lead=any` control:** repeat recipe 1 on the client with `coop.ini`'s `follow_lead`
set to `any` (or absent). Expect NO refusal — `galaxyaccept` reports `allowed` incrementing
instead of `refused`, and the client's own confirm proceeds normally (mirroring the 2026-09-02
section's own step 3).

**4. Stability check:** repeat recipe 1's `galaxymap select <n>` / `galaxyaccept` several times
back to back with different planet indices, watching for any stuck input, a panel that silently
stops responding, or a delayed crash a few seconds later — the same class of symptom the
2026-09-02 section's own live test plan item 4 already asks for, now fully scriptable with no
human click anywhere in the loop.

## 2026-09-03 — LIVE: `galaxymap unlock`, and `galaxyaccept` drives a REAL interplanetary departure end to end

Three live runs on the rig (host GOG pid, client GOG-p2 pid) this pass, on the only saves the
rig has — all Endar Spire (`000001 - AUTOSAVE`). Two of the three had the client connected the
whole time. New this pass: a `galaxymap unlock <n>` STAGING VERB (CheatKind 72; `GalaxyMapVerbRule`,
`Odyssey.UnlockGalaxyMapPlanet`, `CheatController.GalaxyMap.cs`) that writes `CSWPartyTable`'s
`available_planets[n]` (+0x60) and `selectable_planets[n]` (+0xA0) to 1 — the two arrays
`SetSelectedPlanet` (0x00563B60) gates on. Nothing in the shipped flow calls it; it exists purely
so the real departure can be exercised on a save the story has not unlocked.

**Why it was needed.** The 2026-09-03 `galaxyaccept` addendum's own live pass (06:48) found
`galaxymap select 5` and `select 0` both NO-OP'd (`CSWPartyTable+0xE0` stayed -1) on the Endar
Spire save, so `galaxyaccept` ran the confirm script with no destination and nothing departed —
the ALLOW path was proven to fire the hook but never to run a real transition. `unlock` closes
that gap: after `galaxymap unlock 5`, `select 5` applied cleanly (`+0xE0 -1 -> 5`), confirming
both gate offsets from this doc's own §1.2 against the live binary a second way (the write, not
just the paired accessor names).

**What `galaxyaccept` then actually did — the first end-to-end confirmation.** With planet 5 (and,
in run 2b, planet 4) selected, `galaxyaccept` on the host reported `allowed` incrementing (1 -> 2
-> 3 across the three runs, `refused` stayed 0) and the map closed — AND, for the first time, a
genuine scripted departure followed, exactly the `k_sup_galaxymap -> ST_PlayPlanetToPlanet ->
StartNewModule` chain §1.3 traced:

* the module transition queued the take-off movie (`08.bik`; run 2b queued `05_4c` + `08`),
* `StartNewModule` ran into `STUNT_00` — the Ebon Hawk launch cutscene — with a forced
  `StartDialog` and further movies (`0C`, `05_4a`),
* and the engine returned to a FRESH `ebo_m12aa`, never straight to a planet surface — precisely
  §1.3's "Leg 1: confirm -> back into a fresh Ebon Hawk" result. Both host and client ended on
  `ebo_m12aa` at the same coordinates.

So the accept path is now confirmed to run a real interplanetary departure, not merely to close
the panel — the last open NEEDS-LIVE from the `galaxyaccept` addendum.

**The transition happens inside a frozen main thread — two consequences.** Each `StartNewModule`
ran during a ~17–45 s window where the plugin's frame hook did not fire at all (`the main thread
was gone for 17156 ms` … `44907 ms`). Two things follow, both observed:

1. **No scripted announce was published.** `TransitionAnnounceWatch.OnScriptedTransitionBegan`
   keys on the rising edge of `Odyssey.IsMoveToModulePending()`; with no frame callback running
   during the whole transition, that edge is never observed, so no `announce: scripted transition
   beginning (GalaxyMap)` line appears (0 in the log) even though `transition_announce_scripted`
   defaults ON. This is the exact "best-effort … the flag may already have been drained by the
   time this frame sees it" hedge `PluginHost.Frame.cs` already documents — now confirmed to be
   the common case for a galaxy-map departure, not a rare race. The fallback module-change
   detection still carries any follow.
2. **The `PartyCountRestoreRule` crash guard never armed** (no `party count: bounded` line) for
   the same reason — and the host did NOT crash in either peer-connected run, so on this save the
   `DoPCAutosave` array walk did not hit an unresolvable member. (Run 1, a solo-then-rapid pass,
   logged one FIRST-CHANCE `0xC0000005` in the engine's own `CGameObjectArray::Destructor` inside
   `UnloadModule`/`LoadModule` during a follow-on `warp`, and that process later exited
   `0xC0000005`; two clean peer-connected repeats followed, so this reads as the retail engine's
   known rapid-transition teardown fragility, NOT a mod regression — see
   [[kotor-coop-warp-crash-and-snapshot-lesson]] and the transition-crashes write-up.)

**The client CANNOT follow an interplanetary departure — a real, documented limitation.** The
destination passes through `STUNT_00`, which the follow logic refuses by design
(`TransitionPump.Travel.cs`'s `stunt*` gate), and the departure ends on a different save-state
world. In run 2b the client stayed on `ebo_m12aa` while the host went `ebo -> STUNT_00 ->
tar_m10aa -> ebo`, and after sitting it diverged with a `W_SAVE` "different save: enemies, doors,
items and party are off — rejoin" notice. Galaxy-map departure is therefore NOT yet a
coop-followable transition; it is a host-local staging primitive today. See `docs/limitations.md`.

### 2026-09-03 correction — `follow_lead`'s shipped default is `any`, not `host`

Earlier recipe steps in this doc (the 2026-09-02 plan's step 2, the plugin-side plan's step 1,
and the `galaxyaccept` addendum's REFUSE recipe) called `follow_lead=host` "the shipped default."
That is WRONG and has been corrected inline. The code default is `FollowLeadMode.Any`
(`CoopConfig.cs:783` parses `follow_lead` with default `"any"`; `AgentSettings.cs`), `docs/CONFIG.md`
lists `any`, and `profiles/coop.ini.example` ships it commented (`# follow_lead = any`). The rig's
own `coop.ini` files leave it at the `any` default. Consequence for the REFUSE recipe: under the
TRUE default a client's own galaxy-map confirm is ALLOWED (lead-swap), not refused —
`GalaxyMapConfirmSuppressionRule` refuses only a client under an EXPLICIT `follow_lead=host` with
`galaxy_map_host_only=1`. To exercise the refusal live you must set `follow_lead = host` in the
client's `coop.ini` first.
