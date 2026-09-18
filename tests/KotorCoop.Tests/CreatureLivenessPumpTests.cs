using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The agent's half of creature-liveness reconciliation: the enemy owner scans the FULL area
/// (not the 32-slot combat census) and publishes it; a mirroring peer hands whatever it
/// received to the plugin, ordered so a capacity truncation never drops the entries that
/// actually need acting on. See docs/research/creature-liveness.md.
/// </summary>
public class CreatureLivenessPumpTests
{
    private const string Module = "tar_m03aa";

    private sealed class LivenessBridge : IGameBridge
    {
        public string LocalModule { get; set; } = Module;

        /// <summary>The LIVE module read. Null mirrors <see cref="LocalModule"/> — the
        /// steady state where retained and live agree.</summary>
        public string? CurrentModule { get; set; }

        public List<CreatureLivenessEntry> Scan { get; set; } = [];

        public List<CreatureLivenessEntry>? LastWritten { get; private set; }

        public uint AnomalyCount { get; set; }

        public string Description => "liveness-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsEnemySync => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, LocalModule, 20, 0);
            return true;
        }

        public bool TryReadCurrentModule(out string module)
        {
            module = CurrentModule ?? LocalModule;
            return module.Length > 0;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public List<CreatureLivenessEntry> ReadCreatureLiveness() => Scan;

        public bool TryWriteCreatureLiveness(IReadOnlyList<CreatureLivenessEntry> entries)
        {
            LastWritten = [.. entries];
            return true;
        }

        public uint ReadLivenessAnomalyCount() => AnomalyCount;

        public void Dispose()
        {
        }
    }

    private sealed class LivenessSession : ICoopSession
    {
        public bool EnemyAuthority { get; set; }

        public List<(string Module, IReadOnlyList<CreatureLivenessEntry> Entries)> Published { get; } = [];

        public IReadOnlyList<CreatureLivenessEntry> ToDeliver { get; set; } = [];

        public string DeliverModule { get; set; } = Module;

        public uint ReportedAnomalies { get; private set; }

        public uint LocalPeerId => EnemyAuthority ? CoopHost.HostPeerId : 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public JournalStore Journal { get; } = new();

        public bool IsEnemyAuthority => EnemyAuthority;

        public SessionCapabilities Capabilities => SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public void PublishCreatureLiveness(string module, IReadOnlyList<CreatureLivenessEntry> entries) =>
            Published.Add((module, entries));

        public IReadOnlyList<CreatureLivenessEntry> GetCreatureLiveness(string module) =>
            string.Equals(module, DeliverModule, StringComparison.OrdinalIgnoreCase) ? ToDeliver : [];

        public void ReportLivenessAnomalyCount(uint count) => ReportedAnomalies = count;

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state) { }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public void SetFlag(ulong flagId, sbyte value) { }

        public uint DropItem(
            string resref, string module, Vector3f position, ItemIdentity identity = default) => 0;

        public void RemoveItem(uint itemId) { }

        public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies) { }

        public IReadOnlyList<EnemyState> GetEnemies(string module) => [];

        public void PublishWorldDigest(in WorldDigest digest) { }

        public void PublishParty(IReadOnlyList<PartyMemberState> members) { }

        public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = DialogueState.NoNode, uint nodeSerial = 0, bool forced = false) { }

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

        public void SetJournalEntry(string questTag, int state) { }

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() { }
    }

    private static (CoopAgent Agent, LivenessBridge Bridge, LivenessSession Session) Build(bool authority)
    {
        LivenessBridge bridge = new();
        LivenessSession session = new() { EnemyAuthority = authority };
        return (new CoopAgent(bridge, session, new AgentSettings()), bridge, session);
    }

    [Fact]
    public void TheOwnerScansAndPublishesTheFullAreaNotJustTheCensus()
    {
        (CoopAgent agent, LivenessBridge bridge, LivenessSession session) = Build(authority: true);
        bridge.Scan =
        [
            new CreatureLivenessEntry(0x14, CreatureLivenessState.Alive),
            new CreatureLivenessEntry(0x35, CreatureLivenessState.Dead),
        ];

        agent.PumpOnce();

        (string module, IReadOnlyList<CreatureLivenessEntry> entries) = Assert.Single(session.Published);
        Assert.Equal(Module, module);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.ObjectId == 0x14 && e.State == CreatureLivenessState.Alive);
        Assert.Contains(entries, e => e.ObjectId == 0x35 && e.State == CreatureLivenessState.Dead);
    }

    [Fact]
    public void ASkewedCensusLabelSuppressesTheOwnersLivenessPublish()
    {
        // Same defect class ASkewedCensusLabelSuppressesTheOwnersEnemyPublish guards: the
        // retained module and the live world can disagree for a few seconds around a
        // transition, and a mismatch must suppress the publish rather than label a fresh
        // scan with a module it was not read in.
        (CoopAgent agent, LivenessBridge bridge, LivenessSession session) = Build(authority: true);
        bridge.CurrentModule = "end_m01aa";
        bridge.Scan = [new CreatureLivenessEntry(0x14, CreatureLivenessState.Alive)];

        agent.PumpOnce();

        Assert.Empty(session.Published);
    }

    [Fact]
    public void AnIdThatDisappearsFromTheFullScanIsDerivedAsGone()
    {
        // Unlike the 32-slot combat census (capped, distance-shed), this scan is the whole
        // area — so an id remembered from an earlier pass that no longer shows up at all was
        // genuinely destroyed, not merely far from a player. Uses a real short sleep to clear
        // the design's own 2-second scan throttle rather than a mock clock, since the throttle
        // is measured on Environment.TickCount64.
        (CoopAgent agent, LivenessBridge bridge, LivenessSession session) = Build(authority: true);
        bridge.Scan = [new CreatureLivenessEntry(0x35, CreatureLivenessState.Alive)];
        agent.PumpOnce();

        Assert.Contains(session.Published[^1].Entries, e => e.ObjectId == 0x35 && e.State == CreatureLivenessState.Alive);

        System.Threading.Thread.Sleep(2100);
        bridge.Scan = [];
        agent.PumpOnce();

        (string module, IReadOnlyList<CreatureLivenessEntry> entries) = session.Published[^1];
        Assert.Equal(Module, module);
        Assert.Contains(entries, e => e.ObjectId == 0x35 && e.State == CreatureLivenessState.Gone);
    }

    [Fact]
    public void TheMirrorHandsWhateverItReceivedToThePluginOrderedActionableFirst()
    {
        (CoopAgent agent, LivenessBridge bridge, LivenessSession session) = Build(authority: false);
        session.DeliverModule = Module;
        session.ToDeliver =
        [
            new CreatureLivenessEntry(1, CreatureLivenessState.Alive),
            new CreatureLivenessEntry(2, CreatureLivenessState.Alive),
            new CreatureLivenessEntry(0x35, CreatureLivenessState.Dead),
            new CreatureLivenessEntry(0x99, CreatureLivenessState.Gone),
        ];

        agent.PumpOnce();

        Assert.NotNull(bridge.LastWritten);
        Assert.Equal(4, bridge.LastWritten!.Count);

        // Dead/gone entries lead the written page — a capacity truncation upstream (this test
        // has room for all four, but LivenessBlockPage's own tests cover the truncating case)
        // must never be able to drop them behind a long alive tail.
        Assert.Equal(CreatureLivenessState.Dead, bridge.LastWritten[0].State);
        Assert.Equal(CreatureLivenessState.Gone, bridge.LastWritten[1].State);
    }

    [Fact]
    public void TheMirrorReportsThePluginsAnomalyCountIntoTheSession()
    {
        (CoopAgent agent, LivenessBridge bridge, LivenessSession session) = Build(authority: false);
        bridge.AnomalyCount = 3;

        agent.PumpOnce();

        Assert.Equal(3u, session.ReportedAnomalies);
    }

    [Fact]
    public void TheMirrorWritesNothingWhilePlayersAreInDifferentModules()
    {
        (CoopAgent agent, LivenessBridge bridge, LivenessSession session) = Build(authority: false);
        bridge.LocalModule = "end_m01aa";
        session.DeliverModule = Module; // the host published for tar_m03aa
        session.ToDeliver = [new CreatureLivenessEntry(0x35, CreatureLivenessState.Dead)];

        agent.PumpOnce();

        Assert.Null(bridge.LastWritten);
    }

    [Fact]
    public void AFallbackModuleNameSuppressesBothDirections()
    {
        (CoopAgent agent, LivenessBridge bridge, LivenessSession ownerSession) = Build(authority: true);
        bridge.LocalModule = "area1F";
        bridge.Scan = [new CreatureLivenessEntry(0x35, CreatureLivenessState.Dead)];

        agent.PumpOnce();

        Assert.Empty(ownerSession.Published);

        (CoopAgent mirrorAgent, LivenessBridge mirrorBridge, LivenessSession mirrorSession) = Build(authority: false);
        mirrorBridge.LocalModule = "area1F";
        mirrorSession.DeliverModule = "area1F";
        mirrorSession.ToDeliver = [new CreatureLivenessEntry(0x35, CreatureLivenessState.Dead)];

        mirrorAgent.PumpOnce();

        Assert.Null(mirrorBridge.LastWritten);
    }
}
