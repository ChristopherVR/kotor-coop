using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using KotorCoop.Session.Pumps;
using Xunit;

namespace KotorCoop.Tests;

public sealed class DialogueGrantPumpTests
{
    private const string Module = "end_m01aa";

    private sealed class Bridge : IGameBridge
    {
        public string Description => "dialogue-grant-test";
        public bool IsAttached => true;
        public bool CanWriteRemotes => true;
        public string Module { get; set; } = DialogueGrantPumpTests.Module;
        public int WorldEpoch { get; set; }
        public bool IsWorldBetweenLives { get; set; }
        public bool IsItemViewSettled { get; set; } = true;
        public bool IsItemResolverReady { get; set; } = true;
        public bool LastPossessedItemReadWasComplete { get; set; } = true;
        public bool InDialogue { get; set; }
        public int ReadItemsCalls { get; private set; }
        public Action? BeforeReadPossessedItems { get; set; }
        public List<(string Resref, ItemIdentity Identity)> Possessed { get; set; } = [];
        public List<(string Resref, ItemIdentity Identity)>? PersonalPossessed { get; set; }

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(
                Vector3f.Zero, 0f, Module, 20, 0,
                Flags: InDialogue ? PlayerState.FlagInDialogue : 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool TryReadLocalDialogue(out string speaker, out string line)
        {
            speaker = InDialogue ? "dan13_dorak" : string.Empty;
            line = InDialogue ? "test" : string.Empty;
            return InDialogue;
        }

        public bool TryReadLocalDialogueFollow(
            out string partnerTag, out uint replySerial, out int replyIndex, out byte[] pickLog,
            out int nodeIndex, out uint nodeSerial, out bool forced)
        {
            partnerTag = InDialogue ? "dan13_dorak" : string.Empty;
            replySerial = 0;
            replyIndex = 0;
            pickLog = [];
            nodeIndex = InDialogue ? 57 : DialogueState.NoNode;
            nodeSerial = 0;
            forced = false;
            return InDialogue;
        }

        public bool WriteRemoteDialogue(
            bool followingPeer, string partnerTag, uint replySerial, int replyIndex,
            IReadOnlyList<byte>? pickLog = null, int nodeIndex = DialogueState.NoNode,
            uint nodeSerial = 0, bool forced = false) => true;

        public IReadOnlyList<(uint Possessor, string Resref, ItemIdentity Identity)> ReadPossessedItems(
            IReadOnlySet<uint> possessorIds)
        {
            ReadItemsCalls++;
            BeforeReadPossessedItems?.Invoke();
            return [.. Possessed.Select(i => (AgentWellKnownIds.LocalPlayerObjectId, i.Resref, i.Identity))];
        }

        public IReadOnlyList<(uint Possessor, string Resref, ItemIdentity Identity)> ReadPersonalPossessedItems(
            IReadOnlySet<uint> possessorIds)
        {
            ReadItemsCalls++;
            BeforeReadPossessedItems?.Invoke();
            return [.. (PersonalPossessed ?? Possessed)
                .Select(i => (AgentWellKnownIds.LocalPlayerObjectId, i.Resref, i.Identity))];
        }

        public void Dispose()
        {
        }
    }

    private sealed class Session : ICoopSession
    {
        public List<(string PartnerTag, IReadOnlyList<MaterialisedLootItem> Items)> Grants { get; } = [];
        public uint LocalPeerId => CoopHost.HostPeerId;
        public bool IsConnected => true;
        public string LocalName => "test";
        public EventFlagStore Flags { get; } = new();
        public GroundItemStore Items { get; } = new();
        public bool IsEnemyAuthority => true;
        public JournalStore Journal { get; } = new();
        public SessionCapabilities Capabilities => SessionCapabilities.All;
        public CompatibilityVerdict? Compatibility => null;
        public SessionStats GetStats() => new();
        public void PublishLocalState(in PlayerState state) { }
        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];
        public void SetFlag(ulong flagId, sbyte value) { }
        public uint DropItem(string resref, string module, Vector3f position, ItemIdentity identity = default) => 0;
        public void RemoveItem(uint itemId) { }
        public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies) { }
        public IReadOnlyList<EnemyState> GetEnemies(string module) => [];
        public void PublishMaterialisedLoot(uint ownerEnemyId, IReadOnlyList<MaterialisedLootItem> items) { }
        public void PublishWorldDigest(in WorldDigest digest) { }
        public void PublishParty(IReadOnlyList<PartyMemberState> members) { }
        public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];
        public void PublishDialogue(bool active, string speakerName, string line, string partnerTag = "",
            uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = DialogueState.NoNode, uint nodeSerial = 0, bool forced = false) { }
        public DialogueState GetDialogue() => DialogueState.Idle;
        public void PublishDialogueGrant(string partnerTag, IReadOnlyList<MaterialisedLootItem> items) =>
            Grants.Add((partnerTag, items));
        public void PublishProgression(uint gold, uint experience) { }
        public bool TryGetProgression(out uint gold, out uint experience)
        {
            gold = 0;
            experience = 0;
            return false;
        }
        public void SendChat(string text) { }
        public IReadOnlyList<ChatMessage> DrainChat() => [];
        public void SetJournalEntry(string questTag, int state) => Journal.Merge(questTag, state);
        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static (DialoguePump Pump, Bridge Bridge, Session Session) Build()
    {
        Bridge bridge = new();
        Session session = new();
        DialoguePump pump = new(bridge, session, new AgentSettings { DialogueFastJoin = false }, _ => { });
        return (pump, bridge, session);
    }

    private static PlayerState State(Bridge bridge) =>
        new(
            Vector3f.Zero, 0f, bridge.Module, 20,
            0, Flags: bridge.InDialogue ? PlayerState.FlagInDialogue : 0);

    private static ItemIdentity Identity(uint id) =>
        new((ushort)id, 0, 0, 0, ItemIdentity.FlagKnown);

    [Fact]
    public void StableConversationCloseStillPublishesAReward()
    {
        (DialoguePump pump, Bridge bridge, Session session) = Build();
        bridge.Possessed = [("g_w_shortswrd01", Identity(9))];
        pump.PumpDialogue(State(bridge), SessionCapabilities.All); // establish the old-world baseline
        session.Grants.Clear();
        bridge.InDialogue = true;
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);

        bridge.Possessed = [("g_w_blstrpstl001", Identity(1))];
        bridge.InDialogue = false;
        Thread.Sleep(350);
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);

        MaterialisedLootItem item = Assert.Single(Assert.Single(session.Grants).Items);
        Assert.Equal("g_w_blstrpstl001", item.Resref);
    }

    [Fact]
    public void ConversationGrantPumpLeavesSharedPoolItemsToPoolChannel()
    {
        (DialoguePump pump, Bridge bridge, Session session) = Build();
        pump.PumpDialogue(State(bridge), SessionCapabilities.All); // establish empty baseline

        bridge.InDialogue = true;
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);

        // The item exists in the broad PC possession view but is also in the shared pool,
        // whose PartyPool V2 snapshot/intents are the authoritative replication channel.
        bridge.Possessed = [("g_w_lghtsbr04", Identity(4))];
        bridge.PersonalPossessed = [];
        bridge.InDialogue = false;
        Thread.Sleep(350);
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);

        Assert.Empty(session.Grants);
    }

    [Fact]
    public void AnIncompletePossessedReadDoesNotBecomeADialogueRewardOnRecovery()
    {
        (DialoguePump pump, Bridge bridge, Session session) = Build();
        bridge.Possessed = [("g_w_shortswrd01", Identity(9))];
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);
        session.Grants.Clear();
        bridge.InDialogue = true;
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);

        bridge.InDialogue = false;
        bridge.Possessed =
        [
            ("g_w_shortswrd01", Identity(9)),
            ("g_w_blstrpstl001", Identity(1)),
        ];
        bridge.LastPossessedItemReadWasComplete = false;
        Thread.Sleep(350);
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);
        Assert.Empty(session.Grants);

        bridge.LastPossessedItemReadWasComplete = true;
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);
        Assert.Single(session.Grants);
        IReadOnlyList<MaterialisedLootItem> recovered = session.Grants[0].Items;
        Assert.Single(recovered);
        Assert.Equal("g_w_blstrpstl001", recovered[0].Resref);
    }

    [Fact]
    public void SameWorldUnsettledCloseDefersThenPublishesTheSettledReward()
    {
        (DialoguePump pump, Bridge bridge, Session session) = Build();
        bridge.InDialogue = true;
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);

        bridge.InDialogue = false;
        bridge.IsItemViewSettled = false;
        Thread.Sleep(350);
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);
        Assert.Empty(session.Grants);

        bridge.IsItemViewSettled = true;
        bridge.Possessed = [("g_i_pazaak", Identity(2))];
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);

        MaterialisedLootItem item = Assert.Single(Assert.Single(session.Grants).Items);
        Assert.Equal("g_i_pazaak", item.Resref);

    }

    [Fact]
    public void BaselineReadCrossingEpochIsDiscardedBeforeTheReloadedInventoryArrives()
    {
        (DialoguePump pump, Bridge bridge, Session session) = Build();
        bool invalidated = false;
        bridge.BeforeReadPossessedItems = () =>
        {
            if (invalidated)
            {
                return;
            }

            invalidated = true;
            bridge.WorldEpoch++;
            bridge.IsItemViewSettled = false;
        };
        bridge.InDialogue = true;
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);

        // The callback invalidated the first read, so this complete post-reload inventory must
        // seed the new baseline rather than be treated as four dialogue rewards.
        bridge.BeforeReadPossessedItems = null;
        bridge.IsItemViewSettled = true;
        bridge.Possessed =
        [
            ("g_i_pazaak", Identity(2)),
            ("g_a_clothes01", Identity(3)),
            ("g_w_shortsword", Identity(4)),
            ("g_w_blstrpstl001", Identity(5)),
        ];
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);

        bridge.InDialogue = false;
        Thread.Sleep(350);
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);

        Assert.Empty(session.Grants);
    }

    [Fact]
    public void DeferredDialogueCloseKeepsScriptPumpOutUntilDialogueGrantFinishes()
    {
        Bridge bridge = new();
        Session session = new();
        CoopAgent agent = new(
            bridge, session,
            new AgentSettings
            {
                DialogueFastJoin = false,
                CombatPumpIntervalMs = 0,
                HeavyPumpIntervalMs = int.MaxValue,
                ScriptItemGrantSampleIntervalMs = 0,
            });

        bridge.InDialogue = true;
        agent.PumpOnce();

        bridge.InDialogue = false;
        bridge.IsItemViewSettled = false;
        Thread.Sleep(350);
        agent.PumpOnce();

        bridge.IsItemViewSettled = true;
        bridge.Possessed = [("g_i_pazaak", Identity(2))];
        agent.PumpOnce();

        MaterialisedLootItem item = Assert.Single(Assert.Single(session.Grants).Items);
        Assert.Equal("g_i_pazaak", item.Resref);

        // The script pump now owns a fresh post-dialogue baseline. A genuinely later script
        // grant is still detected, while the dialogue reward is not broadcast a second time.
        bridge.Possessed =
        [
            ("g_i_pazaak", Identity(2)),
            ("g_w_blstrpstl001", Identity(3)),
        ];
        agent.PumpOnce();
        Assert.Equal(2, session.Grants.Count);
        Assert.Equal("g_w_blstrpstl001", Assert.Single(session.Grants[1].Items).Resref);
        Assert.Equal(string.Empty, session.Grants[1].PartnerTag);
    }

    [Fact]
    public void ModuleReplacementAndUnsettledInventoryNeverBecomeDialogueRewards()
    {
        (DialoguePump pump, Bridge bridge, Session session) = Build();
        bridge.InDialogue = true;
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);

        bridge.InDialogue = false;
        bridge.WorldEpoch++;
        bridge.IsWorldBetweenLives = true;
        bridge.Possessed =
        [
            ("g_i_pazaak", Identity(2)),
            ("g_a_clothes01", Identity(3)),
            ("g_w_shortsword", Identity(4)),
            ("g_w_blstrpstl001", Identity(5)),
        ];
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);

        bridge.IsWorldBetweenLives = false;
        bridge.IsItemViewSettled = false;
        Thread.Sleep(350);
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);

        bridge.IsItemViewSettled = true;
        pump.PumpDialogue(State(bridge), SessionCapabilities.All);

        Assert.Empty(session.Grants);
        Assert.Equal(1, bridge.ReadItemsCalls);
    }
}
