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
}

/// <summary>A reusable empty dictionary singleton for the default interface member above, so a
/// bridge/session that never overrides <see cref="IPartyPoolSession.PartyPoolTotals"/> does not
/// allocate one on every call.</summary>
internal static class EmptyPartyPoolTotals
{
    public static readonly Dictionary<PartyPoolStackKey, int> Instance = [];
}
