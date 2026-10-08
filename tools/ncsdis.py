"""Disassemble a compiled KOTOR 1 script (.ncs) with engine routine names from nwscript.nss.

  python tools/ncsdis.py <module> <script>       e.g.  python tools/ncsdis.py end_m01aa k_pend_reinfrc06
  python tools/ncsdis.py --global <script>       a script from the global script archive

Read-only. Enough to answer "what does this trigger's OnEnter check and spawn?" without a
decompiler: every engine call is printed by name with the constants pushed before it, and jumps
by absolute offset. The trigger relay's double-spawn question (docs/research/trigger-relay.md
section 3) was the reason it exists.
"""
from __future__ import annotations

import re
import struct
import sys

import kotor_lore
import kotorfiles

NSS_TYPE, NCS_TYPE = 2009, 2010

# opcode -> (mnemonic, operand layout after the type byte)
OPS = {
    0x01: ("CPDOWNSP", "iH"), 0x02: ("RSADD", ""), 0x03: ("CPTOPSP", "iH"), 0x04: ("CONST", "const"),
    0x05: ("ACTION", "HB"), 0x06: ("LOGAND", ""), 0x07: ("LOGOR", ""), 0x08: ("INCOR", ""),
    0x09: ("EXCOR", ""), 0x0A: ("BOOLAND", ""), 0x0B: ("EQUAL", "eq"), 0x0C: ("NEQUAL", "eq"),
    0x0D: ("GEQ", ""), 0x0E: ("GT", ""), 0x0F: ("LT", ""), 0x10: ("LEQ", ""), 0x11: ("SHLEFT", ""),
    0x12: ("SHRIGHT", ""), 0x13: ("USHRIGHT", ""), 0x14: ("ADD", ""), 0x15: ("SUB", ""), 0x16: ("MUL", ""),
    0x17: ("DIV", ""), 0x18: ("MOD", ""), 0x19: ("NEG", ""), 0x1A: ("COMP", ""), 0x1B: ("MOVSP", "i"),
    0x1C: ("STORE_STATEALL", ""), 0x1D: ("JMP", "j"), 0x1E: ("JSR", "j"), 0x1F: ("JZ", "j"),
    0x20: ("RETN", ""), 0x21: ("DESTRUCT", "HHH"), 0x22: ("NOTI", ""), 0x23: ("DECISP", "i"),
    0x24: ("INCISP", "i"), 0x25: ("JNZ", "j"), 0x26: ("CPDOWNBP", "iH"), 0x27: ("CPTOPBP", "iH"),
    0x28: ("DECIBP", "i"), 0x29: ("INCIBP", "i"), 0x2A: ("SAVEBP", ""), 0x2B: ("RESTOREBP", ""),
    0x2C: ("STORE_STATE", "II"), 0x2D: ("NOP", ""),
}


def routine_names(game):
    """Engine routine index -> name, in nwscript.nss declaration order (the compiler's numbering)."""
    bif, index = game.key.entries[("nwscript", NSS_TYPE)]
    text = game.key.bif(bif).by_index[index].read().decode("latin-1")
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    text = re.sub(r"//[^\n]*", "", text)
    names = []
    for m in re.finditer(r"^\s*(?:int|float|string|object|void|vector|location|effect|event|talent|action|"
                         r"itemproperty)\s+(\w+)\s*\([^;{]*\)\s*;", text, flags=re.M):
        names.append(m.group(1))
    return names


def disassemble(code, names):
    if code[:8] != b"NCS V1.0":
        raise ValueError("not an NCS V1.0 file")
    pos, out = 13, []  # header (8) + 0x42 + u32 size
    while pos < len(code):
        at, op, typ = pos, code[pos], code[pos + 1]
        pos += 2
        name, layout = OPS.get(op, (f"OP_{op:02X}", ""))
        args = ""
        if layout == "const":
            if typ == 3:
                args = str(struct.unpack_from(">i", code, pos)[0]); pos += 4
            elif typ == 4:
                args = f"{struct.unpack_from('>f', code, pos)[0]:g}"; pos += 4
            elif typ == 5:
                n = struct.unpack_from(">H", code, pos)[0]
                args = repr(code[pos + 2:pos + 2 + n].decode("latin-1")); pos += 2 + n
            elif typ == 6:
                args = f"object 0x{struct.unpack_from('>I', code, pos)[0]:X}"; pos += 4
        elif layout == "HB":
            routine, argc = struct.unpack_from(">HB", code, pos); pos += 3
            label = names[routine] if routine < len(names) else f"routine{routine}"
            args = f"{label}({argc} args)  #{routine}"
        elif layout == "eq":
            if typ == 0x24:
                args = f"struct size {struct.unpack_from('>H', code, pos)[0]}"; pos += 2
        elif layout == "j":
            args = f"-> {at + struct.unpack_from('>i', code, pos)[0]:#06x}"; pos += 4
        elif layout:
            fmt = ">" + layout
            vals = struct.unpack_from(fmt, code, pos); pos += struct.calcsize(fmt)
            args = " ".join(str(v) for v in vals)
        out.append(f"{at:#06x}  {name:<12} {args}")
    return out


def load(module, script):
    game = kotor_lore._g()
    if module:
        for archive in game.module(module):
            for r in archive:
                if r.name.lower() == script.lower() + ".ncs":
                    return game, r.read()
    bif, index = game.key.entries[(script.lower(), NCS_TYPE)]
    return game, game.key.bif(bif).by_index[index].read()


def main(argv):
    if len(argv) != 2:
        print(__doc__)
        return 2
    module = None if argv[0] == "--global" else argv[0]
    game, code = load(module, argv[1])
    print("\n".join(disassemble(code, routine_names(game))))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
