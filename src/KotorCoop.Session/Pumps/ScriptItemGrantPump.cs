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
    /// </summary>
    public void PumpScriptItemGrants(string module, bool ownedByDialoguePump)
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

        long nowMs = Environment.TickCount64;

        bool moduleChanged = module.Length > 0
            && !ModuleNameGuard.IsFallbackModuleName(module)
            && !string.Equals(module, _baselineModule, StringComparison.OrdinalIgnoreCase);

        if (moduleChanged)
        {
            _baselineModule = module;
            _hasBaseline = false;
            suppression.Clear();
        }

        // Three cases force a silent rebaseline instead of a diff: no baseline has ever been
        // taken (first sample since attach or since a module change just cleared it), or the
        // dialogue pump owned the PREVIOUS tick and just released it (the edge described in
        // this method's own doc).
        bool rebaselineOnly = !_hasBaseline || _wasOwnedByDialoguePump;
        _wasOwnedByDialoguePump = false;

        if (rebaselineOnly)
        {
            _baseline = [.. bridge.ReadPossessedItems(LocalPlayerIdSet)
                .Select(i => (i.Resref, i.Identity))];
            _hasBaseline = true;
            _lastSampleMs = nowMs;
            return;
        }

        if (nowMs - _lastSampleMs < settings.ScriptItemGrantSampleIntervalMs)
        {
            return;
        }

        _lastSampleMs = nowMs;

        List<(string Resref, ItemIdentity Identity)> current =
            [.. bridge.ReadPossessedItems(LocalPlayerIdSet).Select(i => (i.Resref, i.Identity))];

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
