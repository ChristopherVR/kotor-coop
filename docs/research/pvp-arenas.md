# PvP arenas: where to fight, how to ship one, how to get in and out

Status: research proposal, 2026-10-09. Nothing here is built or run. Companion to
[pvp-product.md](pvp-product.md) (rules, consent, pause budget, handicap), which this doc does not
repeat. Evidence below was read from the installed GOG data with `tools/kotorfiles.py` and
`tools/ncsdis.py` (read-only), plus the repo sources named inline. Tags: **VERIFIED** = read from
game files or repo code here; **INFERRED** = deduced; **NEEDS-LIVE** = must be tried in the game.

## 1. Existing spaces that could host a fight

Measured per module: rooms (`.lyt`), creature/trigger counts (`.git`), the area `OnEnter`
script (`.are`), and the walkmesh bounds of the room (`.wok`, BWM vertices).

| Module | Area | Rooms | Creatures / triggers | OnEnter | Verdict |
| --- | --- | --- | --- | --- | --- |
| `tar_m02ae` | Taris Upper City Cantina (contains the duel ring) | 10 | 40 / 7 | `k_ptar_a02ae_en` | Best existing ring (below) |
| `korr_m35aa` | Sith Academy | 23 | 36 / 7 | `kor35_enter` | Too big, story-coupled |
| `ebo_m12aa` | Ebon Hawk | 17 | 0 / 16 | `k_pebo_skybox` | Corridors, no open floor |
| `danm14aa` | Dantooine Courtyard | 9 | 22 / 9 | `k_pdan_14a_area` | Open, but wildlife and quest scripts |
| `danm15` | Dantooine Ruins | 2 | 5 / 5 | `k_pdan_15_area` | Small, Rakata doors and starmap scripts |
| `unk_m44ac` | Unknown World Temple Summit | 2 | 1 / 18 | `k_punk_44ac_en` | Boss-scene triggers (Bastila fight) |
| `danm14ae` | Dantooine Crystal Cave | 2 | 9 / 4 | none | Kinrath and egg placeables |

### The Taris duel ring (`tar_m02ae`, VERIFIED)

- The duel ring is one room, `m02ae_07a`, placed at (200, 70) in `m02ae.lyt`. Its walkmesh
  (`m02ae_07a.wok`) has 114 vertices and 198 faces and spans x 185-215, y 85-117, so about
  30 x 32 units. It has one door hook (`door_07`, behind the placeable-door `tar02_arenadoor`).
- Ready-made start points in `m02ae.git` `WaypointList`: `tar02_wppcarena` (214, 100) against
  `tar02_wpopparena` (187, 100), 27 units apart; closer pair `tar02_wppcarena2` (210, 100) /
  `tar02_wpopparena2` (190, 100); a second pair `BP_FIGHT1_WP` (198, 94) / `BP_FIGHT2_WP`
  (202, 105); a spectator mark `tar02_wppccutsc` (200, 88). Four slots are easy: use the two
  pairs as team A and team B.
- Interference: the module has 40 cantina NPCs, 42 waypoints and 271 scripts in
  `tar_m02ae_s.rim`. `k_ptar_a02ae_en` reads globals `TAR_DUEL`, `DUEL_TIMER` and
  `TAR_SITHARMORPLOT` and tests `GetLoadFromSaveGame`; `k_ptar_02ae_ex` looks for item
  `ptar_sitharmor` on the party and calls `SetItemNonEquippable`. `k_ptar_duelring` removes
  party members and sets min-one-HP on the PC (see [duel-rings.md](duel-rings.md)). Triggers
  `tar02_strdue`, `tar02_dueply`, `tar02_duekil` belong to the story duel (Ajuur/Bendak
  scenes). Entering outside the story is allowed (the `warp <module> [waypoint]` console verb
  goes through the session so the peer follows; CONSOLE.md), but late-game saves have the
  globals in unknown states, so crowd, Bendak and duel-organiser scripts can fire. Treat
  as a prototype only.
- Walkmesh and lighting exist, so no art is needed. The area is `Unescapable=1`.

### Others, briefly

- Sith Academy `korr_m35aa` (23 rooms, 105 placeables) has the NPC-vs-NPC duel at trigger
  `k35_init_duel` (185, 74); heavy final-test globals (`KOR_FINAL_TEST`), see duel-rings.md.
- Ebon Hawk: no creatures, but the module is the mod's hub for galaxy-map departures and its
  16 triggers/`k_pebo_skybox` OnEnter are tied to those flows. No arena-sized open floor.
- Dantooine Ruins: the smallest named place with two 90-unit-wide rooms, but its starmap and
  door scripts (`dan15_*`) fire on the story path.
- Swoop track and Pazaak are not combat spaces.

## 2. Shipping a custom arena module

How KOTOR modules are laid out (VERIFIED on `tar_m02ae`):

- `modules/tar_m02ae.rim` holds only `m02ae.are`, `m02ae.git`, `module.ifo`. The companion
  `tar_m02ae_s.rim` holds blueprints (`.utc/.utd/.utp/.utt/.uts/.uti`), dialogues, `.ncs` scripts
  and `m02ae.pth`.
- Everything geometric lives in the global BIFs, keyed by resref: `m02ae.lyt`
  (`layouts.bif`), `m02ae.vis` (`lightmaps2.bif`), room models and walkmeshes
  `m02ae_01a.mdl/.mdx/.wok` (`models.bif`), lightmaps `m02ae_01a_lm0.tga` and so on. The same
  holds for `danm15` (`m15aa.vis` in `lightmaps5.bif`, `m15aa_01a.mdl/.wok` in `models.bif`).
- `module.ifo` names the entry area (`Mod_Entry_Area`, `Mod_Entry_X/Y/Z`, `Mod_Entry_Dir_X/Y`),
  lists areas in `Mod_Area_list` (one struct, `Area_Name`), and carries module script slots
  (`Mod_OnModLoad`, `Mod_OnClientEntr`, `Mod_OnPlrDeath`, `Mod_OnSpawnBtnDn` ...). Area
  scripts (`OnEnter`, `OnExit`, `OnHeartbeat`, `OnUserDefined`) live in the `.are`.

**Minimal clone into a clean arena** (INFERRED from the format, NEEDS-LIVE):

1. Build `modules/k_coop_arena.mod` (ERF V1.0) with:
   - `module.ifo`: copy of `tar_m02ae`'s with every script slot blanked except what the mod
     wants, `Mod_Entry_Area` = the area resref, entry point at `tar02_wppcarena`.
   - `<area>.are`: copy with `OnEnter`/`OnExit` blanked and `Unescapable=1` kept.
   - `<area>.git`: copy with `Creature List`, `Placeable List`, `Door List`, `TriggerList`,
     `SoundList`, `CameraList` emptied, keeping only the waypoints (spawns) and, if wanted, a few
     `k_coop_*` waypoints. The arena door can be dropped; room 07a then has an open doorway,
     so keep `tar02_arenadoor` closed or ship the door to avoid leaving the ring.
   - `<area>.pth` if the area keeps its own path file (copy from the `_s.rim`).
2. Reusing the original area resref (`m02ae`) means no `.lyt/.vis` need shipping at all, as the
   BIF copies resolve. Using a new area resref instead requires copying `.lyt` (943 bytes) and
   `.vis` out of the BIFs under the new name. Which is safe is NEEDS-LIVE (area-resref
   caches are not documented here).
3. No `.mdl/.wok/.tga` are needed in either case. Only BioWare's own room models are reused, no new
   art.

Repo support (VERIFIED by reading sources):

- `GffWriter.cs` (full writer with every field type, structs and lists) and `GffCopy.cs`
  (field-for-field copy, built for lifting `Mod_PlayerList` out of `module.ifo`) cover the
  `.ifo/.are/.git` edit. `ErfArchive`/`RimArchive` read both container types.
- `ErfWriter.cs` has only `ReplaceMember` (rewrites one member of an existing ERF). A
  `Build(members)` that writes a fresh header, key table and resource table is new but small.
- `DataModInstaller.cs` merges `repute.2da`/`appearance.2da` into Override only. The
  installer (`PackageInstaller.cs`) copies `Override\k_coop_*` and nothing else, and
  `uninstall` removes only those; a `modules\k_coop_arena.mod` is a new target, needing the
  install, `install --check`, uninstall and `install.sh` mirror (see the memory note on the
  Linux package) to learn a second directory.
- The content digest (mod-compatibility.md) already covers module `.rim/_s.rim/.mod`, so a
  shipped `.mod` that differs between players is detected like any other mod.
- Precedence of `.mod` over `.rim` for the same module name is community-documented but
  not read here; use a new module name (`k_coop_arena`) so the question never arises.

Licensing and practical: ship only the edited `.ifo/.are/.git` (the mod's own GFF
transformations of data the player already owns) and BIF references; do not redistribute
BioWare models or textures. Better still, generate the `.mod` on the player's machine at
install time from their own `tar_m02ae.rim`, the same way `DataModInstaller` generates its
2DA, so the package contains no BioWare bytes at all.

## 3. Getting in and out without touching the campaign

Existing machinery (VERIFIED in docs/code):

- `warp <module> [waypoint]` routes through the session so the peer follows (CONSOLE.md).
  The retail engine crashes on rapid warps ([transition-crashes.md](transition-crashes.md)),
  so arena entry must be a single deliberate transition, not a chained one.
- Module changes by script (`StartNewModule`) and the galaxy-map leg are followable
  ([galaxy-map-and-party-select.md](galaxy-map-and-party-select.md)).
- `save <name>` keeps a named copy of a quicksave; `loadgame <slot>` loads a save without the
  pause-menu click; host load triggers world re-adoption on joiners
  ([session-save.md](session-save.md), `host_load_readopt`, default OFF).
- A joiner plays the host's world with their own character via the pifo login fork
  ([world-adoption.md](world-adoption.md)); each peer's character is a phantom in the other's
  process, owned and damaged by its owner (`PhantomHitHook`).

Two entry flows, cheapest first:

**A. In-place arena (recommended first).** `arena` / `duel arena` from the chat or Co-op menu.
The host records `(module, position)` per player, then warps everyone to the arena module and
its spawn waypoints. On end, the host warps everyone back to the recorded spots. Cost: no new
save machinery. Risks: the arena module's state is written into the campaign save on the first
leave (harmless but present), and a quicksave or autosave inside the arena would capture it,
so saving must be blocked while in the arena (pvp-product.md already blocks autosave in
duels). HP/Force/consumables: snapshot at entry, restore at exit (no stakes).

**B. Throwaway world.** The host names a save (`save pre-arena`), warps everyone in, and at
the end `loadgame`s that slot so the campaign is restored byte for byte, including anything
that went wrong in between. This is the only strictly safe flow for items, XP and consumables
but depends on the host-load re-adoption path (flaky-by-history, default OFF) and restores a
slightly older state if quest play continues between. Use for ranked or "real loot" later.

Entry from the main menu (no campaign at all) would require a headless arena world with each
character copied in. That is possible through the same pifo/newcharacter login
([world-adoption.md](world-adoption.md) section on `join_mode = newcharacter`), but is the
largest piece of work and not needed for a first version.

Different levels: the arena should apply a handicap, not clone stats. Existing host-authoritative
damage rewriting (`ScaleHostileDamage` at the `OnApplyDamage` detour, `enemy_damage`) is
restricted to non-player attackers, so a PvP branch there (scale by level gap, cap 50 percent,
per pvp-product.md section 3) is the cheapest option. Fixed-loadout "arena level N" needs
granting feats, powers and gear (class-and-power-grants.md) and is a later step.

## 4. Modes

| Mode | Players | Round flow | Notes |
| --- | --- | --- | --- |
| Duel | 2 | countdown 5, fight, first PC at 0 HP loses, heal, re-spawn, best of 3 | the baseline |
| 2v2 with companions | 2 + AI | companions follow; first side with both PCs down loses | needs companion hostility scoping (combat-participation.md faction lever) |
| Team vs team | 3-4 | team scores by downs, respawn at team waypoint after 5 s | uses the two waypoint pairs |
| Last man standing | 3-4 | no respawn, round ends at one standing | elimination is clear, hardest on spectators |

Death is not real: party members, the PC included, go "temp dead" at 0 HP
([death-handling.md](death-handling.md)). A round ends on the first temp-dead report from the
owner; the host then restores health (the revive path exists). Persistent state: none by
default; an in-session scoreboard (win/loss per player, chat history only) is the sensible
ceiling, as in pvp-product.md. No XP or loot transfer.

## 5. Recommendation and order of work

Cheapest credible arena: **the Taris ring room (`m02ae_07a`) in a stripped clone module**,
entered by flow A. The room is 30 x 32 units, has four ready-made spawns, a single door,
walkmesh and lighting, and needs zero new art.

1. Rules first, in the campaign world (pvp-product.md MVP). Nothing shipped.
2. Prototype arena by `warp tar_m02ae tar02_wppcarena` for the host and a peer-follow, with
   the story's cantina scripts ignored. Learn what fires (crowd, Bendak, TAR_DUEL globals) and
   whether the ring is acceptable as is. NEEDS-LIVE.
3. `ErfWriter.Build` plus a clone generator (`GffCopy` + edits) run at install time, producing
   `modules/k_coop_arena.mod`; extend the installer, `install --check`, uninstall, the Linux
   `install.sh` mirror and the digest tests. Verify `.mod` loads with a new module name.
4. Arena command and round flow (countdown, spawn pair, win detection, restore, return),
   with save blocking.
5. Handicap in the damage detour for player attackers; level display in the accept prompt.
6. Team spawns, 2v2 companions, scoreboard; then flow B (throwaway save) and fixed loadouts.
