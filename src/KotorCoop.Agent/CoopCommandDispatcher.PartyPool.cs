using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

// The shared party item-pool channel's automatic apply half — the same shape as
// CoopCommandDispatcher.Items.cs's DrainAutomaticLoot, submitted through the identical
// single-slot staging-cheat channel. See docs/research/party-pool-sync.md.
public sealed partial class CoopCommandDispatcher
{
    /// <summary>
    /// Drains one queued party-pool apply per tick from the connected <see cref="CoopAgent"/> —
    /// a peer's add/remove this instance's own engine still owes
    /// (<c>Pumps.PartyPoolPump</c>'s own reconciliation, docs/research/party-pool-sync.md) —
    /// through the SAME <see cref="SendCheat"/> call a human's own party-pool console verb
    /// would use.
    ///
    /// Deliberately gated on <see cref="_pendingCheatSerial"/> already being idle — see
    /// <see cref="DrainAutomaticLoot"/>'s identical remarks: this is the same single-slot
    /// channel a human can be mid-command on, and firing here anyway would silently abandon
    /// whichever answer was already in flight.
    /// </summary>
    private void DrainAutomaticPartyPool()
    {
        if (_pendingCheatSerial != 0)
        {
            return;
        }

        if (manager.Agent is not { } agent
            || !agent.TryDequeuePendingPartyPoolApply(out CoopAgent.PendingPartyPoolApply next))
        {
            return;
        }

        if (next.WorldLoadSerial is not { } sourceLoadSerial)
        {
            Log?.Invoke($"party pool: dropping {next.Kind.ToString().ToLowerInvariant()} for '{next.Key.Resref}' — source world serial unavailable");
            return;
        }

        uint kind = next.Kind == KotorCoop.Protocol.PartyPoolOpKind.Add
            ? CheatKinds.PartyPoolAdd
            : CheatKinds.PartyPoolRemove;

        Log?.Invoke(
            $"party pool: submitting {next.Kind.ToString().ToLowerInvariant()} of {next.Count}x "
            + $"'{next.Key.Resref}' through the party-pool channel");

        SendCheat(new CheatRequest(
            kind,
            Target: sourceLoadSerial,
            Text: next.Key.Resref,
            Amount: next.Count,
            X: PartyPoolMailboxFence.Marker,
            Charges: next.Key.Charges,
            MaxCharges: next.Key.MaxCharges,
            Upgrades: next.Key.Upgrades,
            ItemFlags: next.Key.Flags), quiet: true);
    }
}
