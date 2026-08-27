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
}
