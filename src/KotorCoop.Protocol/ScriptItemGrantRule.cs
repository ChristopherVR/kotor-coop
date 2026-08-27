namespace KotorCoop.Protocol;

/// <summary>
/// The pure decision <c>KotorCoop.Session.Pumps.ScriptItemGrantPump</c> drives — see
/// docs/research/dialogue-consequences.md's script-item-sync section. Answers "given what the
/// local PC's inventory looked like last sample and what it looks like now, which of the
/// net-new rows are a genuine script grant (an OnEnter/OnDeath/heartbeat script placing an
/// item straight into the PC's inventory OUTSIDE any conversation) rather than something an
/// already-synced channel accounts for".
///
/// Deliberately separate from <see cref="DialogueItemDiff"/> rather than a copy of it: this
/// rule ADDS the <see cref="RecentlySyncedItems"/> suppression pass <see cref="DialogueItemDiff"/>
/// has no reason to know about (a dialogue reward is never also a ground pickup on the same
/// tick), while still reusing <see cref="DialogueItemDiff.Compute"/> for the actual row-count
/// diff — same resref-row semantics, same stack-growth-is-not-a-grant behaviour.
/// </summary>
public static class ScriptItemGrantRule
{
    /// <param name="ToPublish">The net-new rows to forward as a script grant — never null, may
    /// be empty.</param>
    /// <param name="NewBaseline">What <paramref name="current"/> becomes for the next
    /// call — <see cref="Evaluate"/> always adopts it, whether or not anything diffed.</param>
    public readonly record struct Result(
        IReadOnlyList<MaterialisedLootItem> ToPublish,
        IReadOnlyList<(string Resref, ItemIdentity Identity)> NewBaseline);

    /// <summary>
    /// Evaluates one sample.
    ///
    /// <paramref name="rebaselineOnly"/> covers every case that must NOT diff against
    /// <paramref name="baseline"/>: the very first sample since attach/module-load (there is
    /// nothing yet to diff against) and the first sample after the dialogue pump releases the
    /// window back (see the pump's own remarks on why a fresh read, not a hand-off value, is
    /// what "coordinate baselines" means here). Both cases silently adopt
    /// <paramref name="current"/> and report nothing — exactly
    /// <see cref="KotorCoop.Session.Pumps.DialoguePump"/>'s own "just started owning" branch.
    /// </summary>
    public static Result Evaluate(
        IReadOnlyList<(string Resref, ItemIdentity Identity)> baseline,
        IReadOnlyList<(string Resref, ItemIdentity Identity)> current,
        bool rebaselineOnly,
        RecentlySyncedItems suppression,
        long nowMs)
    {
        if (rebaselineOnly)
        {
            return new Result([], current);
        }

        List<MaterialisedLootItem> gained = DialogueItemDiff.Compute(baseline, current);
        if (gained.Count == 0)
        {
            return new Result([], current);
        }

        List<MaterialisedLootItem> toPublish = [];
        foreach (MaterialisedLootItem item in gained)
        {
            // Consume-on-match: a row already accounted for by a ground pickup, a container/
            // corpse loot, a peer's grant applied via giveloot, or a human's own giveloot is
            // silently dropped here rather than re-broadcast as if a script had just created
            // it — see RecentlySyncedItems' own remarks for why the match is resref-keyed.
            if (suppression.TryConsume(item.Resref, nowMs))
            {
                continue;
            }

            toPublish.Add(item);
        }

        return new Result(toPublish, current);
    }
}
