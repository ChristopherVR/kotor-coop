using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The <see cref="CoopAgent"/>-level wiring for <c>ScriptItemGrantPump</c> — sampling and
/// diffing (the substantive logic) is <see cref="ScriptItemGrantRuleTests"/>'s job; this
/// covers only what only a full pump/agent can prove: the flag gate, the first-sample silent
/// baseline, deferring to the dialogue pump while a conversation is owned, and the module
/// reset. <see cref="AgentSettings.ScriptItemGrantSampleIntervalMs"/> is zeroed throughout so
/// every call re-samples immediately rather than waiting on a real clock — the same
/// testability knob <see cref="AgentSettings.CombatPumpIntervalMs"/> already offers
/// <see cref="MaterialisedLootPumpTests"/>.
/// </summary>
public sealed class ScriptItemGrantPumpTests
{
    private const string Module = "end_m01aa";

    private sealed class ScriptItemBridge : IGameBridge
    {
        public PlayerState Local { get; set; } = new(Vector3f.Zero, 0f, Module, 20, 0);

        /// <summary>What <see cref="ReadPossessedItems"/> answers for the local player.</summary>
        public List<(string Resref, ItemIdentity Identity)> Possessed { get; set; } = [];

        public string Description => "script-item-test";

        /// <summary>What the bridge reports as <see cref="IWorldStateBridge.WorldEpoch"/> — bumped
        /// by a test standing in for a reload of the same module.</summary>
        public int WorldEpoch { get; set; }

        /// <summary>What the bridge reports as <see cref="IWorldStateBridge.IsWorldBetweenLives"/>
        /// — the plugin's own "the world is being torn down or loaded" signal.</summary>
        public bool IsWorldBetweenLives { get; set; }

        /// <summary>What the bridge reports as <see cref="IWorldStateBridge.IsItemViewSettled"/>
        /// — true by default (the interface's own default), set false by a test standing in for
        /// the window right after a world edge where the item view has not yet stood still.</summary>
        public bool IsItemViewSettled { get; set; } = true;

        /// <summary>Whether <see cref="TryReadLocalState"/> succeeds — false stands in for the
        /// engine having no resolvable player (mid-load), the same read the agent retains its
        /// previous state across.</summary>
        public bool ReadSucceeds { get; set; } = true;

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return ReadSucceeds;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public IReadOnlyList<(uint Possessor, string Resref, ItemIdentity Identity)> ReadPossessedItems(
            IReadOnlySet<uint> possessorIds) =>
            [.. Possessed.Select(i => (0x7FFFFFFFu, i.Resref, i.Identity))];

        public void Dispose()
        {
        }
    }

    private sealed class ScriptItemSession : ICoopSession
    {
        public List<(string PartnerTag, IReadOnlyList<MaterialisedLootItem> Items)> PublishedGrants { get; } = [];

        public uint LocalPeerId => CoopHost.HostPeerId;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public bool IsEnemyAuthority => true;

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state)
        {
        }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public void SetFlag(ulong flagId, sbyte value)
        {
        }

        public uint DropItem(
            string resref, string module, Vector3f position, ItemIdentity identity = default) => 0;

        public void RemoveItem(uint itemId)
        {
        }

        public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies)
        {
        }

        public IReadOnlyList<EnemyState> GetEnemies(string module) => [];

        public void PublishMaterialisedLoot(uint ownerEnemyId, IReadOnlyList<MaterialisedLootItem> items)
        {
        }

        public JournalStore Journal { get; } = new();

        public void PublishWorldDigest(in WorldDigest digest)
        {
        }

        public void PublishParty(IReadOnlyList<PartyMemberState> members)
        {
        }

        public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = -1, uint nodeSerial = 0, bool forced = false)
        {
        }

        public DialogueState GetDialogue() => DialogueState.Idle;

        public void PublishDialogueGrant(string partnerTag, IReadOnlyList<MaterialisedLootItem> items) =>
            PublishedGrants.Add((partnerTag, items));

        public void PublishProgression(uint gold, uint experience)
        {
        }

        public bool TryGetProgression(out uint gold, out uint experience)
        {
            gold = 0;
            experience = 0;
            return false;
        }

        public void SendChat(string text)
        {
        }

        public IReadOnlyList<ChatMessage> DrainChat() => [];

        public void SetJournalEntry(string questTag, int state) => Journal.Merge(questTag, state);

        public SessionCapabilities Capabilities { get; set; } = SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static (CoopAgent Agent, ScriptItemBridge Bridge, ScriptItemSession Session) Build(
        bool scriptItemSync = true)
    {
        ScriptItemBridge bridge = new();
        ScriptItemSession session = new();
        CoopAgent agent = new(
            bridge, session,
            new AgentSettings
            {
                CombatPumpIntervalMs = 0,
                ScriptItemSync = scriptItemSync,
                ScriptItemGrantSampleIntervalMs = 0,
            });

        return (agent, bridge, session);
    }

    [Fact]
    public void OffByDefaultPublishesNothingEvenWhenTheInventoryGrows()
    {
        (CoopAgent agent, ScriptItemBridge bridge, ScriptItemSession session) = Build(scriptItemSync: false);

        agent.PumpOnce();
        bridge.Possessed = [("g_w_lghtsbr01", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown))];
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Empty(session.PublishedGrants);
    }

    [Fact]
    public void TheFirstSampleSeedsTheBaselineWithoutPublishing()
    {
        (CoopAgent agent, ScriptItemBridge bridge, ScriptItemSession session) = Build();

        // Already carrying an item on the very first read this instance ever takes — nothing
        // to diff against yet, so this must not be reported as a "grant".
        bridge.Possessed = [("g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown))];
        agent.PumpOnce();

        Assert.Empty(session.PublishedGrants);
    }

    [Fact]
    public void AGenuineScriptGrantOutsideDialogueIsDetectedAndPublished()
    {
        (CoopAgent agent, ScriptItemBridge bridge, ScriptItemSession session) = Build();

        agent.PumpOnce(); // seeds an empty baseline

        bridge.Possessed = [("g_w_lghtsbr01", new ItemIdentity(1, 3, 3, 0x5, ItemIdentity.FlagKnown))];
        agent.PumpOnce(); // a script placed it there between the two reads

        (string partnerTag, IReadOnlyList<MaterialisedLootItem> items) = Assert.Single(session.PublishedGrants);
        Assert.Equal(string.Empty, partnerTag);
        MaterialisedLootItem item = Assert.Single(items);
        Assert.Equal("g_w_lghtsbr01", item.Resref);
        Assert.Equal(0x5u, item.Identity.Upgrades);
    }

    /// <summary>
    /// docs/research/playthrough-endar-spire.md's live-confirmed failure: a stock KOTOR 1
    /// quest item's Tag (<c>k_trask_item</c> here, echoing <c>k_computer_spike</c>) genuinely
    /// diverges from its blueprint resref (<c>g_i_medeqpmnt01</c>, echoing
    /// <c>g_i_progspike01</c>). <see cref="IGameBridge.ReadPossessedItems"/>'s contract is
    /// already resref-keyed (the field is literally called <c>Resref</c>) — this pins that a
    /// bridge answering with the item's REAL resref (what <c>KotorGameBridge.TryGetItemResref</c>
    /// now prefers, via its own spawn-record cache, over the Tag proxy — see
    /// docs/research/item-identity.md §12) is granted exactly once and forwarded under THAT
    /// resref, never the diverging tag, so the receiving `giveloot` has a blueprint it can
    /// actually spawn.
    /// </summary>
    [Fact]
    public void AScriptGrantWhoseTagDivergesFromItsResrefIsPublishedOnceUnderItsRealResref()
    {
        (CoopAgent agent, ScriptItemBridge bridge, ScriptItemSession session) = Build();

        agent.PumpOnce(); // seeds an empty baseline

        // The bridge answers with the item's REAL resref, exactly as KotorGameBridge now does
        // for a body it holds a spawn record for — never the diverging tag (k_trask_item),
        // which IGameBridge's contract never even exposes.
        bridge.Possessed = [("g_i_medeqpmnt01", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown))];
        agent.PumpOnce();

        (string partnerTag, IReadOnlyList<MaterialisedLootItem> items) = Assert.Single(session.PublishedGrants);
        Assert.Equal(string.Empty, partnerTag);
        MaterialisedLootItem item = Assert.Single(items);
        Assert.Equal("g_i_medeqpmnt01", item.Resref);

        // A further sample with nothing new must not re-report the same grant.
        agent.PumpOnce();
        Assert.Single(session.PublishedGrants);
    }

    /// <summary>
    /// The architectural limit this round's fix does NOT close, named so a regression in either
    /// direction is visible: an item a VANILLA engine script placed directly into the PC's
    /// inventory (never something this session asked the plugin to spawn) has no spawn record
    /// for <c>KotorGameBridge</c> to prefer, so it still falls back to reporting whatever the
    /// Tag proxy says — here standing in for that fallback string directly, since
    /// <see cref="IGameBridge.ReadPossessedItems"/> only ever carries one string per row. The
    /// grant must still be forwarded (better a maybe-wrong blueprint name than silence), not
    /// silently dropped — docs/research/playthrough-endar-spire.md's "Root cause, live-confirmed,
    /// not fixed (architectural, not a bug)".
    /// </summary>
    [Fact]
    public void ATagOnlyFallbackGrantIsStillPublishedNotSilentlyDropped()
    {
        (CoopAgent agent, ScriptItemBridge bridge, ScriptItemSession session) = Build();

        agent.PumpOnce(); // seeds an empty baseline

        bridge.Possessed = [("k_trask_item", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown))];
        agent.PumpOnce();

        MaterialisedLootItem item = Assert.Single(Assert.Single(session.PublishedGrants).Items);
        Assert.Equal("k_trask_item", item.Resref);
    }

    [Fact]
    public void WhileOwningAConversationNothingIsSampledOrPublished()
    {
        (CoopAgent agent, ScriptItemBridge bridge, ScriptItemSession session) = Build();

        agent.PumpOnce(); // seeds an empty baseline outside any conversation

        // Enter a conversation and gain an item mid-conversation the way a reward node would —
        // this window belongs entirely to DialoguePump.PumpDialogueItemGrants.
        bridge.Local = bridge.Local with { Flags = PlayerState.FlagInDialogue };
        bridge.Possessed = [("g_w_lghtsbr01", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown))];
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Empty(session.PublishedGrants);
    }

    [Fact]
    public void TheSampleRightAfterAConversationEndsRebaselinesInsteadOfReportingWhatItGranted()
    {
        (CoopAgent agent, ScriptItemBridge bridge, ScriptItemSession session) = Build();

        agent.PumpOnce(); // seeds an empty baseline

        // Own a conversation, gain an item (DialoguePump's own territory), then leave it.
        bridge.Local = bridge.Local with { Flags = PlayerState.FlagInDialogue };
        bridge.Possessed = [("g_w_lghtsbr01", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown))];
        agent.PumpOnce();

        bridge.Local = bridge.Local with { Flags = 0 };

        // DialoguePump debounces the engine's between-entries flicker (DialogueLingerMs =
        // 300 ms): IsOwningConversation stays true for a moment after the flag actually
        // clears, exactly like PumpDialogueItemGrants' own release window. Let it genuinely
        // lapse before asserting the rebaseline edge, the same real-clock wait
        // CreatureLivenessPumpTests uses for its own debounce window.
        System.Threading.Thread.Sleep(350);
        agent.PumpOnce(); // the falling edge: must rebaseline fresh, not diff against the stale baseline

        Assert.Empty(session.PublishedGrants);

        // And the item the conversation granted is now part of the baseline, so a further
        // sample with nothing new does not somehow report it either.
        agent.PumpOnce();
        Assert.Empty(session.PublishedGrants);
    }

    [Fact]
    public void AModuleChangeResetsTheBaselineSilently()
    {
        (CoopAgent agent, ScriptItemBridge bridge, ScriptItemSession session) = Build();

        bridge.Possessed = [("g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown))];
        agent.PumpOnce(); // baseline already carries the pistol

        // A new module: id/possession bookkeeping starts over, and this must seed quietly
        // rather than report the (already-known) pistol as a fresh grant.
        bridge.Local = bridge.Local with { Module = "tar_m03aa" };
        agent.PumpOnce();

        Assert.Empty(session.PublishedGrants);
    }

    /// <summary>
    /// Live 2026-09-02 (docs/research/death-handling.md §6.4): a game-over reload of END_M01AA
    /// into END_M01AA re-mints every object id while the module name never moves. The baseline
    /// taken in the old world diffed the reloaded inventory as freshly granted and published the
    /// lot to the peer, feeding a party-pool add loop. The bridge reports the replacement as a
    /// <see cref="IWorldStateBridge.WorldEpoch"/> edge; the pump must treat it like a module
    /// change — reseed silently — and then carry on diffing in the new world.
    /// </summary>
    [Fact]
    public void AReloadOfTheSameModuleResetsTheBaselineSilently()
    {
        (CoopAgent agent, ScriptItemBridge bridge, ScriptItemSession session) = Build();

        bridge.Possessed = [("g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown))];
        agent.PumpOnce(); // baseline carries the pistol

        // The reloaded save carries MORE than the baseline did (the autosave was taken later):
        // same module, new world. Nothing here was granted between two reads of one world.
        bridge.WorldEpoch++;
        bridge.Possessed =
        [
            ("g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
            ("g_a_clothes01", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
        ];
        agent.PumpOnce();

        Assert.Empty(session.PublishedGrants);

        // A grant in the new world is still seen.
        bridge.Possessed =
        [
            .. bridge.Possessed,
            ("g_w_lghtsbr01", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
        ];
        agent.PumpOnce();

        (_, IReadOnlyList<MaterialisedLootItem> items) = Assert.Single(session.PublishedGrants);
        Assert.Equal("g_w_lghtsbr01", Assert.Single(items).Resref);
    }

    /// <summary>
    /// Live 2026-09-02, 23:31 (a game-over reload of END_M01AA, docs/research/shared-inventory-v2.md's
    /// 2026-09-02 evening round): the host's world settled (<c>IsWorldBetweenLives</c> cleared)
    /// while the save's equipped clothes and short sword were still being created by the engine —
    /// the "settled" item sweep that built this pump's baseline missed them, and the next
    /// ordinary rebuild fifteen seconds later diffed them in as a fresh grant. While the bridge
    /// reports its item view as not yet settled, an item that "appears" must be folded silently
    /// into the baseline (never diffed, never published); once it reports settled, a genuine
    /// grant is seen normally again.
    /// </summary>
    [Fact]
    public void WhileTheItemViewIsNotSettledAnAppearingItemIsRebaselinedNotPublished()
    {
        (CoopAgent agent, ScriptItemBridge bridge, ScriptItemSession session) = Build();

        agent.PumpOnce(); // seeds an empty baseline, item view already settled

        // The world edges: the item view is not yet settled, and the two items the reload is
        // still creating "appear" in the inventory the bridge reports.
        bridge.WorldEpoch++;
        bridge.IsItemViewSettled = false;
        bridge.Possessed =
        [
            ("g_a_clothes01", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
        ];
        agent.PumpOnce();
        Assert.Empty(session.PublishedGrants);

        bridge.Possessed =
        [
            .. bridge.Possessed,
            ("g_w_shortswrd01", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
        ];
        agent.PumpOnce(); // still not settled: the sword must not be reported either

        Assert.Empty(session.PublishedGrants);

        // The item view settles: this sample must rebaseline (fold the reload's own items in)
        // rather than diff them in as a grant now that settling has just cleared.
        bridge.IsItemViewSettled = true;
        agent.PumpOnce();
        Assert.Empty(session.PublishedGrants);

        // A genuine script grant AFTER settling is still seen normally.
        bridge.Possessed =
        [
            .. bridge.Possessed,
            ("g_w_lghtsbr01", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
        ];
        agent.PumpOnce();

        (_, IReadOnlyList<MaterialisedLootItem> items) = Assert.Single(session.PublishedGrants);
        Assert.Equal("g_w_lghtsbr01", Assert.Single(items).Resref);
    }

    /// <summary>
    /// Live 2026-09-02, the client side of the same reload (docs/research/shared-inventory-v2.md
    /// §17): the client's OWN rejoin load emptied the engine's repositories seconds before the
    /// new world's player resolved. In that window the local read was failing and the epoch had
    /// not moved yet, so the pump baselined on "nothing" and, once the world was up, published
    /// the whole reloaded inventory as seven fresh grants — which the host pooled and the client
    /// then reconciled back in. A failed local read is not an observation: no baseline, no diff.
    /// </summary>
    [Fact]
    public void WhileTheLocalReadFailsNothingIsBaselinedOrPublished()
    {
        (CoopAgent agent, ScriptItemBridge bridge, ScriptItemSession session) = Build();

        bridge.Possessed = [("g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown))];
        agent.PumpOnce(); // baseline carries the pistol

        // The load tears the world down: the read fails and the inventory reads as empty.
        bridge.ReadSucceeds = false;
        bridge.Possessed = [];
        agent.PumpOnce();
        agent.PumpOnce();

        // The world comes back up with MORE than the old baseline (the autosave was later), the
        // read recovers, and the epoch edge lands in the same breath the player resolves.
        bridge.ReadSucceeds = true;
        bridge.WorldEpoch++;
        bridge.Possessed =
        [
            ("g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
            ("g_a_clothes01", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
            ("g_w_fraggren01", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
        ];
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Empty(session.PublishedGrants);
    }

    /// <summary>The bridge's own between-lives verdict gates the same way, independently of
    /// whether the local read happens to succeed that tick (the engine keeps answering reads
    /// for a moment after the plugin has already seen the teardown).</summary>
    [Fact]
    public void WhileTheWorldIsBetweenLivesNothingIsBaselinedOrPublished()
    {
        (CoopAgent agent, ScriptItemBridge bridge, ScriptItemSession session) = Build();

        bridge.Possessed = [("g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown))];
        agent.PumpOnce();

        bridge.IsWorldBetweenLives = true;
        bridge.Possessed = [];
        agent.PumpOnce();

        bridge.Possessed =
        [
            ("g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
            ("g_a_clothes01", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
        ];
        agent.PumpOnce();
        Assert.Empty(session.PublishedGrants);

        // The world is up: this is the first observation of the new world and it must reseed
        // (via the epoch edge) rather than diff against the pre-load baseline...
        bridge.IsWorldBetweenLives = false;
        bridge.WorldEpoch++;
        agent.PumpOnce();
        Assert.Empty(session.PublishedGrants);

        // ...and a genuine grant afterwards is still seen.
        bridge.Possessed =
        [
            .. bridge.Possessed,
            ("g_w_lghtsbr01", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
        ];
        agent.PumpOnce();
        (_, IReadOnlyList<MaterialisedLootItem> items) = Assert.Single(session.PublishedGrants);
        Assert.Equal("g_w_lghtsbr01", Assert.Single(items).Resref);
    }

    /// <summary>A blip with NO epoch edge afterwards (a transient read failure in a world that
    /// was never replaced) resumes diffing from the baseline it had — the baseline was left
    /// alone, not dropped, so a grant that landed during the blip is still reported once.</summary>
    [Fact]
    public void ATransientReadFailureKeepsTheBaselineItHad()
    {
        (CoopAgent agent, ScriptItemBridge bridge, ScriptItemSession session) = Build();

        bridge.Possessed = [("g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown))];
        agent.PumpOnce();

        bridge.ReadSucceeds = false;
        agent.PumpOnce();

        bridge.ReadSucceeds = true;
        bridge.Possessed =
        [
            .. bridge.Possessed,
            ("g_w_lghtsbr01", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
        ];
        agent.PumpOnce();

        (_, IReadOnlyList<MaterialisedLootItem> items) = Assert.Single(session.PublishedGrants);
        Assert.Equal("g_w_lghtsbr01", Assert.Single(items).Resref);
    }
}
