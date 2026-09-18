using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <c>coop.ini party_fast_pump</c> end to end at the <see cref="CoopAgent"/> layer — the same
/// move <see cref="AgentSettings.DialogueFastJoin"/> made, applied to the party channel
/// (<c>PumpParty</c>/<c>PumpPartyRoster</c>): docs/research/lag-audit.md names two
/// independently-configured 150 ms windows stacking on the companion-mirror path (this pump,
/// then <c>PartyPresenceController</c>'s own <c>mirror_smooth_ms</c> smoother) as its suspect
/// #2 for companion mirroring reading "laggier" than a peer's own avatar.
///
/// <see cref="AgentSettings.CombatPumpIntervalMs"/> is set to an effectively infinite value in
/// every test here — the same technique <see cref="DialogueFastJoinPumpTests"/> uses — so that
/// when the fast-pump tests still see a party write, it is provably NOT the combat gate firing.
/// </summary>
public sealed class PartyFastPumpTests
{
    private const string ClientModule = "end_m01aa";

    private sealed class MinimalBridge : IGameBridge
    {
        public PlayerState Local { get; set; } = new(Vector3f.Zero, 0f, ClientModule, 20, 0);

        public string Description => "party-fast-pump-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsPartySync => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        /// <summary>Every call this stub received, so a test can tell "gated by the combat
        /// cadence" from "runs every tick" without caring what the party members actually
        /// are.</summary>
        public int TryWritePartyMembersCallCount { get; private set; }

        public int TryWritePartyMembers(IReadOnlyList<PartyMemberState> members)
        {
            TryWritePartyMembersCallCount++;
            return members.Count;
        }

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

        // A client, matching PumpParty's/PumpPartyRoster's "mirror, do not own" path — the
        // one PartyFastPump actually changes the cadence of.
        public bool IsEnemyAuthority => false;

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state)
        {
        }

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

        public JournalStore Journal { get; } = new();

        public void PublishWorldDigest(in WorldDigest digest) { }

        public void PublishParty(IReadOnlyList<PartyMemberState> members) { }

        public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = -1, uint nodeSerial = 0, bool forced = false)
        {
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

    /// <summary>Same rationale as <see cref="DialogueFastJoinPumpTests"/>'s own constant: the
    /// production default, comfortably below any real machine uptime and comfortably above the
    /// gap between two synchronous <c>PumpOnce()</c> calls in a tight test loop.</summary>
    private const int DefaultCombatPumpIntervalMs = 150;

    private static (CoopAgent Agent, MinimalBridge Bridge, StubSession Session) Build(bool fastPump)
    {
        MinimalBridge bridge = new();
        StubSession session = new();
        CoopAgent agent = new(
            bridge, session,
            new AgentSettings
            {
                CombatPumpIntervalMs = DefaultCombatPumpIntervalMs,
                PartyFastPump = fastPump,
            });

        return (agent, bridge, session);
    }

    [Fact]
    public void FastPumpOffLeavesPartyOnTheLegacyCombatCadence()
    {
        (CoopAgent agent, MinimalBridge bridge, _) = Build(fastPump: false);

        agent.PumpOnce(); // the combat gate is wide open on tick one, so this still writes once
        Assert.Equal(1, bridge.TryWritePartyMembersCallCount);

        // Three more ticks, still well inside the 150 ms combat window a tight synchronous
        // test loop cannot itself cross: the OLD gate does not open again.
        agent.PumpOnce();
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Equal(1, bridge.TryWritePartyMembersCallCount);
    }

    /// <summary>⚠ THE ACTUAL FIX. With the combat cadence effectively infinite (so the legacy
    /// gate could not possibly be what is firing), the party channel still writes on every
    /// single <c>PumpOnce()</c> — only possible because <c>party_fast_pump</c> moved it off
    /// that cadence entirely, the same way <c>dialogue_fast_join</c> already did for
    /// dialogue.</summary>
    [Fact]
    public void FastPumpWritesPartyOnEveryTick()
    {
        (CoopAgent agent, MinimalBridge bridge, _) = Build(fastPump: true);

        agent.PumpOnce();
        agent.PumpOnce();
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Equal(4, bridge.TryWritePartyMembersCallCount);
    }
}
