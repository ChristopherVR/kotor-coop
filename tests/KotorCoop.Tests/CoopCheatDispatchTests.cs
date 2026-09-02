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

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(15).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Types one line and drives the single-slot channel's own round trip to completion before
    /// returning — root cause 7 (2026-08-28, docs/research/party-pool-sync.md §26) made
    /// <c>SendCheat</c> a genuinely SERIALIZED issuer: a request typed while a previous one is
    /// still unanswered is now QUEUED rather than overwriting it (never dropped, never
    /// misattributed), so a test that wants to see MULTIPLE distinct requests land at the
    /// plugin one after another — as several in this file do — has to answer each one and let
    /// <c>ReflectCheatAnswer</c> free the slot before the next line can fire, exactly like a
    /// human typing one command, reading its answer, then typing the next. Requires
    /// <c>dispatcher.RunAsync</c> already running: that is the only thing that ever drains
    /// <see cref="_pendingCheatSerial"/>/the queued-cheat FIFO.
    /// </summary>
    private static async Task<CheatRequest> TypeAndAnswer(
        CoopCommandDispatcher dispatcher, CheatBridge bridge, string line)
    {
        int before = bridge.Requests.Count;
        Type(dispatcher, line);

        Assert.True(
            await WaitForAsync(() => bridge.Requests.Count > before, TimeSpan.FromSeconds(5)),
            $"'{line}' never reached the plugin - the single shared slot never freed up");

        CheatRequest request = bridge.Requests[^1];
        bridge.AnsweredSerial = (uint)bridge.Requests.Count;

        // Let the dispatcher's own tick loop notice the matching answer and free the slot
        // (and, if anything was queued behind it, fire that next) before the caller types
        // another line.
        await Task.Delay(300).ConfigureAwait(false);

        return request;
    }

    [Fact]
    public async Task SelfCheatsReachThePluginWithTheirArguments()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            using CancellationTokenSource cts = new();
            Task loop = dispatcher.RunAsync(cts.Token);

            CheatRequest god = await TypeAndAnswer(dispatcher, bridge, "god on");
            CheatRequest topup = await TypeAndAnswer(dispatcher, bridge, "topup 25");
            CheatRequest standup = await TypeAndAnswer(dispatcher, bridge, "standup");
            CheatRequest tp = await TypeAndAnswer(dispatcher, bridge, "tp 3 -4 5");

            Assert.Equal(4, bridge.Requests.Count);

            Assert.Equal(CheatKinds.God, god.Kind);
            Assert.Equal(1, god.Amount);

            Assert.Equal(CheatKinds.TopUp, topup.Kind);
            Assert.Equal(25, topup.Amount);

            Assert.Equal(CheatKinds.StandUp, standup.Kind);

            Assert.Equal(CheatKinds.Teleport, tp.Kind);
            Assert.Equal(3f, tp.X);
            Assert.Equal(-4f, tp.Y);
            Assert.Equal(5f, tp.Z);

            await cts.CancelAsync();
            await loop;
        }
    }

    /// <summary>
    /// hurt/down (2026-09-02, docs/research/death-handling.md §6): the "blank slot = yourself"
    /// console convention becomes <see cref="CheatKinds.LocalPlayerTarget"/> — never the bare
    /// value 0, which is a real party-roster slot — and a typed slot crosses verbatim.
    /// </summary>
    [Fact]
    public async Task HurtAndDownCarryTheDamageAndTheLocalPlayerSentinel()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            using CancellationTokenSource cts = new();
            Task loop = dispatcher.RunAsync(cts.Token);

            CheatRequest hurtSelf = await TypeAndAnswer(dispatcher, bridge, "hurt 50");
            Assert.Equal(CheatKinds.Hurt, hurtSelf.Kind);
            Assert.Equal(50, hurtSelf.Amount);
            Assert.Equal(CheatKinds.LocalPlayerTarget, hurtSelf.Target);

            CheatRequest downSelf = await TypeAndAnswer(dispatcher, bridge, "down");
            Assert.Equal(CheatKinds.Down, downSelf.Kind);
            Assert.Equal(CheatKinds.LocalPlayerTarget, downSelf.Target);

            await cts.CancelAsync();
            await loop;
        }
    }

    /// <summary>A typed party-roster slot — including slot 0, which must NOT be read back as
    /// the local-player sentinel — crosses to the plugin verbatim.</summary>
    [Fact]
    public async Task HurtAndDownCarryATypedPartySlotVerbatim()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            using CancellationTokenSource cts = new();
            Task loop = dispatcher.RunAsync(cts.Token);

            CheatRequest hurtSlot = await TypeAndAnswer(dispatcher, bridge, "hurt 50 3");
            Assert.Equal(50, hurtSlot.Amount);
            Assert.Equal(3u, hurtSlot.Target);

            CheatRequest hurtSlotZero = await TypeAndAnswer(dispatcher, bridge, "hurt 10 0");
            Assert.Equal(0u, hurtSlotZero.Target);

            CheatRequest downSlot = await TypeAndAnswer(dispatcher, bridge, "down 8");
            Assert.Equal(8u, downSlot.Target);

            await cts.CancelAsync();
            await loop;
        }
    }

    /// <summary>
    /// ROOT CAUSE 7's own direct regression pin (2026-08-28, docs/research/party-pool-sync.md
    /// §26): a second staging cheat typed while the first is still unanswered used to overwrite
    /// the first request's own bytes in the shared block outright — <c>SendCheat</c> had no
    /// in-flight check at all, so <c>bridge.Requests</c> would have shown BOTH the god request
    /// AND the topup request the instant the second line was typed, with nothing ever having
    /// answered the first. This is exactly the mechanism that permanently stalled a party-pool
    /// key in §25's live rig round: a reconcile apply's own cheat request, still awaiting the
    /// plugin's answer, silently replaced by a console verb's request before the plugin ever saw
    /// it. Fixed: a request typed while the slot is busy is QUEUED, not fired — <c>topup</c>
    /// must not reach the bridge at all until <c>god</c>'s own answer lands.
    /// </summary>
    [Fact]
    public async Task ASecondCheatTypedWhileTheFirstIsStillUnansweredIsQueuedNotFiredOverIt()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            using CancellationTokenSource cts = new();
            Task loop = dispatcher.RunAsync(cts.Token);

            Type(dispatcher, "god on");

            Assert.True(
                await WaitForAsync(() => bridge.Requests.Count == 1, TimeSpan.FromSeconds(5)),
                "'god on' never reached the plugin");

            // Typed while "god on" is still unanswered (bridge.AnsweredSerial is still 0) - must
            // not touch the bridge at all yet.
            Type(dispatcher, "topup 25");

            await Task.Delay(500);
            CheatRequest onlyRequest = Assert.Single(bridge.Requests);
            Assert.Equal(CheatKinds.God, onlyRequest.Kind);

            // Answer the first - the slot frees up and the queued "topup" is fired.
            bridge.AnsweredSerial = 1;

            Assert.True(
                await WaitForAsync(() => bridge.Requests.Count == 2, TimeSpan.FromSeconds(5)),
                "the queued 'topup' never fired once the slot freed up");
            Assert.Equal(CheatKinds.TopUp, bridge.Requests[1].Kind);
            Assert.Equal(25, bridge.Requests[1].Amount);

            await cts.CancelAsync();
            await loop;
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
    public async Task SlayByIdCarriesTheIdAndSlayByTagCarriesTheText()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            using CancellationTokenSource cts = new();
            Task loop = dispatcher.RunAsync(cts.Token);

            CheatRequest byId = await TypeAndAnswer(dispatcher, bridge, "slay 0x2A");
            Assert.Equal(0x2Au, byId.Target);
            Assert.Equal(string.Empty, byId.Text);

            CheatRequest byTag = await TypeAndAnswer(dispatcher, bridge, "slay sith_soldier01");
            Assert.Equal(0u, byTag.Target);
            Assert.Equal("sith_soldier01", byTag.Text);

            await cts.CancelAsync();
            await loop;
        }
    }

    /// <summary>
    /// STAGING VERB (2026-08-22): the object id reaches the plugin as-is - no census sweep, no
    /// tag lookup, unlike <c>slay</c>. Built to unstick docs/research/creature-liveness.md's
    /// live passes, which found no way to force a creature into the "Gone" state.
    /// </summary>
    [Fact]
    public void DestroyObjectCarriesTheIdVerbatim()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, "destroyobject 0x2A");

            CheatRequest request = Assert.Single(bridge.Requests);
            Assert.Equal(CheatKinds.DestroyObject, request.Kind);
            Assert.Equal(0x2Au, request.Target);
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
    [InlineData("destroyobject me")]
    [InlineData("destroyobject not-an-id")]
    [InlineData("destroyobject")]
    [InlineData("hurt")]
    [InlineData("hurt 0")]
    [InlineData("hurt -5")]
    [InlineData("hurt nonsense")]
    [InlineData("hurt 50 9")]
    [InlineData("hurt 50 -1")]
    [InlineData("hurt 50 1 2")]
    [InlineData("down 9")]
    [InlineData("down -1")]
    [InlineData("down nonsense")]
    [InlineData("down 1 2")]
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
    public async Task UsingADoorByTagSendsTheTagAndByIdSendsTheId()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            using CancellationTokenSource cts = new();
            Task loop = dispatcher.RunAsync(cts.Token);

            CheatRequest byTag = await TypeAndAnswer(dispatcher, bridge, "usedoor end_door10");
            Assert.Equal(CheatKinds.UseDoor, byTag.Kind);
            Assert.Equal("end_door10", byTag.Text);
            Assert.Equal(0u, byTag.Target);

            CheatRequest byId = await TypeAndAnswer(dispatcher, bridge, "usedoor 0x1F");
            Assert.Equal(0x1Fu, byId.Target);
            Assert.Equal(string.Empty, byId.Text);

            // Bare: neither, and the plugin sweeps for the nearest transition door.
            CheatRequest bare = await TypeAndAnswer(dispatcher, bridge, "usedoor");
            Assert.Equal(0u, bare.Target);
            Assert.Equal(string.Empty, bare.Text);

            await cts.CancelAsync();
            await loop;
        }
    }

    /// <summary>
    /// crossdoor (docs/research/cinematics.md §8.3c): posts the door's own crossing event
    /// directly, with the SAME target resolution as usedoor — a tag, an object id, or (bare)
    /// the nearest transition door, resolved inside the plugin either way.
    /// </summary>
    [Fact]
    public async Task CrossingADoorByTagSendsTheTagAndByIdSendsTheId()
    {
        (CoopCommandDispatcher dispatcher, CheatBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            using CancellationTokenSource cts = new();
            Task loop = dispatcher.RunAsync(cts.Token);

            CheatRequest byTag = await TypeAndAnswer(dispatcher, bridge, "crossdoor end_door10");
            Assert.Equal(CheatKinds.CrossDoor, byTag.Kind);
            Assert.Equal("end_door10", byTag.Text);
            Assert.Equal(0u, byTag.Target);

            CheatRequest byId = await TypeAndAnswer(dispatcher, bridge, "crossdoor 0x4A");
            Assert.Equal(0x4Au, byId.Target);
            Assert.Equal(string.Empty, byId.Text);

            // Bare: neither, and the plugin sweeps for the nearest transition door.
            CheatRequest bare = await TypeAndAnswer(dispatcher, bridge, "crossdoor");
            Assert.Equal(0u, bare.Target);
            Assert.Equal(string.Empty, bare.Text);

            await cts.CancelAsync();
            await loop;
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
    [InlineData("crossdoor a b")]
    [InlineData("crossdoor end-door-10")]
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
