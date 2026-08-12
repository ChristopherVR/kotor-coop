using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The shared purse and the shared experience total — two values that travel in opposite
/// ways, for reasons that are easy to get backwards.
///
/// Gold goes DOWN, so it cannot be merged as a level: max would resurrect anything anyone
/// spent, and the host's absolute snapshot is what used to restore a client's money within a
/// tick of them spending it. It travels as a cumulative earned/spent ledger the host applies
/// against its own party table.
///
/// Experience only goes up, and BOTH engines already grant kill XP independently — a client's
/// mirrored enemy dies through the real damage pipeline, so ApplyDeathExperience runs there
/// too. A delta would double every kill in the game. It is absolute, merged by max.
/// </summary>
public sealed class ProgressionPumpTests
{
    private const string Module = "end_m01aa";

    private sealed class ProgressionBridge : IGameBridge
    {
        public uint Gold { get; set; }

        public uint Experience { get; set; }

        public List<(uint Gold, uint Experience)> Writes { get; } = [];

        public string Description => "progression-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsProgressionSync => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, Module, 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool TryReadProgression(out uint gold, out uint experience)
        {
            gold = Gold;
            experience = Experience;
            return true;
        }

        public bool TryWriteProgression(uint gold, uint experience)
        {
            Gold = gold;
            Experience = experience;
            Writes.Add((gold, experience));
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubSession : ICoopSession
    {
        public bool EnemyAuthority { get; set; }

        /// <summary>What clients have reported to this host.</summary>
        public List<PeerProgression> Reports { get; } = [];

        /// <summary>What this client has sent up.</summary>
        public List<(uint Generation, uint Earned, uint Spent, uint Experience)> Sent { get; } = [];

        /// <summary>What this host has broadcast.</summary>
        public List<(uint Gold, uint Experience)> Published { get; } = [];

        /// <summary>The host's latest snapshot, as a client sees it.</summary>
        public (uint Gold, uint Experience)? FromHost { get; set; }

        public uint LocalPeerId => EnemyAuthority ? CoopHost.HostPeerId : 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public JournalStore Journal { get; } = new();

        public bool IsEnemyAuthority => EnemyAuthority;

        public SessionCapabilities Capabilities => SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public void PublishProgression(uint gold, uint experience) => Published.Add((gold, experience));

        public void ReportProgression(uint generation, uint goldEarned, uint goldSpent, uint experience) =>
            Sent.Add((generation, goldEarned, goldSpent, experience));

        public IReadOnlyList<PeerProgression> GetProgressionReports() => Reports;

        public bool TryGetProgression(out uint gold, out uint experience)
        {
            gold = FromHost?.Gold ?? 0;
            experience = FromHost?.Experience ?? 0;
            return FromHost is not null;
        }

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state) { }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public void SetFlag(uint flagId, sbyte value) { }

        public uint DropItem(string resref, string module, Vector3f position) => 0;

        public void RemoveItem(uint itemId) { }

        public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies) { }

        public IReadOnlyList<EnemyState> GetEnemies(string module) => [];

        public void PublishWorldDigest(in WorldDigest digest) { }

        public void PublishParty(IReadOnlyList<PartyMemberState> members) { }

        public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = -1, uint nodeSerial = 0) { }

        public DialogueState GetDialogue() => DialogueState.Idle;

        public void SendChat(string text) { }

        public IReadOnlyList<ChatMessage> DrainChat() => [];

        public void SetJournalEntry(string questTag, int state) { }

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() { }
    }

    /// <summary>HeavyPumpIntervalMs = 0 so every PumpOnce runs the progression pass; the
    /// wall-clock gate is not what these tests are about.</summary>
    private static (CoopAgent Agent, ProgressionBridge Bridge, StubSession Session) Build(bool host)
    {
        ProgressionBridge bridge = new();
        StubSession session = new() { EnemyAuthority = host };
        CoopAgent agent = new(bridge, session, new AgentSettings { HeavyPumpIntervalMs = 0 });
        return (agent, bridge, session);
    }

    // ---- the client's ledger ---------------------------------------------------

    [Fact]
    public void WhatThePartyAlreadyHadIsNotSomebodysContribution()
    {
        // The first observation baselines and nothing else. Counting the save's own 1200
        // gold as "earned" would have the host add a whole playthrough's takings to the
        // purse the moment a client connected.
        (CoopAgent agent, ProgressionBridge bridge, StubSession session) = Build(host: false);
        bridge.Gold = 1200;

        agent.PumpOnce();

        (_, uint earned, uint spent, _) = Assert.Single(session.Sent);
        Assert.Equal(0u, earned);
        Assert.Equal(0u, spent);
    }

    [Fact]
    public void SpendingAndEarningBecomeLedgerEntries()
    {
        (CoopAgent agent, ProgressionBridge bridge, StubSession session) = Build(host: false);
        bridge.Gold = 1000;
        agent.PumpOnce();                 // baseline

        bridge.Gold = 900;                // bought a blaster
        agent.PumpOnce();

        bridge.Gold = 1150;               // looted a corpse
        agent.PumpOnce();

        (_, uint earned, uint spent, _) = session.Sent[^1];
        Assert.Equal(250u, earned);
        Assert.Equal(100u, spent);
    }

    [Fact]
    public void MirroringTheHostsPurseIsNotEarningIt()
    {
        // ⚠ The trap this guards: the engine has no "gold changed" event, so a change is
        // inferred from the level. Without re-baselining after our own write, applying the
        // host's purse would read as this party earning the difference, and the client would
        // report a contribution it never made — inflating the purse on every tick.
        (CoopAgent agent, ProgressionBridge bridge, StubSession session) = Build(host: false);
        bridge.Gold = 500;
        agent.PumpOnce();

        session.FromHost = (2000, 0);
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Equal(2000u, bridge.Gold);
        (_, uint earned, uint spent, _) = session.Sent[^1];
        Assert.Equal(0u, earned);
        Assert.Equal(0u, spent);
    }

    [Fact]
    public void AClientNeverHasItsExperiencePulledBackwards()
    {
        // The bug this closes: a client's plot XP was stamped back down by the host's
        // absolute snapshot within a tick of the conversation that granted it.
        (CoopAgent agent, ProgressionBridge bridge, StubSession session) = Build(host: false);
        bridge.Experience = 900;
        session.FromHost = (0, 500);

        agent.PumpOnce();

        Assert.Equal(900u, bridge.Experience);
    }

    [Fact]
    public void AClientTakesTheHostsHigherExperience()
    {
        (CoopAgent agent, ProgressionBridge bridge, StubSession session) = Build(host: false);
        bridge.Experience = 500;
        session.FromHost = (0, 900);

        agent.PumpOnce();

        Assert.Equal(900u, bridge.Experience);
    }

    // ---- the host's purse ------------------------------------------------------

    [Fact]
    public void AClientsPurchaseComesOutOfThePurseExactlyOnce()
    {
        (CoopAgent agent, ProgressionBridge bridge, StubSession session) = Build(host: true);
        bridge.Gold = 1000;
        agent.PumpOnce();

        session.Reports.Add(new PeerProgression(2, Generation: 7, GoldEarned: 0, GoldSpent: 0, Experience: 0));
        agent.PumpOnce();                 // first sight of the ledger: adopt, do not spend

        session.Reports[0] = new PeerProgression(2, 7, GoldEarned: 0, GoldSpent: 250, Experience: 0);
        agent.PumpOnce();
        Assert.Equal(750u, bridge.Gold);

        // The same report arriving again is not a second purchase.
        agent.PumpOnce();
        agent.PumpOnce();
        Assert.Equal(750u, bridge.Gold);
    }

    [Fact]
    public void AClientsEarningsGoIntoThePurse()
    {
        (CoopAgent agent, ProgressionBridge bridge, StubSession session) = Build(host: true);
        bridge.Gold = 1000;
        session.Reports.Add(new PeerProgression(2, 7, 0, 0, 0));
        agent.PumpOnce();

        session.Reports[0] = new PeerProgression(2, 7, GoldEarned: 400, GoldSpent: 0, Experience: 0);
        agent.PumpOnce();

        Assert.Equal(1400u, bridge.Gold);
        Assert.Contains((1400u, 0u), session.Published);
    }

    [Fact]
    public void AReAttachedAgentsLedgerIsAdoptedNotSpent()
    {
        // ⚠ A restarted agent re-reports everything it ever counted, under a new generation.
        // Applying that as new activity would hand the party a second copy of the session's
        // earnings — the exact failure the generation field exists to prevent.
        (CoopAgent agent, ProgressionBridge bridge, StubSession session) = Build(host: true);
        bridge.Gold = 1000;
        session.Reports.Add(new PeerProgression(2, Generation: 7, GoldEarned: 400, GoldSpent: 100, Experience: 0));
        agent.PumpOnce();
        agent.PumpOnce();

        uint before = bridge.Gold;

        session.Reports[0] = new PeerProgression(2, Generation: 8, GoldEarned: 400, GoldSpent: 100, Experience: 0);
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Equal(before, bridge.Gold);
    }

    [Fact]
    public void AReorderedReportDoesNotRefundAnything()
    {
        (CoopAgent agent, ProgressionBridge bridge, StubSession session) = Build(host: true);
        bridge.Gold = 1000;
        session.Reports.Add(new PeerProgression(2, 7, 0, 0, 0));
        agent.PumpOnce();

        session.Reports[0] = new PeerProgression(2, 7, 0, GoldSpent: 300, Experience: 0);
        agent.PumpOnce();
        Assert.Equal(700u, bridge.Gold);

        // An older datagram arriving late must not put the money back.
        session.Reports[0] = new PeerProgression(2, 7, 0, GoldSpent: 100, Experience: 0);
        agent.PumpOnce();
        Assert.Equal(700u, bridge.Gold);
    }

    [Fact]
    public void ThePartyCannotOweMoney()
    {
        // The engine's gold is unsigned; a negative write would wrap to something enormous.
        (CoopAgent agent, ProgressionBridge bridge, StubSession session) = Build(host: true);
        bridge.Gold = 100;
        session.Reports.Add(new PeerProgression(2, 7, 0, 0, 0));
        agent.PumpOnce();

        session.Reports[0] = new PeerProgression(2, 7, 0, GoldSpent: 5000, Experience: 0);
        agent.PumpOnce();

        Assert.Equal(0u, bridge.Gold);
    }

    [Fact]
    public void TheHostMergesAClientsHigherExperienceUpward()
    {
        // Plot XP granted by a conversation only the client is having.
        (CoopAgent agent, ProgressionBridge bridge, StubSession session) = Build(host: true);
        bridge.Experience = 500;
        session.Reports.Add(new PeerProgression(2, 7, 0, 0, Experience: 900));

        agent.PumpOnce();

        Assert.Equal(900u, bridge.Experience);
        Assert.Contains(session.Published, p => p.Experience == 900);
    }

    [Fact]
    public void TwoClientsBothContributeToOnePurse()
    {
        (CoopAgent agent, ProgressionBridge bridge, StubSession session) = Build(host: true);
        bridge.Gold = 1000;
        session.Reports.Add(new PeerProgression(2, 7, 0, 0, 0));
        session.Reports.Add(new PeerProgression(3, 9, 0, 0, 0));
        agent.PumpOnce();

        session.Reports[0] = new PeerProgression(2, 7, GoldEarned: 400, GoldSpent: 0, Experience: 0);
        session.Reports[1] = new PeerProgression(3, 9, GoldEarned: 0, GoldSpent: 250, Experience: 0);
        agent.PumpOnce();

        Assert.Equal(1150u, bridge.Gold);
    }

    [Fact]
    public void NothingIsWrittenWhenNobodyContributed()
    {
        // A quiet session must not rewrite the party table every second; the write is what
        // re-baselines the ledger, and a needless one is a needless chance to mis-account.
        (CoopAgent agent, ProgressionBridge bridge, StubSession session) = Build(host: true);
        bridge.Gold = 1000;

        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Empty(bridge.Writes);
        Assert.Contains((1000u, 0u), session.Published);
    }

    [Fact]
    public void ProgressionSyncOffLeavesBothSidesAlone()
    {
        ProgressionBridge bridge = new() { Gold = 1000 };
        StubSession session = new() { EnemyAuthority = false, FromHost = (50, 50) };
        CoopAgent agent = new(
            bridge, session, new AgentSettings { HeavyPumpIntervalMs = 0, SyncProgression = false });

        agent.PumpOnce();

        Assert.Equal(1000u, bridge.Gold);
        Assert.Empty(session.Sent);
    }
}
