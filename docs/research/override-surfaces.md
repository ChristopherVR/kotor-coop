# Override / game-file surfaces vs plugin (research 2026-07-31)

`C:\GOG Games\Star Wars KOTOR (GOG)\Override` exists and is EMPTY.

## Resource precedence (proven from the binary)

CExoResMan::GetKeyEntry 0x00407230 probes `+0x14` (override/dirs) → `+0x18` (patch.erf,
rims, mods) → `+0x10` (chitin.key/BIF) LAST. **Override beats patch.erf beats BIF for
every restype.** Both `.gui` (restype 0x7FF) and `.utc` (0x7EB) load through the same
ResMan::Demand path, so override copies win — confirmed by code, not folklore.
DANGER: a `module.ifo` or `<area>.git` in override applies to EVERY module — never ship
module-level resources.

## Verdicts

| Need | File option | Verdict |
| --- | --- | --- |
| Phantom body | **`Override\k_coop_phantom.utc`** | **SHIP AS FILE — highest value/lowest risk** |
| Co-op main-menu entry | `Override\mainmenu.gui` retext of BTN_WARP | **Hybrid** (file cosmetic, plugin rebinds events) |
| Peer on map | `k_coop_mapnote.utw` OR direct construction | Ship (direct construction, see map-presence.md) |
| Per-peer appearance/name/no-attack/move/equip | — | Keep in plugin |
| Branding text (loadscreen) | inline TEXT works but file is shared | Keep in plugin / defer |
| Engine logic via .ncs | ExecuteScript exists but routine dispatch already does it | Keep in plugin (avoid compiler dep + collision) |
| Anything needing a 2DA row or TLK string | — | REFUSE, redesign to avoid |

## k_coop_phantom.utc — the big win

Override .utc beats BIF (CSWSCreature::LoadFromTemplate 0x005026D0, restype 0x7EB). A
unique `k_coop_` resref collides with nothing. Fields the blueprint sets NATIVELY,
shrinking plugin patching:
- `Min1HP`=1, `Plot`=0, `FactionID`=5 (Neutral, explicit not inherited)
- **all 14 `Script*` ResRefs blank** — removes stock commoner AI, zero .ncs authoring,
  and silences barks (`SoundSetFile`=65535)
- `FeatList` += armour profs (4/5/6) + weapon profs (39/40/42/43/44) — **directly fixes
  the "equip refused" risk** (n_commm lacks proficiencies)
- `Appearance_Type`=114, raised HitPoints, `PerceptionRange`=0, `Tag`=coop_phantom
Net: MarkAsRemotePlayer shrinks from 4 writes to 2 (DeadSelectable, attack bit), phantom
stops running commoner AI. **FirstName still can't be inline** (every stock blueprint
uses strrefs) — keep the plugin name path (see name-plates.md).

## Main menu — hybrid, works with stock menu

An override .gui CANNOT add a new button: CSWGuiPanel::InitControl 0x0040B930 instantiates
only HARDCODED tags into fixed CSWGuiMainMenu members; an extra CONTROLS entry is inert,
and StopLoadFromLayout frees the parsed GFF immediately. So: the plugin rebinds an
existing dormant button — BTN_WARP (member +0x1248, TEXT.STRREF already -1/blank) or
BTN_MOVIES (+0x778) — via CSWGuiControl::SetCallback 0x0041AB20 for events 0x27 (click)
and 0x2D (A/Enter). Inline TEXT with STRREF 0xFFFFFFFF gives "Co-op" with NO dialog.tlk
edit (proven by loadscreen.gui's LBL_HINT). The .gui is cosmetic-only; feature must work
on the stock menu; if shipped, MERGE-install (read user's existing mainmenu.gui, rewrite
one control's TEXT) — collision risk HIGH (menu reskins, widescreen packs).

## Mod-collision rules
- `k_coop_*` resrefs (.utc/.uti/.utw) — NONE. Keep the prefix.
- NEVER ship a 2DA (TSLPatcher edits in place) or dialog.tlk. Append to the user's file at
  install if a row is truly needed.
- NEVER override a stock resref (mainmenu.gui, k_ai_master, k_def_*, global.jrl, module
  resources).
