namespace KotorCoop.Memory;

internal static class PlayerControlIdentity
{
    internal static bool TryGetCorrection(uint controlled, uint verifiedPlayerId, out uint corrected)
    {
        corrected = 0;
        if (verifiedPlayerId is 0 or 0x7F000000 || (verifiedPlayerId & 0x80000000) != 0)
            return false;

        corrected = verifiedPlayerId | 0x80000000;
        return (controlled & 0x80000000) != 0 && controlled != corrected;
    }
}
