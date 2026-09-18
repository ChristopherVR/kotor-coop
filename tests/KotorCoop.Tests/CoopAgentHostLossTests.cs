using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="CoopAgent.PumpOnce"/>'s reaction to <see cref="ICoopSession.HostLost"/>, isolated
/// from the network: a fake bridge and a fake session whose <c>HostLost</c> the test flips
/// directly, the same "drive the pump, inspect what it touched" shape <see cref="AwayStateTests"/>
/// already uses for the away transition. <see cref="HostLossTests"/> covers the real end-to-end
/// wire behaviour this consumes; this file is specifically the falling/rising edge reaction —
/// that <c>ClearCoopState</c> runs exactly once per transition, that the control lock
/// (<c>LockControlToPlayer</c>) stops being re-asserted for the whole outage, and that a solo
/// player's own local state keeps being read and published so a rejoin has fresh data.
/// </summary>
public class CoopAgentHostLossTests
{
    private sealed class HostLossBridge : IGameBridge
    {
        public bool ReadShouldSucceed = true;

        public string Description => "host-loss-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsPartySync => true;

        public int ClearCoopStateCalls { get; private set; }

        public int LockControlToPlayerCalls { get; private set; }

        public int TryWriteRemotePeersCalls { get; private set; }

        public List<string> ChatLines { get; } = [];

        public bool TryReadLocalState(out PlayerState state)
        {
            if (ReadShouldSucceed)
            {
                state = new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 20, 0);
                return true;
            }

            state = default;
            return false;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers)
        {
            TryWriteRemotePeersCalls++;
            return true;
        }

        public bool LockControlToPlayer()
        {
            LockControlToPlayerCalls++;
            return true;
        }

        public void ClearCoopState() => ClearCoopStateCalls++;

        public bool PostChatLine(string line)
        {
            ChatLines.Add(line);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class HostLossSession : ICoopSession
    {
        public bool HostLostValue;

        public List<RemotePeer> Peers { get; } = [];

        public uint LocalPeerId => 2;

        public bool IsConnected => !HostLostValue;

        public bool HostLost => HostLostValue;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

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

        public bool IsEnemyAuthority => false;

        public SessionStats GetStats() => new();

        public List<PlayerState> PublishedStates { get; } = [];

        public void PublishLocalState(in PlayerState state) => PublishedStates.Add(state);

        public IReadOnlyList<RemotePeer> GetRemotePeers() => Peers;

        public void SetFlag(ulong flagId, sbyte value) { }

        public uint DropItem(
            string resref, string module, Vector3f position, ItemIdentity identity = default) => 0;

        public void RemoveItem(uint itemId) { }

        public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies) { }

        public IReadOnlyList<EnemyState> GetEnemies(string module) => [];

        public void SetJournalEntry(string questTag, int state) { }

        public SessionCapabilities Capabilities => SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() { }
    }

    [Fact]
    public void HostLossRunsClearCoopStateExactlyOnceOnTheFallingEdge()
    {
        HostLossBridge bridge = new();
        HostLossSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings { SyncParty = true });

        agent.PumpOnce();
        Assert.Equal(0, bridge.ClearCoopStateCalls);

        session.HostLostValue = true;
        agent.PumpOnce();
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Equal(1, bridge.ClearCoopStateCalls);
    }

    [Fact]
    public void HostLossPostsAHudChatNoticeAndRestorationPostsAnother()
    {
        HostLossBridge bridge = new();
        HostLossSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings { SyncParty = true });

        agent.PumpOnce();
        session.HostLostValue = true;
        agent.PumpOnce();

        Assert.Contains(bridge.ChatLines, l => l.Contains("Host connection lost", StringComparison.Ordinal));

        session.HostLostValue = false;
        agent.PumpOnce();

        Assert.Contains(bridge.ChatLines, l => l.Contains("Host connection restored", StringComparison.Ordinal));
    }

    /// <summary>
    /// The bug this whole feature exists to fix: LockControlToPlayer re-asserting the control
    /// lock every tick regardless of connection state, which is what made a companion
    /// permanently unclickable after the host vanished. It must run normally while connected,
    /// stop entirely for the whole outage, and resume the instant the host is back.
    /// </summary>
    [Fact]
    public void TheControlLockStopsBeingReassertedForTheWholeOutageAndResumesOnRejoin()
    {
        HostLossBridge bridge = new();
        HostLossSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings { SyncParty = true });

        agent.PumpOnce();
        Assert.Equal(1, bridge.LockControlToPlayerCalls);

        session.HostLostValue = true;
        for (int i = 0; i < 5; i++)
        {
            agent.PumpOnce();
        }

        Assert.Equal(1, bridge.LockControlToPlayerCalls); // never re-asserted during the outage

        session.HostLostValue = false;
        agent.PumpOnce();

        Assert.Equal(2, bridge.LockControlToPlayerCalls); // resumed on the very next tick
    }

    /// <summary>Same idea for remote-peer writes: nothing is written to the game on behalf of
    /// peers that no longer exist while the host is unreachable.</summary>
    [Fact]
    public void RemotePeersAreNeverWrittenToTheGameWhileHostLost()
    {
        HostLossBridge bridge = new();
        HostLossSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings());

        agent.PumpOnce();
        int before = bridge.TryWriteRemotePeersCalls;
        Assert.True(before > 0);

        session.HostLostValue = true;
        for (int i = 0; i < 5; i++)
        {
            agent.PumpOnce();
        }

        Assert.Equal(before, bridge.TryWriteRemotePeersCalls);
    }

    /// <summary>The local player's own state must keep being read and published while solo, so
    /// a returning host sees fresh data immediately rather than whatever was last true before
    /// the outage began.</summary>
    [Fact]
    public void TheLocalPlayerKeepsBeingReadAndPublishedWhileHostLost()
    {
        HostLossBridge bridge = new();
        HostLossSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings());

        agent.PumpOnce();
        session.HostLostValue = true;
        session.PublishedStates.Clear();

        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Equal(2, session.PublishedStates.Count);
    }
}
