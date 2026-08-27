namespace KotorCoop.Protocol;

/// <summary>
/// The pure "what changed" step of store sync: given what this instance last believed a store's
/// stock held (<paramref name="expected"/> — a <see cref="StoreStackKey"/>'s aggregate quantity)
/// and what a fresh scan just found (<paramref name="actual"/>), decides which stacks grew
/// (publish an Add) and which shrank or vanished (publish a Remove). Dependency-free and
/// side-effect-free, the same shape as <see cref="PartyPoolDiffRule"/> — see that type's own
/// remarks for why this lives in <c>KotorCoop.Protocol</c> rather than <c>KotorCoop.Session</c>.
///
/// Because <see cref="StoreStackKey"/> already carries the store's object id, this rule needs no
/// separate per-store partitioning: two different stores' entries never collide as keys, so a
/// caller can hand it either a single store's totals or several stores' totals merged together
/// and get the same correct answer either way.
/// </summary>
public static class StoreDiffRule
{
    public readonly record struct Delta(StoreStackKey Key, StoreOpKind Kind, uint Count);

    /// <summary>
    /// One entry per key that differs, in the order encountered in <paramref name="actual"/>
    /// (added/changed keys) then <paramref name="expected"/> (keys that vanished outright). A key
    /// present in both with equal quantity produces nothing.
    /// </summary>
    public static List<Delta> Diff(
        IReadOnlyDictionary<StoreStackKey, int> expected,
        IReadOnlyDictionary<StoreStackKey, int> actual)
    {
        List<Delta> deltas = [];

        foreach ((StoreStackKey key, int actualCount) in actual)
        {
            int expectedCount = expected.GetValueOrDefault(key);

            if (actualCount > expectedCount)
            {
                deltas.Add(new Delta(key, StoreOpKind.Add, (uint)(actualCount - expectedCount)));
            }
            else if (actualCount < expectedCount)
            {
                deltas.Add(new Delta(key, StoreOpKind.Remove, (uint)(expectedCount - actualCount)));
            }
        }

        foreach ((StoreStackKey key, int expectedCount) in expected)
        {
            if (expectedCount > 0 && !actual.ContainsKey(key))
            {
                deltas.Add(new Delta(key, StoreOpKind.Remove, (uint)expectedCount));
            }
        }

        return deltas;
    }
}
