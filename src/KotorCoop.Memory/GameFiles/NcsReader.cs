using System.Buffers.Binary;

namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// NWScript compiled-bytecode (.ncs) opcode values. A simple stack machine: every
/// instruction is an opcode byte, then (with one exception — <see cref="ProgramSize"/>,
/// the header-only pseudo-instruction) a type byte, then zero or more operands whose
/// shape depends on the opcode. Confirmed against two real compiled scripts pulled
/// straight out of this game's own <c>modules\tar_m02ab_s.rim</c>
/// (<c>k_ptar_swoop02ab.ncs</c>, <c>k_ptar_swoop.ncs</c>) — both disassemble end to end
/// with zero leftover bytes and every jump target landing on a real instruction
/// boundary. See docs/research/minigames.md, the "An NCS disassembler" round, for the
/// full worked trace.
/// </summary>
public enum NcsOpcode : byte
{
    CopyDownSp = 0x01, // CPDOWNSP: copy top-of-stack down into a stack slot
    Reserve = 0x02, // RSADD: push a zeroed slot of the given type (a local/global declaration)
    CopyTopSp = 0x03, // CPTOPSP: push a copy of a stack slot onto the top
    PushConstant = 0x04, // CONST: push an int/float/string/object literal
    CallEngine = 0x05, // ACTION: call an engine routine by number
    LogicalAnd = 0x06,
    LogicalOr = 0x07,
    BitwiseOr = 0x08,
    BitwiseXor = 0x09,
    BooleanAnd = 0x0A,
    Equal = 0x0B,
    NotEqual = 0x0C,
    GreaterOrEqual = 0x0D,
    GreaterThan = 0x0E,
    LessThan = 0x0F,
    LessOrEqual = 0x10,
    ShiftLeft = 0x11,
    ShiftRight = 0x12,
    UnsignedShiftRight = 0x13,
    Add = 0x14,
    Subtract = 0x15,
    Multiply = 0x16,
    Divide = 0x17,
    Modulo = 0x18,
    Negate = 0x19,
    Complement = 0x1A,
    MoveSp = 0x1B, // MOVSP: adjust the stack pointer (cleanup after a call/block)
    StoreStateAll = 0x1C,
    Jump = 0x1D, // JMP: unconditional relative jump
    JumpToSubroutine = 0x1E, // JSR: relative call, pushes a return address
    JumpIfZero = 0x1F, // JZ: pop an int, jump if it was 0
    Return = 0x20, // RETN: pop the call stack and jump back, or end the program at top level
    Destruct = 0x21,
    LogicalNot = 0x22,
    DecrementSp = 0x23,
    IncrementSp = 0x24,
    JumpIfNonZero = 0x25, // JNZ
    CopyDownBp = 0x26,
    CopyTopBp = 0x27,
    DecrementBp = 0x28,
    IncrementBp = 0x29,
    SaveBp = 0x2A,
    RestoreBp = 0x2B,
    StoreState = 0x2C,
    NoOp = 0x2D,

    /// <summary>Not a real instruction — the 5-byte header pseudo-op every compiled
    /// script opens with (opcode 0x42, no type byte, then a big-endian uint32 giving the
    /// whole file's size). Exposed as an opcode purely so callers can see it in the
    /// instruction stream rather than have it silently swallowed.</summary>
    ProgramSize = 0x42,
}

/// <summary>
/// One decoded NCS instruction. Not every field applies to every opcode — this is a
/// flat record rather than a discriminated union because the instruction set is small
/// and callers mostly care about a handful of shapes (<see cref="NcsOpcode.CallEngine"/>,
/// <see cref="NcsOpcode.PushConstant"/>, the jump family).
/// </summary>
public sealed record NcsInstruction
{
    /// <summary>Byte offset of the opcode byte, from the start of the file.</summary>
    public required int Offset { get; init; }

    public required NcsOpcode Opcode { get; init; }

    /// <summary>The type/qualifier byte, or 0x00 for <see cref="NcsOpcode.ProgramSize"/>
    /// which has none. For binary ops this is a single combined-type code (e.g. 0x20 =
    /// int,int), not two separate type bytes.</summary>
    public byte Type { get; init; }

    /// <summary>Total instruction length in bytes, opcode through the last operand byte
    /// — i.e. <c>Offset + Length</c> is the next instruction's offset.</summary>
    public required int Length { get; init; }

    public int? IntValue { get; init; }

    public float? FloatValue { get; init; }

    public string? StringValue { get; init; }

    /// <summary>Absolute byte offset a jump/call targets (already resolved from the raw
    /// relative operand: NWScript jump offsets are relative to the START of the jump
    /// instruction itself, i.e. its own opcode byte).</summary>
    public int? JumpTarget { get; init; }

    /// <summary>The engine routine number for <see cref="NcsOpcode.CallEngine"/>.</summary>
    public int? ActionRoutine { get; init; }

    public int? ActionArgCount { get; init; }

    /// <summary>Signed stack-relative offset for the stack-copy/adjust family
    /// (CPDOWNSP/CPTOPSP/MOVSP/DECISP/INCISP/CPDOWNBP/CPTOPBP/DECIBP/INCIBP).</summary>
    public int? StackOffset { get; init; }

    /// <summary>Byte size operand for CPDOWNSP/CPTOPSP/CPDOWNBP/CPTOPBP.</summary>
    public int? StackSize { get; init; }

    public override string ToString()
    {
        string operand = Opcode switch
        {
            NcsOpcode.ProgramSize => $"totalSize={IntValue}",
            NcsOpcode.PushConstant => StringValue is not null
                ? $"string \"{StringValue}\""
                : FloatValue is not null
                    ? $"float {FloatValue}"
                    : $"int {IntValue}",
            NcsOpcode.CallEngine => $"routine={ActionRoutine} argcount={ActionArgCount}",
            NcsOpcode.Jump or NcsOpcode.JumpToSubroutine or NcsOpcode.JumpIfZero or NcsOpcode.JumpIfNonZero =>
                $"-> {JumpTarget}",
            NcsOpcode.MoveSp or NcsOpcode.DecrementSp or NcsOpcode.IncrementSp
                or NcsOpcode.DecrementBp or NcsOpcode.IncrementBp => $"{StackOffset}",
            NcsOpcode.CopyDownSp or NcsOpcode.CopyTopSp or NcsOpcode.CopyDownBp or NcsOpcode.CopyTopBp =>
                $"offset={StackOffset} size={StackSize}",
            _ => "",
        };

        return $"{Offset,5}: {Opcode,-14} type={Type:X2} {operand}".TrimEnd();
    }
}

/// <summary>
/// Disassembles NWScript compiled bytecode (.ncs). A pure byte-in, instructions-out
/// reader — no file I/O of its own, so it works equally on bytes pulled from a BIF, a
/// module RIM, or a hand-built test fixture. Throws <see cref="InvalidDataException"/>
/// on a bad header or truncation, matching this project's other GameFiles readers
/// (<see cref="KeyIndex"/>, <see cref="Gff"/>): a corrupt .ncs is a real error, not a
/// "not found" that a Try method should swallow.
/// </summary>
public static class NcsReader
{
    public static IReadOnlyList<NcsInstruction> Disassemble(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < 13 || GameFileText.Decode(data.AsSpan(0, 4)) != "NCS " )
        {
            throw new InvalidDataException("not an NCS file (bad magic)");
        }

        if (GameFileText.Decode(data.AsSpan(4, 4)) != "V1.0")
        {
            throw new InvalidDataException("unsupported NCS version");
        }

        if (data[8] != (byte)NcsOpcode.ProgramSize)
        {
            throw new InvalidDataException("NCS: expected the 0x42 program-size marker at offset 8");
        }

        List<NcsInstruction> instructions = [];
        uint totalSize = ReadU32(data, 9);
        instructions.Add(new NcsInstruction
        {
            Offset = 8,
            Opcode = NcsOpcode.ProgramSize,
            Length = 5,
            IntValue = (int)totalSize,
        });

        int i = 13;

        while (i < data.Length)
        {
            int start = i;
            byte opByte = data[i];

            if (!Enum.IsDefined(typeof(NcsOpcode), opByte) || opByte == (byte)NcsOpcode.ProgramSize)
            {
                throw new InvalidDataException($"NCS: unknown opcode 0x{opByte:X2} at offset {start}");
            }

            NcsOpcode op = (NcsOpcode)opByte;
            RequireBytes(data, i, 2, start);
            byte type = data[i + 1];
            i += 2;

            NcsInstruction insn;

            switch (op)
            {
                case NcsOpcode.Jump:
                case NcsOpcode.JumpToSubroutine:
                case NcsOpcode.JumpIfZero:
                case NcsOpcode.JumpIfNonZero:
                {
                    RequireBytes(data, i, 4, start);
                    int offset = ReadI32(data, i);
                    i += 4;
                    insn = new NcsInstruction
                    {
                        Offset = start, Opcode = op, Type = type, Length = i - start,
                        JumpTarget = start + offset,
                    };
                    break;
                }

                case NcsOpcode.Return:
                case NcsOpcode.NoOp:
                case NcsOpcode.SaveBp:
                case NcsOpcode.RestoreBp:
                case NcsOpcode.Reserve:
                    insn = new NcsInstruction { Offset = start, Opcode = op, Type = type, Length = i - start };
                    break;

                case NcsOpcode.MoveSp:
                case NcsOpcode.DecrementSp:
                case NcsOpcode.IncrementSp:
                case NcsOpcode.DecrementBp:
                case NcsOpcode.IncrementBp:
                {
                    RequireBytes(data, i, 4, start);
                    int val = ReadI32(data, i);
                    i += 4;
                    insn = new NcsInstruction
                    {
                        Offset = start, Opcode = op, Type = type, Length = i - start, StackOffset = val,
                    };
                    break;
                }

                case NcsOpcode.CopyDownSp:
                case NcsOpcode.CopyTopSp:
                case NcsOpcode.CopyDownBp:
                case NcsOpcode.CopyTopBp:
                {
                    RequireBytes(data, i, 6, start);
                    int off = ReadI32(data, i);
                    ushort size = ReadU16(data, i + 4);
                    i += 6;
                    insn = new NcsInstruction
                    {
                        Offset = start, Opcode = op, Type = type, Length = i - start,
                        StackOffset = off, StackSize = size,
                    };
                    break;
                }

                case NcsOpcode.Destruct:
                {
                    RequireBytes(data, i, 6, start);
                    i += 6;
                    insn = new NcsInstruction { Offset = start, Opcode = op, Type = type, Length = i - start };
                    break;
                }

                case NcsOpcode.PushConstant:
                {
                    switch (type)
                    {
                        case 0x03:
                            RequireBytes(data, i, 4, start);
                            insn = new NcsInstruction
                            {
                                Offset = start, Opcode = op, Type = type, Length = (i + 4) - start,
                                IntValue = ReadI32(data, i),
                            };
                            i += 4;
                            break;
                        case 0x04:
                            RequireBytes(data, i, 4, start);
                            insn = new NcsInstruction
                            {
                                Offset = start, Opcode = op, Type = type, Length = (i + 4) - start,
                                FloatValue = BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(i, 4)),
                            };
                            i += 4;
                            break;
                        case 0x05:
                        {
                            RequireBytes(data, i, 2, start);
                            ushort strLen = ReadU16(data, i);
                            i += 2;
                            RequireBytes(data, i, strLen, start);
                            string s = GameFileText.Decode(data.AsSpan(i, strLen));
                            i += strLen;
                            insn = new NcsInstruction
                            {
                                Offset = start, Opcode = op, Type = type, Length = i - start, StringValue = s,
                            };
                            break;
                        }
                        case 0x06:
                            RequireBytes(data, i, 4, start);
                            insn = new NcsInstruction
                            {
                                Offset = start, Opcode = op, Type = type, Length = (i + 4) - start,
                                IntValue = ReadI32(data, i),
                            };
                            i += 4;
                            break;
                        default:
                            throw new InvalidDataException(
                                $"NCS: CONST with unknown type 0x{type:X2} at offset {start}");
                    }

                    break;
                }

                case NcsOpcode.CallEngine:
                {
                    RequireBytes(data, i, 3, start);
                    ushort routine = ReadU16(data, i);
                    byte argCount = data[i + 2];
                    i += 3;
                    insn = new NcsInstruction
                    {
                        Offset = start, Opcode = op, Type = type, Length = i - start,
                        ActionRoutine = routine, ActionArgCount = argCount,
                    };
                    break;
                }

                case NcsOpcode.Equal:
                case NcsOpcode.NotEqual:
                {
                    // Structure comparison (type 0x24) carries an extra uint16 struct
                    // size; every other type is just the one type byte already read.
                    if (type == 0x24)
                    {
                        RequireBytes(data, i, 2, start);
                        i += 2;
                    }

                    insn = new NcsInstruction { Offset = start, Opcode = op, Type = type, Length = i - start };
                    break;
                }

                case NcsOpcode.StoreState:
                {
                    RequireBytes(data, i, 8, start);
                    i += 8;
                    insn = new NcsInstruction { Offset = start, Opcode = op, Type = type, Length = i - start };
                    break;
                }

                default:
                    // The remaining opcodes (logic/compare/arithmetic/unary — LOGAND
                    // through MOD, GEQ/GT/LT/LEQ/ADD/SUB/MUL/DIV, NEG/COMP/NOT,
                    // StoreStateAll) are opcode+type with no further operand.
                    insn = new NcsInstruction { Offset = start, Opcode = op, Type = type, Length = i - start };
                    break;
            }

            instructions.Add(insn);
        }

        return instructions;
    }

    private static void RequireBytes(byte[] data, int at, int count, int instructionStart)
    {
        if (at + count > data.Length)
        {
            throw new InvalidDataException(
                $"NCS: truncated reading instruction at offset {instructionStart} ({count} bytes needed at {at})");
        }
    }

    private static uint ReadU32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

    private static int ReadI32(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));

    private static ushort ReadU16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
}
