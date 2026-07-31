#!/usr/bin/env python3
"""
Builds an Override\\mainmenu*.gui that turns the game's dormant, off-screen BTN_WARP control
into a visible "Coop" entry sitting one row below Quit in the main-menu button column.

The retail main menu keeps a dev-only BTN_WARP button (CONTROLTYPE 6, text "Warp") parked in
a corner (base file: LEFT 499, TOP 362), so it is invisible in the button stack. The co-op
plugin already rebinds that control's click to start a session (ConnectMenuController) -- the
only missing half is making it VISIBLE and labelling it. This does that as a pure data mod:
a byte-exact, in-place edit of the extracted .gui (no GFF re-serialisation, so nothing can
shift), moving BTN_WARP's EXTENT into the column and renaming the inline text "Warp" -> "Coop"
(same 4 bytes, so the CExoString length prefix is untouched).

    python tools/patch_mainmenu.py                 # patch every variant into both installs
    python tools/patch_mainmenu.py --game DIR --out DIR/Override

Every mainmenu resolution variant that carries BTN_WARP is patched, so whatever file the
running resolution loads gets the visible entry.
"""
import argparse
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import kotorfiles as kf

VARIANTS = ["mainmenu", "mainmenu8x6", "mainmenu10x7", "mainmenu12x9", "mainmenu16x12"]
NEW_LABEL = b"Coop"   # must be exactly as long as the stock "Warp" for an in-place edit

INSTALLS = [
    r"C:\GOG Games\Star Wars KOTOR (GOG)",
    r"D:\Games\swkotor-gog-p2",
]


def field_map(gff, struct_index):
    """label -> (field_index, ftype, dod) for one struct, keeping the raw field index so the
    on-disk DataOrDataOffset dword can be located and overwritten."""
    stype, data_or_off, fcount = gff._struct_raw(struct_index)
    if fcount == 1:
        idxs = [data_or_off]
    elif fcount == 0:
        idxs = []
    else:
        idxs = gff._field_indices(data_or_off, fcount)
    out = {}
    for fi in idxs:
        ftype, label_i, dod = gff._field_raw(fi)
        out[gff.labels[label_i]] = (fi, ftype, dod)
    return out


def list_child_indices(gff, dod):
    off = gff.listind_off + dod
    n = struct.unpack_from("<I", gff.data, off)[0]
    return list(struct.unpack_from("<%dI" % n, gff.data, off + 4)) if n else []


def find_control(gff, controls_dod, tag):
    """Return (struct_index, field_map) of the control whose TAG matches, else (None, None)."""
    for si in list_child_indices(gff, controls_dod):
        fm = field_map(gff, si)
        tag_f = fm.get("TAG")
        if tag_f and gff._field_value(tag_f[1], tag_f[2]) == tag:
            return si, fm
    return None, None


def set_int_field(buf, gff, fm, label, value):
    """Overwrite an inline INT/DWORD field's DataOrDataOffset dword in place."""
    fi = fm[label][0]
    off = gff.field_off + 12 * fi + 8
    struct.pack_into("<i", buf, off, value)


def patch_one(data: bytes) -> bytes:
    gff = kf.GFF(data)
    root = field_map(gff, 0)
    if "CONTROLS" not in root:
        raise RuntimeError("no CONTROLS list")
    controls_dod = root["CONTROLS"][2]

    _, warp = find_control(gff, controls_dod, "BTN_WARP")
    _, exit_ = find_control(gff, controls_dod, "BTN_EXIT")
    _, opts = find_control(gff, controls_dod, "BTN_OPTIONS")
    if not warp or not exit_ or not opts:
        raise RuntimeError("missing BTN_WARP / BTN_EXIT / BTN_OPTIONS")

    ext = kf.GFF(data)  # fresh parser for reading struct-typed EXTENT children
    warp_ext = field_map(ext, warp["EXTENT"][2])
    exit_ext = field_map(ext, exit_["EXTENT"][2])
    opts_ext = field_map(ext, opts["EXTENT"][2])

    def geti(fm, lbl):
        f = fm[lbl]
        return ext._field_value(f[1], f[2])

    left = geti(exit_ext, "LEFT")
    top = geti(exit_ext, "TOP")
    width = geti(exit_ext, "WIDTH")
    height = geti(exit_ext, "HEIGHT")
    spacing = top - geti(opts_ext, "TOP")            # one row below Quit
    if spacing <= 0:
        spacing = height + 3
    new_top = top + spacing

    buf = bytearray(data)
    set_int_field(buf, gff, warp_ext, "LEFT", left)
    set_int_field(buf, gff, warp_ext, "TOP", new_top)
    set_int_field(buf, gff, warp_ext, "WIDTH", width)
    set_int_field(buf, gff, warp_ext, "HEIGHT", height)

    # Relabel the inline TEXT "Warp" -> "Coop" (both 4 bytes; CExoString len prefix stays 4).
    text_fm = field_map(gff, warp["TEXT"][2])
    if "TEXT" in text_fm and text_fm["TEXT"][1] == kf.GFF_CEXOSTRING:
        base = gff.fielddata_off + text_fm["TEXT"][2]
        n = struct.unpack_from("<I", buf, base)[0]
        cur = bytes(buf[base + 4:base + 4 + n])
        if n != len(NEW_LABEL):
            raise RuntimeError("BTN_WARP text is %r (len %d); need len %d for an in-place edit"
                               % (cur, n, len(NEW_LABEL)))
        buf[base + 4:base + 4 + n] = NEW_LABEL
    else:
        raise RuntimeError("BTN_WARP has no inline TEXT string")

    return bytes(buf), (left, new_top, width, height)


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=kf.DEFAULT_GAME_DIR)
    ap.add_argument("--out", default=None, help="output Override dir (default: patch all INSTALLS)")
    args = ap.parse_args(argv)

    game = kf.Game(args.game)
    patched = {}
    for name in VARIANTS:
        try:
            data = game.read(name + ".gui")
        except KeyError:
            print("  (skip %s: not present)" % name)
            continue
        out, geom = patch_one(data)
        patched[name + ".gui"] = out
        print("  %-16s BTN_WARP -> Coop at LEFT=%d TOP=%d WIDTH=%d HEIGHT=%d" % ((name,) + geom))

    if not patched:
        print("nothing patched")
        return 1

    targets = [args.out] if args.out else [os.path.join(i, "Override") for i in INSTALLS]
    for tdir in targets:
        os.makedirs(tdir, exist_ok=True)
        for fname, blob in patched.items():
            dest = os.path.join(tdir, fname)
            with open(dest, "wb") as f:
                f.write(blob)
            print("wrote %s (%d bytes)" % (dest, len(blob)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
