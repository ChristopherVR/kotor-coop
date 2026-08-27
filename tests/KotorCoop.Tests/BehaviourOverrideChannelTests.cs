using KotorCoop.Memory;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Roadmap item 7's closed gap (docs/research/mod-compatibility.md's "Behaviour-flag
/// negotiation"): five host-authoritative coop.ini keys (effect_sync, trigger_relay,
/// liveness_sync, map_reveal_sync, dialogue_recruit_sync) are adopted into
/// <see cref="SessionOverrides"/> but were never carried into the SEPARATE plugin process
/// that actually gates each channel. Covers the three pieces that closed it: the pure
/// encode (<see cref="BehaviourOverrideEncoding"/>) and decode (<see cref="BehaviourOverrideRule"/>)
/// halves round-trip, and <see cref="CoopAgent"/> pushes the encoded bits through
/// <see cref="IGameBridge.SetBehaviourOverrides"/> only when they actually change.
/// </summary>
public sealed class BehaviourOverrideChannelTests
{
    // ---- pure rule: override vs local (plugin side) --------------------------------------

    [Fact]
    public void ResolveUsesTheLocalValueWhenTheBitIsNotOverridden()
    {
        Assert.True(BehaviourOverrideRule.Resolve(mask: 0, values: 0, bit: 1, localValue: true));
        Assert.False(BehaviourOverrideRule.Resolve(mask: 0, values: 0, bit: 1, localValue: false));
    }

    [Fact]
    public void ResolveUsesTheOverrideValueWhenTheBitIsOverridden()
    {
        // Overridden ON, local OFF: the host's value wins.
        Assert.True(BehaviourOverrideRule.Resolve(mask: 1, values: 1, bit: 1, localValue: false));

        // Overridden OFF, local ON: the host's value still wins.
        Assert.False(BehaviourOverrideRule.Resolve(mask: 1, values: 0, bit: 1, localValue: true));
    }

    [Fact]
    public void ResolveOnlyEverLooksAtItsOwnBit()
    {
        // Bit 2 (trigger_relay) is overridden; bit 1 (effect_sync) is not, and must fall
        // through to the local value regardless of what bit 2's paired value says.
        uint mask = BehaviourOverrideBits.TriggerRelay;
        uint values = BehaviourOverrideBits.TriggerRelay;

        Assert.True(BehaviourOverrideRule.Resolve(mask, values, BehaviourOverrideBits.EffectSync, true));
        Assert.False(BehaviourOverrideRule.Resolve(mask, values, BehaviourOverrideBits.EffectSync, false));
        Assert.True(BehaviourOverrideRule.Resolve(mask, values, BehaviourOverrideBits.TriggerRelay, false));
    }

    [Fact]
    public void IsOverriddenReportsWhetherTheKeyHasAnOverrideAtAll()
    {
        Assert.False(BehaviourOverrideRule.IsOverridden(mask: 0, bit: BehaviourOverrideBits.LivenessSync));
        Assert.True(BehaviourOverrideRule.IsOverridden(
            mask: BehaviourOverrideBits.LivenessSync, bit: BehaviourOverrideBits.LivenessSync));
    }

    // ---- encode/decode round-trip ---------------------------------------------------------

    [Fact]
    public void SessionOverridesNoneEncodesToAllZero()
    {
        (uint mask, uint values) = BehaviourOverrideEncoding.Encode(SessionOverrides.None);

        Assert.Equal(0u, mask);
        Assert.Equal(0u, values);
    }

    [Fact]
    public void EncodeRoundTripsEveryKeyIndependently()
    {
        SessionOverrides overrides = new(
            EffectSync: true,
            TriggerRelay: false,
            LivenessSync: true,
            MapRevealSync: false,
            DialogueRecruitSync: true);

        (uint mask, uint values) = BehaviourOverrideEncoding.Encode(overrides);

        Assert.True(BehaviourOverrideRule.Resolve(mask, values, BehaviourOverrideBits.EffectSync, localValue: false));
        Assert.False(BehaviourOverrideRule.Resolve(mask, values, BehaviourOverrideBits.TriggerRelay, localValue: true));
        Assert.True(BehaviourOverrideRule.Resolve(mask, values, BehaviourOverrideBits.LivenessSync, localValue: false));
        Assert.False(BehaviourOverrideRule.Resolve(mask, values, BehaviourOverrideBits.MapRevealSync, localValue: true));
        Assert.True(BehaviourOverrideRule.Resolve(
            mask, values, BehaviourOverrideBits.DialogueRecruitSync, localValue: false));
    }

    [Fact]
    public void EncodeLeavesAnUnsetKeyDeferringToLocal()
    {
        // Only EffectSync is adopted; every other key must fall through to whatever the
        // caller's own local coop.ini value says, in both directions.
        SessionOverrides overrides = new(EffectSync: true);

        (uint mask, uint values) = BehaviourOverrideEncoding.Encode(overrides);

        Assert.True(BehaviourOverrideRule.Resolve(mask, values, BehaviourOverrideBits.EffectSync, localValue: false));
        Assert.True(BehaviourOverrideRule.Resolve(mask, values, BehaviourOverrideBits.TriggerRelay, localValue: true));
        Assert.False(BehaviourOverrideRule.Resolve(mask, values, BehaviourOverrideBits.TriggerRelay, localValue: false));
    }

    [Fact]
    public void EncodeIgnoresTheFourKeysWithNoPluginSideBit()
    {
        // PartySync/ScriptItemSync/DialogueItemSync/MovieRelay are consulted entirely inside
        // KotorCoop.Session's own pumps and must not leak a bit into the plugin's block.
        SessionOverrides overrides = new(
            PartySync: true, ScriptItemSync: true, DialogueItemSync: true, MovieRelay: true);

        (uint mask, uint values) = BehaviourOverrideEncoding.Encode(overrides);

        Assert.Equal(0u, mask);
        Assert.Equal(0u, values);
    }

    // ---- CoopAgent: writes on change, silent when unchanged --------------------------------

    private const string Module = "end_m01aa";

    private sealed class RecordingBridge : IGameBridge
    {
        public List<(uint Mask, uint Values)> Writes { get; } = [];

        public string Description => "behaviour-override-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, Module, 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public void SetBehaviourOverrides(uint overrideMask, uint overrideValues) =>
            Writes.Add((overrideMask, overrideValues));

        public void Dispose()
        {
        }
    }

    private sealed class StubSession : ICoopSession
    {
        public SessionOverrides Overrides { get; set; } = SessionOverrides.None;

        public uint LocalPeerId => 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public JournalStore Journal { get; } = new();

        public bool IsEnemyAuthority => false;

        public SessionCapabilities Capabilities { get; set; } = SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public void PublishParty(IReadOnlyList<PartyMemberState> members) { }

        public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        public void PublishProgression(uint gold, uint experience) { }

        public bool TryGetProgression(out uint gold, out uint experience)
        {
            gold = 0;
            experience = 0;
            return false;
        }

        public void PublishCompanionExperience(ushort presentMask, IReadOnlyList<uint> xpBySlot) { }

        public void ReportCompanionExperience(ushort presentMask, IReadOnlyList<uint> xpBySlot) { }

        public IReadOnlyList<PeerCompanionXp> GetCompanionExperienceReports() => [];

        public bool TryGetCompanionExperience(out ushort presentMask, out uint[] xpBySlot)
        {
            presentMask = 0;
            xpBySlot = new uint[ProtocolConstants.PartyRosterSlots];
            return false;
        }

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state) { }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public void SetFlag(uint flagId, sbyte value) { }

        public uint DropItem(
            string resref, string module, Vector3f position, ItemIdentity identity = default) => 0;

        public void RemoveItem(uint itemId) { }

        public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies) { }

        public IReadOnlyList<EnemyState> GetEnemies(string module) => [];

        public void PublishWorldDigest(in WorldDigest digest) { }

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = -1, uint nodeSerial = 0, bool forced = false) { }

        public DialogueState GetDialogue() => DialogueState.Idle;

        public void SendChat(string text) { }

        public IReadOnlyList<ChatMessage> DrainChat() => [];

        public void SetJournalEntry(string questTag, int state) { }

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() { }
    }

    private static (CoopAgent Agent, RecordingBridge Bridge, StubSession Session) Build()
    {
        RecordingBridge bridge = new();
        StubSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings { HeavyPumpIntervalMs = 0 });
        return (agent, bridge, session);
    }

    [Fact]
    public void NoOverridesNeverWritesAnything()
    {
        (CoopAgent agent, RecordingBridge bridge, _) = Build();

        agent.PumpOnce();
        agent.PumpOnce();

        // SessionOverrides.None encodes to (0, 0) - the block's own post-teardown/never-
        // written state - so there is nothing to tell the plugin.
        Assert.Empty(bridge.Writes);
    }

    [Fact]
    public void AnAdoptedOverrideIsWrittenOnceOnTheTickItAppears()
    {
        (CoopAgent agent, RecordingBridge bridge, StubSession session) = Build();

        agent.PumpOnce();
        Assert.Empty(bridge.Writes);

        session.Overrides = new SessionOverrides(MapRevealSync: true);
        agent.PumpOnce();

        (uint mask, uint values) = Assert.Single(bridge.Writes);
        Assert.Equal(BehaviourOverrideBits.MapRevealSync, mask);
        Assert.Equal(BehaviourOverrideBits.MapRevealSync, values);
    }

    [Fact]
    public void AnUnchangedOverrideIsNeverRewritten()
    {
        (CoopAgent agent, RecordingBridge bridge, StubSession session) = Build();

        session.Overrides = new SessionOverrides(TriggerRelay: false);
        agent.PumpOnce();
        agent.PumpOnce();
        agent.PumpOnce();

        // One write for the change from (0,0) to the new state, then silence.
        Assert.Single(bridge.Writes);
    }

    [Fact]
    public void ARevertToNoOverridesIsItselfWritten()
    {
        (CoopAgent agent, RecordingBridge bridge, StubSession session) = Build();

        session.Overrides = new SessionOverrides(EffectSync: true);
        agent.PumpOnce();
        Assert.Single(bridge.Writes);

        // A reconnect (or the host's Welcome no longer differing) drops the override — the
        // agent must tell the plugin, not just stop mentioning it.
        session.Overrides = SessionOverrides.None;
        agent.PumpOnce();

        Assert.Equal(2, bridge.Writes.Count);
        (uint mask, uint values) = bridge.Writes[^1];
        Assert.Equal(0u, mask);
        Assert.Equal(0u, values);
    }
}
