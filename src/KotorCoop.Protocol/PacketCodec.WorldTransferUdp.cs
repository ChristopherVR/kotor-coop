namespace KotorCoop.Protocol;

// The UDP world-transfer fallback: request, header, chunk, chunk-status and complete.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, WorldTransferRequestPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.WorldTransferRequest);
        w.WriteUInt32(packet.RequestId);
        w.WriteUInt32(packet.PasswordHash);
        return w.Failed ? 0 : w.Position;
    }

    public static WorldTransferRequestPacket? ReadWorldTransferRequest(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.WorldTransferRequest)
        {
            return null;
        }

        uint requestId = r.ReadUInt32();
        uint passwordHash = r.ReadUInt32();
        return r.Failed ? null : new WorldTransferRequestPacket(requestId, passwordHash);
    }

    public static int Write(Span<byte> buffer, WorldTransferHeaderPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.WorldTransferHeader);
        w.WriteUInt32(packet.RequestId);
        w.WriteUInt8((byte)packet.Status);
        w.WriteUInt32(packet.SaveIdentity);
        w.WriteUInt32(packet.TotalBytes);
        w.WriteUInt32(packet.TotalChunks);
        return w.Failed ? 0 : w.Position;
    }

    public static WorldTransferHeaderPacket? ReadWorldTransferHeader(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.WorldTransferHeader)
        {
            return null;
        }

        uint requestId = r.ReadUInt32();
        byte status = r.ReadUInt8();
        uint saveIdentity = r.ReadUInt32();
        uint totalBytes = r.ReadUInt32();
        uint totalChunks = r.ReadUInt32();

        if (r.Failed || !Enum.IsDefined((WorldTransferUdpStatus)status))
        {
            return null;
        }

        return new WorldTransferHeaderPacket(
            requestId, (WorldTransferUdpStatus)status, saveIdentity, totalBytes, totalChunks);
    }

    /// <summary>
    /// The payload fills whatever the datagram has left after the fixed fields — there is no
    /// length prefix, the same way <see cref="ProtocolConstants.MaxPacketSize"/>'s own datagram
    /// boundary already tells a receiver how much arrived. A caller writing a full-size chunk
    /// passes exactly <see cref="ProtocolConstants.WorldTransferChunkPayloadSize"/> bytes; the
    /// last chunk of a transfer passes fewer.
    /// </summary>
    public static int Write(Span<byte> buffer, WorldTransferChunkPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.WorldTransferChunk);
        w.WriteUInt32(packet.RequestId);
        w.WriteUInt32(packet.ChunkIndex);
        w.WriteBytes(packet.Payload);
        return w.Failed ? 0 : w.Position;
    }

    public static WorldTransferChunkPacket? ReadWorldTransferChunk(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.WorldTransferChunk)
        {
            return null;
        }

        uint requestId = r.ReadUInt32();
        uint chunkIndex = r.ReadUInt32();

        if (r.Failed || chunkIndex >= ProtocolConstants.MaxWorldTransferChunks
            || r.Remaining == 0 || r.Remaining > ProtocolConstants.WorldTransferChunkPayloadSize)
        {
            return null;
        }

        // Whatever is left in this datagram IS the payload — a chunk carries no length field
        // of its own (see this method's Write-side doc comment).
        byte[] payload = new byte[r.Remaining];
        r.ReadBytes(payload);
        return r.Failed ? null : new WorldTransferChunkPacket(requestId, chunkIndex, payload);
    }

    /// <summary>
    /// The bitmap fills whatever the datagram has left, mirroring
    /// <see cref="Write(Span{byte},WorldTransferChunkPacket)"/>'s no-length-field shape — both
    /// sides already know the transfer's chunk count from the header, so the bitmap's own byte
    /// count is implied rather than repeated on the wire.
    /// </summary>
    public static int Write(Span<byte> buffer, WorldTransferChunkStatusPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.WorldTransferChunkStatus);
        w.WriteUInt32(packet.RequestId);
        w.WriteUInt32(packet.FirstChunk);
        w.WriteBytes(packet.MissingBitmap);
        return w.Failed ? 0 : w.Position;
    }

    public static WorldTransferChunkStatusPacket? ReadWorldTransferChunkStatus(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.WorldTransferChunkStatus)
        {
            return null;
        }

        uint requestId = r.ReadUInt32();
        uint firstChunk = r.ReadUInt32();
        if (r.Failed || firstChunk % 8 != 0 || firstChunk >= ProtocolConstants.MaxWorldTransferChunks
            || r.Remaining == 0 || r.Remaining > ProtocolConstants.WorldTransferStatusBitmapBytes)
        {
            return null;
        }

        byte[] bitmap = new byte[r.Remaining];
        r.ReadBytes(bitmap);
        return r.Failed ? null : new WorldTransferChunkStatusPacket(requestId, bitmap, firstChunk);
    }

    public static int Write(Span<byte> buffer, WorldTransferCompletePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.WorldTransferComplete);
        w.WriteUInt32(packet.RequestId);
        w.WriteBool(packet.Success);
        return w.Failed ? 0 : w.Position;
    }

    public static WorldTransferCompletePacket? ReadWorldTransferComplete(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.WorldTransferComplete)
        {
            return null;
        }

        uint requestId = r.ReadUInt32();
        bool success = r.ReadBool();
        return r.Failed ? null : new WorldTransferCompletePacket(requestId, success);
    }
}
