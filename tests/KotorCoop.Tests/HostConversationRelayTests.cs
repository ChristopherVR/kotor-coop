using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using KotorCoop.Session.Pumps;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The host-start conversation relay (docs/research/dialogue-sync.md): the wire packet, the
/// shared "host-led" list the plugin gate and the host controller both use, and the agent pump
/// halves (client forwards a refused start, host validates and hands it to its plugin).
/// </summary>
public class HostConversationRelayTests
{
    private const string Module = "unk_m44ac";

    // ---- codec ------------------------------------------------------------------------------

    [Fact]
    public void RequestRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        HostConversationRequestPacket original = new(2, 7, Module, "bastilae", "unk44_evilbast");

        int length = PacketCodec.Write(buffer, original);

        Assert.True(length > 0);
        Assert.Equal(original, PacketCodec.ReadHostConversationRequest(buffer[..length]));
    }

    [Fact]
    public void RequestWithAnEmptyResrefRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        HostConversationRequestPacket original = new(3, 1, Module, "bastilae", string.Empty);

        int length = PacketCodec.Write(buffer, original);

        Assert.Equal(original, PacketCodec.ReadHostConversationRequest(buffer[..length]));
    }

    [Fact]
    public void RequestRejectsAZeroSerial()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new HostConversationRequestPacket(2, 0, Module, "bastilae", ""));

        Assert.Null(PacketCodec.ReadHostConversationRequest(buffer[..length]));
    }

    [Fact]
    public void RequestRejectsNamingNothing()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new HostConversationRequestPacket(2, 1, Module, "", ""));

        Assert.Null(PacketCodec.ReadHostConversationRequest(buffer[..length]));
    }

    [Fact]
    public void EveryTruncatedPrefixIsRejected()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(
            buffer, new HostConversationRequestPacket(2, 7, Module, "bastilae", "unk44_evilbast"));

        for (int cut = 0; cut < length; cut++)
        {
            Assert.Null(PacketCodec.ReadHostConversationRequest(buffer[..cut]));
        }
    }

    [Fact]
    public void OverlongStringsAreTruncatedNotCorrupting()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        string longTag = new('x', 60);
        int length = PacketCodec.Write(buffer, new HostConversationRequestPacket(2, 1, Module, longTag, "r"));

        HostConversationRequestPacket? read = PacketCodec.ReadHostConversationRequest(buffer[..length]);
        Assert.NotNull(read);
        Assert.Equal(HostConversationRequestPacket.MaxTagLength, read.Tag.Length);
        Assert.Equal("r", read.Resref);
    }

    // ---- the shared host-led list ------------------------------------------------------------

    [Theory]
    [InlineData("man28_airlock", "", true)]
    [InlineData("MAN28_AIRLOCK", "", true)]
    [InlineData("unk44_evilbast", "", true)]
    [InlineData("", "bastilae", true)]
    [InlineData("", "BastilaE", true)]
    [InlineData("", "bastila", false)]
    [InlineData("", "bastilaeX", false)]
    [InlineData("man28_airlock2", "", false)]
    [InlineData("k_carth", "carth", false)]
    [InlineData("", "", false)]
    public void StringFormMatchesTheList(string resref, string tag, bool expected)
    {
        Assert.Equal(expected, HostLedConversationRule.IsHostLed(resref, tag));
    }

    [Fact]
    public void ByteFormAgreesWithTheStringFormForEveryEntryAndNearMiss()
    {
        List<(string Resref, string Tag)> cases = [("", ""), ("man28_airlock2", ""), ("", "bastila")];
        foreach (string r in HostLedConversationRule.Resrefs)
        {
            cases.Add((r, ""));
            cases.Add((r.ToUpperInvariant(), ""));
            cases.Add((r + "x", ""));
            cases.Add((r[..^1], ""));
        }

        foreach (string t in HostLedConversationRule.Tags)
        {
            cases.Add(("", t));
            cases.Add(("", t.ToUpperInvariant()));
            cases.Add(("", t + "x"));
            cases.Add(("", t[..^1]));
        }

        foreach ((string resref, string tag) in cases)
        {
            // The engine's fixed buffers are NUL-padded; the byte form must stop at the NUL.
            byte[] r = new byte[20];
            byte[] t = new byte[24];
            System.Text.Encoding.ASCII.GetBytes(resref).CopyTo(r, 0);
            System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(t, 0);

            Assert.Equal(
                HostLedConversationRule.IsHostLed(resref, tag),
                HostLedConversationRule.IsHostLed(r, t));
        }
    }

    [Fact]
    public void EveryEntryFitsTheSharedBlockFieldsAndThePacket()
    {
        // Plugin field capacities (SharedBlock.HostConversation*Length) minus the NUL.
        foreach (string r in HostLedConversationRule.Resrefs)
        {
            Assert.InRange(r.Length, 1, 15);
            Assert.Equal(r.ToLowerInvariant(), r);
        }

        foreach (string t in HostLedConversationRule.Tags)
        {
            Assert.InRange(t.Length, 1, HostConversationRequestPacket.MaxTagLength);
            Assert.Equal(t.ToLowerInvariant(), t);
        }
    }

    [Fact]
    public void ThePluginGateUsesTheSharedListInsteadOfItsOwnLiterals()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "KotorCoop.Plugin", "DialogueGateHook.cs"));

        Assert.Contains("HostLedConversationRule.IsHostLed(", source);
        foreach (string entry in HostLedConversationRule.Resrefs.Concat(HostLedConversationRule.Tags))
        {
            Assert.DoesNotContain($"\"{entry}\"", source);
        }

        string controller = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "KotorCoop.Plugin", "HostConversationRequestController.cs"));
        Assert.Contains("HostLedConversationRule.IsHostLed(", controller);

        string csproj = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "KotorCoop.Plugin", "KotorCoop.Plugin.csproj"));
        Assert.Contains("HostLedConversationRule.cs", csproj);
    }

    [Fact]
    public void TheSharedBlockMailboxesMatchBetweenPluginAndAgent()
    {
        string plugin = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "KotorCoop.Plugin", "SharedBlock.HostConversation.cs"));
        string agent = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "KotorCoop.Memory", "KotorGameBridge.PluginBlock.cs"));

        foreach (string name in new[]
                 { "ClientSerial", "ClientTag", "ClientResref", "HostSerial", "HostPeerId", "HostTag", "HostResref" })
        {
            Assert.Equal(
                Offset(plugin, $"OffsetHostConversation{name}"),
                Offset(agent, $"BlockHostConversation{name}"));
        }

        // The whole region sits inside the free 0x5C50..0x6000 gap, below TotalSize.
        Assert.True(Offset(plugin, "OffsetHostConversationHostResref") + 16 <= 0x5CB0);
        Assert.True(Offset(plugin, "OffsetHostConversationClientSerial") >= 0x5C50);
    }

    private static int Offset(string source, string name)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
            source, $@"{name}\s*=\s*0x([0-9A-Fa-f]+)");
        Assert.True(m.Success, $"{name} not found");
        return Convert.ToInt32(m.Groups[1].Value, 16);
    }

    private static string RepositoryRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "KotorCoop.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    // ---- pump -------------------------------------------------------------------------------

    private sealed class FakeBridge : IGameBridge
    {
        public (uint Serial, string Tag, string Resref)? ClientCapture { get; set; }

        public List<(uint PeerId, string Tag, string Resref)> HostRequests { get; } = [];

        public string Description => "host-conversation-pump-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, Module, 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool TryReadHostConversationClientCapture(out uint serial, out string tag, out string resref)
        {
            if (ClientCapture is { } c)
            {
                (serial, tag, resref) = c;
                return true;
            }

            serial = 0;
            tag = string.Empty;
            resref = string.Empty;
            return false;
        }

        public bool TryRequestHostConversation(uint peerId, string tag, string resref)
        {
            HostRequests.Add((peerId, tag, resref));
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubSession : ICoopSession
    {
        public bool EnemyAuthority { get; set; }

        public List<(string Module, string Tag, string Resref, uint Serial)> Requested { get; } = [];

        public Queue<HostConversationRequestPacket> Incoming { get; } = new();

        public void RequestHostConversation(string module, string tag, string resref, uint requestSerial) =>
            Requested.Add((module, tag, resref, requestSerial));

        public IReadOnlyList<HostConversationRequestPacket> DrainHostConversationRequests()
        {
            List<HostConversationRequestPacket> ready = [.. Incoming];
            Incoming.Clear();
            return ready;
        }

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

        public void SetJournalEntry(string questTag, int state) => Journal.Merge(questTag, state);

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static readonly PlayerState Local = new(Vector3f.Zero, 0f, Module, 20, 0);

    private static (TransitionPump Pump, FakeBridge Bridge, StubSession Session, List<string> Log) Build()
    {
        FakeBridge bridge = new();
        StubSession session = new();
        List<string> log = [];
        return (new TransitionPump(bridge, session, new AgentSettings(), log.Add), bridge, session, log);
    }

    [Fact]
    public void TheFirstReadIsABaselineNotARequest()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session, _) = Build();
        bridge.ClientCapture = (3, "bastilae", "");

        pump.PumpHostConversation(Local, []);
        pump.PumpHostConversation(Local, []);

        Assert.Empty(session.Requested);
    }

    [Fact]
    public void ANewCaptureAfterTheBaselineIsSentWithTheLocalModule()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session, _) = Build();
        pump.PumpHostConversation(Local, []);

        bridge.ClientCapture = (1, "bastilae", "");
        pump.PumpHostConversation(Local, []);
        pump.PumpHostConversation(Local, []); // the same serial again is not a second request

        Assert.Single(session.Requested);
        Assert.Equal((Module, "bastilae", "", 1u), session.Requested[0]);
    }

    [Fact]
    public void ASecondCaptureWithinTheDebounceWindowIsNotSent()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session, _) = Build();
        pump.PumpHostConversation(Local, []);

        bridge.ClientCapture = (1, "bastilae", "");
        pump.PumpHostConversation(Local, []);
        bridge.ClientCapture = (2, "bastilae", "");
        pump.PumpHostConversation(Local, []);

        Assert.Single(session.Requested);
    }

    [Fact]
    public void ACaptureOutsideTheHostLedListIsNeverRelayed()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session, List<string> log) = Build();
        pump.PumpHostConversation(Local, []);

        bridge.ClientCapture = (1, "carth", "k_carth");
        pump.PumpHostConversation(Local, []);

        Assert.Empty(session.Requested);
        Assert.Contains(log, l => l.Contains("not a host-led"));
    }

    [Fact]
    public void AHostHandsAnInModuleRequestToItsPlugin()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session, _) = Build();
        session.EnemyAuthority = true;
        session.Incoming.Enqueue(new HostConversationRequestPacket(2, 1, Module, "bastilae", "unk44_evilbast"));

        pump.PumpHostConversation(Local, []);

        Assert.Equal([(2u, "bastilae", "unk44_evilbast")], bridge.HostRequests);
    }

    [Fact]
    public void AHostIgnoresAStaleModuleRequest()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session, List<string> log) = Build();
        session.EnemyAuthority = true;
        session.Incoming.Enqueue(new HostConversationRequestPacket(2, 1, "ebo_m12aa", "bastilae", ""));

        pump.PumpHostConversation(Local, []);

        Assert.Empty(bridge.HostRequests);
        Assert.Contains(log, l => l.Contains("dropping") && l.Contains("ebo_m12aa"));
    }

    [Fact]
    public void AHostRefusesAConversationOutsideTheList()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session, _) = Build();
        session.EnemyAuthority = true;
        session.Incoming.Enqueue(new HostConversationRequestPacket(2, 1, Module, "carth", "k_carth"));

        pump.PumpHostConversation(Local, []);

        Assert.Empty(bridge.HostRequests);
    }

    [Fact]
    public void AClientNeverActsOnARequest()
    {
        (TransitionPump pump, FakeBridge bridge, StubSession session, _) = Build();
        session.EnemyAuthority = false;
        session.Incoming.Enqueue(new HostConversationRequestPacket(3, 1, Module, "bastilae", ""));

        pump.PumpHostConversation(Local, []);

        Assert.Empty(bridge.HostRequests);
    }
}
