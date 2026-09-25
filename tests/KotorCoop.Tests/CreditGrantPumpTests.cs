using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using KotorCoop.Session.Pumps;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>docs/research/personal-inventory.md §4's credits-reward relay, driven directly
/// (fake bridge + fake session, no sockets) — the vehicle <see cref="SharedInventoryV2PumpTests"/>
/// already established for this family of pump.</summary>
public class CreditGrantPumpTests
{
    private const string Module = "end_m01aa";

    private sealed class FakeBridge : IGameBridge
    {
        public uint Gold { get; set; }

        public bool ReadFails { get; set; }

        public bool WriteFails { get; set; }

        public List<(uint Gold, uint Experience)> Writes { get; } = [];

        public string Description => "credit-grant-pump-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, Module, 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool TryReadProgression(out uint gold, out uint experience)
        {
            gold = Gold;
            experience = 0;
            return !ReadFails;
        }

        public bool TryWriteProgression(uint gold, uint experience)
        {
            if (WriteFails)
            {
                return false;
            }

            Gold = gold;
            Writes.Add((gold, experience));
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubSession : ICoopSession
    {
        public bool CreditsPersonal { get; set; } = true;

        public List<int> Published { get; } = [];

        public Queue<CreditGrantPacket> Incoming { get; } = new();

        public SessionOverrides Overrides => new(CreditsPersonal: CreditsPersonal);

        public void PublishCreditGrant(int amount) => Published.Add(amount);

        public IReadOnlyList<CreditGrantPacket> DrainCreditGrants()
        {
            List<CreditGrantPacket> ready = [.. Incoming];
            Incoming.Clear();
            return ready;
        }

        public uint LocalPeerId => 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public JournalStore Journal { get; } = new();

        public bool IsEnemyAuthority => false;

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

    private static (CreditGrantPump Pump, FakeBridge Bridge, StubSession Session) Build()
    {
        FakeBridge bridge = new();
        StubSession session = new();
        AgentSettings settings = new();
        return (new CreditGrantPump(bridge, session, settings, _ => { }), bridge, session);
    }

    [Fact]
    public void NothingIsPublishedWhileNotOwningAConversation()
    {
        (CreditGrantPump pump, FakeBridge bridge, StubSession session) = Build();
        bridge.Gold = 1000;

        pump.PumpCreditGrants(Module, ownedByDialoguePump: false);
        bridge.Gold = 1500;
        pump.PumpCreditGrants(Module, ownedByDialoguePump: false);

        Assert.Empty(session.Published);
    }

    [Fact]
    public void ARiseWhileOwningAConversationIsPublished()
    {
        (CreditGrantPump pump, FakeBridge bridge, StubSession session) = Build();
        bridge.Gold = 1000;
        pump.PumpCreditGrants(Module, ownedByDialoguePump: true); // baseline only

        bridge.Gold = 1350; // the reward node ran
        pump.PumpCreditGrants(Module, ownedByDialoguePump: true);

        Assert.Equal([350], session.Published);
    }

    [Fact]
    public void SharedCreditsModeNeverPublishesEvenWhileOwningAConversation()
    {
        (CreditGrantPump pump, FakeBridge bridge, StubSession session) = Build();
        session.CreditsPersonal = false;
        bridge.Gold = 1000;
        pump.PumpCreditGrants(Module, ownedByDialoguePump: true);
        bridge.Gold = 1350;
        pump.PumpCreditGrants(Module, ownedByDialoguePump: true);

        Assert.Empty(session.Published);
    }

    [Fact]
    public void ReceivedGrantsAreAddedToTheLocalPurse()
    {
        (CreditGrantPump pump, FakeBridge bridge, StubSession session) = Build();
        bridge.Gold = 500;
        session.Incoming.Enqueue(new CreditGrantPacket(OriginPeerId: 1, OpSequence: 1, Amount: 200));

        pump.PumpCreditGrants(Module, ownedByDialoguePump: false);

        Assert.Equal(700u, bridge.Gold);
        Assert.Equal([(700u, 0u)], bridge.Writes);
    }

    [Fact]
    public void ReceivedGrantsClampAtTheEnginesGoldCeiling()
    {
        (CreditGrantPump pump, FakeBridge bridge, StubSession session) = Build();
        bridge.Gold = GoldReadingRule.Max - 10;
        session.Incoming.Enqueue(new CreditGrantPacket(1, 1, 1000));

        pump.PumpCreditGrants(Module, ownedByDialoguePump: false);

        Assert.Equal(GoldReadingRule.Max, bridge.Gold);
    }

    /// <summary>
    /// The amplification guard: a grant APPLIED this tick must not read, on a LATER tick where
    /// this same player also happens to own a (separate) conversation, as if that conversation
    /// had just granted the same amount — see CreditGrantPump's own remarks on why the baseline
    /// is bumped by the applied amount immediately.
    /// </summary>
    [Fact]
    public void AppliedGrantIsNeverMisreadAsAFreshLocalGrantOnALaterTick()
    {
        (CreditGrantPump pump, FakeBridge bridge, StubSession session) = Build();
        bridge.Gold = 1000;

        // This player's OWN conversation is already open (baseline established at 1000)...
        pump.PumpCreditGrants(Module, ownedByDialoguePump: true);
        Assert.Empty(session.Published);

        // ...and, in the SAME tick, a peer's grant lands (the pump applies it after detecting).
        session.Incoming.Enqueue(new CreditGrantPacket(OriginPeerId: 9, OpSequence: 1, Amount: 300));
        pump.PumpCreditGrants(Module, ownedByDialoguePump: true);

        Assert.Equal(1300u, bridge.Gold);
        // The received grant must NEVER be re-published as if this player's own conversation
        // had produced it.
        Assert.Empty(session.Published);

        // One more tick with nothing new happening: still nothing published.
        pump.PumpCreditGrants(Module, ownedByDialoguePump: true);
        Assert.Empty(session.Published);
    }

    [Fact]
    public void AGenuineLocalRiseAfterAnAppliedGrantIsStillPublishedCorrectly()
    {
        (CreditGrantPump pump, FakeBridge bridge, StubSession session) = Build();
        bridge.Gold = 1000;
        pump.PumpCreditGrants(Module, ownedByDialoguePump: true); // baseline 1000

        session.Incoming.Enqueue(new CreditGrantPacket(9, 1, 300));
        pump.PumpCreditGrants(Module, ownedByDialoguePump: true); // applies +300 -> 1300, baseline follows to 1300

        // Now a GENUINE local reward on top.
        bridge.Gold = 1500;
        pump.PumpCreditGrants(Module, ownedByDialoguePump: true);

        Assert.Equal([200], session.Published);
    }

    [Fact]
    public void AModuleChangeRebaselinesWithoutPublishing()
    {
        (CreditGrantPump pump, FakeBridge bridge, StubSession session) = Build();
        bridge.Gold = 1000;
        pump.PumpCreditGrants(Module, ownedByDialoguePump: true);

        bridge.Gold = 5000; // e.g. a different save/module's own starting gold
        pump.PumpCreditGrants("tar_m02aa", ownedByDialoguePump: true);

        Assert.Empty(session.Published);
    }
}
