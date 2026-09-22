using System.Buffers.Binary;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public sealed class LoadProvenanceCodecTests
{
    [Fact]
    public void DecodesStableCapturedMainMenuRequestWithWorldSerial()
    {
        byte[] payload = Payload(
            slot: 7,
            LoadProvenanceSource.MainMenuSaveEntry,
            LoadProvenanceStatus.Captured,
            worldSerial: 19,
            "QUICKSAVE"u8);

        Assert.True(LoadProvenanceCodec.TryDecode(12, payload, 12, out LoadProvenance? result));
        Assert.NotNull(result);
        Assert.Equal(7, result.Slot);
        Assert.Equal(LoadProvenanceSource.MainMenuSaveEntry, result.Source);
        Assert.Equal(LoadProvenanceStatus.Captured, result.Status);
        Assert.Equal(19u, result.WorldLoadSerialAtRequest);
        Assert.Equal("QUICKSAVE", result.SaveName);
    }

    [Theory]
    [InlineData(0, 2, 0, LoadProvenanceCodec.PayloadLength)]
    [InlineData(3, 3, 0, LoadProvenanceCodec.PayloadLength)]
    [InlineData(2, 4, 0, LoadProvenanceCodec.PayloadLength)]
    [InlineData(2, 2, 1, LoadProvenanceCodec.PayloadLength - 1)]
    public void RejectsUnpublishedTornOrPartialRecords(uint before, uint after, int unused, int length)
    {
        _ = unused;
        byte[] payload = Payload(1, LoadProvenanceSource.ServerLoadRequest,
            LoadProvenanceStatus.Captured, 2, "SAVE"u8);
        Assert.False(LoadProvenanceCodec.TryDecode(before, payload.AsSpan(0, length), after, out _));
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(3u)]
    public void RejectsUnknownSource(uint source)
    {
        byte[] payload = Payload(4, LoadProvenanceSource.ServerLoadRequest,
            LoadProvenanceStatus.Captured, 0, "SAVE"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), source);
        Assert.False(LoadProvenanceCodec.TryDecode(8, payload, 8, out _));
    }

    [Fact]
    public void RejectsOversizedLengthAndEmbeddedNul()
    {
        byte[] tooLong = Payload(0, LoadProvenanceSource.ServerLoadRequest,
            LoadProvenanceStatus.Captured, 0, "SAVE"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(tooLong.AsSpan(16, 4), LoadProvenanceCodec.NameCapacity + 1);
        Assert.False(LoadProvenanceCodec.TryDecode(2, tooLong, 2, out _));

        byte[] nul = Payload(0, LoadProvenanceSource.ServerLoadRequest,
            LoadProvenanceStatus.Captured, 0, "A\0B"u8);
        Assert.False(LoadProvenanceCodec.TryDecode(2, nul, 2, out _));
    }

    [Fact]
    public void CExoStringNameRequiresExactlyOneFinalNulAndReturnsContent()
    {
        byte[] stored = [.. "Game12"u8, 0];
        Assert.True(LoadProvenanceCodec.TryStripCExoTerminator(stored, out ReadOnlySpan<byte> content));
        Assert.Equal("Game12"u8.ToArray(), content.ToArray());
        Assert.False(LoadProvenanceCodec.TryStripCExoTerminator("Game12"u8, out _));
        Assert.False(LoadProvenanceCodec.TryStripCExoTerminator([0x41, 0, 0], out _));
    }

    [Fact]
    public void DecodesValidFailureStatusWithoutInventingAName()
    {
        byte[] payload = Payload(-1, LoadProvenanceSource.MainMenuSaveEntry,
            LoadProvenanceStatus.InvalidSaveName, 9, ReadOnlySpan<byte>.Empty);
        Assert.True(LoadProvenanceCodec.TryDecode(6, payload, 6, out LoadProvenance? result));
        Assert.NotNull(result);
        Assert.Equal(LoadProvenanceStatus.InvalidSaveName, result.Status);
        Assert.Empty(result.SaveNameBytes);
    }

    [Fact]
    public void SeparatesRealSaveRowsFromDirectDirectoryAndUnsupportedEntries()
    {
        Assert.Equal(LoadProvenanceSource.MainMenuSaveEntry, LoadProvenanceCodec.ClassifyMainMenuEntry(0x31));
        Assert.Equal(LoadProvenanceSource.MainMenuDirectDirectory, LoadProvenanceCodec.ClassifyMainMenuEntry(0x30));
        Assert.Equal(LoadProvenanceSource.MainMenuUnsupportedEntry, LoadProvenanceCodec.ClassifyMainMenuEntry(0));
        Assert.Equal(LoadProvenanceSource.MainMenuUnsupportedEntry, LoadProvenanceCodec.ClassifyMainMenuEntry(0x32));

        byte[] directory = Payload(LoadProvenance.NoSaveSlot,
            LoadProvenanceSource.MainMenuDirectDirectory,
            LoadProvenanceStatus.CapturedDirectory, 19, "000001 - AUTOSAVE"u8);
        Assert.True(LoadProvenanceCodec.TryDecode(14, directory, 14, out LoadProvenance? result));
        Assert.NotNull(result);
        Assert.False(result.HasSaveSlot);
        Assert.Null(result.SaveName);
        Assert.Equal("000001 - AUTOSAVE", result.DirectoryName);
        Assert.Empty(result.SaveNameBytes);

        byte[] unsupported = Payload(LoadProvenance.NoSaveSlot,
            LoadProvenanceSource.MainMenuUnsupportedEntry,
            LoadProvenanceStatus.UnsupportedEntry, 20, ReadOnlySpan<byte>.Empty);
        Assert.True(LoadProvenanceCodec.TryDecode(16, unsupported, 16, out LoadProvenance? unknown));
        Assert.NotNull(unknown);
        Assert.False(unknown.HasSaveSlot);
        Assert.Empty(unknown.ValueBytes);

        byte[] fakeSlot = Payload(0, LoadProvenanceSource.MainMenuDirectDirectory,
            LoadProvenanceStatus.CapturedDirectory, 19, "000001 - AUTOSAVE"u8);
        Assert.False(LoadProvenanceCodec.TryDecode(14, fakeSlot, 14, out _));
    }

    [Fact]
    public void GenerationAdvancesForRepeatedRequestsAndWrapSkipsZero()
    {
        uint second = LoadProvenanceGeneration.Next(2, out uint firstOdd);
        Assert.Equal(3u, firstOdd);
        Assert.Equal(4u, second);

        uint wrapped = LoadProvenanceGeneration.Next(0xFFFFFFFE, out uint wrappedOdd);
        Assert.Equal(0xFFFFFFFFu, wrappedOdd);
        Assert.Equal(2u, wrapped);
    }

    [Fact]
    public void EncodesBothNativeEntryStubsWithCorrectArgumentsAndContinuationTargets()
    {
        AssertStub(server: false, LoadProvenanceStubBuilder.MainMenuLength,
            callback: 0x00102000, resume: 0x006CA256);
        AssertStub(server: true, LoadProvenanceStubBuilder.ServerLength,
            callback: 0x00103000, resume: 0x004BA646);
    }

    private static void AssertStub(bool server, int length, uint callback, uint resume)
    {
        const uint stubAddress = 0x00100000;
        byte[] code = new byte[length];
        Assert.Equal(length, LoadProvenanceStubBuilder.Write(code, stubAddress, callback, resume, server));

        Assert.Equal(new byte[] { 0x9C, 0x60 }, code.AsSpan(0, 2).ToArray());
        Assert.Equal(new byte[] { 0x61, 0x9D }, code.AsSpan(length - 13, 2).ToArray());
        Assert.Equal(new byte[] { 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8 }, code.AsSpan(length - 11, 6).ToArray());

        int callOpcode = server ? 10 : 6;
        int callDisp = callOpcode + 1;
        Assert.Equal(0xE8, code[callOpcode]);
        uint callNext = unchecked(stubAddress + (uint)callOpcode + 5);
        uint callTarget = unchecked(callNext + (uint)BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(callDisp, 4)));
        Assert.Equal(callback, callTarget);

        int finalJump = length - 5;
        Assert.Equal(0xE9, code[finalJump]);
        uint jumpNext = unchecked(stubAddress + (uint)length);
        uint jumpTarget = unchecked(jumpNext + (uint)BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(finalJump + 1, 4)));
        Assert.Equal(resume, jumpTarget);

        if (server)
        {
            Assert.Equal(new byte[] { 0xFF, 0x74, 0x24, 0x2C, 0xFF, 0x74, 0x24, 0x2C }, code.AsSpan(2, 8).ToArray());
            Assert.Equal(new byte[] { 0x83, 0xC4, 0x08 }, code.AsSpan(15, 3).ToArray());
        }
        else
        {
            Assert.Equal(new byte[] { 0xFF, 0x74, 0x24, 0x28 }, code.AsSpan(2, 4).ToArray());
            Assert.Equal(new byte[] { 0x83, 0xC4, 0x04 }, code.AsSpan(11, 3).ToArray());
        }
    }

    private static byte[] Payload(
        int slot,
        LoadProvenanceSource source,
        LoadProvenanceStatus status,
        uint worldSerial,
        ReadOnlySpan<byte> name)
    {
        byte[] payload = new byte[LoadProvenanceCodec.PayloadLength];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), slot);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)source);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), (uint)status);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12, 4), worldSerial);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16, 4), (uint)name.Length);
        name.CopyTo(payload.AsSpan(20));
        return payload;
    }
}
