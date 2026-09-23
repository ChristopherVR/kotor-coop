using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// Publishes whether the local player is in a conversation (so the session can hold a single
/// arbitrated dialogue token) together with the spectate payload, follows a peer's owned
/// conversation on this player's overlay, and diffs the local PC's inventory across a
/// conversation this instance owns to catch reward items for the dialogue-item-sync channel.
/// See docs/research/dialogue-sync.md, docs/research/dialogue-join-latency.md and
/// docs/research/dialogue-consequences.md §3.
/// </summary>
internal sealed class DialoguePump(IGameBridge bridge, ICoopSession session, AgentSettings settings, Action<string> log)
{
    /// <summary>Token serial of the last dialogue line surfaced to the local overlay, so a
    /// peer's line is posted once per line rather than every tick.</summary>
    private uint _lastSpectateSerial;

    /// <summary>When the local player was last genuinely in a conversation, and who with —
    /// held across the engine's between-entries flicker (see PumpDialogue).</summary>
    private long _dialogueLastActiveMs;
    private string _dialogueHeldPartnerTag = string.Empty;

    /// <summary>The last node the local conversation was really on, held across the engine's
    /// between-entries flicker so a line change does not read as "position unknown".</summary>
    private int _dialogueHeldNode = DialogueState.NoNode;

    private uint _dialogueHeldNodeSerial;

    /// <summary>The local conversation's forced-ness, held across the same flicker as the
    /// partner tag/node above.</summary>
    private bool _dialogueHeldForced;

    /// <summary>
    /// Governs PumpDialogue's work on the <see cref="AgentSettings.DialogueFastJoin"/> path:
    /// a genuine change (a new line, a new node, the conversation starting/ending, the peer's
    /// token moving) is acted on the very next poll tick, and an unchanged read is not
    /// re-published every tick just because the tick loop ran — see
    /// <see cref="KotorCoop.Protocol.DialogueTickGate"/>. The heartbeat matches the legacy
    /// cadence this replaces, so a late joiner or a lost packet still converges no slower than
    /// it always did.
    /// </summary>
    private readonly DialogueTickGate _dialogueTickGate = new(heartbeatIntervalMs: 150);

    // ══════════════════════════════════════════════════════════════════════════════════════
    // Session-layer node-sync diagnostics (docs/research/dialogue-skip-desync.md, 2026-09-10):
    // the plugin's own dialogue-node-pub/dialogue-node-drive logs are PLUGIN-side only — they
    // show what THIS instance's engine read locally and what it was asked to drive to, but
    // nothing about what actually crossed the wire in between. That gap is exactly where the
    // reported asymmetry (client-owns-host-follows losing everything after the first skip;
    // host-owns-client-follows losing the last stretch) had to be isolated: "the plugin never
    // advanced again" (plugin bug), "the wire/arbiter never accepted or relayed the change"
    // (session/arbiter bug), and "the wire delivered it but the plugin never consumed it"
    // (follower drive bug) all look identical from the plugin log alone. The root cause turned
    // out to be a stale interaction lock (see DialogueArbiter's own dated fix) rather than
    // anything in this pump, but the gap in the evidence trail this closes is real and cheap
    // (capped and dedup'd, same discipline as DialogueSpectateController's own node logs) — kept
    // for the next round that needs to tell these three failure shapes apart quickly.
    // ══════════════════════════════════════════════════════════════════════════════════════
    private uint _loggedLocalNodeSerial;
    private uint _loggedTokenNodeSerial;
    private int _wireLogBudget = 500;

    private void LogWireNodeIfChanged(
        bool active, int localNode, uint localNodeSerial, bool published,
        bool followingPeer, DialogueState token)
    {
        if (_wireLogBudget <= 0)
        {
            return;
        }

        if (active && localNodeSerial != _loggedLocalNodeSerial)
        {
            _loggedLocalNodeSerial = localNodeSerial;
            _wireLogBudget--;
            log($"dialogue-wire: OWNER local node -> {localNode} serial {localNodeSerial} "
                + $"published={published} at {Environment.TickCount64}");
        }

        if (followingPeer && token.NodeSerial != _loggedTokenNodeSerial)
        {
            _loggedTokenNodeSerial = token.NodeSerial;
            _wireLogBudget--;
            log($"dialogue-wire: FOLLOWER token now node {token.NodeIndex} serial "
                + $"{token.NodeSerial} owner {token.OwnerPeerId} tokenSerial {token.Serial} "
                + $"at {Environment.TickCount64}");
        }
    }

    /// <summary>
    /// How long a conversation may read inactive before it is reported over.
    ///
    /// 300ms, an order of magnitude past a real between-entries flicker (the frame hook runs
    /// at ~300 Hz, so "a few frames" is on the order of 10-20ms). Live-measured: a followed
    /// conversation's real close-to-census-unfreeze gap dropped from ~2.7s (at the old 1500ms
    /// value) to ~0.8-1.4s at this value; a separate isolation test at 50ms showed no further
    /// improvement (the remaining time is the pre-existing ~150ms×2 wire/pump cadence this
    /// debounce sits on top of, not the debounce itself) — see docs/research/dialogue-sync.md
    /// and docs/research/shared-state-audit.md.
    /// </summary>
    private const long DialogueLingerMs = 300;

    /// <summary>The last value logged for <see cref="IGameBridge.IsInDialogue"/> on THIS
    /// instance. Null before the first read, so the very first poll never logs a spurious
    /// "edge" against nothing.</summary>
    private bool? _lastLoggedIsInDialogue;

    /// <summary>
    /// Whether THIS instance currently owns an active conversation — the same debounced
    /// <c>active</c> flag <see cref="PumpDialogueItemGrants"/> diffs against, mirrored here so
    /// <c>KotorCoop.Session.Pumps.ScriptItemGrantPump</c> can tell "the dialogue pump already
    /// owns whatever is happening to the PC's inventory this tick" apart from "nothing is
    /// claiming it, sample as usual" — see docs/research/dialogue-consequences.md's
    /// script-item-sync section. Updated on EVERY <see cref="PumpDialogue"/> call, ahead of
    /// the <see cref="AgentSettings.DialogueFastJoin"/> tick-gate's possible early return, so
    /// an unchanged-fingerprint tick mid-conversation never leaves this stale — the property
    /// still reflects this tick's true state even when nothing else about the call runs. During
    /// the short deferred-close window, the dialogue item pump still owns the inventory
    /// baseline, so the script-item pump must wait instead of sampling the transient world. If
    /// the runtime dialogue-item flag is disabled, that deferred state is cleared and this
    /// falls back to the active-only meaning.
    /// </summary>
    public bool IsOwningConversation { get; private set; }

    /// <summary>
    /// Edge-triggered log of <see cref="IGameBridge.IsInDialogue"/> on THIS instance, every poll
    /// tick — no cadence gate of its own, called ahead of every other pump so the logged edge
    /// is as close to the engine's own true transition as this process can see. On the enemy
    /// authority (the host) this is the exact flag the enemy census gates on
    /// (<c>!bridge.IsInDialogue &amp;&amp; !bridge.IsInMinigame</c>), so a transition logged
    /// here IS the census unfreezing (or freezing). See docs/research/shared-state-audit.md.
    /// </summary>
    public void LogHostDialogueEdge()
    {
        bool nowInDialogue = bridge.IsInDialogue;
        if (_lastLoggedIsInDialogue == nowInDialogue)
        {
            return;
        }

        bool firstRead = _lastLoggedIsInDialogue is null;
        _lastLoggedIsInDialogue = nowInDialogue;
        if (firstRead)
        {
            return; // nothing to call an edge against yet
        }

        DialogueState token = session.GetDialogue();
        string role = session.IsEnemyAuthority ? "host" : "client";
        log($"dialogue-gate: {role} bridge.IsInDialogue {!nowInDialogue} -> {nowInDialogue} at "
            + $"{Environment.TickCount64} (followingPeer="
            + $"{token.IsHeldByAnotherThan(session.LocalPeerId)}, tokenActive={token.Active}, "
            + $"tokenSerial={token.Serial})");
    }

    public void PumpDialogue(in PlayerState local, SessionCapabilities caps)
    {
        if ((caps & SessionCapabilities.Dialogue) == 0)
        {
            return;
        }

        // What the local player is hearing and doing, straight from the plugin's read.
        bridge.TryReadLocalDialogue(out string speaker, out string line);
        bridge.TryReadLocalDialogueFollow(
            out string partnerTag, out uint replySerial, out int replyIndex, out byte[] pickLog,
            out int nodeIndex, out uint nodeSerial, out bool localForced);

        // Who owns a conversation right now, from the last broadcast. Read FIRST because it
        // gates the local claim below.
        DialogueState token = session.GetDialogue();
        bool followingPeer = token.IsHeldByAnotherThan(session.LocalPeerId);

        // ⚠ STUNT MODULE: a scripted cutscene, not a conversation any peer could meaningfully
        // join or leave this instance in the middle of (see StuntModuleRule). Never follow
        // ANYONE while sitting in one — there is no real dialogue UI here to drive a spectate
        // copy into — and, below, never publish this instance's own "dialogue" as something
        // followable either. DialogueState carries no owner-module field, so this cannot
        // separately detect "the OWNER announced from inside a stunt module" — but that case
        // is already covered from the source: the owner's own PumpDialogue call, running this
        // same gate on ITS local module, is what would otherwise have published a followable
        // token in the first place, so a peer never sees one to follow.
        bool localInStuntModule = StuntModuleRule.IsStuntModule(local.Module);
        if (localInStuntModule)
        {
            followingPeer = false;
        }

        // ⚠ A follower's conversation is NOT a claim. While this instance follows a peer's
        // conversation, its own client is genuinely "in a conversation" — the copy we
        // opened. Publishing that as a claim made the roles flip at the end: the owner
        // released, the follower's still-open copy grabbed the token, and the former owner
        // dutifully started following its own conversation back — the follow/close ping-pong
        // in the logs.
        bool rawInDialogue = local.IsInDialogue && !followingPeer;

        // ⚠ DEBOUNCED, because the engine's "conversation active" flag flickers off for a
        // few frames between entries. Publishing those gaps released and re-granted the
        // token several times inside one conversation, and the spectator tore its copy down
        // and reopened it on every flicker. A conversation is only reported over once it
        // has been gone for a moment; the partner tag is held through the gaps too.
        long nowMs = Environment.TickCount64;
        if (rawInDialogue)
        {
            _dialogueLastActiveMs = nowMs;
            if (partnerTag.Length > 0)
            {
                _dialogueHeldPartnerTag = partnerTag;
            }

            // ⚠ The NODE is held across the flicker for the same reason as the tag, and it
            // matters more. The engine drops its "conversation active" flag for a few frames
            // BETWEEN ENTRIES, and during that gap the plugin reads no conversation and
            // publishes -1. Letting that reach the wire would tell the follower "I no longer
            // know where I am" on every single line change — precisely at the moment the
            // owner has just moved to a new node and the follower most needs to be sent
            // there. Hold the last real node instead; the serial only advances when the node
            // genuinely changed, so holding cannot re-drive anything.
            if (nodeIndex >= 0)
            {
                _dialogueHeldNode = nodeIndex;
                _dialogueHeldNodeSerial = nodeSerial;
            }

            // The plugin latches this for the whole conversation (set once, at its own start
            // edge), so it never needs a "only hold a real value" guard the way the tag/node
            // above do — it is simply carried the same way.
            _dialogueHeldForced = localForced;
        }

        bool active = rawInDialogue
            || (_dialogueLastActiveMs > 0 && (nowMs - _dialogueLastActiveMs) < DialogueLingerMs);

        // Never publish as active while this instance sits in a stunt module — see the gate
        // above. The linger window still exists for an ordinary conversation caught mid-line
        // by an ordinary module change; a stunt module is not that, so it is forced off
        // unconditionally rather than left to expire on its own.
        if (localInStuntModule)
        {
            active = false;
        }

        // Set here, ahead of the tick-gate's possible early return below, so it tracks the
        // engine every call the way _dialogueLastActiveMs/_dialogueHeldPartnerTag above do —
        // see the property's own doc for why staleness here would be a real bug, not just an
        // inefficiency.
        bool dialogueItemSyncEnabled = IsDialogueItemSyncEnabled;
        if (!dialogueItemSyncEnabled)
        {
            ClearDialogueGrantState();
        }

        IsOwningConversation = active || (dialogueItemSyncEnabled && _dialogueGrantOwning);

        if (!active)
        {
            _dialogueHeldPartnerTag = string.Empty;
            _dialogueHeldNode = DialogueState.NoNode;
            _dialogueHeldNodeSerial = 0;
            _dialogueHeldForced = false;
        }

        // ⚠ THE DEBOUNCE ABOVE ALWAYS RUNS, EVEN WHEN THE GATE BELOW SAYS "NOTHING TO DO".
        // _dialogueLastActiveMs/_dialogueHeldPartnerTag/_dialogueHeldNode are this instance's
        // own bookkeeping and must track the engine every tick regardless of whether anything
        // is published this tick, or the linger window would be measured against a stale
        // sample instead of the clock it is supposed to run on.
        //
        // On the fast path, an UNCHANGED fingerprint before the heartbeat is due costs nothing
        // beyond the reads already taken above — see DialogueTickGate. Off the fast path this
        // is a no-op (the flag guards it), so the legacy cadence in PumpOnce is the only gate
        // and turning dialogue_fast_join off is a true rollback.
        bool willPump = true;
        if (settings.DialogueFastJoin)
        {
            long fingerprint = HashCode.Combine(
                active, speaker, line, active ? _dialogueHeldPartnerTag : string.Empty,
                replySerial, replyIndex, active ? _dialogueHeldNode : DialogueState.NoNode);
            fingerprint = HashCode.Combine(
                fingerprint, active ? _dialogueHeldNodeSerial : 0u, followingPeer,
                token.Serial, token.NodeSerial, token.ReplySerial, active && _dialogueHeldForced);

            willPump = _dialogueTickGate.ShouldPump(nowMs, fingerprint);
        }

        // TEMPORARY diagnostic (see _wireLogBudget's own doc): logged BEFORE the possible
        // early return below, so a tick the fast-join gate suppresses still shows up as
        // "published=false" rather than vanishing from the trail entirely.
        LogWireNodeIfChanged(
            active, active ? _dialogueHeldNode : DialogueState.NoNode,
            active ? _dialogueHeldNodeSerial : 0, willPump, followingPeer, token);

        if (!willPump)
        {
            return;
        }

        session.PublishDialogue(
            active, speaker, line,
            active ? _dialogueHeldPartnerTag : string.Empty, replySerial, replyIndex, pickLog,
            active ? _dialogueHeldNode : DialogueState.NoNode,
            active ? _dialogueHeldNodeSerial : 0,
            active && _dialogueHeldForced);

        // The item-grant channel needs "do WE own an active conversation right now", which is
        // exactly what `active` already means (debounced across the engine's between-entries
        // flicker) — computed once here rather than re-derived, so the two never drift apart.
        PumpDialogueItemGrants(
            active, active ? _dialogueHeldPartnerTag : string.Empty, local.Module);
        IsOwningConversation = active
            || (IsDialogueItemSyncEnabled && _dialogueGrantOwning);

        // The other direction: when a peer owns a conversation, hand the plugin who they are
        // talking to and which reply they last picked, so this instance runs the SAME
        // conversation in the game's own dialogue UI and follows it. Written every tick
        // rather than on the serial edge — the plugin is level-triggered on these fields and
        // a dropped edge would strand a spectator outside the conversation.
        bridge.WriteRemoteDialogue(
            followingPeer,
            followingPeer ? token.PartnerTag : string.Empty,
            followingPeer ? token.ReplySerial : 0,
            followingPeer ? token.ReplyIndex : 0,
            followingPeer ? token.PickLog : null,
            followingPeer ? token.NodeIndex : DialogueState.NoNode,
            followingPeer ? token.NodeSerial : 0,
            followingPeer && token.Forced);

        _lastSpectateSerial = token.Serial;
    }

    /// <summary>Resref counts of the local PC's inventory the last time
    /// <see cref="PumpDialogueItemGrants"/> sampled it, for the diff against the next sample.
    /// Empty means "no baseline yet" (conversation just started, or sampling is off).</summary>
    private List<(string Resref, ItemIdentity Identity)> _dialogueGrantBaseline = [];

    private bool _dialogueGrantHasBaseline;

    private string _dialogueGrantBaselineModule = string.Empty;

    private int _dialogueGrantBaselineWorldEpoch;

    private bool _dialogueGrantOwning;

    private long _dialogueGrantLastSampleMs;

    /// <summary>How often the PC's inventory is actually re-swept while a conversation runs.
    /// <see cref="IGameBridge.ReadPossessedItems"/>'s own doc warns it is a full world memory
    /// sweep, "not free" — this is independent of, and much slower than, the combat-cadence
    /// tick <see cref="PumpDialogue"/> (and therefore this method) rides on, so a long
    /// conversation that grants mid-way is still caught without sweeping every ~150 ms.
    /// </summary>
    private const long DialogueGrantSampleIntervalMs = 1000;

    private enum DialogueGrantSampleResult
    {
        Completed,
        Deferred,
        WorldReplaced,
    }

    /// <summary>Single-element set handed to <see cref="IGameBridge.ReadPossessedItems"/> every
    /// sample. A field rather than a fresh allocation per call — the method is on a cadence
    /// hot enough for that to matter over a long conversation.</summary>
    private static readonly HashSet<uint> LocalPlayerIdSet = [AgentWellKnownIds.LocalPlayerObjectId];

    /// <summary>
    /// Diffs the local PC's possessed items across the window this instance owns a
    /// conversation, and publishes whatever appeared as a dialogue grant —
    /// docs/research/dialogue-consequences.md §3.2. Sampled at conversation start, every
    /// <see cref="DialogueGrantSampleIntervalMs"/> while it continues, and once more at the
    /// end, so a reward mid-conversation is caught rather than only a reward on the final node.
    ///
    /// ⚠ ONLY WHILE THIS INSTANCE OWNS THE CONVERSATION. A spectator never runs
    /// <c>HandleReply</c>, so its own inventory cannot have changed from the conversation —
    /// diffing there would be noise at best, and at worst would double-detect the owner's own
    /// grant once it arrives back over the wire (which <see cref="ICoopSession.DrainDialogueGrants"/>
    /// already filters by origin, but there is no reason to also generate the false positive).
    /// </summary>
    private void PumpDialogueItemGrants(
        bool ownsActiveConversation, string partnerTag, string localModule)
    {
        // A client adopts the host's dialogue_item_sync at runtime when they differ —
        // docs/research/mod-compatibility.md's "Behaviour-flag negotiation".
        if (!IsDialogueItemSyncEnabled)
        {
            ClearDialogueGrantState();
            return;
        }

        // A dialogue can close on the same tick that the engine tears down its world. The
        // final inventory read in that window is either empty or only partly rebuilt; diffing
        // it against the old conversation baseline publishes the new world's starter items as
        // rewards. Keep the baseline tied to both the module and bridge epoch, and never read
        // or diff while either lifecycle signal says the item view is provisional.
        string observedModule = localModule;
        if (bridge.TryReadCurrentModule(out string currentModule) && currentModule.Length > 0)
        {
            observedModule = currentModule;
        }

        int worldEpoch = bridge.WorldEpoch;
        bool moduleChanged = observedModule.Length > 0
            && !ModuleNameGuard.IsFallbackModuleName(observedModule)
            && _dialogueGrantBaselineModule.Length > 0
            && !string.Equals(
                observedModule, _dialogueGrantBaselineModule, StringComparison.OrdinalIgnoreCase);
        bool worldReplaced = _dialogueGrantHasBaseline
            && (moduleChanged || worldEpoch != _dialogueGrantBaselineWorldEpoch);
        if (worldReplaced)
        {
            _dialogueGrantBaseline = [];
            _dialogueGrantHasBaseline = false;
            _dialogueGrantBaselineModule = observedModule;
            _dialogueGrantBaselineWorldEpoch = worldEpoch;
        }

        if (bridge.IsWorldBetweenLives)
        {
            // The old baseline cannot survive a world edge, but the transient read cannot
            // become the new baseline either. Keep ownership while the conversation still
            // reports active so the first settled tick reseeds it silently.
            _dialogueGrantBaseline = [];
            _dialogueGrantHasBaseline = false;
            if (!ownsActiveConversation)
            {
                _dialogueGrantOwning = false;
            }

            return;
        }

        if (!bridge.IsItemViewSettled)
        {
            // An unsettled item cache on the SAME world is different from a world edge: the
            // existing conversation baseline remains valid, and a closing reward must still be
            // diffed once the cache settles. Do not replace it with the transient empty/partial
            // read and do not end ownership while the final sample is deferred.
            return;
        }

        if (!ownsActiveConversation)
        {
            if (_dialogueGrantOwning && !worldReplaced && _dialogueGrantHasBaseline)
            {
                // The conversation just ended: one last diff so a reward on the FINAL node
                // (the common case — most reward scripts run on the closing entry) is caught,
                // then the baseline is cleared so the next conversation starts fresh.
                DialogueGrantSampleResult result = SampleAndPublishDialogueGrant(
                    partnerTag, observedModule, worldEpoch);
                if (result == DialogueGrantSampleResult.Deferred)
                {
                    // Keep the owner/baseline so a closing reward is sampled after the item
                    // view settles. A world replacement clears the baseline below instead.
                    return;
                }
            }

            _dialogueGrantOwning = false;
            _dialogueGrantBaseline = [];
            _dialogueGrantHasBaseline = false;
            return;
        }

        if (!_dialogueGrantOwning)
        {
            // Just started owning a conversation: snapshot now, publish nothing yet (there is
            // nothing to diff against on the very first sample).
            _dialogueGrantOwning = true;
            _dialogueGrantLastSampleMs = Environment.TickCount64;
        }

        if (!_dialogueGrantHasBaseline)
        {
            _dialogueGrantLastSampleMs = Environment.TickCount64;
            SeedDialogueGrantBaseline(observedModule, worldEpoch);
            return;
        }

        long nowMs = Environment.TickCount64;
        if (nowMs - _dialogueGrantLastSampleMs < DialogueGrantSampleIntervalMs)
        {
            return;
        }

        _dialogueGrantLastSampleMs = nowMs;
        SampleAndPublishDialogueGrant(partnerTag, observedModule, worldEpoch);
    }

    private DialogueGrantSampleResult SampleAndPublishDialogueGrant(
        string partnerTag, string expectedModule, int expectedWorldEpoch)
    {
        DialogueGrantSampleResult readResult = TryReadStableDialogueItems(
            expectedModule, expectedWorldEpoch, out List<(string Resref, ItemIdentity Identity)> after,
            out string observedModule, out int worldEpoch);
        if (readResult != DialogueGrantSampleResult.Completed)
        {
            if (readResult == DialogueGrantSampleResult.WorldReplaced)
            {
                ResetDialogueGrantBaseline(observedModule, worldEpoch);
            }

            return readResult;
        }

        List<MaterialisedLootItem> gained = DialogueItemDiff.Compute(_dialogueGrantBaseline, after);
        _dialogueGrantBaseline = after;
        _dialogueGrantHasBaseline = true;

        if (gained.Count == 0)
        {
            return DialogueGrantSampleResult.Completed;
        }

        log($"dialogue grant: {gained.Count} new item(s) in the local speaker's inventory "
            + $"('{partnerTag}'): {string.Join(", ", gained.Select(i => i.Resref))}");
        session.PublishDialogueGrant(partnerTag, gained);
        return DialogueGrantSampleResult.Completed;
    }

    private void SeedDialogueGrantBaseline(string expectedModule, int expectedWorldEpoch)
    {
        DialogueGrantSampleResult result = TryReadStableDialogueItems(
            expectedModule, expectedWorldEpoch,
            out List<(string Resref, ItemIdentity Identity)> baseline,
            out string observedModule, out int worldEpoch);
        if (result == DialogueGrantSampleResult.Completed)
        {
            _dialogueGrantBaseline = baseline;
            _dialogueGrantHasBaseline = true;
            _dialogueGrantBaselineModule = observedModule;
            _dialogueGrantBaselineWorldEpoch = worldEpoch;
        }
        else if (result == DialogueGrantSampleResult.WorldReplaced)
        {
            ResetDialogueGrantBaseline(observedModule, worldEpoch);
        }
    }

    private DialogueGrantSampleResult TryReadStableDialogueItems(
        string expectedModule, int expectedWorldEpoch,
        out List<(string Resref, ItemIdentity Identity)> items,
        out string observedModule, out int worldEpoch)
    {
        items = [];
        observedModule = expectedModule;
        worldEpoch = bridge.WorldEpoch;

        if (bridge.IsWorldBetweenLives)
        {
            return DialogueGrantSampleResult.WorldReplaced;
        }

        if (!bridge.IsItemViewSettled)
        {
            return DialogueGrantSampleResult.Deferred;
        }

        // A missing native object array makes ReadPossessedItems intentionally return no
        // rows. Preserve the dialogue baseline until the resolver is ready instead of
        // treating that transient empty view as a reward/removal delta.
        if (!bridge.IsItemResolverReady)
        {
            return DialogueGrantSampleResult.Deferred;
        }

        items = [.. bridge.ReadPersonalPossessedItems(LocalPlayerIdSet)
            .Select(i => (i.Resref, i.Identity))];

        if (!bridge.LastPossessedItemReadWasComplete)
        {
            items = [];
            return DialogueGrantSampleResult.Deferred;
        }

        if (bridge.TryReadCurrentModule(out string currentModule) && currentModule.Length > 0)
        {
            observedModule = currentModule;
        }

        worldEpoch = bridge.WorldEpoch;
        bool moduleChanged = observedModule.Length > 0
            && !ModuleNameGuard.IsFallbackModuleName(observedModule)
            && expectedModule.Length > 0
            && !ModuleNameGuard.IsFallbackModuleName(expectedModule)
            && !string.Equals(observedModule, expectedModule, StringComparison.OrdinalIgnoreCase);
        if (worldEpoch != expectedWorldEpoch || moduleChanged || bridge.IsWorldBetweenLives)
        {
            return DialogueGrantSampleResult.WorldReplaced;
        }

        // The read itself can cross the settling edge. Its rows are not a coherent snapshot,
        // so retain the prior baseline and retry on the next tick.
        if (!bridge.IsItemViewSettled)
        {
            return DialogueGrantSampleResult.Deferred;
        }

        return DialogueGrantSampleResult.Completed;
    }

    private void ResetDialogueGrantBaseline(string module, int worldEpoch)
    {
        _dialogueGrantBaseline = [];
        _dialogueGrantHasBaseline = false;
        _dialogueGrantBaselineModule = module;
        _dialogueGrantBaselineWorldEpoch = worldEpoch;
    }

    private bool IsDialogueItemSyncEnabled =>
        session.Overrides.DialogueItemSync ?? settings.DialogueItemSync;

    private void ClearDialogueGrantState()
    {
        _dialogueGrantOwning = false;
        _dialogueGrantBaseline = [];
        _dialogueGrantHasBaseline = false;
    }
}
