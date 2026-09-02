"""Re-emits CrashProbe.BuildHandler byte for byte and disassembles it.

The stub is hand-assembled machine code that runs on every first-chance exception in the
game; getting an encoding wrong would be worse than having no probe at all. This is the
offline check for it.
"""
import struct
import capstone

RecCode, RecAddress, RecEip, RecEsp, RecStackWords, RecStack = 0x04, 0x08, 0x0C, 0x10, 0x14, 0x18
StackWords = 64
RECORD_SIZE = RecStack + (StackWords * 4)
RecWriteFileCell = RECORD_SIZE
RecSetFilePointerCell = RECORD_SIZE + 4
RecWrittenScratch = RECORD_SIZE + 8
RECORD = 0x00A00000
FILE_HANDLE = 0x000000EC          # a plausible-looking HANDLE value, for the disassembly only
WRITE_FILE_CELL = RECORD + RecWriteFileCell
SET_FILE_POINTER_CELL = RECORD + RecSetFilePointerCell
WRITTEN_CELL = RECORD + RecWrittenScratch

out = bytearray()


def emit(*b):
    out.extend(b)


def emit32(v):
    out.extend(struct.pack("<i", v))


emit(0x53)
emit(0x56)
emit(0x57)
emit(0x8B, 0x74, 0x24, 0x10)
emit(0x85, 0xF6)
emit(0x0F, 0x84); j1 = len(out); emit32(0)
emit(0x8B, 0x06)
emit(0x85, 0xC0)
emit(0x0F, 0x84); j2 = len(out); emit32(0)
emit(0x8B, 0x5E, 0x04)
emit(0x85, 0xDB)
emit(0x0F, 0x84); j3 = len(out); emit32(0)
emit(0xBF); emit32(RECORD)
emit(0x8B, 0x08)
emit(0x89, 0x4F, RecCode)
emit(0x8B, 0x48, 0x0C)
emit(0x89, 0x4F, RecAddress)
emit(0x8B, 0x8B); emit32(0xB8)
emit(0x89, 0x4F, RecEip)
emit(0x8B, 0x8B); emit32(0xC4)
emit(0x89, 0x4F, RecEsp)
emit(0xC7, 0x47, RecStackWords); emit32(0)
emit(0x8B, 0xF1)
emit(0x64, 0xA1); emit32(0x08)
emit(0x3B, 0xF0)
emit(0x72, 0x00); j4 = len(out) - 1
emit(0x64, 0xA1); emit32(0x04)
emit(0x2D); emit32(StackWords * 4)
emit(0x3B, 0xF0)
emit(0x77, 0x00); j5 = len(out) - 1
emit(0x57)
emit(0x83, 0xC7, RecStack)
emit(0xB9); emit32(StackWords)
emit(0xFC)
emit(0xF3, 0xA5)
emit(0x5F)
emit(0xC7, 0x47, RecStackWords); emit32(StackWords)
store = len(out)
emit(0xFF, 0x07)

# SetFilePointer(fileHandle, 0, NULL, FILE_BEGIN)
emit(0x68); emit32(0)                       # push FILE_BEGIN (0)
emit(0x68); emit32(0)                       # push NULL       ; lpDistanceToMoveHigh
emit(0x68); emit32(0)                       # push 0          ; lDistanceToMove
emit(0x68); emit32(FILE_HANDLE)             # push fileHandle
emit(0xFF, 0x15); emit32(SET_FILE_POINTER_CELL)

# WriteFile(fileHandle, record, RecordSize, &written, NULL)
emit(0x68); emit32(0)                       # push NULL       ; lpOverlapped
emit(0x68); emit32(WRITTEN_CELL)            # push &written
emit(0x68); emit32(RECORD_SIZE)             # push RecordSize
emit(0x57)                                  # push edi        ; lpBuffer = record
emit(0x68); emit32(FILE_HANDLE)             # push fileHandle
emit(0xFF, 0x15); emit32(WRITE_FILE_CELL)

done = len(out)
emit(0x5F)
emit(0x5E)
emit(0x5B)
emit(0x33, 0xC0)
emit(0xC2, 0x04, 0x00)

struct.pack_into("<i", out, j1, done - (j1 + 4))
struct.pack_into("<i", out, j2, done - (j2 + 4))
struct.pack_into("<i", out, j3, done - (j3 + 4))
for slot, target in ((j4, store), (j5, store)):
    disp = target - (slot + 1)
    assert 0 <= disp <= 127, (slot, disp)
    out[slot] = disp

md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
base = 0x00B00000
for ins in md.disasm(bytes(out), base):
    mark = ""
    if ins.address - base == store:
        mark = "   <- store:"
    if ins.address - base == done:
        mark = "   <- done:"
    print("%+4d  %-22s %s %s%s" % (ins.address - base, ins.bytes.hex(), ins.mnemonic, ins.op_str, mark))

decoded = sum(1 for _ in md.disasm(bytes(out), base))
total = len(list(md.disasm(bytes(out), base)))
consumed = 0
for ins in md.disasm(bytes(out), base):
    consumed += ins.size
print()
print("bytes emitted: %d, bytes decoded: %d  %s" % (len(out), consumed,
      "OK - fully decodable" if consumed == len(out) else "MISMATCH"))
print("store=%d done=%d" % (store, done))
print("stub size vs 256-byte VirtualAlloc: %d bytes used, %d spare" % (len(out), 256 - len(out)))
