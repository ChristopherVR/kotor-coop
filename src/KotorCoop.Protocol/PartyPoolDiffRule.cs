namespace KotorCoop.Protocol;

/// <summary>
/// The pure "what changed" step of party-pool sync: given what this instance last believed the
/// engine's party pool held (<paramref name="expected"/> — a stack key's aggregate quantity) and
/// what a fresh scan just found (<paramref name="actual"/>), decides which stacks grew (publish
/// an Add) and which shrank or vanished (publish a Remove). Dependency-free and side-effect-free
/// on purpose: this is the one decision in the whole feature worth testing without a store, a
/// bridge or a live game, and <c>KotorCoop.Plugin</c> pulls dependency-free Protocol files in by
/// source (see docs/ARCHITECTURE.md's "plugin compile-include trick"), so keeping it here costs
/// nothing and buys direct testability.
///
/// The anti-echo property lives entirely in WHAT <c>expected</c> is fed as, not in this method:
/// a caller that has already advanced its own expectation the moment it queued an apply for a
/// peer's op (before the next scan can run) never sees that op's own effect reported back here
/// as a fresh local change — see <c>KotorCoop.Session.Pumps.PartyPoolPump</c>.
/// </summary>
public static class PartyPoolDiffRule
{
    public readonly record struct Delta(PartyPoolStackKey Key, PartyPoolOpKind Kind, uint Count);

    /// <summary>
    /// One entry per key that differs, in the order encountered in <paramref name="actual"/>
    /// (added/changed keys) then <paramref name="expected"/> (keys that vanished outright).
    /// A key present in both with equal quantity produces nothing — the common, steady-state
    /// case, so an idle party pool costs zero deltas per call.
    /// </summary>
    public static List<Delta> Diff(
        IReadOnlyDictionary<PartyPoolStackKey, int> expected,
        IReadOnlyDictionary<PartyPoolStackKey, int> actual)
    {
        List<Delta> deltas = [];

        foreach ((PartyPoolStackKey key, int actualCount) in actual)
        {
            int expectedCount = expected.GetValueOrDefault(key);

            if (actualCount > expectedCount)
            {
                deltas.Add(new Delta(key, PartyPoolOpKind.Add, (uint)(actualCount - expectedCount)));
            }
            else if (actualCount < expectedCount)
            {
                deltas.Add(new Delta(key, PartyPoolOpKind.Remove, (uint)(expectedCount - actualCount)));
            }
        }

        foreach ((PartyPoolStackKey key, int expectedCount) in expected)
        {
            if (expectedCount > 0 && !actual.ContainsKey(key))
            {
                deltas.Add(new Delta(key, PartyPoolOpKind.Remove, (uint)expectedCount));
            }
        }

        return deltas;
    }
}
