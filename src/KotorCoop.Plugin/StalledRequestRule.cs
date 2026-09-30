namespace KotorCoop.Plugin;

/// <summary>Only automatic requests become stale when first observed after a blocked thread.
/// A human command has explicit intent, while an automatic request refers to an old session
/// observation. Already-started loads must continue through their native completion path.</summary>
internal static class StalledRequestRule
{
    public const long StaleAfterStallMs = 45_000;

    public static bool ShouldRefuse(long frameGapMs, bool manualIntent, bool alreadyStarted = false)
        => frameGapMs > StaleAfterStallMs && !manualIntent && !alreadyStarted;
}
