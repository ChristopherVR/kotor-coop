using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Host-side state for ONE in-flight UDP world-transfer (<see cref="PacketType.WorldTransferRequest"/>
/// et al. — the fallback for <c>auto_adopt_world</c> when a UDP-only tunnel/NAT path drops
/// <see cref="WorldTransfer"/>'s separate TCP listener). See
/// docs/research/network-topology.md's UDP world-transfer section.
///
/// One instance per (endpoint, <see cref="RequestId"/>): a client only ever has one adoption
/// fetch in flight, so <see cref="CoopHost"/> keeps at most one active sender per client
/// endpoint and replaces it outright if a fresh request arrives (a retry, or a genuinely new
/// attempt) rather than trying to merge two in-flight transfers.
/// </summary>
public sealed class WorldTransferUdpSender
{
    private readonly byte[] _payload;
    private readonly byte[] _missing;

    /// <summary>Where the round-robin scan in <see cref="CollectDue"/> resumes each pump, so
    /// a long transfer's early chunks are not starved once the missing set has shrunk to a
    /// handful of scattered gaps near the end.</summary>
    private int _cursor;

    public WorldTransferUdpSender(uint requestId, byte[] payload, uint saveIdentity, long nowMs)
    {
        RequestId = requestId;
        _payload = payload;
        SaveIdentity = saveIdentity;
        TotalChunks = (payload.Length + ProtocolConstants.WorldTransferChunkPayloadSize - 1)
            / ProtocolConstants.WorldTransferChunkPayloadSize;

        // Every chunk starts "missing" — the client has nothing yet — so the very first pump
        // sends the whole payload without waiting for a status report to ask for it.
        _missing = new byte[(TotalChunks + 7) / 8];
        Array.Fill(_missing, (byte)0xFF);

        LastActivityMs = nowMs;
    }

    public uint RequestId { get; }

    public uint SaveIdentity { get; }

    public int TotalChunks { get; }

    public int TotalBytes => _payload.Length;

    public long LastActivityMs { get; private set; }

    /// <summary>Applies the client's latest <see cref="PacketType.WorldTransferChunkStatus"/>
    /// bitmap. Clamped to however many bytes this transfer's own bitmap holds — a client
    /// report that is a different length (a build with a different chunk-payload constant,
    /// say) is trimmed or padded rather than trusted outright, so it can never read or write
    /// outside this sender's own bounds.</summary>
    public void NoteStatus(ReadOnlySpan<byte> missingBitmap, long nowMs)
    {
        int n = Math.Min(missingBitmap.Length, _missing.Length);
        missingBitmap[..n].CopyTo(_missing);
        LastActivityMs = nowMs;
    }

    public bool IsMissing(int chunkIndex) => (_missing[chunkIndex / 8] & (1 << (chunkIndex % 8))) != 0;

    /// <summary>Chunk indices due to (re)send this pump, up to <paramref name="max"/>.</summary>
    public List<int> CollectDue(int max)
    {
        List<int> due = new(Math.Min(max, TotalChunks));

        for (int i = 0; i < TotalChunks && due.Count < max; i++)
        {
            int index = (_cursor + i) % TotalChunks;
            if (IsMissing(index))
            {
                due.Add(index);
            }
        }

        _cursor = TotalChunks == 0 ? 0 : (_cursor + max) % TotalChunks;
        return due;
    }

    /// <summary>The bytes for one chunk — full-size except the last, which is whatever
    /// remains.</summary>
    public ReadOnlySpan<byte> Chunk(int chunkIndex)
    {
        int offset = chunkIndex * ProtocolConstants.WorldTransferChunkPayloadSize;
        int length = Math.Min(ProtocolConstants.WorldTransferChunkPayloadSize, _payload.Length - offset);
        return _payload.AsSpan(offset, length);
    }
}
