using System.Buffers.Binary;
using System.Text.RegularExpressions;
using KotorCoop.Memory;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// coop.ini <c>local_var_sync</c> (docs/research/quest-sync.md §8.2/§8.3): the key layout, the
/// allowlist's own invariants, the capture stub's bytes and frame offsets, the agent's drain and
/// apply-list rules, the shared-block seqlock, the console verb and the three agreeing defaults.
/// </summary>
public class LocalVarSyncTests
{
    // ---- key layout ------------------------------------------------------------------------

    [Theory]
    [InlineData((ushort)0x4, LocalVarKind.Boolean, (byte)10)]
    [InlineData((ushort)0xFFFF, LocalVarKind.Boolean, (byte)95)]
    [InlineData((ushort)0x1, LocalVarKind.Number, (byte)0)]
    [InlineData((ushort)0x1B6, LocalVarKind.Number, (byte)7)]
    public void AKeyRoundTripsEveryField(ushort objectId, LocalVarKind kind, byte slot)
    {
        uint module = LocalVarSyncRule.HashModule("danm13");
        ulong key = LocalVarSyncRule.MakeKey(module, objectId, kind, slot);

        Assert.True(LocalVarSyncRule.IsLocalVarKey(key));
        Assert.True(LocalVarSyncRule.TryParseKey(key, out uint parsedModule, out ushort parsedId, out LocalVarKind parsedKind, out byte parsedSlot));
        Assert.Equal(module, parsedModule);
        Assert.Equal(objectId, parsedId);
        Assert.Equal(kind, parsedKind);
        Assert.Equal(slot, parsedSlot);
        Assert.Equal(module, ObjectKeyedFlags.ModuleHashOf(key));
        Assert.Equal(LocalVarSyncRule.FlagTag, ObjectKeyedFlags.TagOf(key));
    }

    [Fact]
    public void TheTagCollidesWithNoOtherObjectKeyedChannelAndNoGlobal()
    {
        Assert.NotEqual(DoorFlags.TypeTag, LocalVarSyncRule.FlagTag);
        Assert.NotEqual(PlaceableFlags.TypeTag, LocalVarSyncRule.FlagTag);
        Assert.NotEqual(MineFlags.TypeTag, LocalVarSyncRule.FlagTag);

        uint module = LocalVarSyncRule.HashModule("tar_m02aa");
        ulong key = LocalVarSyncRule.MakeKey(module, 0x44, LocalVarKind.Boolean, 3);
        Assert.False(DoorFlags.IsDoorKey(key));
        Assert.False(PlaceableFlags.IsPlaceableKey(key));
        Assert.False(MineFlags.IsMineKey(key));
        Assert.True(ObjectKeyedFlags.IsObjectKeyed(key));

        Assert.False(LocalVarSyncRule.IsLocalVarKey(DoorFlags.MakeKey(module, 0x44)));
        Assert.False(LocalVarSyncRule.IsLocalVarKey(0x0003_0012UL));
    }

    [Theory]
    [InlineData("danm13")]
    [InlineData("TAR_M02AA")]
    [InlineData("end_m01aa")]
    [InlineData("ebo_m12aa")]
    public void TheModuleHashIsTheObjectKeyedHashInBothForms(string module)
    {
        uint expected = ObjectKeyedFlags.HashModule(module);
        Assert.Equal(expected, LocalVarSyncRule.HashModule(module));
        Assert.Equal(expected, LocalVarSyncRule.HashModuleAscii(System.Text.Encoding.ASCII.GetBytes(module)));
    }

    [Fact]
    public void AnOutOfRangeSlotOrADynamicIdIsNeverAKey()
    {
        Assert.False(LocalVarSyncRule.IsValidSlot(LocalVarKind.Boolean, 96));
        Assert.False(LocalVarSyncRule.IsValidSlot(LocalVarKind.Number, 8));
        Assert.False(LocalVarSyncRule.IsValidSlot(LocalVarKind.Boolean, -1));
        Assert.False(LocalVarSyncRule.IsGitObjectId(0));
        Assert.False(LocalVarSyncRule.IsGitObjectId(0x10000));
        Assert.False(LocalVarSyncRule.IsGitObjectId(0x7F000000));

        // A number key whose slot field says 9 must not parse even though 7 bits could hold it.
        ulong forged = ((ulong)LocalVarSyncRule.FlagTag << 56) | (1UL << 23) | (9UL << 16) | 0x4;
        Assert.False(LocalVarSyncRule.TryParseKey(forged, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(LocalVarKind.Boolean, 5, 1)]
    [InlineData(LocalVarKind.Boolean, -1, 1)]
    [InlineData(LocalVarKind.Boolean, 0, 0)]
    [InlineData(LocalVarKind.Number, -128, -128)]
    [InlineData(LocalVarKind.Number, 127, 127)]
    [InlineData(LocalVarKind.Number, 200, -56)]
    public void ValuesNormaliseTheWayTheEngineStoresThem(LocalVarKind kind, int raw, int expected)
    {
        Assert.Equal((sbyte)expected, LocalVarSyncRule.NormaliseValue(kind, raw));
    }

    [Theory]
    [InlineData((ushort)0x21, LocalVarKind.Boolean, (byte)10, (sbyte)1)]
    [InlineData((ushort)0x38, LocalVarKind.Number, (byte)4, (sbyte)-3)]
    public void AnEntryWordRoundTrips(ushort objectId, LocalVarKind kind, byte slot, sbyte value)
    {
        uint word = LocalVarSyncRule.PackEntry(objectId, kind, slot, value);
        LocalVarSyncRule.UnpackEntry(word, out ushort id, out LocalVarKind k, out byte s, out sbyte v);
        Assert.Equal((objectId, kind, slot, value), (id, k, s, v));
    }

    // ---- allowlist -------------------------------------------------------------------------

    [Fact]
    public void EveryAllowlistEntryIsWellFormedAndUnique()
    {
        HashSet<(string, string, LocalVarKind, byte)> seen = [];
        foreach (LocalVarAllowEntry entry in LocalVarSyncRule.Allowlist)
        {
            Assert.Matches("^[a-z0-9_]{1,16}$", entry.Module);
            Assert.Matches("^[A-Za-z0-9_]{1,31}$", entry.Tag);
            Assert.True(LocalVarSyncRule.IsValidSlot(entry.Kind, entry.Slot), $"{entry.Module}/{entry.Tag} slot {entry.Slot}");
            Assert.True(entry.Kind != LocalVarKind.Boolean || entry.Slot < 20,
                $"{entry.Module}/{entry.Tag} b{entry.Slot}: slots 20+ are the generic AI's per-engine flags");
            Assert.True(LocalVarSyncRule.IsGitObjectId(entry.ObjectId), $"{entry.Module}/{entry.Tag} id");
            Assert.False(string.IsNullOrWhiteSpace(entry.Writer));
            Assert.False(string.IsNullOrWhiteSpace(entry.Reader));
            Assert.False(string.IsNullOrWhiteSpace(entry.Why));
            Assert.True(seen.Add((entry.Module.ToLowerInvariant(), entry.Tag.ToLowerInvariant(), entry.Kind, entry.Slot)),
                $"duplicate entry {entry.Module}/{entry.Tag}");
        }
    }

    [Fact]
    public void MatchNeedsModuleTagKindSlotAndAGitId()
    {
        List<LocalVarAllowEntry> list =
        [
            new("danm13", "dan13_belaya", 0x4, LocalVarKind.Boolean, 10, "w", "r", "why"),
        ];
        uint module = LocalVarSyncRule.HashModule("danm13");

        Assert.NotNull(LocalVarSyncRule.Match(list, module, 0x4, "DAN13_BELAYA", LocalVarKind.Boolean, 10));
        // The live id is NOT required to equal the audit's first-load prediction.
        Assert.NotNull(LocalVarSyncRule.Match(list, module, 0x9, "dan13_belaya", LocalVarKind.Boolean, 10));
        Assert.Null(LocalVarSyncRule.Match(list, module, 0x10004, "dan13_belaya", LocalVarKind.Boolean, 10));
        Assert.Null(LocalVarSyncRule.Match(list, module, 0x4, "dan13_jedi", LocalVarKind.Boolean, 10));
        Assert.Null(LocalVarSyncRule.Match(list, module, 0x4, "dan13_belaya", LocalVarKind.Boolean, 11));
        Assert.Null(LocalVarSyncRule.Match(list, module, 0x4, "dan13_belaya", LocalVarKind.Number, 10 % 8));
        Assert.Null(LocalVarSyncRule.Match(list, LocalVarSyncRule.HashModule("danm14aa"), 0x4, "dan13_belaya", LocalVarKind.Boolean, 10));
    }

    // ---- capture stub ----------------------------------------------------------------------

    [Fact]
    public void TheOverwrittenCallsTargetTheTwoSetters()
    {
        Assert.Equal(LocalVarHookStubBuilder.SetLocalNumber,
            LocalVarHookStubBuilder.CallTarget(LocalVarHookStubBuilder.NumberCall, LocalVarHookStubBuilder.NumberCallSite));
        Assert.Equal(LocalVarHookStubBuilder.SetLocalBoolean,
            LocalVarHookStubBuilder.CallTarget(LocalVarHookStubBuilder.BooleanCall, LocalVarHookStubBuilder.BooleanCallSite));
        Assert.Equal(LocalVarHookStubBuilder.SiteLength, LocalVarHookStubBuilder.NumberCall.Length);
        Assert.Equal(LocalVarHookStubBuilder.SiteLength, LocalVarHookStubBuilder.BooleanCall.Length);
    }

    /// <summary>Every push moves esp by 4: value (frame +4) is read right after pushad, index
    /// (frame +0) after one push, object id (frame +0x14) after two — so value and index share
    /// ONE displacement, and the object id's is 0x20 + 0x14 + 8.</summary>
    [Fact]
    public void TheForwardedFrameOffsetsAccountForEveryPush()
    {
        Assert.Equal(0x24, LocalVarHookStubBuilder.ValueOffsetAfterPushad);
        Assert.Equal(0x24, LocalVarHookStubBuilder.IndexOffsetAfterOnePush);
        Assert.Equal(0x3C, LocalVarHookStubBuilder.ObjectIdOffsetAfterTwoPushes);
    }

    [Theory]
    [InlineData(LocalVarKind.Number, LocalVarHookStubBuilder.SetLocalNumber, LocalVarHookStubBuilder.NumberCallSite)]
    [InlineData(LocalVarKind.Boolean, LocalVarHookStubBuilder.SetLocalBoolean, LocalVarHookStubBuilder.BooleanCallSite)]
    public void TheStubReportsThenMakesTheOriginalCallThenReturns(LocalVarKind kind, uint setter, uint site)
    {
        const uint stub = 0x10000000;
        const uint callback = 0x20001234;
        byte[] bytes = new byte[LocalVarHookStubBuilder.StubLength];
        Assert.Equal(LocalVarHookStubBuilder.StubLength,
            LocalVarHookStubBuilder.WriteStub(bytes, kind, callback, stub, setter, site));

        byte[] expectedPrefix =
        [
            0x60,                         // pushad
            0xFF, 0x74, 0x24, 0x24,       // push [esp+0x24]  value
            0xFF, 0x74, 0x24, 0x24,       // push [esp+0x24]  index
            0xFF, 0x74, 0x24, 0x3C,       // push [esp+0x3C]  object id
            0x50,                         // push eax         table
            0x56,                         // push esi         object
            0x6A, (byte)kind,             // push kind
            0xE8,                         // call callback
        ];
        Assert.Equal(expectedPrefix, bytes[..expectedPrefix.Length]);
        Assert.Equal(callback, Target(bytes, 17, stub));
        Assert.Equal(new byte[] { 0x83, 0xC4, 0x18, 0x61, 0xE8 }, bytes[22..27]); // add esp,0x18; popad; call
        Assert.Equal(setter, Target(bytes, 26, stub));
        Assert.Equal(0xE9, bytes[31]);
        Assert.Equal(site + 5, Target(bytes, 31, stub));
        Assert.Equal(LocalVarHookStubBuilder.StubLength, 36);

        byte[] jump = new byte[5];
        Assert.True(LocalVarHookStubBuilder.WriteSiteJump(jump, stub, site));
        Assert.Equal(0xE9, jump[0]);
        Assert.Equal(stub, unchecked(site + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(jump.AsSpan(1))));

        static uint Target(byte[] code, int opcodeIndex, uint origin) =>
            unchecked(origin + (uint)opcodeIndex + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(opcodeIndex + 1)));
    }

    /// <summary>The verified bytes, checked against the real GOG 1.0.3 image when this machine
    /// has it (the dev rig does; CI does not, and then this is a no-op).</summary>
    [Fact]
    public void TheAnchorsAndCallsMatchTheGogImageWhenPresent()
    {
        const string exe = @"C:\GOG Games\Star Wars KOTOR (GOG)\swkotor.exe";
        if (!File.Exists(exe))
        {
            return;
        }

        byte[] image = File.ReadAllBytes(exe);
        AssertBytes(image, LocalVarHookStubBuilder.NumberCallSite - 8, LocalVarHookStubBuilder.NumberAnchor);
        AssertBytes(image, LocalVarHookStubBuilder.NumberCallSite, LocalVarHookStubBuilder.NumberCall);
        AssertBytes(image, LocalVarHookStubBuilder.BooleanCallSite - 8, LocalVarHookStubBuilder.BooleanAnchor);
        AssertBytes(image, LocalVarHookStubBuilder.BooleanCallSite, LocalVarHookStubBuilder.BooleanCall);

        static void AssertBytes(byte[] image, uint va, ReadOnlySpan<byte> expected)
        {
            int offset = FileOffset(image, va);
            Assert.True(offset > 0, $"0x{va:X} not in a section");
            Assert.True(image.AsSpan(offset, expected.Length).SequenceEqual(expected), $"bytes differ at 0x{va:X}");
        }

        static int FileOffset(byte[] image, uint va)
        {
            int pe = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C));
            int opt = pe + 24;
            uint imageBase = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(opt + 28));
            int sections = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 6));
            int first = opt + BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20));
            uint rva = va - imageBase;
            for (int i = 0; i < sections; i++)
            {
                int s = first + (i * 40);
                uint vsize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 8));
                uint vaddr = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 12));
                uint raw = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 20));
                if (rva >= vaddr && rva < vaddr + vsize)
                {
                    return (int)(raw + (rva - vaddr));
                }
            }

            return -1;
        }
    }

    // ---- agent drain / apply list ----------------------------------------------------------

    private static LocalVarCaptureRow Row(uint ticket, uint module, ushort id, LocalVarKind kind, byte slot, sbyte value, uint generation) =>
        new(module, LocalVarSyncRule.PackEntry(id, kind, slot, value), generation, ticket);

    [Fact]
    public void TheDrainKeepsOnlyCurrentWorldCurrentModuleRowsWithTheirOwnTicket()
    {
        uint here = LocalVarSyncRule.HashModule("danm13");
        uint there = LocalVarSyncRule.HashModule("danm14aa");
        Dictionary<uint, LocalVarCaptureRow> rows = new()
        {
            [1] = Row(1, here, 0x4, LocalVarKind.Boolean, 10, 1, 7),
            [2] = Row(2, there, 0x4, LocalVarKind.Boolean, 10, 1, 7),          // other module
            [3] = Row(3, here, 0x4, LocalVarKind.Boolean, 10, 1, 6),           // older world
            [4] = Row(99, here, 0x4, LocalVarKind.Boolean, 10, 1, 7),          // overwritten row
            [5] = Row(5, here, 0x38, LocalVarKind.Number, 4, -2, 7),
        };

        List<KeyValuePair<ulong, sbyte>> changes = LocalVarBlockRules.Drain(
            cursor: 5, drained: 0, generation: 7, moduleHash: here, t => rows[t], out uint lost);

        Assert.Equal(0u, lost);
        Assert.Equal(2, changes.Count);
        Assert.Equal(LocalVarSyncRule.MakeKey(here, 0x4, LocalVarKind.Boolean, 10), changes[0].Key);
        Assert.Equal(1, changes[0].Value);
        Assert.Equal(LocalVarSyncRule.MakeKey(here, 0x38, LocalVarKind.Number, 4), changes[1].Key);
        Assert.Equal(-2, changes[1].Value);
    }

    [Fact]
    public void TheDrainCountsRowsTheRingOverwroteAndReadsOnlyTheSurvivors()
    {
        uint here = LocalVarSyncRule.HashModule("danm13");
        List<uint> asked = [];
        List<KeyValuePair<ulong, sbyte>> changes = LocalVarBlockRules.Drain(
            cursor: 40, drained: 4, generation: 1, moduleHash: here,
            t => { asked.Add(t); return Row(t, here, 0x4, LocalVarKind.Boolean, 1, 1, 1); }, out uint lost);

        Assert.Equal(20u, lost);
        Assert.Equal(LocalVarBlockRules.CaptureCapacity, asked.Count);
        Assert.Equal(25u, asked[0]);
        Assert.Equal(40u, asked[^1]);
        Assert.Equal(LocalVarBlockRules.CaptureCapacity, changes.Count);
    }

    [Fact]
    public void GenerationZeroOrNothingNewDrainsNothing()
    {
        uint here = LocalVarSyncRule.HashModule("danm13");
        Assert.Empty(LocalVarBlockRules.Drain(3, 0, 0, here, t => Row(t, here, 4, LocalVarKind.Boolean, 1, 1, 0), out _));
        Assert.Empty(LocalVarBlockRules.Drain(3, 3, 1, here, _ => throw new InvalidOperationException(), out _));
    }

    [Fact]
    public void TheApplyListHoldsOnlyThisModulesLocalVarKeysInKeyOrder()
    {
        uint here = LocalVarSyncRule.HashModule("danm13");
        uint there = LocalVarSyncRule.HashModule("danm14aa");
        List<KeyValuePair<ulong, sbyte>> flags =
        [
            new(LocalVarSyncRule.MakeKey(here, 0x21, LocalVarKind.Boolean, 10), 1),
            new(DoorFlags.MakeKey(here, 0x21), 1),
            new(LocalVarSyncRule.MakeKey(there, 0x4, LocalVarKind.Boolean, 10), 1),
            new(LocalVarSyncRule.MakeKey(here, 0x4, LocalVarKind.Boolean, 10), 1),
            new(0x0003_0001UL, 5),
        ];

        int cursor = 0;
        List<uint> words = LocalVarBlockRules.SelectApplyEntries(flags, here, ref cursor);

        Assert.Equal(
            [
                LocalVarSyncRule.PackEntry(0x4, LocalVarKind.Boolean, 10, 1),
                LocalVarSyncRule.PackEntry(0x21, LocalVarKind.Boolean, 10, 1),
            ],
            words);
    }

    [Fact]
    public void ALargerSetRotatesSoEveryKeyIsOffered()
    {
        uint here = LocalVarSyncRule.HashModule("danm13");
        List<KeyValuePair<ulong, sbyte>> flags = [];
        for (ushort id = 1; id <= 40; id++)
        {
            flags.Add(new(LocalVarSyncRule.MakeKey(here, id, LocalVarKind.Boolean, 0), 1));
        }

        int cursor = 0;
        HashSet<uint> offered = [];
        for (int pass = 0; pass < 3; pass++)
        {
            List<uint> words = LocalVarBlockRules.SelectApplyEntries(flags, here, ref cursor);
            Assert.Equal(LocalVarBlockRules.ApplyCapacity, words.Count);
            offered.UnionWith(words);
        }

        Assert.Equal(40, offered.Count);
    }

    [Fact]
    public void TheAgentCapacitiesMirrorThePluginRegion()
    {
        Assert.Equal(SharedBlock.LocalVarCaptureCapacity, LocalVarBlockRules.CaptureCapacity);
        Assert.Equal(SharedBlock.LocalVarApplyCapacity, LocalVarBlockRules.ApplyCapacity);
        Assert.Equal(0x5800, SharedBlock.OffsetLocalVarCaptureCursor);
        Assert.Equal(SharedBlock.OffsetLocalVarApplySerial,
            SharedBlock.OffsetLocalVarCaptureRing + (SharedBlock.LocalVarCaptureCapacity * SharedBlock.LocalVarCaptureStride));
        // The region owns exactly 0x5800..0x5A00; v78's placed mines grow the block past it.
        Assert.Equal(0x5A00,
            SharedBlock.OffsetLocalVarApplyEntries + (SharedBlock.LocalVarApplyCapacity * SharedBlock.LocalVarApplyStride));
        Assert.True(SharedBlock.TotalSize >= 0x5A00);

        string agent = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "KotorCoop.Memory", "KotorGameBridge.LocalVars.cs"));
        Assert.Contains($"BlockLocalVarCaptureStride = 0x{SharedBlock.LocalVarCaptureStride:X2};", agent);
        Assert.Contains($"BlockLocalVarApplyStride = 0x{SharedBlock.LocalVarApplyStride:X2};", agent);
        Assert.Contains("LocalVarStatusEnabled = 1u << 1;", agent);
        Assert.Equal(1u << 1, SharedBlock.LocalVarStatusEnabled);
    }

    // ---- plugin block view -----------------------------------------------------------------

    private static unsafe SharedBlockView NewScratchBlock(out byte[] storage)
    {
        storage = GC.AllocateArray<byte>(SharedBlock.TotalSize, pinned: true);
        return new SharedBlockView((byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref storage[0]));
    }

    [Fact]
    public void ACaptureIsReadableUnderItsTicketUntilTheRingWraps()
    {
        SharedBlockView block = NewScratchBlock(out byte[] storage);
        uint first = block.PublishLocalVarCapture(0xAABBCCDD, 0x0100_0004, 3);
        Assert.Equal(1u, first);
        Assert.Equal(1u, block.LocalVarCaptureCursor);
        Assert.True(block.TryReadLocalVarCapture(1, out uint module, out uint word, out uint generation));
        Assert.Equal((0xAABBCCDDu, 0x0100_0004u, 3u), (module, word, generation));

        for (int i = 0; i < SharedBlock.LocalVarCaptureCapacity; i++)
        {
            block.PublishLocalVarCapture(1, 2, 3);
        }

        Assert.False(block.TryReadLocalVarCapture(1, out _, out _, out _));
        GC.KeepAlive(storage);
    }

    [Fact]
    public unsafe void TheApplyListIsRefusedWhileTheAgentIsWritingIt()
    {
        SharedBlockView block = NewScratchBlock(out byte[] storage);
        uint[] modules = new uint[SharedBlock.LocalVarApplyCapacity];
        uint[] words = new uint[SharedBlock.LocalVarApplyCapacity];

        fixed (byte* p = storage)
        {
            // Nothing published yet (serial 0).
            Assert.False(block.TryReadLocalVarApplyList(modules, words, out _, out _, out _));

            *(uint*)(p + SharedBlock.OffsetLocalVarApplySerial) = 3; // odd: mid-write
            *(uint*)(p + SharedBlock.OffsetLocalVarApplyCount) = 1;
            Assert.False(block.TryReadLocalVarApplyList(modules, words, out _, out _, out _));

            *(uint*)(p + SharedBlock.OffsetLocalVarApplyEntries) = 0x1234;
            *(uint*)(p + SharedBlock.OffsetLocalVarApplyEntries + 4) = 0x5678;
            *(uint*)(p + SharedBlock.OffsetLocalVarApplyGeneration) = 9;
            *(uint*)(p + SharedBlock.OffsetLocalVarApplyDrainedCursor) = 12;
            *(uint*)(p + SharedBlock.OffsetLocalVarApplySerial) = 4;

            Assert.True(block.TryReadLocalVarApplyList(modules, words, out int count, out uint generation, out uint drained));
            Assert.Equal((1, 9u, 12u, 0x1234u, 0x5678u), (count, generation, drained, modules[0], words[0]));
        }
    }

    [Fact]
    public void TheGenerationSkipsZeroOnWrap()
    {
        SharedBlockView block = NewScratchBlock(out byte[] storage);
        Assert.Equal(1u, block.AdvanceLocalVarGeneration());
        Assert.Equal(2u, block.AdvanceLocalVarGeneration());
        GC.KeepAlive(storage);
    }

    // ---- console verb ----------------------------------------------------------------------

    [Theory]
    [InlineData("localvar get dan13_belaya b 10", "get|b|10|dan13_belaya", "0", 0)]
    [InlineData("localvar set 0x4 b 10 true", "set|b|10|", "4", 1)]
    [InlineData("localvar set #38 n 4 -3", "set|n|4|", "56", -3)]
    [InlineData("localvar watch dan15_compb n 3", "watch|n|3|dan15_compb", "0", 0)]
    public void TheVerbParsesToTheCanonicalRequest(string line, string address, string id, int amount)
    {
        CoopCommand command = ConsoleCommand.Parse(line);
        Assert.Equal(CoopCommandKind.LocalVar, command.Kind);
        Assert.Equal(address, command.Address);
        Assert.Equal(id, command.Password);
        Assert.Equal(amount, command.Amount);

        Assert.True(LocalVarSyncRule.TryParseRequest(address, out string op, out _, out _, out _));
        Assert.Equal(address.Split('|')[0], op);
    }

    [Theory]
    [InlineData("localvar")]
    [InlineData("localvar get dan13_belaya b")]
    [InlineData("localvar peek dan13_belaya b 10")]
    [InlineData("localvar get dan13_belaya x 10")]
    [InlineData("localvar get dan13_belaya b 96")]
    [InlineData("localvar get dan13_belaya n 8")]
    [InlineData("localvar set dan13_belaya n 1 200")]
    [InlineData("localvar set dan13_belaya b 1")]
    [InlineData("localvar get 0x10000 b 1")]
    [InlineData("localvar get dan13|belaya b 1")]
    public void BadVerbLinesAreRefusedWithUsage(string line)
    {
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);
    }

    // ---- the OFF default, three readers ----------------------------------------------------

    [Fact]
    public void LocalVarSyncIsOffWhenTheKeyIsAbsentAndOnWhenSet()
    {
        string path = Path.Combine(Path.GetTempPath(), $"coop-localvar-{Guid.NewGuid():N}.ini");
        try
        {
            File.WriteAllText(path, "mode = host");
            Assert.False(CoopConfig.TryLoad(path)!.LocalVarSync);
            File.WriteAllText(path, "mode = host\nlocal_var_sync = 1");
            Assert.True(CoopConfig.TryLoad(path)!.LocalVarSync);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AllThreeDefaultReadersAgreeOnOff()
    {
        string root = RepositoryRoot();
        string composition = File.ReadAllText(Path.Combine(root, "src", "KotorCoop.Agent", "AgentComposition.cs"));
        Assert.Contains("LocalVarSync = config?.LocalVarSync ?? false", composition);
        Assert.Contains("localVarSync: agentSettings.LocalVarSync", composition);

        string plugin = File.ReadAllText(Path.Combine(root, "src", "KotorCoop.Plugin", "LocalVarController.cs"));
        Assert.Contains("PluginConfig.ReadBool(\"local_var_sync\", false)", plugin);

        string configDoc = File.ReadAllText(Path.Combine(root, "docs", "CONFIG.md"));
        Assert.Matches(new Regex(@"\|\s*`local_var_sync`\s*\|\s*OFF"), configDoc);
    }

    [Fact]
    public void TheDigestCarriesTheKeyAndDefaultsOff()
    {
        Assert.False(BehaviourDigest.Default.Has(BehaviourFlags.LocalVarSync));
        BehaviourDigest on = BehaviourDigest.FromFields(BehaviourDigest.Default.Flags | (uint)BehaviourFlags.LocalVarSync, 100, "any");
        BehaviourComparisonResult result = BehaviourCompatibility.Compare(BehaviourDigest.Default, on);
        Assert.Contains(result.Differences, d => d.Key == "local_var_sync" && !d.HostAuthoritative);
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        string? dir = Path.GetDirectoryName(sourcePath);
        while (dir is not null && !File.Exists(Path.Combine(dir, "KotorCoop.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir!;
    }
}
