using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

// The party pool's v2 (host-authoritative) automatic apply half — the same single-slot
// arbitrated drain shape as CoopCommandDispatcher.PartyPool.cs's DrainAutomaticPartyPool,
// reusing the identical PendingPartyPoolApply record type. See docs/research/shared-inventory-v2.md.
public sealed partial class CoopCommandDispatcher
{
    /// <summary>
    /// Drains one queued v2 party-pool apply per tick — on the HOST, a client's accepted intent
    /// this instance's own engine still owes; on a CLIENT, this instance's own reconcile toward
    /// the host's latest snapshot (<c>Pumps.PartyPoolPumpV2</c>) — through the SAME
    /// <see cref="SendCheat"/> call mode 1's <see cref="DrainAutomaticPartyPool"/> and a human's
    /// own party-pool console verb both already use. Harmless to call unconditionally: the v2
    /// queue is only ever non-empty when <c>party_pool_sync=2</c> is active (mode gating happens
    /// upstream, in the pump itself, exactly like mode 1's own queue).
    ///
    /// Deliberately gated on <see cref="_pendingCheatSerial"/> already being idle — see
    /// <see cref="DrainAutomaticPartyPool"/>'s identical remarks.
    /// </summary>
    private void DrainAutomaticPartyPoolV2()
    {
        if (_pendingCheatSerial != 0)
        {
            return;
        }

        if (manager.Agent is not { } agent
            || !agent.TryDequeuePendingPartyPoolApplyV2(out CoopAgent.PendingPartyPoolApply next))
        {
            return;
        }

        if (next.WorldLoadSerial is not { } sourceLoadSerial)
        {
            Log?.Invoke($"party pool v2: refusing {next.Kind.ToString().ToLowerInvariant()} for '{next.Key.Resref}' — source world serial unavailable");
            if (next.IntentSerial != 0)
            {
                agent.CompletePartyPoolIntentV2(next.IntentPeerId, next.IntentSerial, accepted: false);
            }
            return;
        }

        uint kind = next.Kind == KotorCoop.Protocol.PartyPoolOpKind.Add
            ? CheatKinds.PartyPoolAdd
            : CheatKinds.PartyPoolRemove;

        Log?.Invoke(
            $"party pool v2: submitting {next.Kind.ToString().ToLowerInvariant()} of {next.Count}x "
            + $"'{next.Key.Resref}' through the party-pool channel");

        RecentlySyncedItems.Reservation partyPoolReservation = default;
        if (next.Kind == KotorCoop.Protocol.PartyPoolOpKind.Add)
        {
            // Reserve the echo guard before the engine frame runs, so a fast inventory sweep
            // cannot observe a successful add before the callback below. Refusals remove this
            // exact unit immediately and therefore never leave a phantom 20-second budget.
            partyPoolReservation = agent.NoteLocalPartyPoolAdd(next.Key.Resref);
        }

        Action? complete = () =>
        {
            if (next.IntentSerial != 0)
            {
                agent.CompletePartyPoolIntentV2(next.IntentPeerId, next.IntentSerial, accepted: true);
            }
        };
        Action? refuse = () =>
        {
            if (next.Kind == KotorCoop.Protocol.PartyPoolOpKind.Add)
            {
                agent.UnnoteLocalPartyPoolAdd(partyPoolReservation);
            }

            if (next.IntentSerial != 0)
            {
                Log?.Invoke($"party pool v2: engine refused intent {next.IntentSerial} for '{next.Key.Resref}'");
                agent.CompletePartyPoolIntentV2(next.IntentPeerId, next.IntentSerial, accepted: false);
            }
        };

        SendCheat(new CheatRequest(
            kind,
            Target: sourceLoadSerial,
            Text: next.Key.Resref,
            Amount: next.Count,
            X: PartyPoolMailboxFence.Marker,
            Charges: next.Key.Charges,
            MaxCharges: next.Key.MaxCharges,
            Upgrades: next.Key.Upgrades,
            ItemFlags: next.Key.Flags), onApplied: complete, onRefused: refuse, quiet: true);
    }
}
