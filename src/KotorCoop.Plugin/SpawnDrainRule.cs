namespace KotorCoop.Plugin;

/// <summary>How a spawn-drain pass ended.</summary>
internal enum SpawnDrainOutcome
{
    /// <summary>Bodies are still standing and there are passes left; go round again.</summary>
    KeepDraining,

    /// <summary>The very first pass found nothing to destroy. Reported explicitly, never
    /// by silence — see the rule's remarks.</summary>
    NothingWasStanding,

    /// <summary>Everything the session spawned is gone.</summary>
    Cleaned,

    /// <summary>The budget ran out with bodies still standing: the engine refused to destroy
    /// them, and a save from here would serialise them into the module.</summary>
    Refused,
}

/// <summary>
/// When the session's spawn drain stops, and what it has to say about it. Separated from
/// <c>PluginHost</c> so it can be unit-tested — the drain itself is engine destroys and the
/// shared block, neither of which exists in the test runner (the same split
/// <c>SpectateCycle</c> and <c>ControlReturnRule</c> use).
///
/// ⚠ THE POINT OF PINNING THIS IN A TEST IS THAT EVERY PATH TALKS. A drain that finds
/// nothing and says nothing is indistinguishable in a log from a drain that never ran, and
/// that ambiguity is exactly what made the leaked phantom take a second rig round to find:
/// the teardown chain printed five healthy lines while a peer's body stood in the world and
/// the readout said `phantoms 1`. So "nothing was standing" is an OUTCOME here, not a
/// silence.
/// </summary>
internal static class SpawnDrainRule
{
    /// <summary>
    /// Decides a pass.
    ///
    /// <paramref name="standing"/> is how many spawned bodies survived this pass's destroy
    /// attempts. <paramref name="atRelease"/> is how many were standing when the drain began,
    /// or a negative number before the first pass has counted them.
    /// <paramref name="passesLeft"/> is the budget remaining AFTER this pass.
    ///
    /// Returns the outcome and the count to remember as <paramref name="atRelease"/> — the
    /// first pass is what establishes it, so the "removed N" figure is measured rather than
    /// accumulated across passes that may each have destroyed a different number.
    /// </summary>
    public static (SpawnDrainOutcome Outcome, int AtRelease) Decide(
        int standing, int atRelease, int passesLeft)
    {
        bool first = atRelease < 0;
        int total = first ? standing : atRelease;

        if (first && standing == 0)
        {
            return (SpawnDrainOutcome.NothingWasStanding, total);
        }

        if (standing == 0)
        {
            return (SpawnDrainOutcome.Cleaned, total);
        }

        return passesLeft <= 0
            ? (SpawnDrainOutcome.Refused, total)
            : (SpawnDrainOutcome.KeepDraining, total);
    }
}
