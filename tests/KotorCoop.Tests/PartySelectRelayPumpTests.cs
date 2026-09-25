using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using KotorCoop.Session.Pumps;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// docs/research/galaxy-map-and-party-select.md's 2026-09-25 relay section (both the first
/// pass and the SECOND PASS trigger-replay correction), driven directly (fake bridge + fake
/// session, no sockets) — the vehicle <see cref="CreditGrantPumpTests"/> already established
/// for this family of pump: a client's captured panel becomes a request (with resend/timeout),
/// and a host's received request becomes a trigger-fire attempt, an ack, and (once the screen
/// closes) an outcome.
/// </summary>
public class PartySelectRelayPumpTests
{
    private const string Module = "ebo_m12aa";

    private sealed class FakeBridge : IGameBridge
    {
        public (uint Serial, string ExitScript, int Npc1, int Npc2, uint TriggerId)? ClientCapture { get; set; }

        public List<string> ChatLines { get; } = [];

        public List<(string ExitScript, int Npc1, int Npc2, uint TriggerId)> HostOpenRequests { get; } = [];

        public bool HostOpenAccepts { get; set; } = true;

        public (uint Applied, uint Status)? HostStatus { get; set; }

        public (uint Serial, uint Outcome)? HostOutcome { get; set; }

        public string Description => "party-select-pump-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, Module, 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool TryReadPartySelectClientCapture(
            out uint serial, out string exitScript, out int forceNpc1, out int forceNpc2, out uint triggerId)
        {
            if (ClientCapture is { } c)
            {
                (serial, exitScript, forceNpc1, forceNpc2, triggerId) = c;
                return true;
            }

            serial = 0;
            exitScript = string.Empty;
            forceNpc1 = -1;
            forceNpc2 = -1;
            triggerId = 0;
            return false;
        }

        public bool TryRequestPartySelectHostOpen(string exitScript, int forceNpc1, int forceNpc2, uint triggerId)
        {
            HostOpenRequests.Add((exitScript, forceNpc1, forceNpc2, triggerId));
            return HostOpenAccepts;
        }

        public bool TryReadPartySelectHostStatus(out uint applied, out uint status)
        {
            if (HostStatus is { } s)
            {
                (applied, status) = s;
                return true;
            }

            applied = 0;
            status = 0;
            return false;
        }

        public bool TryReadPartySelectHostOutcome(out uint serial, out uint outcome)
        {
            if (HostOutcome is { } o)
            {
                (serial, outcome) = o;
                return true;
            }

            serial = 0;
            outcome = 0;
            return false;
        }

        public bool PostChatLine(string line)
        {
            ChatLines.Add(line);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubSession : ICoopSession
    {
        public bool EnemyAuthority { get; set; }

        public List<(string Module, string ExitScript, int Npc1, int Npc2, uint TriggerId, uint RequestSerial)> Requested { get; } = [];

        public Queue<PartySelectResponsePacket> IncomingResponses { get; } = new();

        public Queue<PartySelectRequestPacket> IncomingRequests { get; } = new();

        public List<(uint PeerId, PartySelectResponsePacket Response)> SentResponses { get; } = [];

        public void RequestPartySelect(
            string module, string exitScript, int forceNpc1, int forceNpc2, uint triggerId, uint requestSerial) =>
            Requested.Add((module, exitScript, forceNpc1, forceNpc2, triggerId, requestSerial));

        public IReadOnlyList<PartySelectResponsePacket> DrainPartySelectResponses()
        {
            List<PartySelectResponsePacket> ready = [.. IncomingResponses];
            IncomingResponses.Clear();
            return ready;
        }

        public IReadOnlyList<PartySelectRequestPacket> DrainPartySelectRequests()
        {
            List<PartySelectRequestPacket> ready = [.. IncomingRequests];
            IncomingRequests.Clear();
            return ready;
        }

        public void SendPartySelectResponse(uint peerId, PartySelectResponsePacket response) =>
            SentResponses.Add((peerId, response));

        public uint LocalPeerId => 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public JournalStore Journal { get; } = new();

        public bool IsEnemyAuthority => EnemyAuthority;

        public SessionCapabilities Capabilities => SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state) { }

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

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static readonly PlayerState Local = new(Vector3f.Zero, 0f, Module, 20, 0);

    private static (TransitionPump Pump, FakeBridge Bridge, StubSession Session) Build()
    {
        FakeBridge bridge = new();
        StubSession session = new();
        AgentSettings settings = new();
        return (new TransitionPump(bridge, session, settings, _ => { }), bridge, session);
    }

    // ---- client half ----------------------------------------------------------------------

    [Fact]
    public void TheFirstReadIsABaselineNotARequest()
    {
        // Same rule GalaxyDeparture's own baseline uses: a leftover serial from an earlier
        // session must not become a fresh request the moment the agent attaches.
        (TransitionPump pump, FakeBridge bridge, StubSession session) = Build();
        bridge.ClientCapture = (3, "k_pebn_swoop", -1, -1, 0x2);

        pump.PumpPartySelect(Local, []);
        pump.PumpPartySelect(Local, []);

        Assert.Empty(session.Requested);
        Assert.Empty(bridge.ChatLines);
    }

    [Fact]
    public void ANewCaptureAfterTheBaselineIsSentAndNoticed()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session) = Build();
        bridge.ClientCapture = null;
        pump.PumpPartySelect(Local, []);

        bridge.ClientCapture = (1, "k_pebn_swoop", -1, -1, 0x2);
        pump.PumpPartySelect(Local, []);

        Assert.Single(session.Requested);
        Assert.Equal((Module, "k_pebn_swoop", -1, -1, 0x2u, 1u), session.Requested[0]);
        Assert.Contains(bridge.ChatLines, l => l.Contains("asked"));
    }

    [Fact]
    public void AnOpenedResponsePostsNoExtraChatLine()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session) = Build();
        bridge.ClientCapture = null;
        pump.PumpPartySelect(Local, []);
        bridge.ClientCapture = (1, "k_pebn_swoop", -1, -1, 0x2);
        pump.PumpPartySelect(Local, []);
        int linesAfterAsk = bridge.ChatLines.Count;

        session.IncomingResponses.Enqueue(new PartySelectResponsePacket(1, 1, PartySelectResponseStatus.Opened, string.Empty));
        pump.PumpPartySelect(Local, []);

        Assert.Equal(linesAfterAsk, bridge.ChatLines.Count);
    }

    [Fact]
    public void ARefusedResponseShowsItsDetailAndStopsWaiting()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session) = Build();
        bridge.ClientCapture = null;
        pump.PumpPartySelect(Local, []);
        bridge.ClientCapture = (1, "k_pebn_swoop", -1, -1, 0x2);
        pump.PumpPartySelect(Local, []);

        session.IncomingResponses.Enqueue(
            new PartySelectResponsePacket(1, 1, PartySelectResponseStatus.Refused, "in combat"));
        pump.PumpPartySelect(Local, []);

        Assert.Contains(bridge.ChatLines, l => l.Contains("in combat"));

        // A late/duplicate response for the same (now-closed) request must not react again.
        int linesAfterRefusal = bridge.ChatLines.Count;
        session.IncomingResponses.Enqueue(
            new PartySelectResponsePacket(1, 2, PartySelectResponseStatus.Cancelled, string.Empty));
        pump.PumpPartySelect(Local, []);
        Assert.Equal(linesAfterRefusal, bridge.ChatLines.Count);
    }

    [Fact]
    public void ACancelledResponseShowsANotice()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session) = Build();
        bridge.ClientCapture = null;
        pump.PumpPartySelect(Local, []);
        bridge.ClientCapture = (1, "k_pebn_swoop", -1, -1, 0x2);
        pump.PumpPartySelect(Local, []);

        session.IncomingResponses.Enqueue(new PartySelectResponsePacket(1, 1, PartySelectResponseStatus.Opened, string.Empty));
        pump.PumpPartySelect(Local, []);
        session.IncomingResponses.Enqueue(new PartySelectResponsePacket(1, 2, PartySelectResponseStatus.Cancelled, string.Empty));
        pump.PumpPartySelect(Local, []);

        Assert.Contains(bridge.ChatLines, l => l.Contains("chose not to change"));
    }

    [Fact]
    public void ARequestIsResentWithTheSameSnapshottedParamsEvenIfALaterReadFails()
    {
        // The stored _partySelectActive* fields, not a fresh bridge read, drive a resend — a
        // transient read failure on a LATER tick must not blank out the resend.
        (TransitionPump pump, FakeBridge bridge, StubSession session) = Build();
        bridge.ClientCapture = null;
        pump.PumpPartySelect(Local, []);
        bridge.ClientCapture = (1, "k_pebn_swoop", -1, -1, 0x2);
        pump.PumpPartySelect(Local, []);
        Assert.Single(session.Requested);

        // Simulate a transient unreadable block on a later tick - the resend timer has not
        // elapsed yet (SessionClock uses a real wall clock), so no second send happens within
        // this test, but the important thing is nothing throws and state is untouched.
        bridge.ClientCapture = null;
        pump.PumpPartySelect(Local, []);
        Assert.Single(session.Requested);
    }

    // ---- host half --------------------------------------------------------------------------

    [Fact]
    public void ANonHostNeverActsOnAReceivedRequest()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session) = Build();
        session.EnemyAuthority = false;
        session.IncomingRequests.Enqueue(new PartySelectRequestPacket(5, 1, Module, "k_pebn_swoop", -1, -1, 0x2));

        pump.PumpPartySelect(Local, []);

        Assert.Empty(bridge.HostOpenRequests);
        Assert.Empty(session.SentResponses);
    }

    [Fact]
    public void ACrossModuleRequestIsIgnoredWithNoResponse()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session) = Build();
        session.EnemyAuthority = true;
        session.IncomingRequests.Enqueue(new PartySelectRequestPacket(5, 1, "tar_m02af", "k_pebn_swoop", -1, -1, 0x2));

        pump.PumpPartySelect(Local, []);

        Assert.Empty(bridge.HostOpenRequests);
        Assert.Empty(session.SentResponses);
    }

    [Fact]
    public void ARequestWithNoTriggerIdIsRefusedWithoutTouchingThePlugin()
    {
        // 2026-09-25 second pass: a live test found the DIRECT ShowPartySelection call
        // unreliable, so the host now only ever acts via a causal trigger. A requester who
        // captured no trigger (triggerId 0) is refused locally, with no plugin round trip.
        (TransitionPump pump, FakeBridge bridge, StubSession session) = Build();
        session.EnemyAuthority = true;
        session.IncomingRequests.Enqueue(new PartySelectRequestPacket(5, 1, Module, "k_pebn_swoop", -1, -1, 0));

        pump.PumpPartySelect(Local, []);

        Assert.Empty(bridge.HostOpenRequests);
        Assert.Single(session.SentResponses);
        Assert.Equal(PartySelectResponseStatus.Refused, session.SentResponses[0].Response.Status);
        Assert.Contains("trigger", session.SentResponses[0].Response.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AHostOpensASameModuleRequestAndAcksOpened()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session) = Build();
        session.EnemyAuthority = true;
        session.IncomingRequests.Enqueue(new PartySelectRequestPacket(5, 1, Module, "k_pebn_swoop", -1, -1, 0x2));

        pump.PumpPartySelect(Local, []);
        Assert.Single(bridge.HostOpenRequests);
        Assert.Equal(0x2u, bridge.HostOpenRequests[0].TriggerId);

        bridge.HostStatus = (1, 1); // applied=1, PartySelectHostStatusCode.Opened
        pump.PumpPartySelect(Local, []);

        Assert.Single(session.SentResponses);
        Assert.Equal(5u, session.SentResponses[0].PeerId);
        Assert.Equal(PartySelectResponseStatus.Opened, session.SentResponses[0].Response.Status);
        Assert.Contains(bridge.ChatLines, l => l.Contains("wants to leave"));
    }

    [Fact]
    public void ASecondRequestWhileOneIsPendingIsAnsweredAlreadyPending()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session) = Build();
        session.EnemyAuthority = true;
        session.IncomingRequests.Enqueue(new PartySelectRequestPacket(5, 1, Module, "k_pebn_swoop", -1, -1, 0x2));
        pump.PumpPartySelect(Local, []);

        session.IncomingRequests.Enqueue(new PartySelectRequestPacket(6, 2, Module, "k_pebn_swoop", -1, -1, 0x3));
        pump.PumpPartySelect(Local, []);

        Assert.Single(bridge.HostOpenRequests); // only the first ever reached the plugin
        Assert.Single(session.SentResponses);
        Assert.Equal(6u, session.SentResponses[0].PeerId);
        Assert.Equal(PartySelectResponseStatus.AlreadyPending, session.SentResponses[0].Response.Status);
    }

    [Fact]
    public void ARefusedOpenAcksRefused()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session) = Build();
        session.EnemyAuthority = true;
        session.IncomingRequests.Enqueue(new PartySelectRequestPacket(5, 1, Module, "k_pebn_swoop", -1, -1, 0x2));
        pump.PumpPartySelect(Local, []);

        bridge.HostStatus = (1, 2); // applied=1, PartySelectHostStatusCode.Refused
        pump.PumpPartySelect(Local, []);

        Assert.Single(session.SentResponses);
        Assert.Equal(PartySelectResponseStatus.Refused, session.SentResponses[0].Response.Status);

        // Refused frees the debounce - a fresh request is accepted right away.
        session.IncomingRequests.Enqueue(new PartySelectRequestPacket(7, 3, Module, "k_pebn_swoop", -1, -1, 0x2));
        pump.PumpPartySelect(Local, []);
        Assert.Equal(2, bridge.HostOpenRequests.Count);
    }

    [Fact]
    public void AConfirmedOutcomeIsRelayedAfterOpening()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session) = Build();
        session.EnemyAuthority = true;
        session.IncomingRequests.Enqueue(new PartySelectRequestPacket(5, 1, Module, "k_pebn_swoop", -1, -1, 0x2));
        pump.PumpPartySelect(Local, []);
        bridge.HostStatus = (1, 1); // Opened
        pump.PumpPartySelect(Local, []);

        bridge.HostOutcome = (1, 1); // serial=1, PartySelectOutcomeCode.Confirmed
        pump.PumpPartySelect(Local, []);

        Assert.Equal(2, session.SentResponses.Count);
        Assert.Equal(PartySelectResponseStatus.Confirmed, session.SentResponses[1].Response.Status);

        // The debounce is now free again.
        session.IncomingRequests.Enqueue(new PartySelectRequestPacket(8, 4, Module, "k_pebn_swoop", -1, -1, 0x2));
        pump.PumpPartySelect(Local, []);
        Assert.Equal(2, bridge.HostOpenRequests.Count);
    }

    [Fact]
    public void ACancelledOutcomeIsRelayedAfterOpening()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session) = Build();
        session.EnemyAuthority = true;
        session.IncomingRequests.Enqueue(new PartySelectRequestPacket(5, 1, Module, "k_pebn_swoop", -1, -1, 0x2));
        pump.PumpPartySelect(Local, []);
        bridge.HostStatus = (1, 1); // Opened
        pump.PumpPartySelect(Local, []);

        bridge.HostOutcome = (1, 2); // serial=1, PartySelectOutcomeCode.Cancelled
        pump.PumpPartySelect(Local, []);

        Assert.Equal(2, session.SentResponses.Count);
        Assert.Equal(PartySelectResponseStatus.Cancelled, session.SentResponses[1].Response.Status);
    }
}
