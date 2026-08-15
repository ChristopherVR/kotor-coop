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

            Assert.Equal(4, bridge.Requests.Count);

            Assert.Equal(CheatKinds.God, bridge.Requests[0].Kind);
            Assert.Equal(1, bridge.Requests[0].Amount);

            Assert.Equal(CheatKinds.TopUp, bridge.Requests[1].Kind);
            Assert.Equal(25, bridge.Requests[1].Amount);

            Assert.Equal(CheatKinds.StandUp, bridge.Requests[2].Kind);

            Assert.Equal(CheatKinds.Teleport, bridge.Requests[3].Kind);
            Assert.Equal(3f, bridge.Requests[3].X);
            Assert.Equal(-4f, bridge.Requests[3].Y);
            Assert.Equal(5f, bridge.Requests[3].Z);
        }
    }

    /// <summary>
    /// ⚠ <c>dropitem</c> is NOT a staging cheat and must never file one.
    ///
    /// It used to: the verb wrote a <c>DropItem</c> request, the plugin called
    /// CreateItemOnFloor, and a body appeared in the typist's own game and nowhere else —
    /// because nothing scans the floor for items to publish. Both the help text and
    /// docs/research/console-cheats.md §4 meanwhile said "the item channel shares it". The
    /// session's own drop is the thing that shares it, so that is the path the verb takes,
    /// and this test exists to stop the cheat route creeping back.
    /// </summary>
    /// <summary>
    /// ⚠ …EXCEPT WITH NO SESSION, where the cheat is the only thing that can put an item on
    /// the floor at all.
    ///
    /// Routing the verb through the session is what fixed a drop that reached no peer, but a
    /// single-player game has no session, and refusing there would take away the way an
    /// operator stages a target BEFORE the pair is up — which is exactly when they want to.
    /// So the fallback is deliberate, and the reply says which of the two paths ran rather
    /// than letting the caller assume it replicated.
    /// </summary>
    [Fact]
    public void DropItemFallsBackToTheCheatOnlyWhenThereIsNoSession()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, "dropitem g_w_blstrpstl001");

            CheatRequest request = Assert.Single(bridge.Requests);
            Assert.Equal(CheatKinds.DropItem, request.Kind);
            Assert.Equal("g_w_blstrpstl001", request.Text);
        }
    }

    /// <summary>
    /// The by-id form has no such fallback, and must not invent one. `CreateItemOnFloor`
    /// takes a blueprint NAME — there is nothing it could do with an object id — so a
    /// sessionless `dropitem 0x131` has to refuse rather than spawn something arbitrary.
    /// </summary>
    [Fact]
    public void ADropByObjectIdNeverFallsBackToASpawn()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, "dropitem 0x131");

            Assert.Empty(bridge.Requests);
        }
    }

    /// <summary>
    /// The two spellings that can name a live item, and the one that cannot.
    ///
    /// A bare number is refused on purpose: it is both a legal resref and a legal decimal id,
    /// and guessing between them would silently drop something other than what was asked for.
    /// </summary>
    [Theory]
    [InlineData("dropitem 0x131", "0x131")]
    [InlineData("dropitem #131", "#131")]
    [InlineData("item 0x131", "#131")]
    [InlineData("setitem 0x131 stack=20", "#131")]
    public void ItemVerbsCarryTheObjectIdTheyWereGiven(string line, string expectedAddress)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.NotEqual(CoopCommandKind.Invalid, command.Kind);
        Assert.Equal(expectedAddress, command.Address);
    }

    [Theory]
    [InlineData("item 131")]
    [InlineData("item")]
    [InlineData("setitem 131 stack=2")]
    [InlineData("setitem 0x131 stack=0")]
    [InlineData("setitem 0x131 charges=999")]
    [InlineData("setitem 0x131 wobble=2")]
    [InlineData("setitem 0x131 stack")]
    public void AnItemVerbRefusesRatherThanGuesses(string line)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.Invalid, command.Kind);
        Assert.NotEqual(string.Empty, command.Error);
    }

    /// <summary>
    /// Every field <c>setitem</c> did not name stays null, and null is what the dispatcher
    /// turns into "leave this alone". A positional grammar could not express that, which is
    /// why this one is named — poking a stack must not silently reset the charges.
    /// </summary>
    [Fact]
    public void SetItemNamesOnlyTheFieldsItWasGiven()
    {
        CoopCommand command = ConsoleCommand.Parse("setitem 0x131 stack=20 upgrades=0x5");

        Assert.Equal(CoopCommandKind.SetItem, command.Kind);
        Assert.Equal(20u, command.Item.StackSize);
        Assert.Equal(5u, command.Item.Upgrades);
        Assert.Null(command.Item.Charges);
        Assert.Null(command.Item.MaxCharges);
        Assert.Null(command.Item.Identified);
        Assert.True(command.Item.Any);
    }

    /// <summary>A bare <c>setitem &lt;id&gt;</c> names nothing, which the plugin reads as
    /// "just tell me what this item holds" — the read half of the same verb.</summary>
    [Fact]
    public void ABareSetItemIsAPureRead()
    {
        CoopCommand command = ConsoleCommand.Parse("setitem 0x131");

        Assert.Equal(CoopCommandKind.SetItem, command.Kind);
        Assert.False(command.Item.Any);
    }

    /// <summary>
    /// A <c>setitem</c> reaches the plugin with a sentinel in every field the line did not
    /// name. The sentinel matters more than the values: 0 is a legal charge count, so
    /// "unchanged" cannot be spelled as zero without silently emptying a medpac.
    /// </summary>
    [Fact]
    public void SetItemFilesSentinelsForTheFieldsItDidNotName()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, "setitem 0x131 stack=20 upgrades=0x5");

            CheatRequest request = Assert.Single(bridge.Requests);
            Assert.Equal(CheatKinds.SetItem, request.Kind);
            Assert.Equal(0x131u, request.Target);
            Assert.Equal(20, request.Amount);
            Assert.Equal(5u, request.Upgrades);
            Assert.Equal(CheatKinds.Unchanged, request.Charges);
            Assert.Equal(CheatKinds.Unchanged, request.MaxCharges);
            Assert.Equal(CheatKinds.Unchanged, request.ItemFlags);
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

    /// <summary>
    /// The staging instrument reaches the plugin as a cheat with its mode and its movie
    /// names — and, in the warp form, as ONE request rather than two. The travel half is
    /// deliberately withheld until the plugin has answered: the plugin's frame takes the
    /// travel branch and returns before it reaches the cheat channel, so filing both together
    /// would warp with no movie and leave the cinematic armed for the next transition.
    /// </summary>
    [Fact]
    public void StagingACinematicFilesTheMoviesAndWaitsBeforeTravelling()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, "moviewarp tar_m02af 01c 01f");

            CheatRequest request = Assert.Single(bridge.Requests);
            Assert.Equal(CheatKinds.StageMovie, request.Kind);
            Assert.Equal(StageMovieModes.WarpWithMovies, request.Amount);
            Assert.Equal("01c,01f", request.Text);
        }
    }

    [Theory]
    [InlineData("moviewarp queue 01c", StageMovieModes.QueueOnly, "01c")]
    [InlineData("moviewarp clear", StageMovieModes.Clear, "")]
    [InlineData("moviewarp show", StageMovieModes.Show, "")]
    public void TheOtherStagingModesAreOneRequestEach(string line, int mode, string text)
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, line);

            CheatRequest request = Assert.Single(bridge.Requests);
            Assert.Equal(CheatKinds.StageMovie, request.Kind);
            Assert.Equal(mode, request.Amount);
            Assert.Equal(text, request.Text);
        }
    }

    /// <summary>
    /// The door half of the cinematic instrument. One request, no follow-up: the movie was
    /// queued by an earlier, separate command and is already in the engine's own transition
    /// queue, and the plugin must NOT be the thing that starts this transition or the flush
    /// window under test never opens.
    /// </summary>
    [Fact]
    public void UsingADoorByTagSendsTheTagAndByIdSendsTheId()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, "usedoor end_door10");
            Assert.Equal(CheatKinds.UseDoor, bridge.Requests[0].Kind);
            Assert.Equal("end_door10", bridge.Requests[0].Text);
            Assert.Equal(0u, bridge.Requests[0].Target);

            Type(dispatcher, "usedoor 0x1F");
            Assert.Equal(0x1Fu, bridge.Requests[1].Target);
            Assert.Equal(string.Empty, bridge.Requests[1].Text);

            // Bare: neither, and the plugin sweeps for the nearest transition door.
            Type(dispatcher, "usedoor");
            Assert.Equal(0u, bridge.Requests[2].Target);
            Assert.Equal(string.Empty, bridge.Requests[2].Text);
        }
    }

    /// <summary>A staging line the parser refuses must not become an engine call — the same
    /// demand every other cheat is held to, and more so for this one.</summary>
    [Theory]
    [InlineData("moviewarp")]
    [InlineData("moviewarp tar_m02af")]
    [InlineData("moviewarp tar_m02af 01c 01c")]
    [InlineData("usedoor end door 10")]
    [InlineData("usedoor end-door-10")]
    public void ARefusedStagingLineNeverReachesThePlugin(string line)
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, line);
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
