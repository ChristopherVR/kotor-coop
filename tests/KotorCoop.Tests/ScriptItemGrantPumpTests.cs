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

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
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

        public void SetFlag(uint flagId, sbyte value)
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
}
