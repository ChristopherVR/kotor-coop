namespace KotorCoop.Protocol;

/// <summary>One tick's verdict from <see cref="CreditGrantRule.Observe"/>: what the pump's own
/// baseline should become, and how much (if any) to publish as a fresh reward this tick.</summary>
public readonly record struct CreditGrantObservation(long? NewBaseline, int AmountToPublish);

/// <summary>
/// Pure decision rule for the credits-reward relay (docs/research/personal-inventory.md §4):
/// while <c>credits_mode = personal</c>, diff the local party's gold ONLY across a conversation
/// this instance owns (<c>DialoguePump.IsOwningConversation</c>) and report a RISE as a reward
/// to relay to the peer — a fall or an unchanged reading is never reported (spending mid-
/// conversation is not a thing K1's engine allows, but treating a fall as "owed nothing" rather
/// than asserting is the safe default either way).
///
/// Deliberately narrower than the item channels' own two-window design
/// (<c>DialoguePump</c>+<c>ScriptItemGrantPump</c>): K1's dialogue windows lock ordinary player
/// input (no looting, no selling mid-conversation), which is what keeps a gold RISE during the
/// window a reliable signal of a genuine reward rather than ordinary gameplay. Sampling gold
/// OUTSIDE a conversation as well — the way script-item-sync does for items — was deliberately
/// left out: without a comparable input lock, an ordinary loot pickup or a merchant sale would
/// misread as a "reward" and leak real money into a personal-mode purse that is supposed to
/// stay separate, a materially worse failure than an item channel's own equivalent false
/// positive (a player briefly APPEARING to hold a duplicate item, not real currency crossing a
/// boundary the player was told would not cross).
/// </summary>
public static class CreditGrantRule
{
    /// <param name="baseline">This pump's own last-recorded gold reading for the CURRENT
    /// window, or null if no window is currently open / no reading has been taken yet.</param>
    /// <param name="current">The local party's gold reading this tick.</param>
    /// <param name="owningWindow">Whether this instance owns a conversation right now
    /// (<c>DialoguePump.IsOwningConversation</c>).</param>
    /// <param name="creditsPersonal">The resolved <c>credits_mode</c> — <see
    /// cref="InventorySyncRule.ShouldMergeCredits"/>'s own negation; this rule only ever
    /// does anything when credits are personal (in shared mode the ordinary progression ledger
    /// already covers a reward, and running this channel too would double-credit it).</param>
    public static CreditGrantObservation Observe(
        long? baseline, long current, bool owningWindow, bool creditsPersonal)
    {
        if (!creditsPersonal || !owningWindow)
        {
            // Not this tick's job to sample at all. Clearing the baseline (rather than leaving
            // a stale one) means the NEXT window entry re-baselines silently instead of diffing
            // across whatever happened while the window was closed.
            return new CreditGrantObservation(null, 0);
        }

        if (baseline is not { } b)
        {
            // First sample since the window opened (or since a rebaseline elsewhere, e.g. a
            // module change) - baseline only, nothing owed yet.
            return new CreditGrantObservation(current, 0);
        }

        long delta = current - b;
        int toPublish = delta > 0 ? (int)Math.Min(delta, int.MaxValue) : 0;
        return new CreditGrantObservation(current, toPublish);
    }
}
