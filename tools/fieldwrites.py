"""Find instructions that WRITE a given struct field offset in swkotor.exe's .text.

  python tools/fieldwrites.py 0xB4

xrefs.py answers "who calls this address". This answers "who assigns this field", which is
the question you have when a flag keeps coming back and the teardown that should clear it
demonstrably runs. There was no tool for it, and the alternative — disassembling candidate
functions by hand hoping to spot the store — is how a field ends up guessed at.

Only offsets that do not fit a signed disp8 (>= 0x80) are worth scanning this way: they force
the assembler to emit a disp32, so the offset appears literally in the instruction stream and
a byte scan is exact rather than heuristic. Smaller offsets are reported as unsupported
rather than silently under-reported.

Matches the two stores that actually occur for a flag:
  C7 /0 mod=10   mov dword ptr [base+disp32], imm32
  89 /r mod=10   mov dword ptr [base+disp32], r32
"""
import struct
import sys

DEFAULT_EXE = r"C:\GOG Games\Star Wars KOTOR (GOG)\swkotor.exe"

TEXT_VA = 0x401000
TEXT_RAW = 0x1000
TEXT_SIZE = 0x33B1D0

REGS = ["eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi"]


def main():
    args = sys.argv[1:]
    exe = DEFAULT_EXE
    if "--exe" in args:
        i = args.index("--exe")
        exe = args[i + 1]
        del args[i:i + 2]

    if not args:
        print(__doc__)
        return 1

    offset = int(args[0], 0)
    if offset < 0x80:
        print(f"offset 0x{offset:X} fits a disp8; a byte scan would miss most stores")
        return 2

    data = open(exe, "rb").read()
    text = data[TEXT_RAW:TEXT_RAW + TEXT_SIZE]
    disp = struct.pack("<i", offset)

    hits = []
    start = 0
    while True:
        i = text.find(disp, start)
        if i < 0:
            break
        start = i + 1

        # The displacement follows the modrm byte (no SIB for rm != 100).
        if i < 2:
            continue

        opcode = text[i - 2]
        modrm = text[i - 1]
        mod = modrm >> 6
        reg = (modrm >> 3) & 7
        rm = modrm & 7

        if mod != 0b10 or rm == 0b100:  # disp32 form, no SIB
            continue

        va = TEXT_VA + i - 2

        if opcode == 0xC7 and reg == 0:
            imm = struct.unpack_from("<I", text, i + 4)[0]
            hits.append((va, f"mov dword ptr [{REGS[rm]}+0x{offset:X}], 0x{imm:X}"))
        elif opcode == 0x89:
            hits.append((va, f"mov dword ptr [{REGS[rm]}+0x{offset:X}], {REGS[reg]}"))

    print(f"{len(hits)} write(s) to +0x{offset:X}:")
    for va, text_form in hits:
        print(f"  0x{va:08X}  {text_form}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
