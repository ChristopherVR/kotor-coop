using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using KotorCoop.Session.Pumps;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="StuntModuleRule"/> — the "cutscene-only world" test a <c>STUNT_*</c> module fails
/// (see that class's own doc) — plus one end-to-end regression per gated pump proving the
/// publish (or apply) is genuinely withheld while the LOCAL module is a stunt module and resumes
/// the moment it is back in an ordinary one. <see cref="TransitionPump"/>'s own refusal to
/// FOLLOW into a stunt module already used this same prefix test before this file existed
/// (<c>TransitionPump.Travel.cs</c>); these tests cover the newer half — the state PUBLISHERS
/// (progression, the party roster/companions, the party pool, dialogue, interaction locks, the
/// store).
/// </summary>
public sealed class StuntModuleRuleTests
{
    private const string StuntModule = "STUNT_00";
    private const string RealModule = "ebo_m12aa";

    // ---- the rule itself ---------------------------------------------------------------

    [Theory]
    [InlineData("STUNT_00", true)]
    [InlineData("stunt_16", true)]
    [InlineData("Stunt00", true)]
    [InlineData("ebo_m12aa", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("area77", false)]
    public void IsStuntModuleMatchesOnlyTheStuntPrefix(string? module, bool expected)
    {
        Assert.Equal(expected, StuntModuleRule.IsStuntModule(module));
    }

    // ---- a shared fake bridge/session, minimal enough that every pump below only needs to
    // turn on the one channel it is testing -----------------------------------------------

    private sealed class StuntGateBridge : IGameBridge
    {
        public string Module { get; set; } = RealModule;

        public string Description => "stunt-gate-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, Module, 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        // ---- progression ----
        public bool SupportsProgressionSync => true;

        public uint Gold { get; set; } = 1000;

        public uint Experience { get; set; }

        public List<(uint Gold, uint Experience)> ProgressionWrites { get; } = [];

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
            ProgressionWrites.Add((gold, experience));
            return true;
        }

        // ---- party companions / roster ----
        public bool SupportsPartySync => true;

        public List<PartyMemberState> LocalPartyMembers { get; set; } = [];

        public int ReadPartyMembersCalls { get; private set; }

        public List<PartyMemberState> ReadPartyMembers(bool includeEquipment = false)
        {
            ReadPartyMembersCalls++;
            return LocalPartyMembers;
        }

        public List<IReadOnlyList<PartyMemberState>> WrittenPartyMembers { get; } = [];

        public int TryWritePartyMembers(IReadOnlyList<PartyMemberState> members)
        {
            WrittenPartyMembers.Add(members);
            return members.Count;
        }

        public PartyRoster LocalRoster { get; set; } = new(0b11, 0b1, null);

        public bool TryReadLocalPartyRoster(out PartyRoster roster)
        {
            roster = LocalRoster;
            return true;
        }

        public List<(bool Valid, PartyRoster Roster)> HostRosterWrites { get; } = [];

        public bool WriteHostPartyRoster(bool valid, PartyRoster roster)
        {
            HostRosterWrites.Add((valid, roster));
            return true;
        }

        // ---- party pool (v1 and v2 share the same scan) ----
        public bool SupportsPartyPool => true;

        public IReadOnlyDictionary<PartyPoolStackKey, int> PartyPoolScan { get; set; } =
            new Dictionary<PartyPoolStackKey, int>();

        public int PartyPoolScanCalls { get; private set; }

        public bool TryScanPartyPoolTotals(out IReadOnlyDictionary<PartyPoolStackKey, int> totals)
        {
            PartyPoolScanCalls++;
            totals = PartyPoolScan;
            return true;
        }

        // ---- store ----
        public bool SupportsStore => true;

        public uint OpenStoreId { get; set; }

        public IReadOnlyDictionary<StoreStackKey, int> StoreScan { get; set; } =
            new Dictionary<StoreStackKey, int>();

        public bool StoreOpen { get; set; }

        public int StoreScanCalls { get; private set; }

        public bool TryScanOpenStore(out uint storeObjectId, out IReadOnlyDictionary<StoreStackKey, int> totals)
        {
            StoreScanCalls++;
            storeObjectId = OpenStoreId;
            totals = StoreScan;
            return StoreOpen;
        }

        // ---- interaction locks ----
        public int InteractionReadCalls { get; private set; }

        public bool TryReadLocalInteraction(out string tag, out uint kind, out bool active, out uint targetId)
        {
            InteractionReadCalls++;
            tag = "end_trask";
            kind = (uint)InteractionKind.Conversation;
            active = true;
            targetId = 0;
            return true;
        }

        public int InteractionWriteCalls { get; private set; }

        public bool WriteInteractionLocks(IReadOnlyList<InteractionLock> locks)
        {
            InteractionWriteCalls++;
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StuntGateSession : ICoopSession
    {
        public bool EnemyAuthority { get; set; }

        public uint LocalPeerId => EnemyAuthority ? CoopHost.HostPeerId : 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public JournalStore Journal { get; } = new();

        public bool IsEnemyAuthority => EnemyAuthority;

        public SessionCapabilities Capabilities => SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state)
        {
        }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public void SetFlag(ulong flagId, sbyte value)
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

        public void PublishWorldDigest(in WorldDigest digest)
        {
        }

        // ---- party companions / roster ----
        public List<IReadOnlyList<PartyMemberState>> PublishedParty { get; } = [];

        public void PublishParty(IReadOnlyList<PartyMemberState> members) => PublishedParty.Add(members);

        public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        public List<PartyRoster> PublishedRoster { get; } = [];

        public void PublishPartyRoster(PartyRoster roster) => PublishedRoster.Add(roster);

        // ---- progression ----
        public List<(uint Gold, uint Experience)> PublishedProgression { get; } = [];

        public void PublishProgression(uint gold, uint experience) => PublishedProgression.Add((gold, experience));

        public bool TryGetProgression(out uint gold, out uint experience)
        {
            gold = 0;
            experience = 0;
            return false;
        }

        // ---- party pool v1 ----
        public List<(PartyPoolOpKind Kind, PartyPoolStackKey Key, uint CountDelta)> PublishedPartyPoolOps { get; } = [];

        public void PublishPartyPoolOp(PartyPoolOpKind kind, PartyPoolStackKey key, uint countDelta) =>
            PublishedPartyPoolOps.Add((kind, key, countDelta));

        // ---- party pool v2 ----
        public List<IReadOnlyDictionary<PartyPoolStackKey, int>> PublishedPartyPoolSnapshotsV2 { get; } = [];

        public void PublishPartyPoolSnapshotV2(IReadOnlyDictionary<PartyPoolStackKey, int> totals) =>
            PublishedPartyPoolSnapshotsV2.Add(totals);

        // ---- store v2 ----
        public List<(uint StoreObjectId, IReadOnlyDictionary<StoreStackKey, int> Totals)> PublishedStoreSnapshotsV2 { get; } = [];

        public void PublishStoreSnapshotV2(uint storeObjectId, IReadOnlyDictionary<StoreStackKey, int> totals) =>
            PublishedStoreSnapshotsV2.Add((storeObjectId, totals));

        // ---- dialogue ----
        public List<bool> PublishedDialogueActive { get; } = [];

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = -1, uint nodeSerial = 0, bool forced = false) =>
            PublishedDialogueActive.Add(active);

        public DialogueState GetDialogue() => DialogueState.Idle;

        // ---- interaction locks ----
        public int PublishedInteractionClaims { get; private set; }

        public void PublishInteractionClaim(
            string module, string tag, InteractionKind kind, bool active, uint targetId = 0) =>
            PublishedInteractionClaims++;

        public IReadOnlyList<InteractionLock> GetInteractionLocks() => [];

        public void SendChat(string text)
        {
        }

        public IReadOnlyList<ChatMessage> DrainChat() => [];

        public void SetJournalEntry(string questTag, int state)
        {
        }

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static AgentSettings Settings() => new() { DialogueFastJoin = false };

    private static PartyPoolStackKey SomeKey() => new("g_i_drdplt001", 0, 0, 0, 0);

    private static StoreStackKey SomeStoreKey(uint storeId) => new(storeId, "g_i_drdplt001", 0, 0, 0, 0);

    // ---- PartyPump.PumpProgression -------------------------------------------------------

    [Fact]
    public void ProgressionHostWithholdsPublishInAStuntModuleAndResumesInARealOne()
    {
        StuntGateBridge bridge = new() { Module = StuntModule, Gold = 1000 };
        StuntGateSession session = new() { EnemyAuthority = true };
        PartyPump pump = new(bridge, session, Settings(), _ => { });

        pump.PumpProgression(SessionCapabilities.All, StuntModule);

        Assert.Empty(session.PublishedProgression);
        Assert.Empty(bridge.ProgressionWrites);

        pump.PumpProgression(SessionCapabilities.All, RealModule);

        Assert.Single(session.PublishedProgression);
    }

    // ---- PartyPump.PumpParty ---------------------------------------------------------------

    [Fact]
    public void PartyHostWithholdsCompanionPublishInAStuntModuleAndResumesInARealOne()
    {
        StuntGateBridge bridge = new()
        {
            Module = StuntModule,
            LocalPartyMembers = [new PartyMemberState(1, Vector3f.Zero, 0f, 100, 100, 0)],
        };
        StuntGateSession session = new() { EnemyAuthority = true };
        PartyPump pump = new(bridge, session, Settings(), _ => { });

        pump.PumpParty(SessionCapabilities.All, StuntModule);

        Assert.Empty(session.PublishedParty);
        Assert.Equal(0, bridge.ReadPartyMembersCalls);

        pump.PumpParty(SessionCapabilities.All, RealModule);

        Assert.Single(session.PublishedParty);
        Assert.Equal(1, bridge.ReadPartyMembersCalls);
    }

    // ---- PartyPump.PumpPartyRoster ---------------------------------------------------------

    [Fact]
    public void PartyRosterHostWithholdsPublishInAStuntModuleAndResumesInARealOne()
    {
        StuntGateBridge bridge = new() { Module = StuntModule };
        StuntGateSession session = new() { EnemyAuthority = true };
        PartyPump pump = new(bridge, session, Settings(), _ => { });

        pump.PumpPartyRoster(SessionCapabilities.All, StuntModule);

        // Held, not cleared: the stunt gate must not even reach the "no authority speaking
        // here" WriteHostPartyRoster(false, Empty) write — the last real roster stays exactly
        // as it was published.
        Assert.Empty(session.PublishedRoster);
        Assert.Empty(bridge.HostRosterWrites);

        pump.PumpPartyRoster(SessionCapabilities.All, RealModule);

        Assert.Single(session.PublishedRoster);
    }

    // ---- PartyPoolPumpV2.PumpHost -----------------------------------------------------------

    [Fact]
    public void PartyPoolV2HostWithholdsSnapshotPublishInAStuntModuleAndResumesInARealOne()
    {
        StuntGateBridge bridge = new()
        {
            Module = StuntModule,
            PartyPoolScan = new Dictionary<PartyPoolStackKey, int> { [SomeKey()] = 5 },
        };
        StuntGateSession session = new() { EnemyAuthority = true };
        PartyPoolPumpV2 pump = new(bridge, session, _ => { });

        pump.Pump(SessionCapabilities.All, StuntModule);

        Assert.Empty(session.PublishedPartyPoolSnapshotsV2);
        Assert.Equal(0, bridge.PartyPoolScanCalls);

        pump.Pump(SessionCapabilities.All, RealModule);

        Assert.Single(session.PublishedPartyPoolSnapshotsV2);
    }

    // ---- PartyPoolPump (v1) ------------------------------------------------------------------

    [Fact]
    public void PartyPoolV1WithholdsDetectionInAStuntModuleAndResumesInARealOne()
    {
        StuntGateBridge bridge = new() { Module = StuntModule };
        StuntGateSession session = new() { EnemyAuthority = true };
        PartyPoolPump pump = new(bridge, session, _ => { });

        // Baseline (the pump's own first-scan-is-silent rule) — while still in a real module,
        // so the very next call has something to compare against.
        bridge.Module = RealModule;
        pump.Pump(SessionCapabilities.All, RealModule);
        Assert.Equal(1, bridge.PartyPoolScanCalls);

        // A genuine local change appears while the module is a stunt one — must not even be
        // scanned, let alone published.
        bridge.Module = StuntModule;
        bridge.PartyPoolScan = new Dictionary<PartyPoolStackKey, int> { [SomeKey()] = 3 };
        pump.Pump(SessionCapabilities.All, StuntModule);

        Assert.Empty(session.PublishedPartyPoolOps);
        Assert.Equal(1, bridge.PartyPoolScanCalls);

        // Back in a real module: the same standing change is now genuinely detected.
        bridge.Module = RealModule;
        pump.Pump(SessionCapabilities.All, RealModule);

        Assert.Single(session.PublishedPartyPoolOps);
    }

    // ---- StorePumpV2.PumpHost -----------------------------------------------------------------

    [Fact]
    public void StoreV2HostWithholdsSnapshotPublishInAStuntModuleAndResumesInARealOne()
    {
        StuntGateBridge bridge = new()
        {
            Module = StuntModule,
            StoreOpen = true,
            OpenStoreId = 0x131,
            StoreScan = new Dictionary<StoreStackKey, int> { [SomeStoreKey(0x131)] = 2 },
        };
        StuntGateSession session = new() { EnemyAuthority = true };
        StorePumpV2 pump = new(bridge, session, _ => { });

        pump.Pump(SessionCapabilities.All, StuntModule);

        Assert.Empty(session.PublishedStoreSnapshotsV2);
        Assert.Equal(0, bridge.StoreScanCalls);

        pump.Pump(SessionCapabilities.All, RealModule);

        Assert.Single(session.PublishedStoreSnapshotsV2);
    }

    // ---- DialoguePump.PumpDialogue --------------------------------------------------------

    [Fact]
    public void DialoguePublishesInactiveWhileLocalIsAStuntModuleAndResumesInARealOne()
    {
        StuntGateBridge bridge = new() { Module = StuntModule };
        StuntGateSession session = new();
        DialoguePump pump = new(bridge, session, Settings(), _ => { });

        // The engine reads "in a conversation" — the scripted cutscene entry — while the local
        // module is a stunt one.
        PlayerState inCutscene = new(Vector3f.Zero, 0f, StuntModule, 20, 0, Flags: PlayerState.FlagInDialogue);
        pump.PumpDialogue(inCutscene, SessionCapabilities.All);

        Assert.Single(session.PublishedDialogueActive);
        Assert.False(session.PublishedDialogueActive[0]);

        // Back in a real module, a genuine conversation publishes as active exactly as before.
        PlayerState inRealConversation = new(Vector3f.Zero, 0f, RealModule, 20, 0, Flags: PlayerState.FlagInDialogue);
        pump.PumpDialogue(inRealConversation, SessionCapabilities.All);

        Assert.Equal(2, session.PublishedDialogueActive.Count);
        Assert.True(session.PublishedDialogueActive[1]);
    }

    // ---- WorldStatePump.PumpInteractionLocks ----------------------------------------------

    [Fact]
    public void InteractionLocksAreNeitherPublishedNorAppliedInAStuntModuleAndResumeInARealOne()
    {
        StuntGateBridge bridge = new() { Module = StuntModule };
        StuntGateSession session = new();
        WorldStatePump pump = new(bridge, session, Settings(), new RecentlySyncedItems());

        PlayerState inStunt = new(Vector3f.Zero, 0f, StuntModule, 20, 0);
        pump.PumpInteractionLocks(inStunt);

        Assert.Equal(0, bridge.InteractionReadCalls);
        Assert.Equal(0, bridge.InteractionWriteCalls);
        Assert.Equal(0, session.PublishedInteractionClaims);

        PlayerState inRealModule = new(Vector3f.Zero, 0f, RealModule, 20, 0);
        pump.PumpInteractionLocks(inRealModule);

        Assert.Equal(1, bridge.InteractionReadCalls);
        Assert.Equal(1, bridge.InteractionWriteCalls);
        Assert.Equal(1, session.PublishedInteractionClaims);
    }
}
