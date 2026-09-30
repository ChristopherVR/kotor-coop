namespace KotorCoop.Plugin;

internal static class NewGameLoadFenceRule
{
    // Manual intent allows a newly submitted load after New Game. It never allows
    // an older queued load to overwrite character creation.
    internal static bool MayStart(uint expectedGeneration,uint currentGeneration)
        => (expectedGeneration&1)==0 && (currentGeneration&1)==0 && expectedGeneration==currentGeneration;
}
