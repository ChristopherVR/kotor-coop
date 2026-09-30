namespace KotorCoop.Memory;

internal static class PlaceableLocalChangeRule
{
    public static bool MayRelease(bool published, byte actualOpen, bool canonicalMatches, byte localOpen) =>
        published && actualOpen == localOpen && canonicalMatches;
}
