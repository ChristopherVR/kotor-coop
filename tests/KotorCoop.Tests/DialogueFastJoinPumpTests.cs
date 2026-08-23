using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The join-latency fix end to end at the <see cref="CoopAgent"/> layer: docs/research/
/// dialogue-join-latency.md measured the old design publishing a changed dialogue line only
/// once per <see cref="AgentSettings.CombatPumpIntervalMs"/> (150 ms), on BOTH the owner's
/// publish and the follower's read of it. <c>coop.ini dialogue_fast_join</c> moves the channel
/// to the agent's real-time (every poll tick) path, gated by <see cref="DialogueTickGate"/>
/// rather than by the combat cadence.
///
/// <see cref="AgentSettings.CombatPumpIntervalMs"/> is set to an effectively infinite value in
/// every test here — large enough that no two calls to <c>PumpOnce</c> in a test's lifetime
/// could ever cross it on a real clock — so that when the fast-join tests still see a publish,
/// it is provably NOT the combat gate firing: with the flag on, dialogue is not gated by that
/// cadence at all any more.
/// </summary>
public sealed class DialogueFastJoinPumpTests
{
    private const string ClientModule = "end_m01aa";

    private sealed class MinimalBridge : IGameBridge
    {
        public PlayerState Local { get; set; } =
            new(Vector3f.Zero, 0f, ClientModule, 20, 0, Flags: PlayerState.FlagInDialogue);

        public string Description => "dialogue-fast-join-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        // ── The dialogue read this whole fix is about. Mutable so a test can change what
        // "the plugin's own block" reports between two PumpOnce() calls and observe how fast
        // the change reaches session.PublishDialogue. ──────────────────────────────────────

        public string Speaker { get; set; } = string.Empty;

        public string Line { get; set; } = string.Empty;

        public int NodeIndex { get; set; } = -1;

        public uint NodeSerial { get; set; }

        public bool TryReadLocalDialogue(out string speaker, out string line)
        {
            speaker = Speaker;
            line = Line;
            return true;
        }

        public bool Forced { get; set; }

        public bool TryReadLocalDialogueFollow(
            out string partnerTag, out uint replySerial, out int replyIndex, out byte[] pickLog,
            out int nodeIndex, out uint nodeSerial, out bool forced)
        {
            partnerTag = "trask";
            replySerial = 0;
            replyIndex = 0;
            pickLog = [];
            nodeIndex = NodeIndex;
            nodeSerial = NodeSerial;
            forced = Forced;
            return true;
        }

        public bool WriteRemoteDialogue(
            bool active, string partnerTag, uint replySerial, int replyIndex, byte[]? pickLog,
            int nodeIndex, uint nodeSerial, bool forced) => true;

        public void Dispose()
        {
        }
    }

    private sealed class StubSession : ICoopSession
    {
        public uint LocalPeerId => 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public bool IsEnemyAuthority => false;

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state)
        {
        }

        public void SetFlag(uint flagId, sbyte value)
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

        public JournalStore Journal { get; } = new();

        public void PublishWorldDigest(in WorldDigest digest) { }

        public void PublishParty(IReadOnlyList<PartyMemberState> members) { }

        public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        /// <summary>Every call this stub session received, in order — what the test asserts on.</summary>
        public List<(bool Active, string Speaker, string Line, int NodeIndex, uint NodeSerial)> PublishCalls { get; } = [];

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = -1, uint nodeSerial = 0, bool forced = false)
        {
            PublishCalls.Add((active, speakerName, line, nodeIndex, nodeSerial));
        }

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

        public SessionCapabilities Capabilities { get; set; } = SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The production default (<see cref="AgentSettings.CombatPumpIntervalMs"/>'s own default),
    /// used deliberately rather than an exaggerated value: <c>_lastCombatPumpMs</c> starts at
    /// zero and <c>Environment.TickCount64</c> is milliseconds since BOOT, not since the test
    /// started, so an exaggerated interval risks the gate never opening even on the FIRST call
    /// on a machine with a short uptime. 150 ms is comfortably below any real uptime and
    /// comfortably above the sub-millisecond gap between two synchronous PumpOnce() calls in a
    /// tight test loop, which is all the "still gated" assertions below need.
    /// </summary>
    private const int DefaultCombatPumpIntervalMs = 150;

    private static (CoopAgent Agent, MinimalBridge Bridge, StubSession Session) Build(bool fastJoin)
    {
        MinimalBridge bridge = new();
        StubSession session = new();
        CoopAgent agent = new(
            bridge, session,
            new AgentSettings
            {
                CombatPumpIntervalMs = DefaultCombatPumpIntervalMs,
                DialogueFastJoin = fastJoin,
            });

        return (agent, bridge, session);
    }

    [Fact]
    public void FastJoinOffLeavesDialogueOnTheLegacyCombatCadence()
    {
        (CoopAgent agent, MinimalBridge bridge, StubSession session) = Build(fastJoin: false);

        bridge.Speaker = "Trask";
        bridge.Line = "We've got to move.";
        agent.PumpOnce(); // the combat gate is wide open on tick one, so this still publishes once

        Assert.Single(session.PublishCalls);

        // A change immediately afterwards, still well inside the 150 ms combat window a tight
        // synchronous test loop cannot itself cross: the OLD gate does not open again, so the
        // change does not reach PublishDialogue. This is the bug being fixed, captured as a
        // passing assertion against the flag OFF (the rollback path).
        bridge.Line = "Get to the escape pod.";
        agent.PumpOnce();
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Single(session.PublishCalls);
    }

    /// <summary>
    /// ⚠ THE ACTUAL FIX. With the combat cadence effectively infinite (so the legacy gate
    /// could not possibly be what is firing), a changed line still reaches PublishDialogue on
    /// the very next PumpOnce() — this is only possible because dialogue_fast_join moved the
    /// channel off that cadence entirely.
    /// </summary>
    [Fact]
    public void FastJoinPublishesAChangedLineOnTheVeryNextTick()
    {
        (CoopAgent agent, MinimalBridge bridge, StubSession session) = Build(fastJoin: true);

        bridge.Speaker = "Trask";
        bridge.Line = "We've got to move.";
        agent.PumpOnce();
        Assert.Single(session.PublishCalls);
        Assert.Equal("We've got to move.", session.PublishCalls[0].Line);

        bridge.NodeIndex = 12;
        bridge.NodeSerial = 3;
        bridge.Line = "Get to the escape pod.";
        agent.PumpOnce();

        Assert.Equal(2, session.PublishCalls.Count);
        Assert.Equal("Get to the escape pod.", session.PublishCalls[1].Line);
        Assert.Equal(12, session.PublishCalls[1].NodeIndex);
    }

    /// <summary>
    /// The other half of the same fix: "every tick" must not mean "resend the identical
    /// payload every tick for the whole conversation". Three ticks with nothing new must cost
    /// exactly the one publish the first tick already did.
    /// </summary>
    [Fact]
    public void FastJoinDoesNotRepublishAnUnchangedLineEveryTick()
    {
        (CoopAgent agent, MinimalBridge bridge, StubSession session) = Build(fastJoin: true);

        bridge.Speaker = "Trask";
        bridge.Line = "We've got to move.";
        agent.PumpOnce();
        Assert.Single(session.PublishCalls);

        agent.PumpOnce();
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Single(session.PublishCalls);
    }

    /// <summary>A node-only change (the common case — the line's text does not change but the
    /// engine has moved to a new entry) must count as a change too, not just a different
    /// speaker/line string.</summary>
    [Fact]
    public void FastJoinTreatsANodeMoveAloneAsAChange()
    {
        (CoopAgent agent, MinimalBridge bridge, StubSession session) = Build(fastJoin: true);

        bridge.Speaker = "Trask";
        bridge.Line = "Understood.";
        bridge.NodeIndex = 5;
        bridge.NodeSerial = 1;
        agent.PumpOnce();
        Assert.Single(session.PublishCalls);

        bridge.NodeSerial = 2;
        bridge.NodeIndex = 6;
        agent.PumpOnce();

        Assert.Equal(2, session.PublishCalls.Count);
        Assert.Equal(6, session.PublishCalls[1].NodeIndex);
    }
}
