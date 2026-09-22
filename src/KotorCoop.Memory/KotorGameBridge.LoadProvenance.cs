using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    private const int BlockLoadProvenanceGeneration = 0x4E50;
    private const int BlockLoadProvenancePayload = 0x4E54;

    /// <summary>Reads one stable v74 native-load observation. The generation brackets the
    /// payload read; an odd, changed, zero, or unreadable generation is a transient miss.</summary>
    public bool TryReadLoadProvenance(out LoadProvenance? provenance)
    {
        provenance = null;
        nint block = _pluginBlock;
        if (block == 0) return false;

        Span<byte> payload = stackalloc byte[LoadProvenanceCodec.PayloadLength];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!_memory.TryReadUInt32(block + BlockLoadProvenanceGeneration, out uint before)
                || before == 0 || (before & 1) != 0
                || !_memory.TryRead(block + BlockLoadProvenancePayload, payload)
                || !_memory.TryReadUInt32(block + BlockLoadProvenanceGeneration, out uint after))
            {
                continue;
            }

            if (LoadProvenanceCodec.TryDecode(before, payload, after, out provenance))
            {
                return true;
            }
        }

        return false;
    }
}
