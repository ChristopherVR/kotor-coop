namespace KotorCoop.Session;

/// <summary>
/// Whether a client should ask the host for a full copy of one world store this tick.
///
/// ⚠ THE PENDING-OPS GATE IS NOT A BLANKET RULE, AND TREATING IT AS ONE STARVES A CLIENT.
/// Every one of the four world channels used to refuse to request a resync while this client
/// still had an unacked op of its own on that channel. The reason was real but narrow: a
/// snapshot that REPLACES the store wholesale would erase a local change the host has not
/// applied yet (a ground-item drop respawning the instant it is picked up). Applied to the
/// channels whose merge is last-writer-wins by (Serial, OriginPeerId) it buys nothing, and it
/// costs the one thing this client cannot do without.
///
/// What it costs: an op is only retired by an ACK, so the gate makes "can I learn what the
/// host did" depend on a ROUND TRIP of an unrelated change of my own. On a lossy link that
/// round trip has an unbounded tail, and a client that keeps making changes — which is what
/// playing the game is — can hold something pending indefinitely. The result is a client that
/// silently never adopts a single host-side flag, journal entry or looted container for as
/// long as the bad link lasts, while its own store looks perfectly healthy. Measured as the
/// dominant term in <c>HeavyPacketLossStillConverges</c>'s convergence time, and the reason
/// that test had a multi-percent failure rate at a 40-second budget.
///
/// So the gate now follows the MERGE, not the channel:
///
/// <list type="bullet">
/// <item><b>Flags</b> (<see cref="EventFlagStore.MergeSnapshot"/>) — per-entry last-writer-wins
/// by (Serial, OriginPeerId). A local change carries a serial minted from this store's own
/// Lamport clock, so it strictly outranks anything the host can be holding for that key; the
/// snapshot cannot stomp it. No gate.</item>
/// <item><b>Journal</b> (<see cref="JournalStore.MergeSnapshot"/>) — same rule, plus tombstones
/// so a removal survives a stale re-add. No gate.</item>
/// <item><b>Container loot</b> — the host's "resync" is a re-offer of every removal through the
/// ordinary reliable per-op sender, and a removal is an idempotent (container, item) fact, not
/// a replacement. Nothing to overwrite. No gate.</item>
/// <item><b>Ground items</b> (<see cref="GroundItemStore.ReplaceAll"/>) — genuinely destructive:
/// the host decides which items are on the ground, and a snapshot arriving while this client's
/// own drop is in flight would erase it, then the retransmit would land and put it back.
/// GATED, and this is the one channel where it is load-bearing.</item>
/// </list>
/// </summary>
public static class WorldResyncRule
{
    /// <summary>
    /// <paramref name="forced"/> is the once-per-admission ask (see <c>CoopClient</c>'s
    /// <c>_forceFlagResync</c> and friends): a reconnection asks for everything regardless of
    /// what the tripwire says, because an outage is exactly the window in which two stores can
    /// diverge to equal version numbers. <paramref name="tripwireDiffers"/> is the ordinary
    /// version-or-digest disagreement. <paramref name="snapshotReplacesLocal"/> is the per-channel
    /// property documented on this class — true only for ground items.
    /// </summary>
    public static bool ShouldRequest(
        bool forced,
        bool tripwireDiffers,
        bool localOpsPending,
        bool snapshotReplacesLocal,
        long nowMs,
        long lastRequestMs,
        int intervalMs)
    {
        if (!forced && !tripwireDiffers)
        {
            return false;
        }

        // The one case the gate is for: a wholesale replacement would undo an op of ours that
        // the host has not acked. Everything else merges, so waiting only loses us the host's
        // news. See the class remarks.
        if (snapshotReplacesLocal && localOpsPending)
        {
            return false;
        }

        return nowMs - lastRequestMs >= intervalMs;
    }
}
