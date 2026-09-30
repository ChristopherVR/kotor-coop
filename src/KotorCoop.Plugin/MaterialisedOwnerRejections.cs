namespace KotorCoop.Plugin;

// An invalidated mapping must not become permission to use the owner's raw id.
// Engine creature ids are 16-bit; this bitmap stays bounded for a whole world.
internal sealed class MaterialisedOwnerRejections
{
    internal const uint InvalidObject = 0x7F000000;
    private readonly uint[] _bits = new uint[2048];
    public void Reject(uint ownerId)
    {
        if (ownerId is > 0 and <= 0xFFFF) _bits[ownerId >> 5] |= 1u << (int)(ownerId & 31);
    }
    public void AdmitFreshBody(uint ownerId)
    {
        if (ownerId is > 0 and <= 0xFFFF) _bits[ownerId >> 5] &= ~(1u << (int)(ownerId & 31));
    }
    public uint Resolve(uint ownerId, uint mappedId) => mappedId != 0 ? mappedId
        : ownerId is > 0 and <= 0xFFFF && (_bits[ownerId >> 5] & (1u << (int)(ownerId & 31))) != 0
            ? InvalidObject : 0;
    public void Clear() => Array.Clear(_bits);
}
