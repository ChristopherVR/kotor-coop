"""
make_ending_fixture.py -- build a save the KOTOR ending can actually be played from.

The rig's Star Forge saves descend from the Endar Spire save through console recruits and warps,
so Trask was never written out of party slot 0 (vanilla does that when he dies,
k_pend_trask_d: RemovePartyMember + RemoveAvailableNPC) and Bastila never took the slot. Every
ending scene that brings Bastila back (STUNT_55a's k_scene_start: IsAvailableCreature(0) ->
AddAvailableNPCByTemplate(0, "p_bastilla001") -> SpawnAvailableNPC) then finds slot 0 already
"available", spawns Trask, and its conversation waits forever for a Bastila who never arrives
(2026-10-10, both games, the dark-side ceremony).

This copies a save and writes Trask out of the party table the way the game's own script would:
slot 0 not available, no party members. Everything else is untouched. Usage:

  python tools/make_ending_fixture.py "000919 - campaign_sta_m45ac" "000921 - ending_fixture"
  [--game "C:/GOG Games/Star Wars KOTOR (GOG)"]

Read-only on the source save; refuses to overwrite an existing target folder.
"""
from __future__ import annotations

import argparse
import os
import shutil
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import kotorfiles as kf  # noqa: E402

DEFAULT_GAME = r"C:\GOG Games\Star Wars KOTOR (GOG)"


def field_slots(gff: kf.GFF, struct_index: int) -> dict[str, tuple[int, int, int]]:
    """label -> (field index, type, data-or-offset) for one struct, read from the raw tables."""
    _stype, data_or_off, count = gff._struct_raw(struct_index)
    if count == 0:
        indices = []
    elif count == 1:
        indices = [data_or_off]
    else:
        indices = gff._field_indices(data_or_off, count)
    out = {}
    for fi in indices:
        ftype, label_i, dod = gff._field_raw(fi)
        out[gff.labels[label_i]] = (fi, ftype, dod)
    return out


def patch_inline(buf: bytearray, gff: kf.GFF, field_index: int, value: int) -> None:
    """Overwrite the inline data dword of a BYTE/WORD/DWORD/INT field."""
    struct.pack_into("<I", buf, gff.field_off + 12 * field_index + 8, value & 0xFFFFFFFF)


def list_struct_indices(gff: kf.GFF, list_dod: int) -> list[int]:
    off = gff.listind_off + list_dod
    n = struct.unpack_from("<I", gff.data, off)[0]
    return list(struct.unpack_from("<%dI" % n, gff.data, off + 4)) if n else []


def write_trask_out(partytable: bytes) -> bytes:
    gff = kf.GFF(partytable, "PARTYTABLE.res")
    buf = bytearray(partytable)
    root = field_slots(gff, 0)

    fi, _t, _d = root["PT_NUM_MEMBERS"]
    patch_inline(buf, gff, fi, 0)

    # An empty list keeps its slot in the list-indices block; only the count changes.
    _fi, _t, members_dod = root["PT_MEMBERS"]
    struct.pack_into("<I", buf, gff.listind_off + members_dod, 0)

    _fi, _t, avail_dod = root["PT_AVAIL_NPCS"]
    slot0 = list_struct_indices(gff, avail_dod)[0]
    fi, _t, _d = field_slots(gff, slot0)["PT_NPC_AVAIL"]
    patch_inline(buf, gff, fi, 0)
    return bytes(buf)


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("source")
    ap.add_argument("target")
    ap.add_argument("--game", default=DEFAULT_GAME)
    args = ap.parse_args(argv)

    saves = os.path.join(args.game, "saves")
    src, dst = os.path.join(saves, args.source), os.path.join(saves, args.target)
    if not os.path.isdir(src):
        print(f"no save folder {src}", file=sys.stderr)
        return 2
    if os.path.exists(dst):
        print(f"refusing to overwrite {dst}", file=sys.stderr)
        return 2

    with open(os.path.join(src, "PARTYTABLE.res"), "rb") as f:
        patched = write_trask_out(f.read())
    shutil.copytree(src, dst)
    with open(os.path.join(dst, "PARTYTABLE.res"), "wb") as f:
        f.write(patched)

    check = kf.GFF(patched).root
    print(f"wrote {dst}: members {check['PT_NUM_MEMBERS']}/{len(check['PT_MEMBERS'])}, "
          f"slot 0 available {check['PT_AVAIL_NPCS'][0]['PT_NPC_AVAIL']}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
