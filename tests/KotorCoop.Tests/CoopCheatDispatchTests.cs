using KotorCoop.Agent;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The agent half of the staging cheats: what actually reaches the plugin when a line is
/// typed, and — just as important — what does NOT, because a cheat aimed at something the
/// caller does not own has to stop before it becomes an engine call.
/// </summary>
public class CoopCheatDispatchTests
{
    /// <summary>A game that records the cheats filed with it and can answer them.</summary>
    private sealed class CheatBridge : IGameBridge
    {
        public readonly List<CheatRequest> Requests = [];

        public List<EnemyState> Enemies = [];

        public bool Supports = true;

        public int AnswerReads;

        public uint AnsweredSerial;

        public string Description => "cheat-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsCheats => Supports;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public List<EnemyState> ReadEnemies(IReadOnlyList<Vector3f> otherPlayers) => Enemies;

        public bool TryRequestCheat(CheatRequest request, out uint serial)
        {
            serial = 0;
            if (!Supports)
            {
                return false;
            }

            Requests.Add(request);
            serial = (uint)Requests.Count;
            return true;
        }

        public bool TryReadCheatAnswer(out uint applied, out int status, out string detail)
        {
            AnswerReads++;
            applied = AnsweredSerial;
            status = CheatStatus.Applied;
            detail = "did the thing";
            return true;
        }

        public void Dispose()
        {
        }
    }

    private static (CoopCommandDispatcher Dispatcher, CheatBridge Bridge, SessionManager Manager) NewDispatcher()
    {
        CheatBridge bridge = new();
        SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, 27140,
            password => new SessionSettings { PlayerName = "Tester", Port = 0, TickRate = 30, Password = password });

        return (dispatcher, bridge, manager);
    }

    private static void Type(CoopCommandDispatcher dispatcher, string line) =>
        dispatcher.Dispatch(ConsoleCommand.Parse(line));

    [Fact]
    public void SelfCheatsReachThePluginWithTheirArguments()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, "god on");
            Type(dispatcher, "topup 25");
            Type(dispatcher, "standup");
            Type(dispatcher, "tp 3 -4 5");
            Type(dispatcher, "dropitem g_w_blstrpstl001");

            Assert.Equal(5, bridge.Requests.Count);

            Assert.Equal(CheatKinds.God, bridge.Requests[0].Kind);
            Assert.Equal(1, bridge.Requests[0].Amount);

            Assert.Equal(CheatKinds.TopUp, bridge.Requests[1].Kind);
            Assert.Equal(25, bridge.Requests[1].Amount);

            Assert.Equal(CheatKinds.StandUp, bridge.Requests[2].Kind);

            Assert.Equal(CheatKinds.Teleport, bridge.Requests[3].Kind);
            Assert.Equal(3f, bridge.Requests[3].X);
            Assert.Equal(-4f, bridge.Requests[3].Y);
            Assert.Equal(5f, bridge.Requests[3].Z);

            Assert.Equal(CheatKinds.DropItem, bridge.Requests[4].Kind);
            Assert.Equal("g_w_blstrpstl001", bridge.Requests[4].Text);
        }
    }

    /// <summary>
    /// A bare <c>slay</c> is resolved HERE, because the agent is the side that runs the enemy
    /// census. The plugin is handed a number, not a policy.
    /// </summary>
    [Fact]
    public void BareSlayPicksTheNearestLivingEnemy()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            bridge.Enemies =
            [
                new EnemyState(0x10, 12, 12, false, new Vector3f(30f, 0f, 0f)),
                new EnemyState(0x11, 8, 12, false, new Vector3f(4f, 3f, 0f)),   // 5 away: nearest
                new EnemyState(0x12, 0, 12, true, new Vector3f(1f, 0f, 0f)),    // dead: skipped
                new EnemyState(0x13, 9, 12, false, new Vector3f(2f, 0f, 0f), Plot: true), // plot: skipped
            ];

            Type(dispatcher, "slay");

            CheatRequest request = Assert.Single(bridge.Requests);
            Assert.Equal(CheatKinds.Slay, request.Kind);
            Assert.Equal(0x11u, request.Target);
        }
    }

    [Fact]
    public void BareSlayWithNothingAliveSendsNothing()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            bridge.Enemies = [new EnemyState(0x12, 0, 12, true, new Vector3f(1f, 0f, 0f))];

            Type(dispatcher, "slay");

            Assert.Empty(bridge.Requests);
        }
    }

    [Fact]
    public void SlayByIdCarriesTheIdAndSlayByTagCarriesTheText()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, "slay 0x2A");
            Assert.Equal(0x2Au, bridge.Requests[0].Target);
            Assert.Equal(string.Empty, bridge.Requests[0].Text);

            Type(dispatcher, "slay sith_soldier01");
            Assert.Equal(0u, bridge.Requests[1].Target);
            Assert.Equal("sith_soldier01", bridge.Requests[1].Text);
        }
    }

    /// <summary>The rejection the whole channel is judged on: a cheat aimed at something the
    /// caller does not own must not become an engine call.</summary>
    [Theory]
    [InlineData("god malak")]
    [InlineData("slay me")]
    [InlineData("topup nonsense")]
    [InlineData("tp over there")]
    [InlineData("dropitem not-a-resref")]
    [InlineData("goto")]
    public void ARefusedLineNeverReachesThePlugin(string line)
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, line);
            Assert.Empty(bridge.Requests);
        }
    }

    /// <summary><c>goto</c> needs a session to name a player in; without one it says so
    /// rather than teleporting anybody to the origin.</summary>
    [Fact]
    public void GoToWithoutASessionSendsNothing()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, "goto Malak");
            Assert.Empty(bridge.Requests);
        }
    }

    [Fact]
    public void WithoutAPluginNothingIsFiled()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            bridge.Supports = false;

            Type(dispatcher, "god on");

            Assert.Empty(bridge.Requests);
        }
    }

    /// <summary>
    /// The answer is polled only while a cheat is outstanding, and reported once. An
    /// unattended verification run depends on that: a channel that kept re-printing the last
    /// answer would make "did my slay land?" unanswerable.
    /// </summary>
    [Fact]
    public async Task TheAnswerIsPolledUntilItArrivesAndThenStops()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            using CancellationTokenSource cts = new();
            Task loop = dispatcher.RunAsync(cts.Token);

            // Nothing outstanding: the dispatcher must not be reading the answer field.
            await Task.Delay(400);
            Assert.Equal(0, bridge.AnswerReads);

            Type(dispatcher, "god on");

            // Now it polls, and the plugin has not finished yet (serial 0 != 1).
            await Task.Delay(400);
            int whilePending = bridge.AnswerReads;
            Assert.True(whilePending > 0, "the dispatcher never polled for the answer");

            bridge.AnsweredSerial = 1;
            await Task.Delay(400);
            int afterAnswer = bridge.AnswerReads;

            // One more read consumed the answer; after that it is quiet again.
            await Task.Delay(400);
            Assert.Equal(afterAnswer, bridge.AnswerReads);

            await cts.CancelAsync();
            await loop;
        }
    }

    /// <summary>
    /// A cheat the game never answers is reported, not waited on forever. It really can
    /// happen: the executor runs on the frame hook, which is dormant at the main menu, so a
    /// cheat typed there would otherwise wedge the (single-slot) channel for good.
    /// </summary>
    [Fact]
    public async Task ACheatTheGameNeverAnswersGivesUp()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            using CancellationTokenSource cts = new();
            Task loop = dispatcher.RunAsync(cts.Token);

            Type(dispatcher, "god on");

            // The bridge never moves AnsweredSerial off 0, so the answer can never match.
            await Task.Delay(4000);
            int reads = bridge.AnswerReads;

            // Given up: polling has stopped...
            await Task.Delay(500);
            Assert.Equal(reads, bridge.AnswerReads);

            // ...and the channel is free for the next one.
            Type(dispatcher, "topup");
            await Task.Delay(400);
            Assert.True(bridge.AnswerReads > reads, "the channel stayed wedged after the timeout");

            await cts.CancelAsync();
            await loop;
        }
    }
}
