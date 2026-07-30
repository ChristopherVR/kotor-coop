using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The shared-pause rule at the pump level: while any peer's own-pause flag is up, the
/// bridge is asked to hold a pause; when the last one drops, it is released. The engine
/// half (SetPauseState, re-assertion, the mirrored-pause echo guard) lives in the plugin
/// and bridge and can only be exercised live.
/// </summary>
public class PausePumpTests
{
    private sealed class PauseBridge : IGameBridge
    {
        public List<bool> Requests { get; } = [];

        public string Description => "pause-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsPauseSync => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool TrySetRemotePause(bool desired)
        {
            Requests.Add(desired);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class PauseSession : ICoopSession
    {
        public List<RemotePeer> Peers { get; } = [];

        public uint LocalPeerId => 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public JournalStore Journal { get; } = new();

        public void PublishWorldDigest(uint globalsCatalogue) { }

        public void PublishParty(System.Collections.Generic.IReadOnlyList<PartyMemberState> members) { }

        public System.Collections.Generic.IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        public void PublishDialogue(bool active, string speakerName, string line) { }

        public DialogueState GetDialogue() => DialogueState.Idle;

        public bool IsEnemyAuthority => false;

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state)
        {
        }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => Peers;

        public void SetFlag(uint flagId, sbyte value)
        {
        }

        public uint DropItem(string resref, string module, Vector3f position) => 0;

        public void RemoveItem(uint itemId)
        {
        }

        public void PublishEnemies(IReadOnlyList<EnemyState> enemies)
        {
        }

        public IReadOnlyList<EnemyState> GetEnemies() => [];

        public void ReportEnemies(string module, IReadOnlyList<EnemyState> enemies)
        {
        }

        public IReadOnlyList<EnemyState> GetEnemyReports(string module) => [];

        public void SetJournalEntry(string questTag, int state)
        {
        }

        public SessionCapabilities Capabilities => SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static RemotePeer Peer(uint id, uint flags) =>
        new(id, $"peer{id}", new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 20, 0, Flags: flags));

    [Fact]
    public void APausedPeerPausesThisInstanceOnce()
    {
        PauseBridge bridge = new();
        PauseSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings());

        session.Peers.Add(Peer(1, PlayerState.FlagPaused));

        agent.PumpOnce();
        agent.PumpOnce();

        // Edge-triggered: the plugin holds the pause; the pump only reports changes.
        Assert.Equal([true], bridge.Requests);
    }

    [Fact]
    public void ReleaseFollowsTheLastPausedPeer()
    {
        PauseBridge bridge = new();
        PauseSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings());

        session.Peers.Add(Peer(1, PlayerState.FlagPaused));
        session.Peers.Add(Peer(3, PlayerState.FlagPaused));
        agent.PumpOnce();

        // One peer unpauses; the other still holds it.
        session.Peers[0] = Peer(1, 0);
        agent.PumpOnce();
        Assert.Equal([true], bridge.Requests);

        session.Peers[1] = Peer(3, 0);
        agent.PumpOnce();
        Assert.Equal([true, false], bridge.Requests);
    }

    [Fact]
    public void OtherFlagsDoNotPause()
    {
        PauseBridge bridge = new();
        PauseSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings());

        session.Peers.Add(Peer(1, PlayerState.FlagInDialogue | PlayerState.FlagInCombat));
        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }
}
