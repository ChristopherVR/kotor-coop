namespace KotorCoop.Memory;

/// <summary>
/// When is a walk of a <c>CItemRepository</c> an OBSERVATION of what that repository holds, and
/// when is it merely what this process managed to read?
///
/// Both shared-inventory scans — <c>KotorGameBridge.TryScanPartyPoolTotals</c> and
/// <c>TryScanOpenStore</c> — walk a repository's id array and aggregate stack sizes per
/// <c>PartyPoolStackKey</c>/<c>StoreStackKey</c>. Every id they cannot turn into a resref and an
/// identity was skipped with a bare <c>continue</c>, and the partial aggregate was then returned
/// as <c>true</c>: a complete, authoritative statement of the repository's contents.
///
/// ⚠ IT IS NOT, AND THE DIFFERENCE IS A FABRICATED REMOVAL ON EVERY OTHER INSTANCE.
/// <c>IPartyPoolBridge</c> already says this in words for the scan's own hard-failure paths —
/// *"the caller must not treat that as 'the pool is empty'"* — but a skipped ITEM was never
/// covered, and it is the far likelier failure. <c>TryGetItemResref</c> resolves an unknown id
/// by rebuilding the item-tag cache, and <c>EnsureItemKnown</c> REFUSES to rebuild for
/// <c>ItemTagRebuildMs</c> (15 seconds) after the last rebuild. So for up to fifteen seconds
/// after any rebuild, every id not already in that cache resolves to nothing — and if that is
/// every id in the pool, the aggregate is EMPTY and gets published as "the shared stash is
/// empty".
///
/// Watched happen, live, 2026-08-29 (docs/research/shared-inventory-v2.md §14): the host's
/// snapshot went to zero for ALL FIVE keys at once on a single tick, the client correctly
/// reconciled its engine down to match — emptying a pool nothing had touched — and then put all
/// five back a tick later when the snapshot returned. The client's own bookkeeping was faultless
/// throughout (<c>published=0</c> on every trace line: it fabricated nothing). The lie was in the
/// scan.
///
/// The rule is therefore the strict one, and deliberately so. A PARTIAL miss is the same defect
/// with a smaller blast radius — three of five keys resolving under-reports the other two, and
/// under-reporting a total in a host-authoritative absolute-state design IS a removal order. An
/// aggregate that skipped anything is not evidence about the whole.
/// </summary>
public static class ItemAggregateScanRule
{
    /// <summary>
    /// True when an aggregate built from <paramref name="walked"/> repository entries, of which
    /// <paramref name="unresolved"/> could not be turned into a keyed stack, may be published as
    /// what the repository holds.
    ///
    /// An EMPTY repository is a perfectly good observation (<c>walked == 0</c>): a party stash
    /// nobody has used really does hold nothing, and refusing to say so would leave a genuine
    /// emptying unreplicable forever. What is not an observation is a NON-empty repository we
    /// only partly read.
    /// </summary>
    public static bool IsObservation(int walked, int unresolved) =>
        walked >= 0 && unresolved <= 0;

    /// <summary>
    /// A one-line account of a refusal, for the caller's own diagnostic note — the project's
    /// "a guard that fails safe still has to say what it saw" rule. Five live rounds inferred
    /// this class of failure from log timestamps; it should never have to be inferred again.
    /// </summary>
    public static string Explain(string what, int walked, int unresolved) =>
        $"{what}: {unresolved} of {walked} repository entries could not be resolved to a "
        + "resref+identity this pass — NOT publishing a partial aggregate as the repository's "
        + "contents (the item-tag cache refuses to rebuild for 15s after its last rebuild, so a "
        + "cold id reads as nothing; see ItemAggregateScanRule)";
}
