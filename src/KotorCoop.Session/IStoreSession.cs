// The merchant/store stock channel (docs/research/store-sync.md).
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IStoreSession
{
    /// <summary>
    /// Announces that <paramref name="countDelta"/> units of <paramref name="key"/> (which
    /// names the store) were added to (a sell) or removed from (a buy) that store's stock,
    /// delivered reliably to every OTHER peer so their copy of that store converges. Symmetric:
    /// a client calls this exactly like the host does — see <see cref="IPartyPoolSession
    /// .PublishPartyPoolOp"/> for the identical wire-layer echo-suppression this mirrors.
    /// </summary>
    void PublishStoreOp(StoreOpKind kind, StoreStackKey key, uint countDelta) { }

    /// <summary>Every (store, stack) key this instance currently believes exists, across every
    /// store this session has ever learned about — this peer's own changes plus every other
    /// peer's, merged. The pump seeds its own local "what does the engine currently show"
    /// baseline from this per store on that store's first scan.</summary>
    IReadOnlyDictionary<StoreStackKey, int> StoreTotals => EmptyStoreTotals.Instance;

    /// <summary>Every store op received from a peer since the last drain, oldest first — for the
    /// pump to submit through the plugin's staging-cheat channel and clear. Matches
    /// <see cref="IPartyPoolSession.DrainPartyPoolOps"/>'s shape, including this instance's OWN
    /// relayed ops on <c>CoopClient</c> (the host always echoes what it applied).</summary>
    IReadOnlyList<StoreOpPacket> DrainStoreOps() => [];
}

/// <summary>A reusable empty dictionary singleton for the default interface member above, so a
/// bridge/session that never overrides <see cref="IStoreSession.StoreTotals"/> does not allocate
/// one on every call.</summary>
internal static class EmptyStoreTotals
{
    public static readonly Dictionary<StoreStackKey, int> Instance = [];
}
