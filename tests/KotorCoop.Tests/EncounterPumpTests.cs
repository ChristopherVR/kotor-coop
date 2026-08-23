using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The agent's half of client-triggered encounters: a peer that does not own enemies runs
/// the watch and reports what it is standing in; the owner runs no watch and forwards what
/// the session hands it into the plugin. The engine calls are the plugin's and cannot run
/// outside the game — everything that decides whether to make them is here.
/// </summary>
public class EncounterPumpTests
{
    private const string Module = "tar_m03aa";

    private sealed class EncounterBridge : IGameBridge
    {
        public string LocalModule { get; set; } = Module;

        /// <summary>The LIVE module read (IGameBridge.TryReadCurrentModule). Null mirrors
        /// <see cref="LocalModule"/> — the steady state, where retained and live agree.</summary>
        public string? CurrentModule { get; set; }

        public List<uint> Inside { get; } = [];

        public List<(uint EncounterId, uint PeerId)> Fired { get; } = [];

        public List<bool> WatchWrites { get; } = [];

        public int DisableCalls { get; private set; }

        public string Description => "encounter-test";

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

        public int DisableLocalEncounters()
        {
            DisableCalls++;
            return 0;
        }

        public void SetEncounterWatch(bool enabled) => WatchWrites.Add(enabled);

        public IReadOnlyList<uint> ReadEnteredEncounters() => Inside;

        public bool TryRequestEncounterFire(IReadOnlyList<(uint EncounterId, uint PeerId)> requests)
        {
            Fired.AddRange(requests);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubSession : ICoopSession
    {
        private readonly EncounterTriggerStore _store = new();

        public bool EnemyAuthority { get; set; }

        public List<(string Module, IReadOnlyList<uint> Ids)> Published { get; } = [];

        public long Now { get; set; } = 1000;

        public uint LocalPeerId => EnemyAuthority ? CoopHost.HostPeerId : 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public JournalStore Journal { get; } = new();

        public bool IsEnemyAuthority => EnemyAuthority;

        public SessionCapabilities Capabilities => SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        /// <summary>Lets a test stand in for a client's datagram arriving at the host.</summary>
        public void ReceiveFromPeer(uint peerId, string module, params uint[] ids) =>
            _store.Report(new EncounterTriggerPacket(peerId, module, ids), Now);

        public void PublishEncounterTriggers(string module, IReadOnlyList<uint> encounterIds) =>
            Published.Add((module, encounterIds));

        public IReadOnlyList<EncounterFireRequest> GetEncounterFireRequests(string module) =>
            _store.Collect(module, Now);

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state) { }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public void SetFlag(uint flagId, sbyte value) { }

        public uint DropItem(
            string resref, string module, Vector3f position, ItemIdentity identity = default) => 0;

        public void RemoveItem(uint itemId) { }

        public List<string> PublishedEnemyModules { get; } = [];

        public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies) =>
            PublishedEnemyModules.Add(module);

        public IReadOnlyList<EnemyState> GetEnemies(string module) => [];

        public void ReportEnemies(string module, IReadOnlyList<EnemyState> enemies) { }

        public IReadOnlyList<EnemyState> GetEnemyReports(string module) => [];

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

        public void SetJournalEntry(string questTag, int state) { }

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() { }
    }

    private static (CoopAgent Agent, EncounterBridge Bridge, StubSession Session) Build(bool authority)
    {
        EncounterBridge bridge = new();
        StubSession session = new() { EnemyAuthority = authority };
        return (new CoopAgent(bridge, session, new AgentSettings()), bridge, session);
    }

    [Fact]
    public void ANonOwnerTurnsTheWatchOnAndReportsWhatItIsStandingIn()
    {
        (CoopAgent agent, EncounterBridge bridge, StubSession session) = Build(authority: false);
        bridge.Inside.Add(0x11B);

        agent.PumpOnce();

        Assert.Contains(true, bridge.WatchWrites);
        (string module, IReadOnlyList<uint> ids) = Assert.Single(session.Published);
        Assert.Equal(Module, module);
        Assert.Equal<uint[]>([0x11B], [.. ids]);
    }

    [Fact]
    public void ANonOwnerStillDisarmsItsOwnEncounters()
    {
        // The watch is an addition, not a replacement: a client that could still spawn
        // locally would drift its object-id counter, which is the whole reason the
        // encounters were switched off.
        (CoopAgent agent, EncounterBridge bridge, _) = Build(authority: false);

        agent.PumpOnce();

        Assert.Equal(1, bridge.DisableCalls);
    }

    [Fact]
    public void TheOwnerRunsNoWatchAndDisarmsNothing()
    {
        // Its own engine fires its own encounters; there is nobody to ask.
        (CoopAgent agent, EncounterBridge bridge, StubSession session) = Build(authority: true);

        agent.PumpOnce();

        Assert.Contains(false, bridge.WatchWrites);
        Assert.DoesNotContain(true, bridge.WatchWrites);
        Assert.Empty(session.Published);
        Assert.Equal(0, bridge.DisableCalls);
    }

    [Fact]
    public void TheOwnerForwardsAPeersEncounterToThePlugin()
    {
        (CoopAgent agent, EncounterBridge bridge, StubSession session) = Build(authority: true);
        session.ReceiveFromPeer(5, Module, 0x11B);

        agent.PumpOnce();

        Assert.Equal([(0x11Bu, 5u)], bridge.Fired);
    }

    [Fact]
    public void TheOwnerWritesNothingWhileNobodyIsAsking()
    {
        (CoopAgent agent, EncounterBridge bridge, _) = Build(authority: true);

        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Empty(bridge.Fired);
    }

    [Fact]
    public void TheRequestRegionIsClearedOnceAfterThePeerWalksOut()
    {
        // The plugin acts on whatever the region says, so the last write after a peer stops
        // asking has to be an empty one — otherwise the region keeps offering an encounter
        // on behalf of somebody standing somewhere else entirely.
        (CoopAgent agent, EncounterBridge bridge, StubSession session) = Build(authority: true);
        session.ReceiveFromPeer(5, Module, 0x11B);
        agent.PumpOnce();

        session.ReceiveFromPeer(5, Module);
        agent.PumpOnce();
        agent.PumpOnce();

        // One entry from the first pump, one clearing write, and nothing after that.
        Assert.Equal([(0x11Bu, 5u)], bridge.Fired);
    }

    [Fact]
    public void ASkewedCensusLabelSuppressesTheOwnersEnemyPublish()
    {
        // `local.Module` is retained across a failed read while the census reads the live
        // world, so around a module transition the label and the payload can belong to
        // different worlds. Measured live: the owner arrived in tar_m03aa and for a few
        // seconds published a fresh Taris census labelled END_M01AA — which passed the
        // module gate of a client genuinely standing on the Endar Spire and drove Taris
        // ids onto its creatures. A label that disagrees with the live module read must
        // suppress the publish.
        (CoopAgent agent, EncounterBridge bridge, StubSession session) = Build(authority: true);
        bridge.CurrentModule = "end_m01aa";

        agent.PumpOnce();

        Assert.Empty(session.PublishedEnemyModules);
    }

    [Fact]
    public void ACleanCensusLabelPublishesTheOwnersEnemies()
    {
        // The control for the test above: retained and live agree, the census goes out.
        (CoopAgent agent, _, StubSession session) = Build(authority: true);

        agent.PumpOnce();

        Assert.Contains(Module, session.PublishedEnemyModules);
    }

    [Fact]
    public void AFallbackModuleNameSuppressesTheReport()
    {
        // "areaXX" is the bridge's stand-in when the module name was unreadable, which
        // means a transition is in flight — the ids we are standing among are about to be
        // reassigned. An EMPTY set goes out rather than nothing, so a set published a
        // moment ago is superseded instead of being left to age out on the host.
        (CoopAgent agent, EncounterBridge bridge, StubSession session) = Build(authority: false);
        bridge.LocalModule = "area1F";
        bridge.Inside.Add(0x11B);

        agent.PumpOnce();

        (_, IReadOnlyList<uint> ids) = Assert.Single(session.Published);
        Assert.Empty(ids);
    }

    [Fact]
    public void AFallbackModuleNameStopsTheOwnerActingOnRequests()
    {
        (CoopAgent agent, EncounterBridge bridge, StubSession session) = Build(authority: true);
        bridge.LocalModule = "area1F";
        session.ReceiveFromPeer(5, "area1F", 0x11B);

        agent.PumpOnce();

        Assert.Empty(bridge.Fired);
    }

    [Fact]
    public void APeerInAnotherModuleIsNotForwarded()
    {
        (CoopAgent agent, EncounterBridge bridge, StubSession session) = Build(authority: true);
        session.ReceiveFromPeer(5, "korr_m34aa", 0x60);

        agent.PumpOnce();

        Assert.Empty(bridge.Fired);
    }
}
