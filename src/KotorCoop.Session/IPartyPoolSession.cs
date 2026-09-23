// The shared party item-pool channel (docs/research/party-pool-sync.md).
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IPartyPoolSession
{
    /// <summary>
    /// Announces that <paramref name="countDelta"/> units of <paramref name="key"/> were added
    /// to (or removed from) the shared party pool, delivered reliably to every OTHER peer so
    /// their copy of the stash converges. Symmetric: a client calls this exactly like the host
    /// does. <c>CoopHost</c> broadcasts directly; <c>CoopClient</c> sends to the host, which
    /// relays to every other client (including back down to the reporting client itself,
    /// harmlessly — the store's op-sequence dedup on the relay's own fresh sequence would not
    /// normally save it, but the caller never re-announces a change it already recorded — see
    /// <c>Pumps.PartyPoolPump</c>'s echo-suppression).
    /// </summary>
    void PublishPartyPoolOp(PartyPoolOpKind kind, PartyPoolStackKey key, uint countDelta) { }

    /// <summary>Every stack key this instance currently believes the pool holds — this peer's
    /// own changes plus every other peer's, merged. Symmetric: non-empty on both host and
    /// client. The pump seeds its own local "what does the engine currently show" baseline from
    /// this on connect/resync, so content learned before this instance's own first scan is not
    /// mistaken for a local removal.</summary>
    IReadOnlyDictionary<PartyPoolStackKey, int> PartyPoolTotals => EmptyPartyPoolTotals.Instance;

    /// <summary>Every party-pool op received from a peer since the last drain, oldest first —
    /// for the pump to submit through the plugin's staging-cheat channel and clear. Matches
    /// <see cref="ILootSession.DrainMaterialisedLoot"/>'s shape. Includes this instance's OWN
    /// relayed ops on <c>CoopClient</c> (the host always echoes what it applied) — the pump
    /// recognises those by the totals already matching and skips re-applying them; see
    /// <c>Pumps.PartyPoolPump</c>.</summary>
    IReadOnlyList<PartyPoolOpPacket> DrainPartyPoolOps() => [];

    /// <summary>
    /// <c>Pumps.PartyPoolPump</c>'s own hard loop breaker (docs/research/party-pool-sync.md
    /// §22) tripped for some key: ask for a full resync so every peer's store converges back to
    /// the host's own absolute totals, discarding whatever this channel's own detect/reconcile
    /// loop diverged into. <c>CoopHost</c> is already authoritative, so it just re-broadcasts
    /// its own current totals (the same call <c>HandleHello</c>/a version-digest mismatch
    /// already use); <c>CoopClient</c> sends a <see cref="PartyPoolSyncRequestPacket"/> to the
    /// host, exactly as if a version/digest mismatch had been noticed. Default no-op for a
    /// session with no party-pool channel at all.
    /// </summary>
    void RequestPartyPoolResync() { }

    // ---- v2: host-authoritative absolute snapshot + client intents (party_pool_sync=2) ------
    // See docs/research/shared-inventory-v2.md. Mode 1's members above are untouched; these are
    // purely additive (docs/CONTRIBUTING.md's "never remove or rename an existing member").

    /// <summary>Host only: publishes this publish cycle's ABSOLUTE totals — see
    /// docs/research/shared-inventory-v2.md §2.4. A client's own call is a no-op (a client never
    /// asserts state).</summary>
    void PublishPartyPoolSnapshotV2(IReadOnlyDictionary<PartyPoolStackKey, int> totals) { }

    /// <summary>Host only: stop broadcasting the last published totals until the next
    /// <see cref="PublishPartyPoolSnapshotV2"/>. The host's own world was just replaced (a
    /// reload — <c>IWorldStateBridge.WorldEpoch</c>) and the pool it last scanned no longer
    /// exists; a client rejoining into the NEW world must not first be handed the OLD world's
    /// totals as the truth to reconcile toward. Live 2026-09-02 (shared-inventory-v2.md §17): a
    /// rejoined client saw <c>snap=0</c> for five keys both worlds visibly held and deposited
    /// all five to the host again. Default no-op (a client never broadcasts).</summary>
    void WithdrawPartyPoolSnapshotV2() { }

    /// <summary>The latest FULLY-gathered publish cycle's absolute totals; false until the first
    /// complete cycle has arrived — mirrors <see cref="IPartySession.TryGetPartyRoster"/>'s own
    /// "unheard-of vs. empty" distinction (an empty pool and a not-yet-heard-from host must never
    /// be confused). Always false on the host — it IS the truth, nothing to mirror.</summary>
    bool TryGetPartyPoolSnapshotV2(out IReadOnlyDictionary<PartyPoolStackKey, int> totals)
    {
        totals = EmptyPartyPoolTotals.Instance;
        return false;
    }

    /// <summary>Client only: publishes one bounded REQUEST ("I deposited/withdrew this many"),
    /// returning the serial minted for it — reliable, retransmitted until acked or timed out
    /// (docs/research/shared-inventory-v2.md §2.5) — or 0 if refused outright (too many already
    /// in flight). A host's own call is a no-op: the host never sends intents, only applies
    /// accepted ones (see <see cref="DrainPartyPoolIntentsV2"/>).</summary>
    uint PublishPartyPoolIntentV2(PartyPoolOpKind kind, PartyPoolStackKey key, ushort count) => 0;

    /// <summary>Host only: every intent this instance's wire layer has accepted (deduped by
    /// (peer, serial)) since the last drain, oldest first — <c>Pumps.PartyPoolPumpV2</c> applies
    /// each one to the local engine through the identical staging-cheat primitive v1 already
    /// uses. The host calls <see cref="CompletePartyPoolIntentV2"/> from the engine result
    /// callback; this is intentionally separate so queue admission is never reported as a
    /// successful spawn.</summary>
    IReadOnlyList<PartyPoolIntentPacketV2> DrainPartyPoolIntentsV2() => [];

    /// <summary>Reports the result of the host engine apply for one accepted intent. The wire
    /// ACK is deliberately delayed until this result arrives, so an intent is never told it
    /// succeeded merely because it reached the host's queue. A client implementation turns a
    /// negative result into a bounded retry/failure event for its pump.</summary>
    void CompletePartyPoolIntentV2(uint peerId, uint intentSerial, bool accepted) { }

    /// <summary>Client only: complete results for intents previously sent by this instance.
    /// Unlike the legacy serial-only drain this preserves the original operation and verdict,
    /// allowing the pump to retry a refused engine apply without deleting the local item.</summary>
    IReadOnlyList<PartyPoolIntentResultV2> DrainPartyPoolIntentResultsV2() => [];

    /// <summary>Client only: every intent serial this client has seen acked OR refused since the
    /// last drain, oldest first — both clear the same way
    /// (docs/research/shared-inventory-v2.md §2.5), so this carries only the serial, not the
    /// verdict. <c>Pumps.PartyPoolPumpV2</c> removes each from its own "still outstanding"
    /// reconcile-target adjustment.</summary>
    IReadOnlyList<uint> DrainAckedPartyPoolIntentsV2() => [];

    /// <summary>Client only: discard party-pool intents from the world that was just left.
    /// A load-edge quarantine calls this before a new host snapshot can be reconciled. Hosts
    /// have no outbound intents, so the default is a no-op.</summary>
    void DiscardPendingPartyPoolIntentsV2() { }
}

public readonly record struct PartyPoolIntentResultV2(
    PartyPoolIntentPacketV2 Intent, bool Accepted);

/// <summary>A reusable empty dictionary singleton for the default interface member above, so a
/// bridge/session that never overrides <see cref="IPartyPoolSession.PartyPoolTotals"/> does not
/// allocate one on every call.</summary>
internal static class EmptyPartyPoolTotals
{
    public static readonly Dictionary<PartyPoolStackKey, int> Instance = [];
}
