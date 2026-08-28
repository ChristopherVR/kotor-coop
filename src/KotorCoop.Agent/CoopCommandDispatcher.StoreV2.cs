using KotorCoop.Memory;
using KotorCoop.Session;

namespace KotorCoop.Agent;

// The store's v2 (host-authoritative) automatic apply half — see
// CoopCommandDispatcher.PartyPoolV2.cs's identical remarks.
public sealed partial class CoopCommandDispatcher
{
    private void DrainAutomaticStoreV2()
    {
        if (_pendingCheatSerial != 0)
        {
            return;
        }

        if (manager.Agent is not { } agent
            || !agent.TryDequeuePendingStoreApplyV2(out CoopAgent.PendingStoreApply next))
        {
            return;
        }

        uint kind = next.Kind == KotorCoop.Protocol.StoreOpKind.Add
            ? CheatKinds.StoreInsert
            : CheatKinds.StoreRemove;

        Log?.Invoke(
            $"store v2: submitting {next.Kind.ToString().ToLowerInvariant()} of {next.Count}x "
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
}
