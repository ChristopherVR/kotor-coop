namespace KotorCoop.Protocol;

/// <summary>
/// Pure diff between two snapshots of a possessed-items sweep, for detecting what a reward
/// node's script placed into the local speaker's inventory during a conversation this instance
/// owns — docs/research/dialogue-consequences.md §3.2.
///
/// Deliberately a RESREF COUNT diff, not an identity diff: <c>IGameBridge.ReadPossessedItems</c>
/// returns one row per item OBJECT, with no object id to track across two sweeps, so the only
/// thing that can be compared is how many rows of each resref exist before versus after. A
/// stack growing (five spikes becoming eight) is the SAME row with a different
/// <see cref="ItemIdentity.StackSize"/>, so it never changes the row COUNT for that resref and
/// is correctly ignored; a genuinely new item — a fresh row appearing — raises the count and is
/// reported once, carrying whatever identity that new row actually has.
/// </summary>
public static class DialogueItemDiff
{
    /// <summary>
    /// Returns one <see cref="MaterialisedLootItem"/> per row in <paramref name="after"/> that
    /// has no corresponding row of the same resref in <paramref name="before"/> — the net-new
    /// items gained between the two snapshots, in the order they appear in <paramref
    /// name="after"/>.
    /// </summary>
    public static List<MaterialisedLootItem> Compute(
        IReadOnlyList<(string Resref, ItemIdentity Identity)> before,
        IReadOnlyList<(string Resref, ItemIdentity Identity)> after)
    {
        Dictionary<string, int> beforeCounts = [];
        foreach ((string resref, _) in before)
        {
            if (resref.Length == 0)
            {
                continue;
            }

            beforeCounts[resref] = beforeCounts.GetValueOrDefault(resref) + 1;
        }

        List<MaterialisedLootItem> gained = [];
        Dictionary<string, int> matched = [];

        foreach ((string resref, ItemIdentity identity) in after)
        {
            if (resref.Length == 0)
            {
                continue;
            }

            int budget = beforeCounts.GetValueOrDefault(resref);
            int used = matched.GetValueOrDefault(resref);

            if (used < budget)
            {
                // This row matches one that was already there before the conversation.
                matched[resref] = used + 1;
                continue;
            }

            matched[resref] = used + 1;
            gained.Add(new MaterialisedLootItem(resref, identity));
        }

        return gained;
    }
}
