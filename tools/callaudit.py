"""Audit every engine function the plugin calls through against the binary and the symbol DB.

  python tools/callaudit.py            # all findings
  python tools/callaudit.py --bad      # only disagreements
  python tools/callaudit.py --markdown # the table in docs/research/engine-api.md

The plugin reaches the engine through ~109 raw function pointers, each written as a
`delegate* unmanaged[Conv]<args..., ret>` cast over a hardcoded address. Nothing checked those
declarations against the function actually living at the address, and the failure mode is the
nastiest one available: a `__thiscall`/`__stdcall` callee pops its OWN arguments, so a
declaration with the wrong number of them leaves the stack pointer skewed by 4 x (difference)
at every call. That does not fault where it happens. It corrupts a frame somewhere else and
surfaces as a stack-cookie failure — STATUS_STACK_BUFFER_OVERRUN, 0xC0000409 — inside whatever
function happens to return next.

The oracle is the callee's own epilogue: a function that cleans its own stack ends in
`ret imm16`, and that immediate IS the argument size in bytes. A plain `ret` means it takes
none. The symbol DB supplies names and a second opinion on the calling convention.

A `this` pointer travels in ECX and is NOT counted in the immediate, so a thiscall declared
<nint, A, B, ret> should meet `ret 8`, not `ret 0xc`. That asymmetry is exactly what a human
miscounts, which is the point of having a tool.

⚠ Finding the epilogue needs a DECODER, not a byte scan, and needs three things right. All
three were got wrong first and each produced confident nonsense:

  * 0xC2/0xC3 appear constantly inside displacements and immediates. A byte scan reported a
    callee popping 15116 bytes. Use capstone (already a dependency of tools/disasm.py).
  * Many engine entry points are FORWARDING THUNKS (`mov ecx, [ecx+4]; jmp impl`) whose stack
    cleanup belongs to the target. Stopping at the thunk decodes through the alignment padding
    into the next function and reports its epilogue: 18 false mismatches, every one a thunk.
  * A `jmp` deeper inside a body is an ordinary branch, and a `jmp dword ptr [reg*4 + table]`
    is a switch whose arms follow inline. Treating either as a tail call loses the function.
"""
import os
import re
import sqlite3
import sys

import capstone

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DB = os.path.join(ROOT, "tools", "kotor1_0_3.db")
PLUGIN = os.path.join(ROOT, "src", "KotorCoop.Plugin")
DEFAULT_EXE = r"C:\GOG Games\Star Wars KOTOR (GOG)\swkotor.exe"

TEXT_VA = 0x401000
TEXT_RAW = 0x1000
TEXT_SIZE = 0x33B1D0

DECL = re.compile(
    r"delegate\*\s+unmanaged\[(?P<conv>\w+)\]<(?P<args>[^>]*)>\s*\)\s*(?P<addr>0x[0-9A-Fa-f]+)"
)

# The `ret` is found by DISASSEMBLING from the entry point, not by scanning for a C2/C3
# byte. The byte scan was tried first and it is worthless here: 0xC2 and 0xC3 occur constantly
# inside displacements, immediates and modrm bytes, so it reported a callee popping 15116
# bytes and a dozen functions with "no ret" that plainly have one. A decoder costs nothing —
# capstone is already a dependency of tools/disasm.py — and turns a guess into a fact.
#
# Following jumps is unnecessary: MSVC lays a function's epilogue out in the same block for
# everything here, and any `ret` reached by linear decode from the entry belongs to this
# function. Decoding stops at the first `ret`, so a later function's epilogue is never seen.


def ret_immediate(text: bytes, va: int, hops=0):
    """('ret-imm', n) / ('ret', 0) / ('no-ret', None) for the function at va.

    FOLLOWS TAIL JUMPS. A large share of the engine's public entry points are one-line
    forwarding thunks — `mov ecx, [ecx+4]; jmp <impl>` — where the `this` pointer is adjusted
    to a base subobject and control leaves immediately. The stack cleanup is the TARGET's, so
    an audit that stops at the thunk decodes through the alignment padding into whatever
    function happens to be laid out next and reports its epilogue. That produced 18 false
    mismatches on the first run, every one of them a thunk.
    """
    i = va - TEXT_VA
    if i < 0 or i >= len(text):
        return ("out-of-range", None)
    if hops > 8:
        return ("thunk-loop", None)

    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    for idx, insn in enumerate(md.disasm(text[i:i + 0x6000], va)):
        if insn.mnemonic == "ret":
            return ("ret-imm", int(insn.op_str, 0)) if insn.op_str else ("ret", 0)

        if insn.mnemonic == "jmp":
            # A TAIL CALL only if it is the entry's own control flow. A forwarding thunk is
            # `mov ecx, [ecx+N]; jmp impl` — two or three instructions — whereas a `jmp` deeper
            # in a body is an ordinary intra-function branch. Following those as tail calls
            # burned the hop budget on a function with three 2-byte forward jumps and reported
            # it as an unresolvable thunk loop.
            if idx < 4 and insn.op_str.startswith("0x"):
                return ret_immediate(text, int(insn.op_str, 0), hops + 1)
            # `jmp dword ptr [reg*4 + table]` is a SWITCH, not a dispatch to elsewhere: MSVC
            # lays the arms out immediately after it and the jump table after those, so the
            # next `ret` still belongs to this function. Bailing here reported four functions
            # as unresolvable that a decoder can read perfectly well.
            continue

    return ("no-ret", None)


# Declarations the audit flags but that were examined by hand and are known-good. Each carries
# the reason, so a NEW disagreement still fails and these do not hide behind a blanket ignore.
KNOWN_EXCEPTIONS = {
    0x00506B30: "CSWSArea::SetTransitionPending reads [esp+4] and [esp+8] on entry (two stack args); the "
                "audit took the ret-imm of its early-out path. (this,int,char) is right.",
    0x005EDA50: "a two-instruction thunk (mov ecx,[ecx+4]; jmp target) - it loads ecx itself, so a "
                "Thiscall declaration matches; the database label 'stdcall' is the target's.",
    0x005F4B50: "CClientExoAppInternal::DoQuickSave, no stack args, declared Cdecl; live-verified by every "
                "'save' verb and quicksave the rig has issued (2026-09-30 again), never a stack skew.",
    0x006EB320: "CSWGuiMainCharGen::BeginPlayGame is a tail-jump thunk (mov ecx,[esi+0x68]; jmp 0x6DBDF0); "
                "the audit reads the target's ret. Live-verified by five new-game runs on 2026-09-30.",
}


def main():
    args = sys.argv[1:]
    only_bad = "--bad" in args
    markdown = "--markdown" in args
    exe = DEFAULT_EXE
    if "--exe" in args:
        exe = args[args.index("--exe") + 1]

    text = open(exe, "rb").read()[TEXT_RAW:TEXT_RAW + TEXT_SIZE]

    con = sqlite3.connect(DB)
    cur = con.cursor()

    seen = {}
    for name in sorted(os.listdir(PLUGIN)):
        if not name.endswith(".cs"):
            continue
        source = open(os.path.join(PLUGIN, name), encoding="utf-8", errors="replace").read()
        for m in DECL.finditer(source):
            addr = int(m.group("addr"), 0)
            conv = m.group("conv")
            parts = [a.strip() for a in m.group("args").split(",") if a.strip()]
            seen.setdefault((addr, conv, len(parts)), []).append(name)

    rows = []
    for (addr, conv, arity), files in sorted(seen.items()):
        # Last type parameter is the RETURN type, so arguments = arity - 1.
        nargs = max(0, arity - 1)
        stack_args = nargs - 1 if conv == "Thiscall" else nargs
        expected = stack_args * 4

        kind, imm = ret_immediate(text, addr)

        cur.execute(
            "SELECT class_name, function_name, calling_convention, param_size_bytes "
            "FROM functions WHERE address=?", (addr,))
        db = cur.fetchone()

        verdict = "ok"
        detail = ""

        if kind == "ret-imm":
            if imm != expected:
                verdict = "MISMATCH"
                detail = f"callee pops {imm}, declaration passes {expected}"
        elif kind == "ret":
            if conv in ("Thiscall", "Stdcall") and expected != 0:
                verdict = "MISMATCH"
                detail = f"callee is `ret` (pops 0) but declaration passes {expected}"
        else:
            verdict = "unknown"
            detail = kind

        if db and db[2] and verdict == "ok":
            dbconv = (db[2] or "").lower().replace("__", "")
            if dbconv and dbconv not in conv.lower() and conv.lower() not in dbconv:
                verdict = "conv?"
                detail = f"DB says {db[2]}, declared {conv}"

        name = f"{db[0]}::{db[1]}" if db and db[0] else (db[1] if db else "")
        rows.append((verdict, addr, conv, nargs, expected, kind, imm, name, detail, files))

    bad = [r for r in rows if r[0] != "ok" and r[1] not in KNOWN_EXCEPTIONS]

    if markdown:
        print("| Address | Symbol | Conv | Args | Callee pops | Declared in |")
        print("| --- | --- | --- | --- | --- | --- |")
        for verdict, addr, conv, nargs, expected, kind, imm, name, detail, files in rows:
            pops = f"`{kind} {imm}`" if imm is not None else f"`{kind}`"
            mark = "" if verdict == "ok" else " ⚠"
            print(f"| `0x{addr:08X}` | `{name or '(unnamed)'}` | {conv} | {nargs} | "
                  f"{pops}{mark} | {', '.join(sorted(set(f[:-3] for f in files)))} |")
        return 1 if bad else 0

    print(f"{len(rows)} distinct engine call declarations in the plugin")
    print(f"  {len(rows) - len(bad)} agree with the binary, {len(bad)} do not\n")

    for verdict, addr, conv, nargs, expected, kind, imm, name, detail, files in rows:
        if only_bad and verdict == "ok":
            continue
        flag = " " if verdict == "ok" else "!"
        immtext = f"{kind}({imm})" if imm is not None else kind
        print(f"{flag} 0x{addr:08X} {conv:9} args={nargs} needs={expected:3} {immtext:12} "
              f"{name[:44]:44} {detail}")
        if verdict != "ok":
            print(f"      declared in: {', '.join(sorted(set(files)))}")

    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
