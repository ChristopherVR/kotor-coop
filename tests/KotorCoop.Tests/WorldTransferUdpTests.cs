using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The UDP world-transfer fallback's non-network-facing pieces: the flat-payload
/// serialise/parse round trip <see cref="WorldTransfer"/> shares with the TCP path, and the
/// chunking/reassembly/missing-bitmap logic in <see cref="WorldTransferUdpSender"/>/
/// <see cref="WorldTransferUdpReceiver"/>. See docs/research/network-topology.md's UDP
/// world-transfer section.
/// </summary>
public class WorldTransferUdpTests
{
    private static WorldFile[] SampleWorld() =>
    [
        new WorldFile("SAVEGAME.sav", [.. Enumerable.Range(0, 500).Select(i => (byte)i)]),
        new WorldFile("GLOBALVARS.res", [1, 2, 3, 4, 5]),
        new WorldFile("PARTYTABLE.res", []),
    ];

    [Fact]
    public void SharedPayloadDecoderAcceptsSeventeenMiBModArchive()
    {
        byte[] archive = new byte[17 * 1024 * 1024];
        archive[^1] = 71;
        var parsed = WorldTransfer.ParsePayload(WorldTransfer.SerializePayload([new("SAVEGAME.sav", archive)]));
        Assert.Equal(archive, Assert.Single(parsed).Data);
    }

    [Fact]
    public void SharedPayloadDecoderRejectsOversizedFileBeforeAllocation()
    {
        byte[] header = new byte[8];
        header[0] = 1; header[2] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 64u * 1024 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => WorldTransfer.ParsePayload(header));
    }

    [Fact]
    public void SharedPayloadAggregateStillRejectsThirdFileBeyond128MiB()
    {
        byte[] payload = WorldTransfer.SerializePayload([
            new("SAVEGAME.sav", new byte[64 * 1024 * 1024]),
            new("PARTYTABLE.res", new byte[64 * 1024 * 1024]),
            new("GLOBALVARS.res", [1])]);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => WorldTransfer.ParsePayload(payload));
        Assert.Contains("total exceeds", error.Message);
    }

    [Fact]
    public void SerializePayloadThenParsePayloadRoundTrips()
    {
        WorldFile[] world = SampleWorld();

        byte[] payload = WorldTransfer.SerializePayload(world);
        IReadOnlyList<WorldFile> parsed = WorldTransfer.ParsePayload(payload);

        Assert.Equal(world.Length, parsed.Count);
        for (int i = 0; i < world.Length; i++)
        {
            Assert.Equal(world[i].Name, parsed[i].Name);
            Assert.Equal(world[i].Data, parsed[i].Data);
        }
    }

    [Fact]
    public void ParsePayloadRefusesAnImplausibleFileCount()
    {
        byte[] payload = [255, 255]; // count = 65535, way past MaxFiles
        Assert.Throws<InvalidDataException>(() => WorldTransfer.ParsePayload(payload));
    }

    [Fact]
    public void ParsePayloadRefusesATruncatedPayload()
    {
        byte[] full = WorldTransfer.SerializePayload(SampleWorld());
        byte[] truncated = full[..(full.Length - 10)];
        Assert.Throws<InvalidDataException>(() => WorldTransfer.ParsePayload(truncated));
    }

    [Fact]
    public void SenderChunksAPayloadAndCollectsEveryChunkOnTheFirstPass()
    {
        byte[] payload = [.. Enumerable.Range(0, 5000).Select(i => (byte)i)];
        WorldTransferUdpSender sender = new(requestId: 1, payload, saveIdentity: 0xABCD, nowMs: 0);

        int expectedChunks = (payload.Length + 1023) / 1024;
        Assert.Equal(expectedChunks, sender.TotalChunks);
        Assert.Equal(payload.Length, sender.TotalBytes);

        // Nothing has been reported received yet, so every chunk is due across enough pumps.
        HashSet<int> seen = [];
        for (int i = 0; i < expectedChunks; i++)
        {
            List<int> due = sender.CollectDue(max: 1);
            Assert.Single(due);
            seen.Add(due[0]);
        }

        Assert.Equal(expectedChunks, seen.Count);
    }

    [Fact]
    public void SenderStopsOfferingAChunkOnceTheClientReportsItReceived()
    {
        byte[] payload = new byte[3000]; // 3 chunks at 1024 bytes
        WorldTransferUdpSender sender = new(requestId: 1, payload, saveIdentity: 0, nowMs: 0);
        Assert.Equal(3, sender.TotalChunks);

        // Client reports it already has chunk 0 and 2 (bits 0 and 2 clear, bit 1 set = missing).
        sender.NoteStatus([0b0000_0010], nowMs: 100);

        List<int> due = sender.CollectDue(max: 10);
        Assert.Equal([1], due);
    }

    [Fact]
    public void SenderChunkBytesMatchThePayloadSlice()
    {
        byte[] payload = [.. Enumerable.Range(0, 2500).Select(i => (byte)(i % 256))];
        WorldTransferUdpSender sender = new(requestId: 1, payload, saveIdentity: 0, nowMs: 0);

        Assert.Equal(payload[..1024], sender.Chunk(0).ToArray());
        Assert.Equal(payload[1024..2048], sender.Chunk(1).ToArray());
        Assert.Equal(payload[2048..2500], sender.Chunk(2).ToArray()); // short last chunk
    }

    [Fact]
    public void ReceiverReassemblesEveryChunkIntoTheOriginalPayload()
    {
        byte[] payload = [.. Enumerable.Range(0, 2500).Select(i => (byte)(i % 256))];
        WorldTransferUdpSender sender = new(requestId: 1, payload, saveIdentity: 0x99, nowMs: 0);
        WorldTransferUdpReceiver receiver = new(
            requestId: 1, saveIdentity: 0x99, totalBytes: (uint)payload.Length, totalChunks: (ushort)sender.TotalChunks);

        Assert.False(receiver.IsComplete);

        for (int i = 0; i < sender.TotalChunks; i++)
        {
            receiver.ReceiveChunk(i, sender.Chunk(i));
        }

        Assert.True(receiver.IsComplete);
        Assert.Equal(payload, receiver.Payload);
    }

    [Fact]
    public void ReceiverIgnoresADuplicateChunkAndAnOutOfRangeIndex()
    {
        WorldTransferUdpReceiver receiver = new(requestId: 1, saveIdentity: 0, totalBytes: 10, totalChunks: 1);

        receiver.ReceiveChunk(0, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        Assert.True(receiver.IsComplete);
        Assert.Equal(1, receiver.ReceivedCount);

        receiver.ReceiveChunk(0, [9, 9, 9, 9, 9, 9, 9, 9, 9, 9]); // duplicate — ignored
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], receiver.Payload);

        receiver.ReceiveChunk(5, [1]); // out of range — ignored, no throw
        Assert.Equal(1, receiver.ReceivedCount);
    }

    [Fact]
    public void ReceiverMissingBitmapReflectsWhatHasNotArrivedYet()
    {
        WorldTransferUdpReceiver receiver = new(requestId: 1, saveIdentity: 0, totalBytes: 3000, totalChunks: 3);

        Assert.Equal([0b0000_0111], receiver.MissingBitmap()); // all three missing

        receiver.ReceiveChunk(1, new byte[1024]);
        Assert.Equal([0b0000_0101], receiver.MissingBitmap()); // bit 1 clear now

        receiver.ReceiveChunk(0, new byte[1024]);
        receiver.ReceiveChunk(2, new byte[952]);
        Assert.Equal([0b0000_0000], receiver.MissingBitmap());
        Assert.True(receiver.IsComplete);
    }

    [Fact]
    public void ReceiverRefusesAnImplausibleSizeBeforeAllocating()
    {
        Assert.Throws<InvalidDataException>(() => new WorldTransferUdpReceiver(
            requestId: 1, saveIdentity: 0,
            totalBytes: Protocol.ProtocolConstants.MaxWorldTransferBytes + 1, totalChunks: 1));

        Assert.Throws<InvalidDataException>(() => new WorldTransferUdpReceiver(
            requestId: 1, saveIdentity: 0,
            totalBytes: 10, totalChunks: (ushort)(Protocol.ProtocolConstants.MaxWorldTransferChunks + 1)));

        Assert.Throws<InvalidDataException>(() => new WorldTransferUdpReceiver(
            requestId: 1, saveIdentity: 0, totalBytes: 10, totalChunks: 0));
    }
}
