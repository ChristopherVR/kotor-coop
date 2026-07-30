"""Find call/jmp sites targeting an address in swkotor.exe's .text.

GOG build only: its entry point is in .text rather than a packer section, so the bytes on
disk are the bytes that execute and a static scan is meaningful.

  python tools/xrefs.py 0x4AE6F0
"""
import struct
import sys

DEFAULT_EXE = r"C:\GOG Games\Star Wars KOTOR (GOG)\swkotor.exe"

TEXT_VA = 0x401000
TEXT_RAW = 0x1000
TEXT_SIZE = 0x33B1D0


def main():
    args = sys.argv[1:]
    exe = DEFAULT_EXE
    if "--exe" in args:
        i = args.index("--exe")
        exe = args[i + 1]
        del args[i:i + 2]

    target = int(args[0], 0)
    data = open(exe, "rb").read()

    direct = []      # E8 rel32 (call) / E9 rel32 (jmp)
    indirect = []    # any dword equal to the address (vtable slots, tables)

    for off in range(TEXT_RAW, TEXT_RAW + TEXT_SIZE - 5):
        op = data[off]
        if op not in (0xE8, 0xE9):
            continue
        rel = struct.unpack_from("<i", data, off + 1)[0]
        site = TEXT_VA + (off - TEXT_RAW)
        if site + 5 + rel == target:
            direct.append((site, "call" if op == 0xE8 else "jmp"))

    needle = struct.pack("<I", target)
    off = 0
    while True:
        off = data.find(needle, off)
        if off < 0:
            break
        # Report the VA if the hit lands in a mapped section we know about.
        if TEXT_RAW <= off < TEXT_RAW + TEXT_SIZE:
            indirect.append(TEXT_VA + (off - TEXT_RAW))
        elif 0x33D000 <= off < 0x38D000:
            indirect.append(0x73D000 + (off - 0x33D000))
        off += 1

    print(f"target 0x{target:08X}")
    print(f"  {len(direct)} direct call/jmp site(s):")
    for site, kind in direct:
        print(f"    0x{site:08X}  {kind}")
    print(f"  {len(indirect)} address-literal occurrence(s) (vtables, jump tables, data):")
    for site in indirect[:40]:
        print(f"    0x{site:08X}")


if __name__ == "__main__":
    main()
