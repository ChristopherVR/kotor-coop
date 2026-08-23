using KotorCoop.Memory.GameFiles;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Marks a test that needs the real GOG install's swoop-race module archive
/// (<c>modules\tar_m02ab_s.rim</c>). Skipped — not failed — everywhere that file is
/// absent, same discipline as <see cref="GogInstallFactAttribute"/>.
/// </summary>
public sealed class SwoopModuleFactAttribute : FactAttribute
{
    public static string RimPath => Path.Combine(GameFilesTests.GogDirectory, "modules", "tar_m02ab_s.rim");

    public SwoopModuleFactAttribute()
    {
        if (!File.Exists(RimPath))
        {
            Skip = $"no {RimPath}";
        }
    }
}

/// <summary>
/// <see cref="NcsReader"/> against hand-built synthetic bytecode (always run — this is
/// the "matches the documented format, independent of any real game file" proof), plus
/// the real question this tool was built to answer: what does the Taris swoop-race
/// entrance trigger's OnEnter script (<c>k_ptar_swoop02ab.ncs</c>) actually check, read
/// straight out of this game's own <c>modules\tar_m02ab_s.rim</c> (skipped, not failed,
/// on any machine without the GOG install — see <see cref="SwoopModuleFactAttribute"/>).
/// </summary>
public sealed class NcsReaderTests
{
    // ---- a tiny builder for hand-crafted synthetic .ncs byte streams -----------
    //
    // Mirrors, byte-for-byte, the format this reader implements: header, the 0x42
    // program-size pseudo-instruction, then opcode+type+operand instructions with
    // NWScript's own jump-offset convention (a jump/call's 4-byte operand is relative
    // to the START of the jump instruction itself, i.e. its own opcode byte — verified
    // against the real compiled scripts this round pulled out of the game, where every
    // jump target lands exactly on a real instruction boundary with zero leftover bytes
    // in the whole file).
    private sealed class NcsBuilder
    {
        private readonly List<byte> _body = [];
        private readonly Dictionary<string, int> _labels = [];
        private readonly List<(int PatchAt, string Label)> _pendingJumps = [];

        private const int HeaderLength = 13; // 8-byte magic + 5-byte size marker

        public NcsBuilder Label(string name)
        {
            _labels[name] = HeaderLength + _body.Count;
            return this;
        }

        public NcsBuilder Rsadd(byte type)
        {
            _body.Add(0x02);
            _body.Add(type);
            return this;
        }

        public NcsBuilder ConstInt(int value)
        {
            _body.Add(0x04);
            _body.Add(0x03);
            AppendI32(value);
            return this;
        }

        public NcsBuilder ConstString(string value)
        {
            _body.Add(0x04);
            _body.Add(0x05);
            byte[] chars = System.Text.Encoding.Latin1.GetBytes(value);
            AppendU16((ushort)chars.Length);
            _body.AddRange(chars);
            return this;
        }

        public NcsBuilder Action(ushort routine, byte argCount)
        {
            _body.Add(0x05);
            _body.Add(0x00);
            AppendU16(routine);
            _body.Add(argCount);
            return this;
        }

        public NcsBuilder Retn()
        {
            _body.Add(0x20);
            _body.Add(0x00);
            return this;
        }

        public NcsBuilder MoveSp(int delta)
        {
            _body.Add(0x1B);
            _body.Add(0x00);
            AppendI32(delta);
            return this;
        }

        public NcsBuilder CopyDownSp(int offset, ushort size)
        {
            _body.Add(0x01);
            _body.Add(0x01);
            AppendI32(offset);
            AppendU16(size);
            return this;
        }

        public NcsBuilder CopyTopSp(int offset, ushort size)
        {
            _body.Add(0x03);
            _body.Add(0x01);
            AppendI32(offset);
            AppendU16(size);
            return this;
        }

        public NcsBuilder JumpToSubroutine(string label) => Jump(0x1E, label);

        public NcsBuilder JumpIfZero(string label) => Jump(0x1F, label);

        private NcsBuilder Jump(byte opcode, string label)
        {
            _body.Add(opcode);
            _body.Add(0x00);
            _pendingJumps.Add((_body.Count, label));
            AppendI32(0); // patched in Build()
            return this;
        }

        private void AppendI32(int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);
            _body.AddRange(bytes);
        }

        private void AppendU16(ushort value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);
            _body.AddRange(bytes);
        }

        public byte[] Build()
        {
            int totalSize = HeaderLength + _body.Count;
            byte[] result = new byte[totalSize];

            System.Text.Encoding.ASCII.GetBytes("NCS V1.0").CopyTo(result, 0);
            result[8] = 0x42;
            byte[] sizeBytes = BitConverter.GetBytes(totalSize);
            Array.Reverse(sizeBytes);
            sizeBytes.CopyTo(result, 9);
            _body.CopyTo(result.AsSpan(HeaderLength));

            foreach ((int patchAt, string label) in _pendingJumps)
            {
                int instructionStart = HeaderLength + patchAt - 2; // back up over opcode+type
                int relative = _labels[label] - instructionStart;
                byte[] rel = BitConverter.GetBytes(relative);
                Array.Reverse(rel);
                rel.CopyTo(result, HeaderLength + patchAt);
            }

            return result;
        }
    }

    /// <summary>
    /// A hand-built script matching the exact shape this round found in the real
    /// k_ptar_swoop02ab.ncs: <c>if (GetIsPC(GetEnteringObject())) SetGlobalBoolean(sIdent, TRUE);</c>
    /// — but with fabricated routine/identifier data, entirely independent of any real
    /// game file.
    /// </summary>
    private static byte[] BuildSyntheticOnEnterScript() =>
        new NcsBuilder()
            .JumpToSubroutine("main")
            .Retn()
            .Label("main")
            .Rsadd(0x06) // object local
            .Action(25, 0) // GetEnteringObject()
            .CopyDownSp(-8, 4)
            .MoveSp(-4)
            .CopyTopSp(-4, 4)
            .Action(217, 1) // GetIsPC(oPC)
            .JumpIfZero("after")
            .ConstInt(1)
            .ConstString("TEST_FLAG_ONE")
            .Action(579, 2) // SetGlobalBoolean("TEST_FLAG_ONE", TRUE)
            .Label("after")
            .MoveSp(-4)
            .Retn()
            .Build();

    [Fact]
    public void DisassemblesTheProgramSizeHeaderCorrectly()
    {
        byte[] data = BuildSyntheticOnEnterScript();

        IReadOnlyList<NcsInstruction> instructions = NcsReader.Disassemble(data);

        Assert.Equal(NcsOpcode.ProgramSize, instructions[0].Opcode);
        Assert.Equal(data.Length, instructions[0].IntValue);
    }

    [Fact]
    public void DisassemblesEveryByteWithNoLeftover()
    {
        byte[] data = BuildSyntheticOnEnterScript();

        IReadOnlyList<NcsInstruction> instructions = NcsReader.Disassemble(data);

        NcsInstruction last = instructions[^1];
        Assert.Equal(data.Length, last.Offset + last.Length);
    }

    [Fact]
    public void ResolvesJumpToSubroutineTargetToTheLabelledInstruction()
    {
        byte[] data = BuildSyntheticOnEnterScript();
        IReadOnlyList<NcsInstruction> instructions = NcsReader.Disassemble(data);

        NcsInstruction jsr = Assert.Single(instructions, i => i.Opcode == NcsOpcode.JumpToSubroutine);
        NcsInstruction target = instructions.Single(i => i.Offset == jsr.JumpTarget);

        Assert.Equal(NcsOpcode.Reserve, target.Opcode);
    }

    [Fact]
    public void ResolvesJumpIfZeroTargetPastTheGuardedBlock()
    {
        byte[] data = BuildSyntheticOnEnterScript();
        IReadOnlyList<NcsInstruction> instructions = NcsReader.Disassemble(data);

        NcsInstruction jz = Assert.Single(instructions, i => i.Opcode == NcsOpcode.JumpIfZero);
        NcsInstruction target = instructions.Single(i => i.Offset == jz.JumpTarget);

        // The JZ skips straight to the trailing MoveSp/Retn cleanup, past the
        // ConstInt/ConstString/Action block it guards.
        Assert.Equal(NcsOpcode.MoveSp, target.Opcode);
        Assert.Equal(-4, target.StackOffset);
    }

    [Fact]
    public void DecodesConstantStringsAndIntsFaithfully()
    {
        byte[] data = BuildSyntheticOnEnterScript();
        IReadOnlyList<NcsInstruction> instructions = NcsReader.Disassemble(data);

        NcsInstruction str = Assert.Single(
            instructions, i => i.Opcode == NcsOpcode.PushConstant && i.StringValue is not null);
        Assert.Equal("TEST_FLAG_ONE", str.StringValue);

        NcsInstruction intConst = Assert.Single(
            instructions, i => i.Opcode == NcsOpcode.PushConstant && i.IntValue == 1 && i.Type == 0x03);
        Assert.NotNull(intConst);
    }

    [Fact]
    public void FindActionCallsPairsTheGlobalNameWithItsSetGlobalBooleanCall()
    {
        byte[] data = BuildSyntheticOnEnterScript();
        IReadOnlyList<NcsInstruction> instructions = NcsReader.Disassemble(data);

        IReadOnlyList<NcsActionCall> calls = instructions.FindActionCalls();

        NcsActionCall setGlobal = Assert.Single(calls, c => c.Routine == 579);
        Assert.Equal("SetGlobalBoolean", setGlobal.ResolvedName);
        Assert.Equal(2, setGlobal.ArgCount);
        Assert.Equal(["TEST_FLAG_ONE"], setGlobal.PrecedingStringConstants);

        NcsActionCall getEntering = Assert.Single(calls, c => c.Routine == 25);
        Assert.Empty(getEntering.PrecedingStringConstants); // no string arg to this one
    }

    [Fact]
    public void ThrowsOnBadMagic()
    {
        byte[] data = BuildSyntheticOnEnterScript();
        data[0] = (byte)'X';

        Assert.Throws<InvalidDataException>(() => NcsReader.Disassemble(data));
    }

    [Fact]
    public void ThrowsOnTruncatedInstructionStream()
    {
        byte[] data = BuildSyntheticOnEnterScript();
        byte[] truncated = data[..(data.Length - 3)];

        Assert.Throws<InvalidDataException>(() => NcsReader.Disassemble(truncated));
    }

    [Fact]
    public void UnknownRoutineNumbersFormatAsHashNumber()
    {
        Assert.Equal("#99999", NwscriptActions.NameFor(99999));
        Assert.Equal("SetGlobalBoolean", NwscriptActions.NameFor(579));
    }

    // ---- the real question: what does the swoop-race entrance trigger actually check? ---

    /// <summary>
    /// The definitive answer, read straight out of the game's own compiled bytecode
    /// rather than guessed from a global-variable name list: the swoop-race entrance
    /// trigger's OnEnter script (<c>k_ptar_swoop02ab.ncs</c>, module tar_m02ab, tag
    /// tar02_swoop02ab) does not check ANY precondition at all. It unconditionally
    /// (once gated only on "is the entering object the PC") calls
    /// <c>SetGlobalBoolean("K_TAR_SWOOP_POINT_01", TRUE)</c> and
    /// <c>SetGlobalBoolean("K_TAR_SWOOP_POINT_02", TRUE)</c> — a "found this collectible
    /// swoop point" marker, not a race-start gate. This is why every prior round's
    /// forced-flag experiment (TAR_FIRST_SWOOP_USE, Tar_SwoopRace, Tar_SwoopNew,
    /// TAR_STARTED, all forced to 1 before crossing this exact trigger) could never have
    /// started the race: the script that runs on crossing it doesn't read those
    /// names, or any name, as a condition — it only ever WRITES two different names.
    /// See docs/research/minigames.md for the full disassembly and the follow-on
    /// implication (the real race-start interaction is elsewhere, not this trigger).
    /// </summary>
    [SwoopModuleFact]
    public void SwoopTriggerOnEnterScript_UnconditionallySetsTwoCollectibleFlags_NotARaceStartGate()
    {
        RimArchive rim = RimArchive.Open(SwoopModuleFactAttribute.RimPath);
        Assert.True(rim.TryRead("k_ptar_swoop02ab", "ncs", out byte[] data), "k_ptar_swoop02ab.ncs not in the RIM");

        IReadOnlyList<NcsInstruction> instructions = NcsReader.Disassemble(data);
        IReadOnlyList<NcsActionCall> calls = instructions.FindActionCalls();

        List<NcsActionCall> setGlobalBooleanCalls = [.. calls.Where(c => c.Routine == 579)];
        Assert.Equal(2, setGlobalBooleanCalls.Count);
        Assert.Equal(["K_TAR_SWOOP_POINT_01"], setGlobalBooleanCalls[0].PrecedingStringConstants);
        Assert.Equal(["K_TAR_SWOOP_POINT_02"], setGlobalBooleanCalls[1].PrecedingStringConstants);
        Assert.All(setGlobalBooleanCalls, c => Assert.Equal(2, c.ArgCount));

        // The negative half of the disproof: none of the five names every prior live
        // round forced to 1 (docs/research/minigames.md §11/§12) are read OR written
        // anywhere in this script.
        string[] previouslyTriedNames =
        [
            "TAR_FIRST_SWOOP_USE", "Tar_SwoopRace", "Tar_SwoopNew", "Tar_SwoopStatus", "TAR_STARTED",
        ];
        IEnumerable<string> stringsTouched = instructions
            .Where(i => i.Opcode == NcsOpcode.PushConstant && i.StringValue is not null)
            .Select(i => i.StringValue!);
        Assert.Empty(stringsTouched.Intersect(previouslyTriedNames, StringComparer.OrdinalIgnoreCase));

        // No GetGlobalBoolean/GetGlobalNumber (578/580) call at all — this script never
        // reads a precondition, it only ever sets one.
        Assert.DoesNotContain(calls, c => c.Routine is 578 or 580);
    }

    /// <summary>
    /// The companion script <c>k_ptar_swoop.ncs</c> (same RIM, no "02ab" suffix) is the
    /// one script in this module that actually reads <c>Tar_SwoopRace</c> — but as a
    /// boolean-returning conditional (<c>return GetGlobalBoolean("Tar_SwoopRace") == 1;</c>),
    /// the compiled shape of a dialogue/conversation condition script, not a trigger's
    /// OnEnter. This is the concrete lead for where the real race-start gate lives: not
    /// the entrance trigger, but a conversation node somewhere in this module gated by
    /// this exact global.
    /// </summary>
    [SwoopModuleFact]
    public void SwoopCompanionScript_ReadsTarSwoopRaceAsABooleanCondition()
    {
        RimArchive rim = RimArchive.Open(SwoopModuleFactAttribute.RimPath);
        Assert.True(rim.TryRead("k_ptar_swoop", "ncs", out byte[] data), "k_ptar_swoop.ncs not in the RIM");

        IReadOnlyList<NcsInstruction> instructions = NcsReader.Disassemble(data);
        IReadOnlyList<NcsActionCall> calls = instructions.FindActionCalls();

        NcsActionCall getGlobal = Assert.Single(calls, c => c.Routine == 578);
        Assert.Equal("GetGlobalBoolean", getGlobal.ResolvedName);
        Assert.Equal(1, getGlobal.ArgCount);
        Assert.Equal(["Tar_SwoopRace"], getGlobal.PrecedingStringConstants);
    }
}
