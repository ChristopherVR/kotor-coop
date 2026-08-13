namespace KotorCoop.Plugin;

/// <summary>
/// The pick-next arithmetic for the spectate camera, separated from the controller so it
/// can be unit-tested: the controller's other half is engine calls and the shared block,
/// neither of which exists in the test runner.
///
/// The candidate list is rebuilt by the caller every pass from live state, so an entry
/// listed here is valid BY CONSTRUCTION this pass; this class only decides which one the
/// camera should hold, given what it held last pass and whether the cycle key fired.
/// </summary>
internal static class SpectateCycle
{
    /// <summary>
    /// Picks the object id to watch.
    ///
    /// <paramref name="candidates"/> is the ordered list of currently-valid targets (no
    /// zeros); <paramref name="current"/> is the id watched last pass (0 = none yet);
    /// <paramref name="advance"/> is the cycle key's edge. Rules, in order:
    /// a missing current (vanished, died, or first pass) snaps to the FIRST candidate —
    /// stability over cleverness, the viewer can cycle onward from there; no advance
    /// keeps the current hold; advance moves to the next entry, wrapping. Empty list
    /// returns 0 (nobody left to watch).
    /// </summary>
    public static uint Pick(ReadOnlySpan<uint> candidates, uint current, bool advance)
    {
        if (candidates.Length == 0)
        {
            return 0;
        }

        int index = -1;
        for (int i = 0; i < candidates.Length; i++)
        {
            if (candidates[i] == current)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return candidates[0];
        }

        return advance ? candidates[(index + 1) % candidates.Length] : current;
    }
}
