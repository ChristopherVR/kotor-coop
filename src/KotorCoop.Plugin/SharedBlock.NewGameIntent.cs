namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // v94: accepted native/console new-game intent; survives world invalidation.
    public const int OffsetNewGameIntent = 0xC400;
    public const int NewGameIntentSize = 24;
    // v95: generation sampled before publishing a native load request.
    public const int OffsetMenuLoadNewGameGeneration = 0xC418;
}
