using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// End-to-end (through the real <see cref="CoopAgent.RunAsync"/> timer loop, not just
/// <see cref="PumpFailureTracker"/> in isolation) coverage of the recovery/notice wiring:
/// <see cref="IGameBridge.ResetCaches"/> actually gets called once the same exception type has
/// repeated, and it is <see cref="PumpFailureTracker.ResetCachesAfterSameFailures"/> that
/// governs when — reached quickly at any poll rate, unlike the ~10s shutdown ceiling
/// (<see cref="PumpFailureTrackerTests.ShutdownFiresAtTenSecondsWorthOfTicksRegardlessOfPollRate"/>
/// covers that decision without a real timer, because driving the ACTUAL 10s through
/// <see cref="CoopAgent.RunAsync"/> would make this test file itself take 10 seconds).
/// </summary>
public class CoopAgentPumpFailureRecoveryTests
{
    private sealed class ThrowingBridge : IGameBridge
    {
        /// <summary>0 = never throw (a normal successful pump tick).</summary>
        public int ThrowUntilCallNumber;

        private int _calls;

        public int ResetCachesCalls { get; private set; }

        public System.Collections.Generic.List<string> ChatLines { get; } = [];

        public string Description => "throwing-bridge-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            _calls++;
            if (_calls <= ThrowUntilCallNumber)
            {
                // The exact exception the live bug threw (KotorGameBridge.EnumerateCreatures's
                // _scanCache/_creatureCache torn between two threads) — this test proves the
                // RECOVERY wiring, not the race itself (see BridgeConcurrencyTests for that).
                throw new InvalidOperationException(
                    "A concurrent update was performed on this collection and corrupted its state.");
            }

            state = new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public void ResetCaches() => ResetCachesCalls++;

        public bool PostChatLine(string line)
        {
            ChatLines.Add(line);
            return true;
        }

        public void Dispose() { }
    }

    private sealed class NoopSession : ICoopSession
    {
        public uint LocalPeerId => 1;

        public bool IsConnected => true;

        public bool HostLost => false;

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

        public void PublishLocalState(in PlayerState state) { }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

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
    public async Task RepeatedIdenticalPumpFailuresResetTheBridgesCaches()
    {
        // A high poll rate so PumpFailureTracker.ResetCachesAfterSameFailures ticks elapse in
        // well under a second of real wall-clock time (the ~10s shutdown ceiling deliberately
        // is NOT exercised here — see this file's class remarks).
        ThrowingBridge bridge = new() { ThrowUntilCallNumber = PumpFailureTracker.ResetCachesAfterSameFailures };
        NoopSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings { PollRate = 200 });

        System.Collections.Generic.List<string> logLines = [];
        agent.Log = line => { lock (logLines) { logLines.Add(line); } };

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        // Stop the loop the tick after the reset fires, rather than running until the token's
        // own 5s safety deadline: cancelling from inside the pump's own success path is the
        // first point at which "the reset actually worked" is provable (the next tick pumped
        // cleanly with the SAME bridge instance, uncorrupted caches or not).
        int successesAfterReset = 0;
        agent.LocalStateRead += _ =>
        {
            if (bridge.ResetCachesCalls > 0 && ++successesAfterReset >= 1)
            {
                cts.Cancel();
            }
        };

        await agent.RunAsync(cts.Token);

        Assert.Equal(1, bridge.ResetCachesCalls);
        Assert.Contains(logLines, l => l.Contains("resetting the bridge's caches", StringComparison.Ordinal));

        // First failure logs the FULL exception (stack trace / type name included via ToString()),
        // not just ex.Message — that is what lets a future occurrence name the collection.
        Assert.Contains(
            logLines,
            l => l.Contains("pump tick failed (1 in a row):", StringComparison.Ordinal)
                && l.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABridgeThatNeverFailsNeverResetsItsCaches()
    {
        ThrowingBridge bridge = new() { ThrowUntilCallNumber = 0 };
        NoopSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings { PollRate = 200 });

        using CancellationTokenSource cts = new();
        int ticks = 0;
        agent.LocalStateRead += _ =>
        {
            if (++ticks >= 10)
            {
                cts.Cancel();
            }
        };

        await agent.RunAsync(cts.Token);

        Assert.Equal(0, bridge.ResetCachesCalls);
        Assert.Empty(bridge.ChatLines);
    }
}
