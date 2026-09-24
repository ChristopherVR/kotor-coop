# Enemy health baseline persistence

**Status (2026-09-24): wired end to end, unit-tested, NOT live-verified.** Shared block
version 76. The `enemy_health` default stays at 100%. The recipe at the end of this page has
not been run yet. It has to pass before `auto` can become the default again.

## The defect this closes

The controller writes the scaled maximum into the creature's stored `+0xE0` field. The save
serialises that field, so the next load started with an empty latch and scaled the saved
creature again. The restart probe showed this at 150%: `131/197 -> 197/296`. The per-epoch
latch cannot tell these cases apart on its own, and inferring from the numbers is unsafe (a
vanilla 23 looks the same as a 15 scaled to 150%). So the design never guesses. Every saved
creature's unscaled maximum is carried beside the archive, bound to the archive's exact bytes.
A world whose saved health cannot be proven gets no health writes at all.

## Static facts this rests on (GOG `swkotor.exe`, `tools/disasm.py`)

- **GIT `HitPoints` is the stored maximum `+0xE0`.** `CSWSCreatureStats::SaveStats`
  (`0x5B1B90`) writes `HitPoints` from `movsx eax, word [creature+0xE0]` at
  `0x5B1FCD..0x5B1FDD`. `ReadStatsFromGff` (`0x5AFCE0`) reads it straight back into `+0xE0` at
  `0x5B1264..0x5B127B`. `MaxHitPoints` is the derived `GetMaxHitPoints(1)` (vtable `+0x98`) and
  the join does not use it.
- **Save directory names.** `CServerExoAppInternal::LoadGame` (`0x4BA640`) builds
  `SAVES:` + `%06d - %s` (format `0x7455AC`, at `0x4BA71B`) from `(slot, name)`. Slot `-1` uses
  the name as the directory (`0x4BA72B`). `SaveGame` (`0x4B58A0`) creates the same
  `%06d - %s` directory (see `SaveFlushHook`).
- **Saved object ids are restored on load.** This is the premise of `SaveDigest`'s
  `TryHashSavedGit`, and the reason an id can key a baseline at all.

## Shared block version 76 (region 0x5400..0x57C0, block grown to 0x5800)

The layout and codec are in `src/KotorCoop.Protocol/EnemyScalingBaselineChannel.cs`, compiled
into both halves. Each of the three channels is a seqlock: odd while it is being written, and a
nonzero even value once published.

| Offset | Direction | Content |
| --- | --- | --- |
| `0x5400` | plugin → agent | world/export page sequence |
| `0x5404..0x5440` | plugin → agent | world generation, origin, origin load-request generation, origin flags, `WorldLoadSerial` at world-up, health mode, export generation, previous world generation, previous world's FINAL export generation, page index/count, record count + flags (complete, overflow), module name (16 bytes) |
| `0x5440..0x5640` | plugin → agent | up to 64 latch records `(u32 id, u16 base max, u16 percent)` |
| `0x5640` | agent → plugin | export acknowledgement (the consumed sequence) |
| `0x5648` | agent → plugin | verdict/import page sequence |
| `0x564C..0x5660` | agent → plugin | world generation, verdict (Fresh/Import/Refuse), page index/count, record count, total |
| `0x5660..0x5760` | agent → plugin | up to 32 import records |
| `0x5760` | plugin → agent | import acknowledgement |
| `0x5768` | plugin → agent | save-request sequence |
| `0x576C..0x57C0` | plugin → agent | slot, world generation, name length, request FILETIME (UTC), name (64 bytes) |

`PluginHost` `_block->Version` = 76 and `KotorGameBridge.PluginSignature` byte `0x4C` were
bumped together. No wire packet, cheat kind, console verb or `coop.ini` key was added.

## The pieces

**Plugin (`EnemyScalingController`, `PluginHost.EnemyScalingBaseline.cs`).** Every new world
epoch calls `BeginWorld`. It releases the latch, advances the world generation, opens the
world as **Pending**, and publishes the world-up page. Pending means no health write of any
kind. The latch is no longer released by `Invalidate()`. `InvalidateAll` also runs for
"transition flushed but not taken", and in that case the same creatures stay standing with
their scaled maxima. Clearing the latch there would have scaled them all again, a second
compounding path that this change also closes. The agent's verdict arrives through
`EnemyScalingImportGate`:

- **Fresh**: scale exactly as before.
- **Import**: the verified saved baselines are seeded into `ScaledObjectLatch` as *imported*.
  The first sighting skips every latched id, so an imported creature is never multiplied
  again. Before its first write, its live stored maximum must equal
  `ScaleMaximum(base, recorded %)`. Any other value refuses the whole world.
- **Refuse**, a contradictory import, a latch overflow, or a live mismatch: the mode becomes
  **Refused**. Health stays exactly as loaded for the rest of the world, and the world's export
  is marked incomplete.

The latch is exported page by page. A changed latch or mode starts a new export generation.
An acknowledged page advances to the next page, cycling, so a restarted agent still sees every
page. The native `SaveGame` entry (`SaveFlushHook`'s existing detour; no new hook) publishes
every request's slot, name and time before it decides whether to hold it.

**Percentage change** (`EnemyScalingReconcileRule`). An imported creature whose recorded
percent differs from the current one is rescaled from its ORIGINAL maximum. Current health
keeps its ratio against the old stored maximum, with half-up rounding, at least 1 while alive,
and never above the new maximum. A dead creature (current ≤ 0) is never written. It keeps its
saved numbers and recorded percent, so it is never resurrected. This also runs at 100%, so a
scaled save loaded solo comes back to vanilla maxima. Creatures scaled at first sighting in
the running world still keep the percent they were scaled at. The mid-module behaviour for
them is unchanged.

**Agent (`EnemyScalingPersistence`, pumped by `CoopCommandDispatcher` every 150 ms,
independent of any session).** It keeps a **lineage**: the chain of worlds since the last
proven load or new game. For each module, the lineage records either *covered* (the complete
set of scaled baselines, possibly empty) or *unknown*.

- **World-up verdict.** Unknown lineage or unreadable module → Refuse. Covered module →
  Import its records (an empty set is still an Import). Unknown module → Refuse. A module
  neither in the loaded archive nor visited in the lineage → Fresh. The engine builds that
  module from its pristine GIT, so none of its creatures has been scaled.
- **Collection.** Export pages feed `EnemyScalingBaselineCollector`. When a world ends, the
  new world's page names the old world's final export generation. The old module becomes
  covered only if the collector has that exact export complete. A Pending world that never
  got its verdict keeps the coverage it was loaded with, because nothing was written. A
  Refused world, an overflow, a final export the agent missed, or a missed world generation
  makes the module (or the whole lineage) unknown.
- **Save capture (the settled-archive rule).** A new save-request sequence arms a capture of
  `saves/%06d - <name>/`. The archive counts as settled when all of these hold:
  `SAVEGAME.sav` and `savenfo.res` exist; both were written no earlier than the request time
  minus 2 s; `SAVEGAME.sav` opens with `FileShare.Read`, so no writer holds it; and its length
  and write time have not changed for 1.5 s. Captures are abandoned after 60 s (a held save
  that was dropped, a refused save). A request already in the mailbox when the agent starts is
  never captured. The archive is then read once into memory and hashed, and every saved module
  is joined (`EnemyScalingArchiveJoin.TryJoinModule`) against the lineage's coverage. The
  current world uses its latest complete export, and the capture waits if a newer export is
  still in flight. For each latch entry:
  - absent from the save → dropped;
  - saved `HitPoints` equal to the scaled value → kept, with the saved tag/template;
  - saved `HitPoints` equal to the original → dropped, because the creature was scaled after
    the store (or rounding made no change), so the loaded creature is unscaled and will be
    scaled once, fresh;
  - anything else → the module is unproven.

  `EnemyScalingBaselineStore.TryWriteForArchive` writes schema 2 (`CoveredModules` plus
  records) atomically, bound to the SHA-256 of exactly the joined bytes. It refuses if the
  archive changed at any point.
- **Load binding.** A world-up with origin Load names the provenance generation it came from.
  The agent binds it only when all of these hold:
  1. the provenance mailbox still holds that generation, and its status is Captured or
     CapturedDirectory;
  2. there is a successful-load edge: `WorldLoadSerial` at world-up is later than at the
     request (an in-world load's unannounced teardown), or the request was made with no world
     standing (main menu; plugin flag);
  3. the directory resolves inside `saves`;
  4. the archive's active module (`savenfo.res` `LASTMODULE`) is the loaded module;
  5. the archive parses, and its `SaveDigest` identity is readable.

  Every saved module then starts as unknown. It becomes covered only if the sidecar reads back
  bound to this archive's SHA-256 and identity, and every one of its records matches the saved
  creature's id, tag, template and stored maximum (`VerifyImported`). A bound load with no
  usable sidecar is still a valid lineage: its saved modules refuse and new modules scale.

**Sidecar location.** `kotorcoop-enemyscaling.json` sits inside the save directory. Staging
(`TryStageSaveAsAutosaveFromDirectory`) and the host-to-joiner world transfer
(`WorldTransfer.ReadSaveDirectory`) copy every file in the directory, so a byte-identical
`000001 - AUTOSAVE` and a joiner's transferred world carry their proof with them. The sidecar
is bound to the content, not to the directory name. `TryReadForArchiveContent` requires the
SHA-256 and the content identity, not the slot or name: identical bytes hold identical
creatures. A joiner therefore imports the host's baselines and reconciles to the same
host-authoritative percent.

## Fresh-world classification (why a never-saved world still scales)

The plugin reports the origin; the agent decides what to trust.

| Plugin sees at world-up | Origin | Agent does |
| --- | --- | --- |
| A native load request published since the previous world-up | Load | Binds the exact archive (above); otherwise the lineage becomes unknown |
| No load request, announced teardown (door, trigger, scripted `StartNewModule`, co-op warp) | Transition | Continues the lineage if it saw the previous world |
| No load request, unannounced teardown or first world since start, load observers installed, module `end_m01aa` | NewGame | Starts an empty lineage: every module is Fresh until visited |
| Anything else | Unknown | Lineage unknown: every world refuses until the next proven load or new game |

The new-game rule needs the load observers installed, so an unobserved load path can never
look new. It also needs the retail start module, so a load that bypasses both hooked entry
points could only be misread if it lands in `end_m01aa` with no request observed. A modded
start module fails closed: 100%, and nothing is written. An agent that starts mid-playthrough
trusts nothing until the next load or new game.

## Known limits (all fail closed)

- A save made without the agent running, or before this build, has no sidecar. Its saved
  modules never scale until they are reloaded from a save that was captured. Modules visited
  after that load scale normally.
- The final export of a world is only as fresh as the agent's last read. An enemy scaled in
  the last ~150-600 ms before a door can leave that module unknown for the rest of the
  lineage.
- If the configured percent changes between a save and its capture (a few seconds), a
  reconciled imported creature no longer matches either value, and that module is unproven in
  that sidecar.
- `save_flush` off, or `SaveFlushHook` refusing its bytes, means no save requests, so no
  sidecars.
- A MainMenuSaveEntry load assumes the row's `+0x1D8` name forms the directory the same way
  `LoadGame` does. If it does not, the directory does not resolve and the lineage is unknown.

## NEEDS-LIVE

- The reconciliation write (stored max, then `SetCurrentHitPoints`). It uses the same
  write order as the live-verified first-sighting write.
- The settled-archive timing on a real save, including `savenfo.res` being rewritten by
  every save.
- Two assumptions about the files: that the engine ignores the extra JSON in a save
  directory, and that it deletes the stale sidecar when it overwrites a save.
- Classification of each origin on the rig: new game, main-menu load, in-world load,
  staged autosave, door, and galaxy map.

## Live-test recipe

Run it with `coop.ini enemy_health = 150` on the host. With 150, `auto` is not needed and a
single instance is enough for steps 1-5. Watch the plugin log (`scaling-baseline:` and
`scaling:`) and the agent log (`enemy-baseline:`).

1. **Scale once.** Start a new game (or load any save) and reach a hostile, or use
   `spawncreature n_sithsoldier001 1`. Expected:
   - plugin: `scaling-baseline: world N 'end_m01aa' up (NewGame)`, then
     `-> Fresh (no creature here carries a scaled maximum)`, then
     `scaling: enemy 0x.. hp 15/15 -> 23/23 (150%)`;
   - agent: `enemy-baseline: world N 'end_m01aa' (NewGame) -> Fresh`.
2. **Save.** Save to a new slot, e.g. "Game7". Agent:
   `enemy-baseline: save '000007 - Game7': sidecar written, 1 module(s) proven (k scaled creature(s)), 0 unknown`.
   The directory now contains `kotorcoop-enemyscaling.json`.
3. **Reload, no second multiplication.** Load `Game7` from the pause menu. Expected:
   - agent: `world M '...' bound to '000007 - Game7' (... 1 of 1 saved module(s) proven)` and
     `(Load) -> Import k baseline(s)`;
   - plugin: `-> Imported (k saved baseline(s) imported, never re-multiplied)` and NO
     `scaling: enemy 0x.. hp 23/23 -> 35/35` line for the same id;
   - `sethp.ps1` reads 23/23.

   Repeat the save and reload twice more; the value must stay 23.
4. **Percentage change.** Set `enemy_health = 200`, restart the agent (or adopt the host's
   setting), and reload `Game7`. Plugin: `scaling-baseline: saved enemy 0x.. hp 23/23 -> 30/30
   (150% -> 200% of original 15)`. That value is 30 and never 46. Wound it first (e.g. to
   12/23) and the result is `16/30`. Set `enemy_health = 100` and reload: `-> 15/15`.
5. **A dead enemy stays dead.** Kill a scaled enemy whose corpse persists, save, set a
   different percent, and reload. There is no `saved enemy 0x<id>` line for the corpse, and
   it stays dead.
6. **Fail closed.** Load a save made before this build (no sidecar). Agent:
   `no usable baseline sidecar ... saved module(s) stay unscaled`, then `-> Refuse`; plugin
   `-> Refused`; no `scaling: enemy` line in that module. Walk into a module never visited in
   that save: `-> Fresh` and scaling resumes.
7. **Joiner.** With a joiner adopting the host's world, the joiner's agent binds
   `000001 - AUTOSAVE` and imports the same baselines (the sidecar travels with the world).
