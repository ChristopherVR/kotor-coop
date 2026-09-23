using System.Buffers.Binary;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public class StorePriceRuleTests
{
    [Theory]
    [InlineData(110, 100, 110)]
    [InlineData(110, 150, 165)]
    [InlineData(111, 150, 166)]
    [InlineData(1, 50, 1)]
    [InlineData(2, 50, 1)]
    [InlineData(0, 400, 0)]
    [InlineData(-1, 200, -1)]
    [InlineData(int.MaxValue, 400, int.MaxValue)]
    public void ScalesRetailIntegerOnceWithFloorAndSafeBounds(int retail, int percent, int expected) =>
        Assert.Equal(expected, StorePriceRule.ScaleRetailPrice(retail, percent));

    [Fact]
    public void NeutralPercentPreservesRetailResultExactly() =>
        Assert.Equal(123456789, StorePriceRule.ScaleRetailPrice(123456789, 100));

    [Fact]
    public void EntryStubArgumentsResolveToTheOriginalThisAndStackItemAfterPushad()
    {
        byte[] stub = new byte[StorePriceHookStubBuilder.DetourLength];
        Assert.Equal(StorePriceHookStubBuilder.DetourLength,
            StorePriceHookStubBuilder.WriteDetour(stub, callbackAddress: 0x2000, stubAddress: 0x1000));

        // PUSHAD lays out EDI, ESI, EBP, original ESP, EBX, EDX, ECX, EAX. At entry
        // [ESP+4] is item; after PUSHAD it is +0x24. After pushing item, saved ECX is +0x1C.
        // Exercise the offsets emitted in the real stub against known register/stack values.
        Assert.Equal(new byte[] { 0xFF, 0x74, 0x24 }, stub.AsSpan(1, 3).ToArray());
        Assert.Equal(new byte[] { 0xFF, 0x74, 0x24 }, stub.AsSpan(5, 3).ToArray());
        uint[] afterPushad = [0xD1, 0x51, 0xB0, 0x1000, 0xB0, 0xD2, 0xCAFE, 0xA0, 0xAAAA, 0xBEEF];

        uint pushedItem = afterPushad[stub[4] / sizeof(uint)];
        Assert.Equal(0xBEEFu, pushedItem);
        List<uint> afterItemPush = [pushedItem, .. afterPushad];
        uint pushedPanel = afterItemPush[stub[8] / sizeof(uint)];
        Assert.Equal(0xCAFEu, pushedPanel);
        List<uint> callbackStack = [pushedPanel, pushedItem, .. afterPushad];
        Assert.Equal(new uint[] { 0xCAFE, 0xBEEF }, callbackStack.Take(2));

        int callbackDisplacement = BinaryPrimitives.ReadInt32LittleEndian(stub.AsSpan(10, 4));
        Assert.Equal(unchecked(0x2000 - (0x1000 + 14)), callbackDisplacement);
        Assert.Equal(new byte[] { 0x83, 0xC4, 0x08, 0x89, 0x44, 0x24, 0x1C, 0x61, 0xC2, 0x04, 0x00 },
            stub.AsSpan(14, 11).ToArray());
    }

    [Fact]
    public void TrampolineReplaysTheWholeNonRelativeGetterEntryAndReturnsAtPlusEight()
    {
        byte[] trampoline = new byte[StorePriceHookStubBuilder.TrampolineLength];
        Assert.Equal(13, StorePriceHookStubBuilder.WriteTrampoline(
            trampoline, StorePriceHookStubBuilder.GetterPrologue, entryAddress: 0x006C0790, trampolineAddress: 0x3000));
        Assert.Equal(StorePriceHookStubBuilder.GetterPrologue.ToArray(), trampoline.AsSpan(0, 8).ToArray());
        Assert.Equal(0xE9, trampoline[8]);
        int displacement = BinaryPrimitives.ReadInt32LittleEndian(trampoline.AsSpan(9, 4));
        Assert.Equal(unchecked((0x006C0790 + 8) - (0x3000 + 13)), displacement);
        Assert.Equal(0, StorePriceHookStubBuilder.WriteTrampoline(
            trampoline, new byte[8], entryAddress: 0x006C0790, trampolineAddress: 0x3000));
    }

    [Fact]
    public void EntryJumpReplacesExactlyTheVerifiedEightBytePrologue()
    {
        byte[] entry = new byte[8];
        Assert.True(StorePriceHookStubBuilder.WriteEntryJump(entry, stubAddress: 0x4000, entryAddress: 0x006C07F0));
        Assert.Equal(0xE9, entry[0]);
        Assert.Equal(unchecked(0x4000 - (0x006C07F0 + 5)),
            BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(1, 4)));
        Assert.Equal(new byte[] { 0x90, 0x90, 0x90 }, entry.AsSpan(5, 3).ToArray());
    }
}
