namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // v93, keyed to the existing pre-request LoadProvenance generation. No wire change.
    public const int OffsetNativePartyLoadProof=0xC200;
    public const int NativePartyLoadProofSize=224;
}
