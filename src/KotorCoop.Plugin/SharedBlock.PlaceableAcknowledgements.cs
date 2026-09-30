namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // v85 native applied state, separate from the placeable/mine desired slot bytes.
    // 32 rows of { objectId:u32, actual tuple:u32, generation:u32 }. Tuple bits are
    // open, unlocked, currently trapped. Generation zero means no completed apply.
    public const int OffsetPlaceableAcknowledgements = 0xB800;
    public const int PlaceableAcknowledgementStride = 12;
    // v86 genuine native local interaction edges, independent of mirror ACKs.
    public const int OffsetLocalPlaceableChanges = 0xBA00;
    public const int LocalPlaceableChangeStride = 12;
}
