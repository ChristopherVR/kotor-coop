using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Client-side reassembly buffer for ONE in-flight UDP world-transfer — the receiving half of
/// <see cref="WorldTransferUdpSender"/>. See docs/research/network-topology.md's UDP
/// world-transfer section.
/// </summary>
public sealed class WorldTransferUdpReceiver
{
    private readonly byte[] _payload;
    private readonly bool[] _have;
    private int _haveCount;

    /// <summary>
    /// Validates <paramref name="totalBytes"/>/<paramref name="totalChunks"/> against
    /// <see cref="ProtocolConstants.MaxWorldTransferBytes"/>/<see cref="ProtocolConstants.MaxWorldTransferChunks"/>
    /// before allocating anything — a host must never be able to make a joiner allocate on the
    /// strength of a number it merely claimed, any more over this channel than
    /// <see cref="WorldTransfer"/>'s TCP path already refuses to.
    /// </summary>
    public WorldTransferUdpReceiver(uint requestId, uint saveIdentity, uint totalBytes, uint totalChunks)
    {
        if (totalBytes == 0 || totalBytes > ProtocolConstants.MaxWorldTransferBytes
            || totalChunks > ProtocolConstants.MaxWorldTransferChunks
            || totalChunks != (totalBytes + ProtocolConstants.WorldTransferChunkPayloadSize - 1)
                / ProtocolConstants.WorldTransferChunkPayloadSize)
        {
            throw new InvalidDataException(
                $"world transfer (UDP): implausible size ({totalBytes:N0} bytes, {totalChunks} chunks)");
        }

        RequestId = requestId;
        SaveIdentity = saveIdentity;
        TotalChunks = (int)totalChunks;
        _payload = new byte[totalBytes];
        _have = new bool[totalChunks];
    }

    public uint RequestId { get; }

    public uint SaveIdentity { get; }

    public int TotalChunks { get; }

    public bool IsComplete => _haveCount == TotalChunks;

    public int ReceivedCount => _haveCount;

    /// <summary>Stores one chunk. Out-of-range indices and a chunk whose claimed offset would
    /// spill past the payload are silently ignored — malformed input from this channel is a
    /// dropped chunk, exactly like a malformed datagram is a dropped packet everywhere else in
    /// this protocol, never a thrown exception mid-transfer. A duplicate (already-received)
    /// chunk is a no-op, so a resent chunk racing this one's own status report costs nothing.</summary>
    public void ReceiveChunk(int chunkIndex, ReadOnlySpan<byte> data)
    {
        if (chunkIndex < 0 || chunkIndex >= TotalChunks || _have[chunkIndex])
        {
            return;
        }

        int offset = chunkIndex * ProtocolConstants.WorldTransferChunkPayloadSize;
        if (data.Length != Math.Min(ProtocolConstants.WorldTransferChunkPayloadSize, _payload.Length - offset))
        {
            return;
        }

        data.CopyTo(_payload.AsSpan(offset));
        _have[chunkIndex] = true;
        _haveCount++;
    }

    /// <summary>One bit per chunk, set where still missing — the status report
    /// <see cref="PacketType.WorldTransferChunkStatus"/> ships.</summary>
    public byte[] MissingBitmap(int firstChunk = 0, int maxChunks = int.MaxValue)
    {
        if (firstChunk < 0 || firstChunk >= TotalChunks || firstChunk % 8 != 0 || maxChunks <= 0)
            throw new ArgumentOutOfRangeException(nameof(firstChunk));
        int count = Math.Min(maxChunks, TotalChunks - firstChunk);
        byte[] bitmap = new byte[(count + 7) / 8];
        for (int i = 0; i < count; i++)
        {
            if (!_have[firstChunk + i])
            {
                bitmap[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return bitmap;
    }

    /// <summary>The reassembled flat payload, valid once <see cref="IsComplete"/> — parse it
    /// with <see cref="WorldTransfer.ParsePayload"/>.</summary>
    public byte[] Payload => _payload;
}
