#!/usr/bin/env python3
"""Count what a KOTOR save baked into its module: creatures, waypoints, and the co-op
ghosts among them (docs/research/save-ghosts.md).

Reads SAVEGAME.sav (an ERF) -> the nested <module>.sav (another ERF) -> <module>.git (GFF)
with tools/kotorfiles.py, and reports, per save:

  * Creature List length, and how many carry the stand-in tag `coop_phantom`;
  * WaypointList length, and how many are peer map pins: tagged `coop_mapnote`, or an
    UNTAGGED map note that is enabled (what builds before the tag existed spawned; every
    shipped map note carries a tag, so an untagged enabled one can only be ours);
  * module.ifo's Mod_NextObjId0 (the low half of the next object id), which moves by one
    per baked object.

This is the offline oracle for the save-time flush (roadmap #36): on the Endar Spire deck a
clean save reads 25 creatures / 58 waypoints; a save taken with a joiner beside the host
and no flush read 26 / 59 (2026-09-18).

  python tools/saveinspect.py "C:/GOG Games/Star Wars KOTOR (GOG)/saves/000000 - QUICKSAVE"
  python tools/saveinspect.py --all "C:/GOG Games/Star Wars KOTOR (GOG)/saves"
  python tools/saveinspect.py --expect-clean <save dir>     # exit 1 if any ghost is found

A positional argument is a save directory, a SAVEGAME.sav, or (with --all) a saves\\ root.
Nothing here writes to disk.
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from kotorfiles import ERF, GFF, restype_name  # noqa: E402

GHOST_CREATURE_TAG = "coop_phantom"
GHOST_MARKER_TAG = "coop_mapnote"


def erf_entries(data: bytes):
    """Yield (name, ext, bytes) for every entry of an in-memory ERF/SAV/MOD image — the
    nested <module>.sav inside SAVEGAME.sav never touches disk, so kotorfiles.ERF (which
    opens a path) cannot be used for it. Same header layout as kotorfiles.ERF documents."""
    if len(data) < 160 or data[4:8].strip() != b"V1.0":
        raise ValueError("not an ERF V1.0 image")
    (_lang_count, _locstr_size, entry_count, _off_locstr,
     off_keys, off_res, _byear, _bday, _desc) = struct.unpack_from("<9I", data, 8)
    for i in range(entry_count):
        resref, _res_id, rtype, _unused = struct.unpack_from("<16sIHH", data, off_keys + 24 * i)
        off, size = struct.unpack_from("<II", data, off_res + 8 * i)
        name = resref.split(b"\0", 1)[0].decode("ascii", "replace")
        yield name, restype_name(rtype), data[off:off + size]


def is_ghost_creature(tag: str) -> bool:
    return tag.lower() == GHOST_CREATURE_TAG


def is_ghost_marker(tag: str, has_map_note: bool, map_note_enabled: bool) -> bool:
    if tag.lower() == GHOST_MARKER_TAG:
        return True
    return tag == "" and has_map_note and map_note_enabled


def inspect_save(path: str) -> dict:
    """path: a save directory or its SAVEGAME.sav. Returns the counts as a dict."""
    sav_path = path if os.path.isfile(path) else os.path.join(path, "SAVEGAME.sav")
    if not os.path.isfile(sav_path):
        raise FileNotFoundError(sav_path)

    outer = ERF(sav_path)
    # The nested module image is restype 2057 (kotorfiles' table calls it "4pc"; the engine's
    # own list says sav) — sniff the ERF header rather than trust the extension.
    modules = []
    for r in outer:
        head = r.read()[:8] if r.size >= 160 else b""
        if head[4:8] == b"V1.0" and head[:4] in (b"SAV ", b"ERF ", b"MOD "):
            modules.append(r)
    if not modules:
        raise ValueError("%s holds no nested module .sav" % sav_path)

    # The module the player is standing in is the one named by savenfo/the last-module
    # field; a save can nest several visited modules. Report each.
    result = {"save": os.path.dirname(sav_path) or sav_path, "modules": []}
    for mod in modules:
        git = ifo = None
        for name, ext, blob in erf_entries(mod.read()):
            if ext == "git":
                git = GFF(blob, name + ".git")
            elif ext == "ifo":
                ifo = GFF(blob, name + ".ifo")
        entry = {"module": mod.name, "creatures": None, "ghost_creatures": [],
                 "waypoints": None, "ghost_pins": [], "next_object_id": None}
        if git is not None:
            creatures = git.root.get("Creature List", []) or []
            entry["creatures"] = len(creatures)
            for i, c in enumerate(creatures):
                tag = str(c.get("Tag", "") or "")
                if is_ghost_creature(tag):
                    entry["ghost_creatures"].append(
                        (i, tag, c.get("Appearance_Type", c.get("TemplateResRef", "?"))))
            waypoints = git.root.get("WaypointList", []) or []
            entry["waypoints"] = len(waypoints)
            for i, w in enumerate(waypoints):
                tag = str(w.get("Tag", "") or "")
                has_note = bool(w.get("HasMapNote", 0))
                enabled = bool(w.get("MapNoteEnabled", 0))
                if is_ghost_marker(tag, has_note, enabled):
                    entry["ghost_pins"].append((i, tag or "<untagged>"))
        if ifo is not None:
            entry["next_object_id"] = ifo.root.get("Mod_NextObjId0", ifo.root.get("Mod_NextObjId"))
        result["modules"].append(entry)
    return result


def format_result(result: dict) -> str:
    lines = []
    for m in result["modules"]:
        ghosts = len(m["ghost_creatures"])
        pins = len(m["ghost_pins"])
        line = "%s  module %-10s creatures %s (ghosts %d)  waypoints %s (ghost pins %d)  next-id %s" % (
            os.path.basename(result["save"]), m["module"], m["creatures"], ghosts,
            m["waypoints"], pins, m["next_object_id"])
        lines.append(line)
        for i, tag, appearance in m["ghost_creatures"]:
            lines.append("    creature[%d] tag %r appearance %s" % (i, tag, appearance))
        for i, tag in m["ghost_pins"]:
            lines.append("    waypoint[%d] tag %s" % (i, tag))
    return "\n".join(lines)


def main(argv) -> int:
    args = list(argv)
    expect_clean = "--expect-clean" in args
    all_saves = "--all" in args
    args = [a for a in args if a not in ("--expect-clean", "--all")]
    if not args:
        print(__doc__)
        return 2

    targets = []
    for a in args:
        if all_saves and os.path.isdir(a) and not os.path.isfile(os.path.join(a, "SAVEGAME.sav")):
            for d in sorted(os.listdir(a)):
                full = os.path.join(a, d)
                if os.path.isfile(os.path.join(full, "SAVEGAME.sav")):
                    targets.append(full)
        else:
            targets.append(a)

    dirty = 0
    for t in targets:
        try:
            r = inspect_save(t)
        except Exception as e:  # keep going over a saves\ root with one odd directory
            print("%s: %s" % (t, e))
            dirty += 1 if expect_clean else 0
            continue
        print(format_result(r))
        if any(m["ghost_creatures"] or m["ghost_pins"] for m in r["modules"]):
            dirty += 1
    return 1 if (expect_clean and dirty) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
