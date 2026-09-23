using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// Catches item grants a KOTOR script places directly into the local PC's inventory OUTSIDE
/// any conversation — an OnEnter trigger, an OnDeath script, a cutscene, a heartbeat script —
/// which <see cref="DialoguePump.PumpDialogueItemGrants"/> never sees because it only diffs
/// while THIS instance owns a conversation. See docs/research/dialogue-consequences.md's
/// script-item-sync section for the design and the NEEDS-LIVE test plan.
///
/// Always-on (gated only by <see cref="AgentSettings.ScriptItemSync"/>), sampling on its own
/// slow cadence whenever the dialogue pump does not already own the window. Reuses
/// <see cref="DialogueItemDiff.Compute"/> for the actual diff and
/// <see cref="ICoopSession.PublishDialogueGrant"/> for the wire — the exact same channel a
/// dialogue reward uses, with an empty partner tag since there is no conversation to name.
///
/// The hardest part of this pump is NOT the diff, it is knowing what NOT to report: an item
/// that arrived through a channel that already replicates it (ground pickup, container/corpse
/// loot, a peer's own grant applied via giveloot, a human's own giveloot) must never be
/// re-broadcast as if a script had just created it, or both players end up with two copies.
/// <see cref="RecentlySyncedItems"/> is the suppression ledger the other pumps
/// (<see cref="WorldStatePump"/>'s ground-pickup path, <see cref="LootPump"/>'s container-loot
/// and peer-grant paths, <see cref="CoopAgent.NoteLocalItemGiven"/> for a human's own
/// giveloot) feed; this pump only consumes it.
/// </summary>
internal sealed class ScriptItemGrantPump(
    IGameBridge bridge, ICoopSession session, AgentSettings settings,
    RecentlySyncedItems suppression, Action<string> log)
{
    private static readonly HashSet<uint> LocalPlayerIdSet = [AgentWellKnownIds.LocalPlayerObjectId];

    private List<(string Resref, ItemIdentity Identity)> _baseline = [];

    private bool _hasBaseline;

    private string _baselineModule = string.Empty;

    private int _baselineWorldEpoch;

    private long _lastSampleMs;

    /// <summary>True while the dialogue pump owned the previous tick — the edge this flips on
    /// (true -&gt; false) is exactly when a fresh, non-diffed baseline read is needed, whether
    /// the dialogue pump ever published anything from its own window or not. See the doc on
    /// <see cref="PumpScriptItemGrants"/>'s <paramref name="ownedByDialoguePump"/> parameter
    /// for why a FRESH read, not a value handed over from the dialogue pump, is what
    /// "coordinate baselines" means here.</summary>
    private bool _wasOwnedByDialoguePump;

    /// <summary>
    /// Samples the local PC's inventory and publishes anything a script (not a conversation,
    /// not an already-synced channel) just placed into it.
    ///
    /// <paramref name="ownedByDialoguePump"/> is <see cref="DialoguePump.IsOwningConversation"/>,
    /// read by the caller ahead of this call every tick: while true, this pump does not sample
    /// at all — the dialogue pump's own diff already covers that window, and running both
    /// would double-report the same reward node's grant under two different channels. The
    /// falling edge (dialogue pump just released the window) forces exactly ONE fresh,
    /// non-diffed baseline read here rather than diffing against a baseline taken before the
    /// conversation started: a fresh read at the same instant the dialogue pump's own final
    /// sample ran is, in practice, THE SAME real inventory state — the dialogue pump already
    /// reported whatever changed during its window, so re-diffing against a stale pre-
    /// conversation baseline would just report it a second time. This needs no reference to
    /// the dialogue pump's own baseline field, and cannot drift from it even if a call is
    /// missed, because both are reading the SAME live game state, not exchanging a value.
    ///
    /// <paramref name="localReadable"/> is whether the caller's own local-state read succeeded
    /// this tick. While it did not — or while the bridge itself says the world is between lives
    /// (<see cref="IWorldStateBridge.IsWorldBetweenLives"/>) — NOTHING here is an observation:
    /// the engine's repositories are empty or half-built, and neither a baseline nor a diff may
    /// be taken from them. Live 2026-09-02 (docs/research/shared-inventory-v2.md §17): a client's
    /// own rejoin load emptied its inventory, this pump baselined on that emptiness (the read
    /// was failing, the module was still "-", the WorldEpoch had not moved yet because the new
    /// world's player had not resolved), and the reloaded inventory then diffed as seven fresh
    /// grants that the host pooled and the client reconciled back in. The pump simply waits;
    /// the epoch edge that follows the load reseeds it properly.
    ///
    /// Even once <see cref="IWorldStateBridge.IsWorldBetweenLives"/> clears, one more window has
    /// to be respected: <see cref="IWorldStateBridge.IsItemViewSettled"/>. Live 2026-09-02, 23:31
    /// (a game-over reload of END_M01AA): the host's player resolved and the world was acked on
    /// screen while the PC was still being finished (hp read 8/8, then 12/12 a moment later) —
    /// the save's equipped clothes and short sword are created by the engine AFTER the player
    /// resolves, so the FIRST "settled" item sweep missed them exactly the way a between-lives
    /// sweep would have, except this one was not provisional and nothing looked again for the
    /// ordinary 15 s rebuild interval. By the time it did, the two items diffed in as a fresh
    /// script grant (`script item grant: … g_a_clothes01, g_w_shortswrd01`) and the party pool
    /// grew from three keys to five reconciling them into the peer's own inventory. While the
    /// bridge's item view is not yet settled this pump takes a silent rebaseline every sample —
    /// the same path a first-ever sample or a just-released dialogue window takes — instead of
    /// diffing, so an item that merely finishes appearing during that window is folded into the
    /// baseline rather than reported as granted; a genuine grant that lands AFTER settling is
    /// still seen normally.
    /// </summary>
    public void PumpScriptItemGrants(string module, bool ownedByDialoguePump, bool localReadable = true)
    {
        // A client adopts the host's script_item_sync at runtime when they differ —
        // docs/research/mod-compatibility.md's "Behaviour-flag negotiation".
        if (!(session.Overrides.ScriptItemSync ?? settings.ScriptItemSync))
        {
            return;
        }

        if (ownedByDialoguePump)
        {
            _wasOwnedByDialoguePump = true;
            return;
        }

        // Between lives: not an observation. Leave the baseline AND the dialogue-edge latch
        // untouched — whatever was true before the load is still the right thing to resume
        // from once the epoch edge reseeds (or, if no edge follows, once the read recovers).
        if (!localReadable || bridge.IsWorldBetweenLives)
        {
            return;
        }

        // The native server object array is the completeness/liveness gate for the item scan.
        // An unavailable array must not look like an empty inventory, or the next ready sample
        // would publish the whole character as a fresh script grant.
        if (!bridge.IsItemResolverReady)
        {
            return;
        }

        long nowMs = Environment.TickCount64;

        // A reload of the same module is a new world with the same name (every id re-minted,
        // the bridge's own caches dropped — IWorldStateBridge.WorldEpoch); the baseline taken
        // in the old one would diff the reloaded inventory as freshly granted. Live 2026-09-02:
        // that diff published the whole inventory to the peer after a game-over reload.
        int worldEpoch = bridge.WorldEpoch;
        bool moduleChanged = module.Length > 0
            && !ModuleNameGuard.IsFallbackModuleName(module)
            && !string.Equals(module, _baselineModule, StringComparison.OrdinalIgnoreCase);
        bool worldReplaced = worldEpoch != _baselineWorldEpoch;

        if (moduleChanged || worldReplaced)
        {
            _baselineModule = module;
            _baselineWorldEpoch = worldEpoch;
            _hasBaseline = false;
            suppression.Clear();
        }

        // Four cases force a silent rebaseline instead of a diff: no baseline has ever been
        // taken (first sample since attach or since a module change just cleared it), the
        // dialogue pump owned the PREVIOUS tick and just released it (the edge described in
        // this method's own doc), or the bridge's item view has not settled since the last world
        // edge (this method's own doc, the 2026-09-02 23:31 round) — an item that only just
        // finished being created belongs in the baseline, not in a diff.
        bool rebaselineOnly = !_hasBaseline || _wasOwnedByDialoguePump || !bridge.IsItemViewSettled;

        if (rebaselineOnly)
        {
            List<(string Resref, ItemIdentity Identity)> candidate = [.. bridge.ReadPersonalPossessedItems(LocalPlayerIdSet)
                .Select(i => (i.Resref, i.Identity))];
            if (!bridge.LastPossessedItemReadWasComplete)
            {
                return;
            }
            _baseline = candidate;
            _wasOwnedByDialoguePump = false;
            _hasBaseline = true;
            _lastSampleMs = nowMs;
            return;
        }

        if (nowMs - _lastSampleMs < settings.ScriptItemGrantSampleIntervalMs)
        {
            return;
        }

        List<(string Resref, ItemIdentity Identity)> current =
            [.. bridge.ReadPersonalPossessedItems(LocalPlayerIdSet).Select(i => (i.Resref, i.Identity))];

        if (!bridge.LastPossessedItemReadWasComplete)
        {
            return;
        }

        _lastSampleMs = nowMs;

        ScriptItemGrantRule.Result result = ScriptItemGrantRule.Evaluate(
            _baseline, current, rebaselineOnly: false, suppression, nowMs);

        _baseline = [.. result.NewBaseline];

        if (result.ToPublish.Count == 0)
        {
            return;
        }

        log($"script item grant: {result.ToPublish.Count} new item(s) in the local PC's "
            + $"inventory outside dialogue: {string.Join(", ", result.ToPublish.Select(i => i.Resref))}");

        // Empty partner tag: PartnerTag is diagnostic-only (see DialogueGrantPacket's own
        // doc), and there is no conversation to name here.
        session.PublishDialogueGrant(string.Empty, result.ToPublish);
    }
}
