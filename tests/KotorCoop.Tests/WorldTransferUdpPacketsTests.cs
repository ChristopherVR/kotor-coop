using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public class WorldTransferUdpPacketsTests
{
    [Fact]
    public void RequestPacketRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        WorldTransferRequestPacket request = new(42, 0xDEADBEEF);
        int length = PacketCodec.Write(buffer, request);
        Assert.Equal(request, PacketCodec.ReadWorldTransferRequest(buffer[..length]));
    }

    [Theory]
    [InlineData(WorldTransferUdpStatus.Ok)]
    [InlineData(WorldTransferUdpStatus.BadPassword)]
    [InlineData(WorldTransferUdpStatus.NoWorld)]
    public void HeaderPacketRoundTrips(WorldTransferUdpStatus status)
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        WorldTransferHeaderPacket header = new(42, status, 0x1234ABCD, 654_000, 640);
        int length = PacketCodec.Write(buffer, header);
        Assert.Equal(header, PacketCodec.ReadWorldTransferHeader(buffer[..length]));
    }

    [Fact]
    public void HeaderPacketRejectsAnUndefinedStatus()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new WorldTransferHeaderPacket(1, WorldTransferUdpStatus.Ok, 0, 0, 0));

        // Corrupt the status byte (right after the 4-byte header + 4-byte RequestId) to a value
        // no WorldTransferUdpStatus member uses.
        buffer[8] = 200;
        Assert.Null(PacketCodec.ReadWorldTransferHeader(buffer[..length]));
    }

    [Fact]
    public void ChunkPacketCarriesExactlyWhateverPayloadItWasGiven()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        byte[] payload = [.. Enumerable.Range(0, ProtocolConstants.WorldTransferChunkPayloadSize).Select(i => (byte)i)];
        WorldTransferChunkPacket chunk = new(7, 3, payload);
        int length = PacketCodec.Write(buffer, chunk);

        WorldTransferChunkPacket? read = PacketCodec.ReadWorldTransferChunk(buffer[..length]);
        Assert.NotNull(read);
        Assert.Equal(chunk.RequestId, read.RequestId);
        Assert.Equal(chunk.ChunkIndex, read.ChunkIndex);
        Assert.Equal(payload, read.Payload);
    }

    [Fact]
    public void ChunkPacketAllowsAShortLastChunk()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        byte[] shortPayload = [1, 2, 3];
        WorldTransferChunkPacket chunk = new(7, 639, shortPayload);
        int length = PacketCodec.Write(buffer, chunk);

        WorldTransferChunkPacket? read = PacketCodec.ReadWorldTransferChunk(buffer[..length]);
        Assert.NotNull(read);
        Assert.Equal(shortPayload, read.Payload);
    }

    [Fact]
    public void ChunkStatusPacketCarriesTheWholeBitmap()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        byte[] bitmap = [0xFF, 0x00, 0x0F, 0xF0];
        WorldTransferChunkStatusPacket status = new(9, bitmap);
        int length = PacketCodec.Write(buffer, status);

        WorldTransferChunkStatusPacket? read = PacketCodec.ReadWorldTransferChunkStatus(buffer[..length]);
        Assert.NotNull(read);
        Assert.Equal(status.RequestId, read.RequestId);
        Assert.Equal(bitmap, read.MissingBitmap);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletePacketRoundTrips(bool success)
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        WorldTransferCompletePacket complete = new(11, success);
        int length = PacketCodec.Write(buffer, complete);
        Assert.Equal(complete, PacketCodec.ReadWorldTransferComplete(buffer[..length]));
    }
}
