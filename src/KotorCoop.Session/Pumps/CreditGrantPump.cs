using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// The credits-reward relay (docs/research/personal-inventory.md §4): while
/// <c>credits_mode = personal</c> (<see cref="Pumps.PartyPump.PumpProgression"/> is no longer
/// folding a peer's earn/spend into a shared purse — see <see cref="InventorySyncRule"/>), a
/// quest reward's <c>GiveGoldToCreature</c> would otherwise reach only the conversation-driving
/// player. This pump detects that rise (via <see cref="CreditGrantRule"/>, scoped to a
/// conversation this instance owns) and relays it to the peer as a fresh grant
/// (<see cref="ICreditGrantSession.PublishCreditGrant"/>), and separately applies whatever the
/// peer's own conversation granted (<see cref="ICreditGrantSession.DrainCreditGrants"/>) — added
/// directly to this engine's own gold, never merged/overwritten the way the ordinary ledger
/// does, so it composes correctly with credits staying otherwise personal.
///
/// Both halves run every tick this pump is called, independent of each other — detecting while
/// personal mode, applying regardless of mode (a grant already in flight when the mode changes
/// mid-session should still land).
/// </summary>
internal sealed class CreditGrantPump(IGameBridge bridge, ICoopSession session, AgentSettings settings, Action<string> log)
{
    private long? _goldBaseline;

    private string _baselineModule = string.Empty;

    private int _baselineWorldEpoch;

    /// <param name="module">The local module, for the same reload/module-change rebaseline
    /// guard <see cref="Pumps.ScriptItemGrantPump"/> already uses.</param>
    /// <param name="ownedByDialoguePump"><see cref="DialoguePump.IsOwningConversation"/>, read
    /// by the caller ahead of this call every tick — see that pump's own remarks.</param>
    /// <param name="localReadable">Whether the caller's own local-state read succeeded this
    /// tick — see <see cref="Pumps.ScriptItemGrantPump.PumpScriptItemGrants"/>'s identical
    /// parameter for why an unreadable/between-lives tick is never an observation.</param>
    public void PumpCreditGrants(string module, bool ownedByDialoguePump, bool localReadable = true)
    {
        bool creditsPersonal = session.Overrides.CreditsPersonal ?? settings.CreditsPersonal;

        DetectLocalGrant(module, ownedByDialoguePump, localReadable, creditsPersonal);

        int applied = ApplyReceivedGrants();
        if (applied > 0 && _goldBaseline is { } baseline)
        {
            // ⚠ Without this, a grant APPLIED here would read, on the very next tick, as this
            // engine's own gold having risen — and if a (separate, unrelated) conversation this
            // player owns also happens to be open at that moment, DetectLocalGrant would
            // misattribute the applied grant to "my own conversation just granted this" and
            // re-publish it right back onto the wire: the exact "a receiver's own applied
            // change gets misdetected as a fresh local action" amplification shape
            // shared-inventory-v2.md's whole history warns about. Folding the applied amount
            // into the baseline immediately keeps the next diff looking at ONLY what actually
            // changed for reasons other than this pump's own write.
            _goldBaseline = baseline + applied;
        }
    }

    private void DetectLocalGrant(string module, bool ownedByDialoguePump, bool localReadable, bool creditsPersonal)
    {
        if (!creditsPersonal)
        {
            // credits_mode = shared: the ordinary progression ledger already covers a reward
            // (it merges everything). Running this channel too would double-credit it. Drop any
            // stale baseline so a later flip back to personal starts clean.
            _goldBaseline = null;
            return;
        }

        if (!localReadable || bridge.IsWorldBetweenLives)
        {
            // Not an observation - leave the baseline untouched, exactly like
            // ScriptItemGrantPump's identical guard.
            return;
        }

        // A reload/module change invalidates any in-progress baseline the same way it does for
        // the item channels - the gold reading after a load is the save's own value, not
        // something earned during whatever window was open before.
        int worldEpoch = bridge.WorldEpoch;
        bool moduleChanged = module.Length > 0
            && !ModuleNameGuard.IsFallbackModuleName(module)
            && !string.Equals(module, _baselineModule, StringComparison.OrdinalIgnoreCase);
        if (moduleChanged || worldEpoch != _baselineWorldEpoch)
        {
            _baselineModule = module;
            _baselineWorldEpoch = worldEpoch;
            _goldBaseline = null;
        }

        if (!bridge.TryReadProgression(out uint rawGold, out _))
        {
            return;
        }

        uint gold = GoldReadingRule.Sanitise(rawGold);

        CreditGrantObservation observation = CreditGrantRule.Observe(
            _goldBaseline, gold, ownedByDialoguePump, creditsPersonal: true);
        _goldBaseline = observation.NewBaseline;

        if (observation.AmountToPublish <= 0)
        {
            return;
        }

        session.PublishCreditGrant(observation.AmountToPublish);
        log($"credit grant: local conversation handed +{observation.AmountToPublish} credits - "
            + "requesting the peer's own purse receive the same amount");
    }

    /// <returns>The total gold genuinely written to the local purse this tick (may be less than
    /// the sum of grant amounts if the engine ceiling clamped one) — see the caller's own
    /// remarks for why this feeds straight back into <see cref="_goldBaseline"/>.</returns>
    private int ApplyReceivedGrants()
    {
        int totalApplied = 0;

        foreach (CreditGrantPacket grant in session.DrainCreditGrants())
        {
            if (grant.Amount <= 0)
            {
                continue;
            }

            if (!bridge.TryReadProgression(out uint currentGold, out uint currentXp))
            {
                log($"credit grant: received +{grant.Amount} from peer {grant.OriginPeerId} but "
                    + "could not read the local purse to apply it - dropped");
                continue;
            }

            long target = Math.Clamp((long)currentGold + grant.Amount, 0, GoldReadingRule.Max);
            uint newGold = (uint)target;

            if (bridge.TryWriteProgression(newGold, currentXp))
            {
                totalApplied += (int)(newGold - currentGold);
                log($"credit grant: received +{grant.Amount} from peer {grant.OriginPeerId}, "
                    + $"purse {currentGold} -> {newGold}");
            }
        }

        return totalApplied;
    }
}
