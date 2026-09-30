namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    public const int OffsetLocalPartyOccupants = 0xBC00;
    public const int OffsetHostPartyOccupants = 0xBD00;
    public const int PartyOccupantRowsOffset = 24;
    public const int PartyOccupantStride = 24;
    public const int PartyOccupantBankSize = 256;
}
