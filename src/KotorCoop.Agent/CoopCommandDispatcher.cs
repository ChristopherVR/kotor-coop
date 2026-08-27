using System.Net;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// The runtime command surface: polls the in-game console channel and the main-menu Connect
/// button out of the shared block and drives the <see cref="SessionManager"/> accordingly, so
/// a player can <c>connect</c>, <c>disconnect</c> and <c>reconnect</c> from inside the game.
///
/// Replies flow back the other way — status lines and the "password required" prompt are
/// written into the block for the plugin overlay to render, and mirrored to stdout for the
/// headless case.
///
/// Split into <c>CoopCommandDispatcher.*.cs</c> partial-class files by area (connect/session,
/// browser, announcers, save/load, items, effects, movies, doors, staging cheats,
/// diagnostics). This file keeps the tick loop, the cheat-answer channel every area shares,
/// and <see cref="Dispatch"/>'s verb table — the table just indexes methods living in the
/// area files; primary-constructor parameters (<c>manager</c>, <c>bridge</c>, ...) and fields
/// declared in one file are reachable from every other partial-class file of the same type.
/// </summary>
public sealed partial class CoopCommandDispatcher(
    SessionManager manager,
    IGameBridge bridge,
    int defaultPort,
    Func<string, SessionSettings> settingsForPassword,
    Func<CoopConfig?>? loadConfig = null,
    Action? menuAutoLoad = null,
    string gameDirectory = "")
{
    private readonly KotorGameBridge? _kotor = bridge as KotorGameBridge;

    /// <summary>
    /// The serial of a staging cheat still waiting on the plugin, or 0 for none.
    ///
    /// Held rather than fired-and-forgotten because a cheat's WHOLE VALUE is its answer: an
    /// unattended verification run types `slay` and needs to know whether the kill happened
    /// here, went to the host, or was refused because the thing it aimed at turned out to be
    /// a phantom. Reporting "sent" and moving on would make the channel untestable.
    /// </summary>
    private uint _pendingCheatSerial;

    /// <summary>
    /// When that cheat was filed, so an answer that never comes is reported as such.
    ///
    /// It really can never come: the cheat executor runs on the SwapBuffers frame hook, which
    /// is dormant behind the main-menu movie, so a cheat typed at the menu sits unread until
    /// a world loads. Waiting silently forever is the one behaviour a staging channel must
    /// not have — and it would also wedge the next cheat, because only one is in flight.
    /// </summary>
    private DateTime _pendingCheatFiled;

    /// <summary>How long to wait for the game thread. Generous: the plugin answers on its
    /// next frame, so anything past a couple of seconds means no frames are running.</summary>
    private static readonly TimeSpan CheatAnswerTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// What to do once the pending cheat has been answered, and what to do instead if it was
    /// refused. Exactly one command needs it — <c>moviewarp</c>, whose two halves (stage the
    /// cinematic, then travel) MUST NOT be filed in the same tick, because the plugin's frame
    /// takes the travel branch and returns before it ever reaches the cheat channel. Modelling
    /// that as "wait for the answer" rather than "sleep a bit" is what makes a refused staging
    /// stop the warp instead of silently producing the movie-less transition that has already
    /// been tested. See <see cref="CheatFollowUp"/>.
    /// </summary>
    private readonly CheatFollowUp _afterCheat = new();

    /// <summary>Mirror of SharedBlock.ConnectStatusConnecting for the agent side.</summary>
    private const uint ConnectStatusConnecting = 1;

    /// <summary>Polls the block for console/menu commands and reflects status until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_browsing)
        {
            _browsing = true;
            _browser.StartListening();
        }

        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(150));

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // One bad tick must not end the command surface. Without this, a single throw
                // anywhere below unwinds the loop and the ~ console, the menu button and the
                // browser all go quiet for the rest of the session — with nothing on screen to
                // say why, because the thing that reports problems is what died.
                try
                {
                    // Written unconditionally, every tick, whether or not a session even
                    // exists — this is "the agent is pumping at all", not "connected". The
                    // plugin watches it stall to notice a hung or crashed agent and freeze
                    // remote-peer writes rather than keep driving stale phantoms.
                    _kotor?.PublishHeartbeat();

                    manager.Poll();
                    DrainInputs();
                    PublishBrowserList();
                    ReflectPhase();
                    AnnouncePeerChanges();
                    AnnounceCompatibilityChanges();
                    AnnounceConnectionStatus();
                    AnnounceHostSaveAvailability();
                    AnnouncePublicAddress();
                    MaintainSessionSave();
                    MaintainWorldServing();
                    DrainChat();
                    ReflectCheatAnswer();
                    DrainAutomaticLoot();
                    DrainAutomaticPartyPool();
                    PumpPeerEffectLog();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"[coop] command tick failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void DrainInputs()
    {
        if (_kotor is null)
        {
            return;
        }

        // The ~ console. A whole command line typed in-game.
        if (_kotor.TryTakeConsoleCommand(out string line))
        {
            DispatchFromGame(ConsoleCommand.Parse(line));
        }

        // The main-menu Connect button. It carries an optional typed host; empty means
        // "use the configured host" — a reconnect when a session existed, coop.ini's say
        // otherwise.
        if (_kotor.TryTakeConnectRequest(out string typedHost))
        {
            _kotor.SetConnectStatus(ConnectStatusConnecting);
            OnConnectRequest(typedHost);
        }

        // The in-game server browser: the HOST button, or a listed row confirmed. The row
        // is dialed as `connect #n` so it takes exactly the same path a typed pick would.
        if (_kotor.TryTakeBrowserHost())
        {
            DispatchFromGame(new CoopCommand(CoopCommandKind.Host));
        }

        if (_kotor.TryTakeBrowserConnect(out int picked))
        {
            if (picked >= 0 && picked < _lastServerList.Count)
            {
                DispatchFromGame(new CoopCommand(CoopCommandKind.Connect, Address: $"#{picked + 1}"));
            }
            else
            {
                Reply("that server is no longer listed");
            }
        }
    }

    /// <summary>
    /// Applies a parsed command to the session manager. Public so the initial CLI/menu
    /// connection can go through exactly the same path as an in-game command.
    ///
    /// The dispatch itself is a table lookup: <see cref="DispatchTable"/> has one entry per
    /// <see cref="CoopCommandKind"/>, built once per instance (a C# field initializer cannot
    /// call an instance method — CS0236 — so this uses the ordinary lazy-once pattern instead)
    /// by <see cref="BuildDispatchTable"/>. Each entry's handler lives in whichever
    /// <c>CoopCommandDispatcher.*.cs</c> file owns that verb's area.
    /// </summary>
    public void Dispatch(CoopCommand command)
    {
        if (DispatchTable.TryGetValue(command.Kind, out Action<CoopCommand>? handler))
        {
            handler(command);
        }
    }

    private Dictionary<CoopCommandKind, Action<CoopCommand>>? _dispatchTable;

    private Dictionary<CoopCommandKind, Action<CoopCommand>> DispatchTable => _dispatchTable ??= BuildDispatchTable();

    private Dictionary<CoopCommandKind, Action<CoopCommand>> BuildDispatchTable() => new()
    {
        // None/Unknown/Invalid: the original switch's `case None or Unknown` plus its own
        // Invalid case, kept as three explicit entries so the table has a row for every kind
        // Dispatch ever handled, sentinels included.
        [CoopCommandKind.None] = _ => { },
        [CoopCommandKind.Unknown] = _ => Reply("unknown command. " + ConsoleCommand.HelpText),
        [CoopCommandKind.Invalid] = c => Reply(c.Error),

        [CoopCommandKind.Connect] = c => Join(c.Address, c.Password),
        [CoopCommandKind.Host] = HostCommand,
        [CoopCommandKind.Password] = c => AnswerPasswordPrompt(c.Password ?? string.Empty),
        [CoopCommandKind.Disconnect] = _ => manager.Disconnect(),
        [CoopCommandKind.Reconnect] = ReconnectCommand,
        [CoopCommandKind.Say] = SayCommand,
        [CoopCommandKind.Clear] = _ => _kotor?.PostChatClear(),
        [CoopCommandKind.Servers] = _ => ListServers(),
        [CoopCommandKind.Status] = StatusCommand,
        [CoopCommandKind.Help] = _ => Reply(ConsoleCommand.HelpText),

        // Rides the existing agent->plugin travel channel (the same one peer-follow travel
        // uses): the plugin flushes everything it spawned, then the engine's own
        // move-to-module path runs — party collection, store-out, load screen. The parser
        // already validated the resref, so a null Address here would mean no plugin.
        [CoopCommandKind.Warp] = c => Warp(c.Address!, c.Password ?? string.Empty),

        [CoopCommandKind.StageMovie] = StageMovie,
        [CoopCommandKind.UseDoor] = c => UseDoor(c.Address ?? string.Empty),
        [CoopCommandKind.OpenDoor] = c => OpenDoor(c.Address ?? string.Empty),
        [CoopCommandKind.UsePlaceable] = c => UsePlaceable(c.Address ?? string.Empty),
        [CoopCommandKind.ClosePlaceable] = c => ClosePlaceable(c.Address ?? string.Empty),
        [CoopCommandKind.Who] = WhoCommand,
        [CoopCommandKind.NetSim] = c => ApplyNetSim(c.Address ?? "status", c.Password ?? string.Empty),
        [CoopCommandKind.Where] = WhereCommand,
        [CoopCommandKind.Census] = c => Census(c.Address),
        [CoopCommandKind.LagStats] = _ => LagStats(manager),
        [CoopCommandKind.PublicAddress] = _ => PublicAddressCommand(),

        // Straight to the plugin: these questions can only be answered on the game thread,
        // and the value of the answer is that it is taken in ONE frame.
        [CoopCommandKind.ResolveObject] = ResolveObjectCommand,
        [CoopCommandKind.MoveNear] = MoveNearCommand,
        [CoopCommandKind.FireScript] = FireScriptCommand,

        // ---- staging cheats ---------------------------------------------
        [CoopCommandKind.God] = c => SendCheat(new CheatRequest(CheatKinds.God, Amount: c.Amount)),
        [CoopCommandKind.TopUp] = c => SendCheat(new CheatRequest(CheatKinds.TopUp, Amount: c.Amount)),
        [CoopCommandKind.StandUp] = c => SendCheat(new CheatRequest(CheatKinds.StandUp, Amount: c.Amount)),
        [CoopCommandKind.Slay] = c => Slay(c.Address),
        [CoopCommandKind.Teleport] = c => SendCheat(new CheatRequest(CheatKinds.Teleport, X: c.X, Y: c.Y, Z: c.Z)),
        [CoopCommandKind.WalkTo] = c => SendCheat(new CheatRequest(CheatKinds.WalkTo, X: c.X, Y: c.Y, Z: c.Z)),
        [CoopCommandKind.GoTo] = c => GoTo(c.Address ?? string.Empty),
        [CoopCommandKind.DropItem] = c => DropItem(c.Address ?? string.Empty),
        [CoopCommandKind.Items] = c => ListItems(c.Amount),
        [CoopCommandKind.InspectItem] = c => InspectItem(c.Address ?? string.Empty),
        [CoopCommandKind.SetItem] = c => SetItem(c.Address ?? string.Empty, c.Item),
        [CoopCommandKind.GiveLoot] = c => GiveLoot(c.Address ?? string.Empty, c.Password ?? string.Empty, c.Amount),
        [CoopCommandKind.ApplyEffect] = ApplyEffect,
        [CoopCommandKind.Effects] = _ => ReportEffects(),
        [CoopCommandKind.MoviePlay] = c => MoviePlay(c.Address ?? string.Empty),
        [CoopCommandKind.DestroyObject] = c => DestroyObject(c.Address ?? string.Empty),
        [CoopCommandKind.Leader] = c => Leader(c.Amount),
        [CoopCommandKind.Recruit] = c => Recruit(c.Amount, c.Address ?? string.Empty),
        [CoopCommandKind.SkipLine] = _ => SendCheat(new CheatRequest(CheatKinds.SkipLine)),
        [CoopCommandKind.SelectReply] = c => SendCheat(new CheatRequest(CheatKinds.SelectReply, Amount: c.Amount)),
        [CoopCommandKind.LoadGame] = c => TriggerMidSessionLoad(c.Amount),
        [CoopCommandKind.QuitToMenu] = _ => SendCheat(new CheatRequest(CheatKinds.QuitToMenu)),

        // ⚠ Deliberately does NOT call NoteLocalItemGiven, unlike CoopCommandKind.GiveLoot's own
        // dispatcher entry just above — see CoopCommandKind.ScriptGive's own doc comment
        // (ConsoleCommand.cs) for why an UNTRACKED grant is the entire point of this verb.
        [CoopCommandKind.ScriptGive] = c => SendCheat(new CheatRequest(
            CheatKinds.ScriptGive, Text: c.Address ?? string.Empty, Amount: c.Amount)),

        [CoopCommandKind.LootTake] = c => LootTake(c.Address ?? string.Empty),

        [CoopCommandKind.Cheats] = CheatsCommand,
    };

    /// <summary>
    /// Files a staging cheat with the plugin and remembers its serial so the answer can be
    /// reported. Refuses out loud when there is no plugin: "nothing happened" is the one
    /// outcome a staging command must never produce silently.
    /// </summary>
    private void SendCheat(CheatRequest request, Action? onApplied = null, Action? onRefused = null)
    {
        // Whatever the previous cheat was going to do next, it is not going to do it now:
        // only one request is in flight, so filing a new one abandons the old one's answer.
        _afterCheat.Clear();

        if (!bridge.SupportsCheats)
        {
            Reply("staging cheats need the in-game plugin (no shared block found)");
            onRefused?.Invoke();
            return;
        }

        if (bridge.TryRequestCheat(request, out uint serial))
        {
            _pendingCheatSerial = serial;
            _pendingCheatFiled = DateTime.UtcNow;
            _afterCheat.Arm(onApplied, onRefused);
        }
        else
        {
            Reply("could not reach the plugin to run that");
            onRefused?.Invoke();
        }
    }

    /// <summary>
    /// Prints the plugin's answer to the cheat we are waiting on, once. The detail sentence
    /// is what the plugin actually SAW — a refusal names the rule and the object id — so it
    /// is printed verbatim rather than being restated from the status code.
    /// </summary>
    private void ReflectCheatAnswer()
    {
        if (_pendingCheatSerial == 0)
        {
            return;
        }

        if (!bridge.TryReadCheatAnswer(out uint applied, out int status, out string detail)
            || applied != _pendingCheatSerial)
        {
            if (DateTime.UtcNow - _pendingCheatFiled > CheatAnswerTimeout)
            {
                _pendingCheatSerial = 0;
                Reply("the game never answered that - is a world loaded? (cheats run on the game's frame)");
                _afterCheat.Take(applied: false)?.Invoke();
            }

            return;
        }

        _pendingCheatSerial = 0;

        string prefix = status switch
        {
            CheatStatus.Applied => string.Empty,
            CheatStatus.Forwarded => string.Empty,
            CheatStatus.NoWorld => "not in a world: ",
            CheatStatus.NoTarget => "no target: ",
            CheatStatus.Refused => "refused: ",
            CheatStatus.EngineRefused => "the engine refused it: ",
            _ => $"status {status}: ",
        };

        Reply(detail.Length > 0 ? prefix + detail : $"the plugin answered with status {status} and no detail");

        // The follow-up runs AFTER the plugin's own sentence is on screen, so a reader sees
        // what the game said before what we did about it.
        _afterCheat.Take(applied: status is CheatStatus.Applied or CheatStatus.Forwarded)?.Invoke();
    }

    /// <summary>
    /// The agent's log sink, wired to <c>AgentDashboard.Note</c> at startup — the same hook
    /// <c>SessionManager.Log</c> uses, and for the same reason.
    ///
    /// ⚠ WITHOUT THIS EVERY REPLY IS INVISIBLE TO AN UNATTENDED RUN. `demo.ps1` gives each
    /// agent a VISIBLE console (deliberately — the readout redraws in place, and a pipe turns
    /// every redraw into another appended copy) and a SEPARATE `--log` file written by the
    /// dashboard. A reply printed with Console.WriteLine therefore lands in a window nobody
    /// is watching and never reaches the file every check actually reads.
    ///
    /// Null in tests and in any host that has no dashboard; the console fallback below covers
    /// that case.
    /// </summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// All co-op feedback — command replies and chat alike — goes to the always-on overlay as
    /// well as the console reply line, because the console closes on Enter (so a reply that
    /// arrives a tick later would never be seen there) and the overlay persists regardless.
    ///
    /// ⚠ The in-game reply line is a 96-byte field and truncates without complaint, so a
    /// reply is a SUMMARY and the substance belongs in the log. Multi-line answers (`items`,
    /// `who`, `servers`) rely on that: only the last line survives in the block, and all of
    /// them survive in the log.
    /// </summary>
    private void Reply(string text)
    {
        // The log sink already writes to a redirected stdout, timestamped. Writing here too
        // would double every line in a piped run; writing nowhere would lose them in a
        // windowed one. So: the window when there is a window, the sink otherwise — and the
        // sink always, because the file is the record.
        if (Log is not { } log)
        {
            Console.WriteLine($"[coop] {text}");
        }
        else
        {
            if (!Console.IsOutputRedirected)
            {
                Console.WriteLine($"[coop] {text}");
            }

            log(text);
        }

        _kotor?.PostConsoleReply(text);
        _kotor?.PostChatLine(text);
    }

    /// <summary>Alias kept for call sites that specifically mean a chat line.</summary>
    private void Chat(string text) => Reply(text);
}
