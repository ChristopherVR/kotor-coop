# No-RE features: data-driven & modding surfaces (research 2026-07-31)

What we can add to the co-op build using KOTOR's **data-driven / modding surfaces** — files and
config the modding community has documented for 20 years — with **zero new memory hooks and zero
exe RE**. Scope check first: the community's own verdict is that a *networked* co-op is impossible
from data alone (["a mod won't be able to add it… we can't edit the .exe"](https://lucasforumsarchive.com/thread/167002-a-kotor-coop-mp-mod),
[Deadly Stream](https://deadlystream.com/topic/11140-can-kotor-games-have-a-multiplayer-mode/)) — so
the networking stays ours. What data mods *can* do is give that networking better **bodies, faces,
factions, spawn points and install hygiene**. This doc is only those.

## What our pipeline already reads/writes (audit)

| Surface | C# (`src/KotorCoop.Memory/GameFiles/`) | Python (`tools/kotorfiles.py`) | Gap |
| --- | --- | --- | --- |
| GFF read (.utc/.utp/.uti/.utd/.utw/.git/.are/.ifo/.dlg/.jrl) | `Gff.cs` ✅ | `GFF` ✅ | — |
| GFF **write** | `GffWriter.cs` ✅ (used by `PhantomBlueprint.cs`) | ❌ (read-only tool) | Python can't author GFF |
| 2DA read | ❌ | `TwoDA` ✅ | **No C# 2DA reader** |
| 2DA **write** | ❌ | ❌ | **Neither writes 2DA** |
| TLK read | ❌ | `TLK` ✅ | **No C# TLK reader** |
| TLK **write / append** | ❌ | ❌ | **Neither appends TLK** |
| RIM/ERF/MOD read | `Archives.cs` ✅ | `RIM`/`ERF` ✅ | — |
| RIM/ERF/MOD **write** (build a module) | ❌ | ❌ | Can't pack a .mod |
| KEY/BIF index | `KeyIndex.cs` ✅ | `KeyIndex` ✅ | — |
| Module object-id prediction | — | `predict_object_ids` ✅ | (already have it) |

`tools/kotorfiles.py` states outright "Nothing here writes to disk; every open() is 'rb'." So the
whole toolchain can **read** every format but can only **write GFF** (C# side). That single gap —
2DA/TLK authoring — is what decides "ship a file" vs "refuse" below.

## 1. Data-driven surfaces and what each changes without RE

| Surface | Files | What it changes | Our verdict |
| --- | --- | --- | --- |
| **Creature/appearance table** | `appearance.2da` | model+texture per body slot, move speed, `perceptionrange`, head-track, sound; row referenced by `Appearance_Type` in a .utc | Row add = distinct P2 body. **2DA — TSLPatcher merge only, never raw** |
| **Head table** | `heads.2da` | head model + DS-corruption texture stages | Row add for a P2-only face |
| **Portrait table** | `portraits.2da` | GUI portrait, links to an appearance row, DS variants; drives name-plate/portrait art | Row add for a distinct P2 portrait/name band art |
| **Faction/reputation** | `repute.2da` | mutual like/hate between faction rows (0–100); <mutual> value decides auto-hostility | A "coop_player" friendly-faction row = phantom never provokes AI **without** per-tick faction patching |
| **Feats / classes / spells** | `feat.2da`, `classes.2da`, `spells.2da` | proficiency gates, class tables (no new Force-using class — engine crash), power rows | We already grant proficiency feats *in the .utc FeatList*, not the 2DA — keep it there |
| **GFF blueprints** | `.utc .utp .uti .utd .utw` | creature/placeable/item/door/waypoint instances | **Author freely as `k_coop_*` files — GffWriter already does** |
| **Dialog** | `.dlg` (GFF) | conversation trees, script hooks per node | Read for spectate; authoring a co-op .dlg is possible but not needed yet |
| **Scripts** | `.nss` source → `.ncs` bytecode | engine logic via `ExecuteScript` | Skip — routine dispatch already does this in-plugin; adds a compiler dep + collision risk |
| **Module files** | `.rim .mod .git .are .ifo` | area layout, spawns, module props | **Never override module-level resources** (a stray `.git`/`.ifo` applies to *every* module) — see `override-surfaces.md` |
| **String table** | `dialog.tlk` | every UI/name/dialogue string by StrRef | **Never ship — append via TSLPatcher `[TLKList]` only** |
| **GUI** | `.gui` (GFF, restype 0x7FF) | panel layouts; can't add hardcoded controls | Cosmetic-only, merge-install — see `override-surfaces.md` |
| **Override precedence** | `Override/` dir | Override > patch.erf/rims/mods > chitin.key/BIF | Proven in `override-surfaces.md`; our `k_coop_*` files win |

Sources: [Appearance 2DA — KotOR Modding Wiki](https://kotor-modding.fandom.com/wiki/Appearance_2DA),
[Detailed information of 2da files — Deadly Stream](https://deadlystream.com/topic/4725-detailed-information-of-2da-files/),
[New Playable Character Tutorial — ModDB](https://www.moddb.com/tutorials/new-playable-character-tutorial).

### The one hard rule that shapes everything
The community-standard installer **TSLPatcher / HoloPatcher** exists precisely because two mods must
be able to touch the *same* `appearance.2da` / `dialog.tlk` without clobbering each other. TSLPatcher
[modifies/adds 2DA rows and columns in the user's existing file](https://www.nexusmods.com/kotor/mods/2)
and [appends new TLK strings, then substitutes a `StrRef#` token](https://deadlystream.com/files/file/1039-tsl-patcher-tlked-and-accessories/)
into the 2DA/GFF that referenced it (config lives in `tslpatchdata/changes.ini`, sections
`[TLKList] [2DAList] [GFFList] [InstallList] [CompileList]`). **Therefore: any feature needing a 2DA
row or a TLK string must be a TSLPatcher/HoloPatcher merge-install, never a raw Override drop.** Pure
`k_coop_*` GFF blueprints have no such constraint (unique resref, collides with nothing) and ship raw.

## 2. Community co-op / multiplayer attempts — what's reusable

Every serious thread lands on the same conclusion, which is useful because it tells us what *not* to
waste time on:

- **[Deadly Stream "Can KOTOR have Multiplayer"](https://deadlystream.com/topic/11140-can-kotor-games-have-a-multiplayer-mode/)** and **[LucasForums "A KOTOR COOP MP mod?!"](https://lucasforumsarchive.com/thread/167002-a-kotor-coop-mp-mod)**: no engine networking; a data mod cannot add it; editing the exe is the only path — which is exactly the path we already took. No reusable data artifact, but it confirms our architecture is the *only* viable one and nobody has a competing blueprint to borrow.
- **[TSL "multiplayer" leftover in `swplayer.ini`](https://deadlystream.com/topic/5012-i-found-something-in-kotor-2s-files/)**: dead dev-era config, not a functioning code path — no value.
- **Engine reimplementations** — **[xoreos](https://xoreos.org/)** and **[reone](https://github.com/seedhartha/reone)**: neither exposes networking; xoreos is spectator-graphics only, reone has movement/walkmesh/dialogue but no MP. **Not** a runtime target for us. Their *value is as tooling and as a file-format oracle* (see §3) — reone in particular has a clean, readable GFF/2DA/module implementation to check our writers against.

Net: nothing to fork for gameplay; the ecosystem's contribution is **file-format libraries and installers**, which is where §3–§5 focus.

## 3. PyKotor / reone / xoreos-tools as authoring libraries

Ranked by fit for "generate co-op content programmatically."

### PyKotor (best fit — closes our 2DA/TLK write gap)
Active repo: **[github.com/OldRepublicDevs/PyKotor](https://github.com/OldRepublicDevs/PyKotor)**
(the older [NickHugi/PyKotor](https://github.com/NickHugi/PyKotor) is archived — "no longer maintained").
Pure-Python, read **and write** every format we lack, plus an `Installation` class that models
Override/chitin.key precedence and a `HoloPatcher` config emitter. Import shape (from repo docs):

```python
from pykotor.resource.formats.twoda import read_2da, write_2da   # 2DA read+write  ← our gap
from pykotor.resource.formats.tlk  import read_tlk, write_tlk    # TLK append      ← our gap
from pykotor.resource.formats.gff  import read_gff, write_gff    # GFF (cross-check GffWriter)
from pykotor.resource.formats.erf  import ERF, ERFType, write_erf  # build a .mod
from pykotor.resource.type import ResourceType
from pykotor.extract.installation import Installation            # Override/BIF-aware resource lookup
```

Also ships a CLI (`pykotor`, e.g. `uvx pykotor 2da2csv file.2da`) for quick inspection. **This is the
recommended tool** to (a) read the user's `appearance.2da`/`portraits.2da` at install time and compute
the next free row, and (b) emit a HoloPatcher `changes.ini` that merges our rows — i.e. the thing that
turns "ship a 2DA" (forbidden) into "merge a row" (safe).

### xoreos-tools (CLI, scriptable from C#/Python)
**[github.com/xoreos/xoreos-tools](https://github.com/xoreos/xoreos-tools)** — command-line, easy to
shell out to: `gff2xml`/`xml2gff` (GFF ↔ XML round-trip), `convert2da` (reads all 2DA/GDA;
**gained the ability to write KOTOR binary 2DA**), `tlk2xml`/`xml2tlk`, `unerf`/`unrim`, `ncsdis`
([Running xoreos-tools](https://wiki.xoreos.org/index.php?title=Running_xoreos-tools),
[Convert2da](https://github.com/xoreos/xoreos-wiki/blob/master/Convert2da.mw)). Good fallback if we
want a dependency-free binary rather than a Python env, but it's file-in/file-out — no `Installation`
awareness, so *we'd* have to compute free rows.

### reone toolkit (GUI + reference implementation)
**[github.com/seedhartha/reone](https://github.com/seedhartha/reone)** /
[toolkit download](https://deadlystream.com/files/file/1862-reone-toolkit/): GUI to preview/edit
2DA/GFF/TLK/LIP/SSF/NCS and **decompile NCS → NSS pseudocode**. Not a library to call, but the best
**human-facing verifier** for the blueprints our `GffWriter` emits and a decompiler if we ever need to
read a stock `.ncs`.

**Recommendation:** keep GFF authoring in C# (`GffWriter`, already proven), and add a small
**PyKotor-based install step** for the 2DA/TLK merge — don't build a 2DA/TLK writer in C# from scratch.

## 4. QoL / content features that specifically help co-op

| Feature | Driving file(s) | How it helps co-op | Install |
| --- | --- | --- | --- |
| **Distinct P2 body/face** | new row in `appearance.2da` (+ `heads.2da` if a unique head) | phantom reads as a *different person*, not a clone of the host | 2DA merge (PyKotor/TSLPatcher) |
| **P2 portrait & name-band art** | new row in `portraits.2da` + a `.tga` | correct portrait in the selection band / party bar for the peer | 2DA merge + raw `.tga` in Override |
| **Friendly "player" faction** | new row in `repute.2da` (mutual 100 with player factions, ≥ neutral with hostiles) | phantom never trips auto-hostility AI — **removes the per-tick faction patch** from the plugin | 2DA merge |
| **Silent, safe phantom body** | `k_coop_phantom.utc` (already shipped) — blank Script*, `SoundSetFile 65535`, `Min1HP 1`, proficiency `FeatList`, explicit faction | no commoner AI, no barks, equip never bounces | raw Override (unique resref) — **done** |
| **Co-op spawn / lobby waypoint** | `k_coop_spawn.utw` waypoint (unique resref) | deterministic peer spawn / "connect here" point without touching a module `.git` | raw Override (unique resref) |
| **Party-size / companion tweaks** | NOT a clean 2DA lever in K1 (party cap is engine-side) | avoid — this is where data ends and hooks begin; keep host-authoritative party in-plugin | — |

Note on §4 row-adds: they all want a **StrRef** for the display name (every stock appearance/portrait
name is a `dialog.tlk` reference, not inline — confirmed in `override-surfaces.md`: "FirstName still
can't be inline"). That's the TLK-append case → TSLPatcher/HoloPatcher `[TLKList]`, or keep the peer
**name in the plugin** (which we already do via the name-plate client-twin write, `name-plates.md`).
Cheapest path: **appearance/faction rows via 2DA merge; names stay in the plugin; skip portraits.2da
until we actually want per-peer portrait art.**

## 5. Prioritized shortlist — incorporate now, no RE

1. **`repute.2da` friendly-faction row (highest value).** Add one "coop_player" faction that is mutual-100
   with the player faction and non-hostile to everything, set the phantom's `FactionID` to it in
   `k_coop_phantom.utc`. Directly *deletes* the plugin's per-tick faction-patch write. **File:** `repute.2da`
   (merge). **Gotcha:** must be a TSLPatcher/HoloPatcher `[2DAList]` merge, never a raw 2DA drop, or it
   clobbers faction mods; verify the mutual-value math against
   [repute semantics](https://deadlystream.com/topic/4725-detailed-information-of-2da-files/).

2. **PyKotor install step for 2DA/TLK merges (enabler for 1 & 3).** Add a Python install script using
   `read_2da`/`write_2da` + `Installation` from **[OldRepublicDevs/PyKotor](https://github.com/OldRepublicDevs/PyKotor)**
   to compute free rows in the *user's* `appearance.2da`/`repute.2da` and emit a HoloPatcher `changes.ini`.
   This is the missing writer; without it every 2DA feature is unsafe. **Gotcha:** ship HoloPatcher
   (cross-platform) rather than legacy TSLPatcher; pin the PyKotor version.

3. **Distinct P2 `appearance.2da` row.** Copy a player-body row, swap texture, reference it from the peer
   at spawn. Makes the phantom visually a second person. **File:** `appearance.2da` (merge via step 2).
   **Gotcha:** the display name is a StrRef — leave the name to the plugin, don't append TLK just for this.

4. **`k_coop_spawn.utw` waypoint blueprint.** Author with `GffWriter` (same path as `PhantomBlueprint`),
   unique `k_coop_` resref, ship raw in Override. Deterministic peer spawn without editing any module.
   **Gotcha:** never let it become a module `.git`/`.utw` name a module already uses.

5. **Cross-check `GffWriter` output against reone/xoreos-tools.** Round-trip `k_coop_phantom.utc` through
   `gff2xml` (xoreos-tools) or the reone toolkit as a regression check on our writer — cheap correctness
   insurance, no shipping impact.

**Deliberately excluded (data ends, hooks begin):** party-size cap, any networked logic, module/area
edits, `dialog.tlk` shipping, `.ncs` engine logic, GUI new-controls — all covered by existing plugin
hooks or forbidden by precedence rules in `override-surfaces.md`.

### GOG vs Steam / path gotchas
- Install target is the **GOG** tree (`C:\GOG Games\Star Wars KOTOR (GOG)\Override`) — see MEMORY /
  `override-surfaces.md`; Override is empty on GOG, so our raw `k_coop_*` files land clean.
- Any 2DA/TLK merge tool must be pointed at the **GOG** `swkotor.exe` dir, not a Steam path — PyKotor's
  `Installation(path)` and HoloPatcher both take the game dir explicitly; don't assume Steam's
  `steamapps/common/swkotor`.
- `dialog.tlk` StrRef management: only ever **append** (TSLPatcher backs up the original and assigns the
  index); never rewrite existing StrRefs — other mods and our own saved games reference them by number.
