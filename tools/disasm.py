"""Disassemble a range of swkotor.exe by virtual address.

Only correct for the GOG build, whose entry point lives in .text rather than a packer
section, so the bytes on disk really are the bytes that execute. Do not point this at the
Steam copy and believe the output.

  python tools/dis.py 0x402800 400
  python tools/dis.py 0x402800 400 --exe "C:/path/to/swkotor.exe"
"""
import struct
import sys

import capstone

DEFAULT_EXE = r"C:\GOG Games\Star Wars KOTOR (GOG)\swkotor.exe"


def load(path):
    data = open(path, "rb").read()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    opt = pe + 24
    base = struct.unpack_from("<I", data, opt + 28)[0]
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    first = opt + struct.unpack_from("<H", data, pe + 20)[0]

    sections = []
    for i in range(nsec):
        off = first + (i * 40)
        name = data[off:off + 8].rstrip(b"\0").decode(errors="replace")
        vsize, vaddr, rsize, raw = struct.unpack_from("<IIII", data, off + 8)
        sections.append((name, vaddr, vsize, raw, rsize))

    return data, base, sections


def to_offset(base, sections, va):
    rva = va - base
    for _name, vaddr, vsize, raw, rsize in sections:
        if vaddr <= rva < vaddr + max(vsize, rsize):
            return raw + (rva - vaddr)
    return None


def main():
    args = [a for a in sys.argv[1:]]
    exe = DEFAULT_EXE
    if "--exe" in args:
        i = args.index("--exe")
        exe = args[i + 1]
        del args[i:i + 2]

    va = int(args[0], 0)
    length = int(args[1], 0) if len(args) > 1 else 256

    data, base, sections = load(exe)
    off = to_offset(base, sections, va)
    if off is None:
        print(f"0x{va:08X} is not inside any section")
        return 1

    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    md.detail = False

    for ins in md.disasm(data[off:off + length], va):
        print(f"0x{ins.address:08X}  {ins.bytes.hex():<20} {ins.mnemonic} {ins.op_str}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
