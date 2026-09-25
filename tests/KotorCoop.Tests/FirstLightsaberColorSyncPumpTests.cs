using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// End-to-end (through a real <see cref="CoopAgent"/> tick) coverage of
/// docs/research/class-and-power-grants.md §11's wiring:
/// <c>KotorCoop.Session.Pumps.FirstLightsaberBuildPump</c> detecting a local equip-slot edge,
/// publishing the party-wide flag, and building this character's own colour once the flag is
/// set — behind <see cref="AgentSettings.FirstSaberColorSync"/>.
/// </summary>
public class FirstLightsaberColorSyncPumpTests
{
    private const string Module = "danm13";
    private const string Blue = "g_w_lghtsbr01";   // Guardian (3)
    private const string Yellow = "g_w_lghtsbr04"; // Sentinel (5)

    private sealed class ClassAwareBridge : IGameBridge
    {
        public PlayerState Local { get; set; } = new(Vector3f.Zero, 0f, Module, 20, 0);

        public List<int> ClassIds { get; set; } = [];

        public bool IsInDialogue { get; set; }

        public string Description => "first-saber-build-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public IReadOnlyList<int> ReadLocalClassIds() => ClassIds;

        public void Dispose()
        {
        }
    }

    private sealed class StubSession : ICoopSession
    {
        public EventFlagStore Flags { get; } = new();

        public void SetFlag(ulong flagId, sbyte value) => Flags.Set(flagId, value, LocalPeerId);

        public uint LocalPeerId => 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public GroundItemStore Items { get; } = new();

        public bool IsEnemyAuthority => false;

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state)
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

        public JournalStore Journal { get; } = new();

        public void PublishWorldDigest(in WorldDigest digest) { }

        public void PublishParty(IReadOnlyList<PartyMemberState> members) { }

        public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = -1, uint nodeSerial = 0, bool forced = false) { }

        public DialogueState GetDialogue() => DialogueState.Idle;

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

        public SessionCapabilities Capabilities { get; set; } = SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static (CoopAgent Agent, ClassAwareBridge Bridge, StubSession Session) Build(bool firstSaberColorSync)
    {
        ClassAwareBridge bridge = new();
        StubSession session = new();

        CoopAgent agent = new(
            bridge, session,
            new AgentSettings { CombatPumpIntervalMs = 0, FirstSaberColorSync = firstSaberColorSync });

        return (agent, bridge, session);
    }

    [Fact]
    public void OffByDefaultNeverPublishesOrBuildsAnything()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: false);
        bridge.Local = bridge.Local with { EquippedRightWeapon = Blue }; // the builder's own equip
        agent.PumpOnce();

        Assert.False(session.Flags.TryGet(FirstLightsaberColorRule.FirstSaberBuiltFlagId, out _));
        Assert.False(agent.TryDequeuePendingFirstSaberBuild(out _));
    }

    [Fact]
    public void TheBuildersOwnEquipEdgePublishesTheFlagAndBuildsNothingForThemselves()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [3]; // Guardian - matches what they equipped
        bridge.Local = bridge.Local with { EquippedRightWeapon = Blue };

        agent.PumpOnce();

        Assert.True(session.Flags.TryGet(FirstLightsaberColorRule.FirstSaberBuiltFlagId, out sbyte value));
        Assert.NotEqual(0, value);
        // They already have one (it is equipped) - never queue a build for themselves.
        Assert.False(agent.TryDequeuePendingFirstSaberBuild(out _));
    }

    [Fact]
    public void AnotherPlayerWithNoSaberYetBuildsTheirOwnClassColourOnceTheFlagIsSet()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [5]; // Sentinel
        // Nothing equipped yet - the flag arrives from a peer's build (simulated directly here,
        // since a real peer relay is exercised by SessionIntegrationTests/EventFlagStore's own
        // suite, not this file).
        session.Flags.Set(FirstLightsaberColorRule.FirstSaberBuiltFlagId, 1, originPeerId: 1);

        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingFirstSaberBuild(out string resref));
        Assert.Equal(Yellow, resref);
    }

    [Fact]
    public void APlayerWithNoJediClassYetBuildsNothingButKeepsWatching()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [0]; // Soldier only, no Jedi class yet
        session.Flags.Set(FirstLightsaberColorRule.FirstSaberBuiltFlagId, 1, originPeerId: 1);

        agent.PumpOnce();
        Assert.False(agent.TryDequeuePendingFirstSaberBuild(out _));

        // A class grant lands later - the very next tick should now build it.
        bridge.ClassIds = [3]; // Guardian
        agent.PumpOnce();
        Assert.True(agent.TryDequeuePendingFirstSaberBuild(out string resref));
        Assert.Equal(Blue, resref);
    }

    [Fact]
    public void APlayerWhoAlreadyHasAFirstSaberEquippedNeverBuildsASecondOne()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [5]; // Sentinel
        bridge.Local = bridge.Local with { EquippedRightWeapon = Yellow }; // built it themselves already
        session.Flags.Set(FirstLightsaberColorRule.FirstSaberBuiltFlagId, 1, originPeerId: 1);

        agent.PumpOnce();

        Assert.False(agent.TryDequeuePendingFirstSaberBuild(out _));
    }

    [Fact]
    public void NoBuildIsQueuedWhileInDialogueOrMidTransition()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [5];
        bridge.IsInDialogue = true;
        session.Flags.Set(FirstLightsaberColorRule.FirstSaberBuiltFlagId, 1, originPeerId: 1);

        agent.PumpOnce();
        Assert.False(agent.TryDequeuePendingFirstSaberBuild(out _));

        bridge.IsInDialogue = false;
        agent.PumpOnce();
        Assert.True(agent.TryDequeuePendingFirstSaberBuild(out string resref));
        Assert.Equal(Yellow, resref);
    }

    [Fact]
    public void ARepeatedFlagAfterTheFirstBuildLandedIsNotDoubled()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [5];
        session.Flags.Set(FirstLightsaberColorRule.FirstSaberBuiltFlagId, 1, originPeerId: 1);

        agent.PumpOnce();
        Assert.True(agent.TryDequeuePendingFirstSaberBuild(out string resref));
        Assert.Equal(Yellow, resref);

        // Simulate the build having actually landed (a real rejoin/resync would re-read this
        // from the live equip slot).
        bridge.Local = bridge.Local with { EquippedRightWeapon = Yellow };

        // The flag is already true and stays true (a resync would simply re-affirm it) - a
        // second pump must not queue a second build.
        agent.PumpOnce();
        Assert.False(agent.TryDequeuePendingFirstSaberBuild(out _));
    }
}
