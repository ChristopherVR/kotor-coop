using KotorCoop.Memory;
using KotorCoop.Session;

namespace KotorCoop.Agent;

// The merchant/store stock channel's automatic apply half — the same shape as
// CoopCommandDispatcher.PartyPool.cs's DrainAutomaticPartyPool, submitted through the identical
// single-slot staging-cheat channel. See docs/research/store-sync.md.
public sealed partial class CoopCommandDispatcher
{
    /// <summary>
    /// Drains one queued store apply per tick from the connected <see cref="CoopAgent"/> — a
    /// peer's buy/sell this instance's own engine still owes for a specific store
    /// (<c>Pumps.StorePump</c>'s own reconciliation, docs/research/store-sync.md) — through the
    /// SAME <see cref="SendCheat"/> call a human's own store console verb would use.
    ///
    /// Deliberately gated on <see cref="_pendingCheatSerial"/> already being idle — see
    /// <see cref="DrainAutomaticPartyPool"/>'s identical remarks.
    /// </summary>
    private void DrainAutomaticStore()
    {
        if (_pendingCheatSerial != 0)
        {
            return;
        }

        if (manager.Agent is not { } agent
            || !agent.TryDequeuePendingStoreApply(out CoopAgent.PendingStoreApply next))
        {
            return;
        }

        uint kind = next.Kind == KotorCoop.Protocol.StoreOpKind.Add
            ? CheatKinds.StoreInsert
            : CheatKinds.StoreRemove;

        Log?.Invoke(
            $"store: submitting {next.Kind.ToString().ToLowerInvariant()} of {next.Count}x "
            + $"'{next.Key.Resref}' in store 0x{next.Key.StoreObjectId:X} through the store channel");

        SendCheat(new CheatRequest(
            kind,
            Target: next.Key.StoreObjectId,
            Text: next.Key.Resref,
            Amount: next.Count,
            Charges: next.Key.Charges,
            MaxCharges: next.Key.MaxCharges,
            Upgrades: next.Key.Upgrades,
            ItemFlags: next.Key.Flags));
    }

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/store-sync.md's 2026-08-27 addendum): opens the store
    /// GUI against <paramref name="token"/> (an object id or a tag — required, no "nearest
    /// store" fallback), through <c>CheatKinds.OpenStore</c>. Same id-or-tag dispatch shape as
    /// <see cref="UsePlaceable"/>. See <c>CoopCommandKind.OpenStore</c>.
    /// </summary>
    private void OpenStore(string token)
    {
        if (token.Length == 0)
        {
            Reply("openstore: no store tag or id given");
            return;
        }

        if (ConsoleCommand.TryParseObjectId(token, out uint objectId) && objectId != 0)
        {
            SendCheat(new CheatRequest(CheatKinds.OpenStore, Target: objectId));
            return;
        }

        SendCheat(new CheatRequest(CheatKinds.OpenStore, Text: token));
    }

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/store-sync.md's 2026-08-28 addendum): buys
    /// <paramref name="count"/> units of <paramref name="resref"/> from the store named by
    /// <paramref name="storeToken"/> (an object id or a tag), through
    /// <see cref="CheatKinds.StoreBuy"/>. See <see cref="Sell"/> for the sell side and
    /// <c>SharedBlock.CheatKindStoreBuy</c>'s own doc comment for the field packing this
    /// mirrors: <paramref name="storeToken"/> parsing as a numeric id sends it as
    /// <c>Target</c> (with <paramref name="resref"/> as the bare <c>Text</c>); a tag sends
    /// <c>Target=0</c> and packs <c>"token|resref"</c> into <c>Text</c> instead — the ONE
    /// string field the wire cheat request has, and this verb needs two independent strings at
    /// once (unlike <c>openstore</c>, which only ever needs one).
    /// </summary>
    private void Buy(string storeToken, string resref, int count) => Trade(CheatKinds.StoreBuy, storeToken, resref, count, "buy");

    /// <summary>Sell side of <see cref="Buy"/> — see that method's own doc comment.</summary>
    private void Sell(string storeToken, string resref, int count) => Trade(CheatKinds.StoreSell, storeToken, resref, count, "sell");

    private void Trade(uint kind, string storeToken, string resref, int count, string verb)
    {
        if (storeToken.Length == 0)
        {
            Reply($"{verb}: no store tag or id given");
            return;
        }

        if (resref.Length == 0)
        {
            Reply($"{verb}: no resref given");
            return;
        }

        if (ConsoleCommand.TryParseObjectId(storeToken, out uint objectId) && objectId != 0)
        {
            SendCheat(new CheatRequest(kind, Target: objectId, Text: resref, Amount: count));
            return;
        }

        SendCheat(new CheatRequest(kind, Text: $"{storeToken}|{resref}", Amount: count));
    }
}
