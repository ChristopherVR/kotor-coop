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
    [Fact]
    [Trait("Category", "Slow")]
    public async Task SeventeenMiBWorldTransfersThroughRealUdpHostAndPagedAcknowledgements()
    {
        byte[] archive = new byte[17 * 1024 * 1024];
        Random.Shared.NextBytes(archive);
        using CoopHost host = new(new SessionSettings { Port = 0, TickRate = 30, UpnpEnabled = false, StunServers = [] });
        host.SetWorldTransferProvider(() => ([new("SAVEGAME.sav", archive)], 0x12345678), 0);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(50));
        Task running = host.RunAsync(cts.Token);
        try
        {
            FetchedWorld fetched = await WorldTransferUdp.FetchAsync(
                new(System.Net.IPAddress.Loopback, host.ListenEndPoint.Port), 0, cts.Token);
            Assert.Equal(0x12345678u, fetched.SaveIdentity);
            Assert.Equal(archive, Assert.Single(fetched.Files).Data);
        }
        finally
        {
            cts.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
    }
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
        byte[] valid = WorldTransfer.SerializePayload([
            new("SAVEGAME.sav", new byte[64 * 1024 * 1024]),
            new("PARTYTABLE.res", new byte[64 * 1024 * 1024])]);
        byte[] payload = [..valid, 1, 0, 1, 0, 0, 0, (byte)'x', 1];
        payload[0] = 3;
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => WorldTransfer.ParsePayload(payload));
        Assert.Contains("total exceeds", error.Message);
    }

    [Fact]
    public void PagedAcknowledgementsAreMonotonicAndIndependent()
    {
        WorldTransferUdpSender sender = new(1, new byte[5000 * 1024], 1, 0);
        sender.NoteStatus([0], 1, 4096);
        Assert.False(sender.IsMissing(4096));
        Assert.True(sender.IsMissing(0));
        sender.NoteStatus([255], 2, 4096);
        Assert.False(sender.IsMissing(4096)); // late old page cannot undo an acknowledgement
        sender.NoteStatus([0], 3, 1); // unaligned page is ignored
        Assert.True(sender.IsMissing(0));
    }

    [Fact]
    public void ReceiverRequiresExactHeaderAndChunkLengths()
    {
        Assert.Throws<InvalidDataException>(() => new WorldTransferUdpReceiver(1, 1, uint.MaxValue, uint.MaxValue));
        Assert.Throws<InvalidDataException>(() => new WorldTransferUdpReceiver(1, 1, 1, uint.MaxValue));
        Assert.Throws<InvalidDataException>(() => new WorldTransferUdpReceiver(1, 1, 1025, 1));
        Assert.Throws<InvalidDataException>(() => new WorldTransferUdpReceiver(1, 1, 0, 1));
        WorldTransferUdpReceiver receiver = new(1, 1, 1025, 2);
        receiver.ReceiveChunk(0, new byte[1023]);
        receiver.ReceiveChunk(1, new byte[2]);
        Assert.Equal(0, receiver.ReceivedCount);
        receiver.ReceiveChunk(0, new byte[1024]);
        receiver.ReceiveChunk(1, new byte[1]);
        Assert.True(receiver.IsComplete);
    }

    [Fact]
    public void ChunkIndicesBeyondUshortRoundTripWithoutWrapping()
    {
        byte[] bytes = new byte[Protocol.ProtocolConstants.MaxPacketSize];
        Protocol.WorldTransferChunkPacket chunk = new(1, 65536, [42]);
        int length = Protocol.PacketCodec.Write(bytes, chunk);
        Assert.Equal(65536u, Protocol.PacketCodec.ReadWorldTransferChunk(bytes.AsSpan(0, length))!.ChunkIndex);
        Protocol.WorldTransferChunkStatusPacket page = new(1, [0], 65536);
        length = Protocol.PacketCodec.Write(bytes, page);
        Assert.Equal(65536u, Protocol.PacketCodec.ReadWorldTransferChunkStatus(bytes.AsSpan(0, length))!.FirstChunk);
    }

    [Fact]
    public void InvalidPageAndChunkWireBoundsRefuseBeforePayloadAllocation()
    {
        byte[] buffer = new byte[Protocol.ProtocolConstants.MaxPacketSize];
        foreach (Protocol.WorldTransferChunkStatusPacket page in new Protocol.WorldTransferChunkStatusPacket[]
            { new(1, [], 0), new(1, new byte[513], 0), new(1, [0], 1), new(1, [0], uint.MaxValue) })
        {
            int length = Protocol.PacketCodec.Write(buffer, page);
            Assert.Null(Protocol.PacketCodec.ReadWorldTransferChunkStatus(buffer.AsSpan(0, length)));
        }
        int chunkLength = Protocol.PacketCodec.Write(buffer, new Protocol.WorldTransferChunkPacket(1, uint.MaxValue, [1]));
        Assert.Null(Protocol.PacketCodec.ReadWorldTransferChunk(buffer.AsSpan(0, chunkLength)));
        chunkLength = Protocol.PacketCodec.Write(buffer, new Protocol.WorldTransferChunkPacket(1, 0, new byte[1025]));
        Assert.Null(Protocol.PacketCodec.ReadWorldTransferChunk(buffer.AsSpan(0, chunkLength)));
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
            totalBytes: 10, totalChunks: (uint)(Protocol.ProtocolConstants.MaxWorldTransferChunks + 1)));

        Assert.Throws<InvalidDataException>(() => new WorldTransferUdpReceiver(
            requestId: 1, saveIdentity: 0, totalBytes: 10, totalChunks: 0));
    }
}
