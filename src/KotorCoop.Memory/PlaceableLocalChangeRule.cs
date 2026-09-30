namespace KotorCoop.Memory;

internal static class PlaceableLocalChangeRule
{
    public static bool ShouldPublish(bool published, byte previousLocked, byte previousTrapped,
        byte locked, byte trapped) =>
        !published || previousLocked != locked || previousTrapped != trapped;
    public static byte MergeOpenWithCurrentState(byte recorded, byte locked, byte trapped) =>
        (byte)((recorded & 1) | (locked == 0 ? 2 : 0) | (trapped != 0 ? 4 : 0));
    public static bool MayRelease(bool published, byte actualOpen, bool canonicalMatches, byte localOpen) =>
        published && actualOpen == localOpen && canonicalMatches;
}
