using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

internal sealed unsafe partial class SharedBlockView
{
    /// <summary>Writes a single pre-load observation. Caller publishes the final even
    /// generation only after this payload is complete.</summary>
    public void WriteLoadProvenancePayload(
        int slot,
        LoadProvenanceSource source,
        LoadProvenanceStatus status,
        uint worldLoadSerial,
        ReadOnlySpan<byte> saveName)
    {
        *I32(SharedBlock.OffsetLoadProvenanceSlot) = slot;
        *U32(SharedBlock.OffsetLoadProvenanceSource) = (uint)source;
        *U32(SharedBlock.OffsetLoadProvenanceStatus) = (uint)status;
        *U32(SharedBlock.OffsetLoadProvenanceWorldLoadSerial) = worldLoadSerial;
        *U32(SharedBlock.OffsetLoadProvenanceNameLength) = (uint)saveName.Length;

        byte* destination = baseAddress + SharedBlock.OffsetLoadProvenanceName;
        new Span<byte>(destination, SharedBlock.LoadProvenanceNameCapacity).Clear();
        saveName.CopyTo(new Span<byte>(destination, SharedBlock.LoadProvenanceNameCapacity));
    }

    public void PublishLoadProvenanceGeneration(uint generation)
    {
        int* cell = (int*)U32(SharedBlock.OffsetLoadProvenanceGeneration);
        System.Threading.Volatile.Write(ref *cell, unchecked((int)generation));
    }

    public void BeginLoadProvenanceWrite(uint generation)
    {
        int* cell = (int*)U32(SharedBlock.OffsetLoadProvenanceGeneration);
        System.Threading.Interlocked.Exchange(ref *cell, unchecked((int)generation));
    }
}
