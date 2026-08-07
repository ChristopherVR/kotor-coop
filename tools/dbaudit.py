"""Validate the whole symbol DB against the GOG binary, function by function.

  python tools/dbaudit.py                # summary
  python tools/dbaudit.py --bad          # the disagreements
  python tools/dbaudit.py --class CSWSObject

tools/callaudit.py checks the ~109 functions the plugin actually calls. This checks all of
them — every row in `functions` that carries a param_size_bytes — against the callee's own
epilogue, using the same decoder.

Why bother, when the DB is the thing everything else trusts? Because it is imported from
another build's symbols and nothing had ever confirmed it describes THIS binary. A row that is
merely wrong is worse than a missing one: it is the source you consult when writing the
declaration that then corrupts the stack. Knowing which rows are trustworthy — and, more
usefully, which specific ones are not — is what makes the DB safe to build on.

⚠ `param_size_bytes` IS NOT THE STACK SIZE. It is the sum of the raw `sizeof` of each declared
parameter, so `SetSTRBase(int)` reads 1 and `HasFeat(short)` reads 2 — while the stack gives
every argument its own 4-byte slot, which is what the `ret` immediate counts. Comparing the
two directly makes the DB look 15% wrong (1422 "disagreements", almost all of them off by
+3 or +2 — exactly the rounding). It is the check that is wrong there, not the data.

So only rows whose size is already a MULTIPLE OF 4 are compared: for those the rounding is a
no-op and the comparison is sound. Everything else is reported as not-comparable rather than
being counted either way, because a check that cannot distinguish "wrong" from "encoded
differently" is not a check. A __cdecl function (caller cleans, plain `ret`) is skipped too.
"""
import os
import sqlite3
import sys

import capstone

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DB = os.path.join(ROOT, "tools", "kotor1_0_3.db")
DEFAULT_EXE = r"C:\GOG Games\Star Wars KOTOR (GOG)\swkotor.exe"

TEXT_VA = 0x401000
TEXT_RAW = 0x1000
TEXT_SIZE = 0x33B1D0


def ret_immediate(text, va, hops=0):
    """('ret-imm', n) / ('ret', 0) / (reason, None). Follows forwarding thunks.

    Identical rules to tools/callaudit.py, and for the same hard-won reasons: decode rather
    than byte-scan, follow an entry `jmp` (thunk) but not a branch deeper in the body, and
    keep going through a switch's `jmp dword ptr [reg*4 + table]`.
    """
    i = va - TEXT_VA
    if i < 0 or i >= len(text) or hops > 8:
        return ("out-of-range" if hops <= 8 else "thunk-loop", None)

    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    for idx, insn in enumerate(md.disasm(text[i:i + 0x6000], va)):
        if insn.mnemonic == "ret":
            return ("ret-imm", int(insn.op_str, 0)) if insn.op_str else ("ret", 0)
        if insn.mnemonic == "jmp":
            if idx < 4 and insn.op_str.startswith("0x"):
                return ret_immediate(text, int(insn.op_str, 0), hops + 1)
            continue
    return ("no-ret", None)


def main():
    args = sys.argv[1:]
    only_bad = "--bad" in args
    want_class = args[args.index("--class") + 1] if "--class" in args else None
    exe = args[args.index("--exe") + 1] if "--exe" in args else DEFAULT_EXE

    text = open(exe, "rb").read()[TEXT_RAW:TEXT_RAW + TEXT_SIZE]

    con = sqlite3.connect(DB)
    sql = ("SELECT class_name, function_name, address, calling_convention, param_size_bytes "
           "FROM functions WHERE address IS NOT NULL AND param_size_bytes IS NOT NULL")
    params = ()
    if want_class:
        sql += " AND class_name = ?"
        params = (want_class,)

    agree = disagree = skipped = unreadable = incomparable = unknown = 0
    bad = []

    for cls, fn, addr, conv, size in con.execute(sql + " ORDER BY address", params):
        if addr < TEXT_VA or addr >= TEXT_VA + TEXT_SIZE:
            skipped += 1
            continue

        # ⚠ ZERO MEANS UNKNOWN, not "takes no arguments". 336 of the first run's 646
        # "disagreements" were size 0 against a callee that plainly pops something —
        # CResRef::operator== among them, which cannot take no arguments. The column is
        # simply unpopulated for much of the DB, and an unpopulated row is a gap, not an
        # error. Counting it as one buries the rows that really are wrong.
        if size == 0:
            unknown += 1
            continue

        # Not a whole number of stack slots: the DB is summing declared sizeof, and the
        # true slot count cannot be recovered from the total alone.
        if size % 4 != 0:
            incomparable += 1
            continue

        kind, imm = ret_immediate(text, addr)

        if kind == "ret-imm":
            if imm == size:
                agree += 1
            else:
                disagree += 1
                bad.append((addr, cls, fn, conv, size, f"ret {imm}"))
        elif kind == "ret":
            # A plain `ret` is right for __cdecl (caller cleans) and for a zero-argument
            # callee. Only a stack-cleaning convention that claims arguments disagrees.
            if (conv or "").lower().replace("__", "") == "cdecl":
                agree += 1
            else:
                disagree += 1
                bad.append((addr, cls, fn, conv, size, "ret (pops 0)"))
        else:
            unreadable += 1

    total = agree + disagree + unreadable + incomparable + unknown
    print(f"{total} DB functions with a declared param size, inside .text")
    print(f"  agree with the binary : {agree}")
    print(f"  DISAGREE              : {disagree}")
    print(f"  epilogue unreadable   : {unreadable}")
    print(f"  size not a multiple of 4 (see the docstring): {incomparable}")
    print(f"  size unpopulated (0)  : {unknown}")
    if skipped:
        print(f"  outside .text (skipped): {skipped}")

    if only_bad and bad:
        print()
        for addr, cls, fn, conv, size, saw in sorted(bad):
            name = f"{cls}::{fn}" if cls else fn
            print(f"  0x{addr:08X}  {name[:52]:52} DB says {size:3}, binary {saw}  [{conv}]")

    return 0


if __name__ == "__main__":
    sys.exit(main())
