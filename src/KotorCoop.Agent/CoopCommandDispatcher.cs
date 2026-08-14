using System.Net;
using KotorCoop.Memory;
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
/// </summary>
public sealed class CoopCommandDispatcher(
    SessionManager manager,
    IGameBridge bridge,
    int defaultPort,
    Func<string, SessionSettings> settingsForPassword,
    Func<CoopConfig?>? loadConfig = null,
    Action? menuAutoLoad = null)
{
    private readonly KotorGameBridge? _kotor = bridge as KotorGameBridge;

    /// <summary>Listens for co-op servers on the LAN so `servers` can list them and
    /// `connect #n` can dial one without an IP. Started on the first RunAsync tick.</summary>
    private readonly ServerDiscovery _browser = new();
    private bool _browsing;

    /// <summary>The servers listed by the last `servers` command, so `connect #n` resolves.</summary>
    private IReadOnlyList<ServerInfo> _lastServerList = [];

    /// <summary>The address of the last join attempt, so a password prompt can echo it back.</summary>
    private string? _pendingAddress;

    /// <summary>
    /// The endpoint of the last join attempt, so answering a password prompt can retry it
    /// without the player retyping an address. This is what makes <c>password &lt;pw&gt;</c> a
    /// complete answer: a player who picked a row in the browser never saw an IP, and asking
    /// them to produce one is the same as not prompting at all.
    /// </summary>
    private IPEndPoint? _pendingEndpoint;

    /// <summary>
    /// The password the current attempt is using. Tracked so a locked server can be recognised
    /// as unanswerable BEFORE dialling it — "the row says locked and we hold nothing" is worth a
    /// prompt straight away rather than a round trip that can only end in E_PASSWORD.
    /// </summary>
    private string _password = string.Empty;

    private SessionPhase _lastPhase = SessionPhase.Idle;
    private bool _promptedForPassword;

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
    /// Armed by a connect/host that originated inside the game (Co-op button, browser
    /// panel, ~ console) — i.e. one made while the player could plausibly still be at the
    /// main menu. When such a session reaches its load condition the save auto-loads once,
    /// so "pick a server, arrive in the world" is one action.
    /// </summary>
    private bool _menuAutoLoadArmed;

    /// <summary>Mirror of SharedBlock.ConnectStatusConnecting for the agent side.</summary>
    private const uint ConnectStatusConnecting = 1;

    /// <summary>Roster changes become lines on BOTH screens: "Malak joined the game" /
    /// "Malak left the game" (see <see cref="PeerAnnouncer"/> for why this exists).</summary>
    private readonly PeerAnnouncer _peerAnnouncer = new();

    /// <summary>Content differences become lines on screen too — see
    /// <see cref="CompatibilityAnnouncer"/> for why a degradation that is only logged is
    /// indistinguishable from a bug.</summary>
    private readonly CompatibilityAnnouncer _compatibilityAnnouncer = new();

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
                    manager.Poll();
                    DrainInputs();
                    PublishBrowserList();
                    ReflectPhase();
                    AnnouncePeerChanges();
                    AnnounceCompatibilityChanges();
                    DrainChat();
                    ReflectCheatAnswer();
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
    /// Handles a Co-op button press; <paramref name="typedHost"/> is the host typed
    /// in-game, empty for a bare press. Public so the no-plugin <c>--menu</c> startup (and
    /// tests) can drive the same path the button does.
    /// </summary>
    public void OnConnectRequest(string typedHost)
    {
        if (typedHost.Length > 0)
        {
            DispatchFromGame(new CoopCommand(CoopCommandKind.Connect, Address: typedHost));
        }
        else if (manager.CanReconnect)
        {
            DispatchFromGame(new CoopCommand(CoopCommandKind.Reconnect));
        }
        else
        {
            DispatchConfiguredConnect();
        }
    }

    /// <summary>
    /// A bare connect with no session to reconnect to: coop.ini decides. mode=host hosts,
    /// host=&lt;address&gt; joins it, and with neither the player is pointed at the server
    /// browser instead of getting "nothing to reconnect to".
    /// </summary>
    private void DispatchConfiguredConnect()
    {
        CoopConfig? config = loadConfig?.Invoke();

        if (config is { Host: true })
        {
            DispatchFromGame(new CoopCommand(
                CoopCommandKind.Host,
                Password: config.Password.Length > 0 ? config.Password : null));
            return;
        }

        if (config is { HasHostAddress: true })
        {
            DispatchFromGame(new CoopCommand(
                CoopCommandKind.Connect,
                Address: $"{config.HostAddress}:{config.Port}",
                Password: config.Password.Length > 0 ? config.Password : null));
            return;
        }

        Reply("no co-op server configured - pick one in the server browser, or set host=<ip> in coop.ini");
    }

    /// <summary>
    /// Dispatches a command that originated inside the game rather than from the command
    /// line. A connect or host chosen this way arms the one-shot menu auto-load; a host
    /// has nothing to wait for, so its load fires immediately, while a joiner's fires when
    /// the session reaches Connected (see <see cref="ReflectPhase"/>).
    /// </summary>
    private void DispatchFromGame(CoopCommand command)
    {
        if (command.Kind is CoopCommandKind.Connect or CoopCommandKind.Host)
        {
            _menuAutoLoadArmed = true;
        }

        Dispatch(command);

        if (command.Kind == CoopCommandKind.Host)
        {
            FireMenuAutoLoadIfArmed();
        }
    }

    /// <summary>
    /// Fires the menu auto-load at most once per menu-initiated connect, and only when the
    /// game is verifiably still at the main menu (no readable player state) — never
    /// mid-world, never again on a reconnect.
    /// </summary>
    private void FireMenuAutoLoadIfArmed()
    {
        if (!_menuAutoLoadArmed)
        {
            return;
        }

        _menuAutoLoadArmed = false;

        if (menuAutoLoad is not null && !bridge.TryReadLocalState(out _))
        {
            menuAutoLoad();
        }
    }

    /// <summary>
    /// Applies a parsed command to the session manager. Public so the initial CLI/menu
    /// connection can go through exactly the same path as an in-game command.
    /// </summary>
    public void Dispatch(CoopCommand command)
    {
        switch (command.Kind)
        {
            case CoopCommandKind.None or CoopCommandKind.Unknown:
                if (command.Kind == CoopCommandKind.Unknown)
                {
                    Reply("unknown command. " + ConsoleCommand.HelpText);
                }

                break;

            case CoopCommandKind.Invalid:
                Reply(command.Error);
                break;

            case CoopCommandKind.Connect:
                Join(command.Address, command.Password);
                break;

            case CoopCommandKind.Host:
                _pendingAddress = null;
                _pendingEndpoint = null;
                _promptedForPassword = false;
                _password = command.Password ?? string.Empty;
                manager.Connect(host: true, endpoint: null, settingsForPassword(_password));
                break;

            case CoopCommandKind.Password:
                AnswerPasswordPrompt(command.Password ?? string.Empty);
                break;

            case CoopCommandKind.Disconnect:
                manager.Disconnect();
                break;

            case CoopCommandKind.Reconnect:
                _promptedForPassword = false;
                manager.Reconnect();
                break;

            case CoopCommandKind.Say:
                if (manager.Current is { } session && command.Address is { Length: > 0 } message)
                {
                    session.SendChat(message);
                    Chat($"you: {message}"); // echo locally; peers get it over the wire
                }
                else
                {
                    Reply("not connected - nothing to say to");
                }

                break;

            case CoopCommandKind.Clear:
                _kotor?.PostChatClear();
                break;

            case CoopCommandKind.Servers:
                ListServers();
                break;

            case CoopCommandKind.Status:
                Reply($"{manager.Phase.ToString().ToLowerInvariant()} - {manager.Detail}");
                break;

            case CoopCommandKind.Help:
                Reply(ConsoleCommand.HelpText);
                break;

            case CoopCommandKind.Warp:
                // Rides the existing agent->plugin travel channel (the same one peer-follow
                // travel uses): the plugin flushes everything it spawned, then the engine's
                // own move-to-module path runs — party collection, store-out, load screen.
                // The parser already validated the resref, so a false here means no plugin.
                if (_kotor is null || !_kotor.SupportsTravel)
                {
                    Reply("warp needs the in-game plugin (no shared block found)");
                }
                else if (_kotor.TryRequestTravel(command.Address!, command.Password ?? string.Empty))
                {
                    Reply($"warping to {command.Address}...");
                }
                else
                {
                    Reply($"could not request warp to {command.Address}");
                }

                break;

            case CoopCommandKind.Who:
                if (manager.Current is not { } who)
                {
                    Reply("not connected - no players to list");
                    break;
                }

                ListPlayers(who);
                break;

            case CoopCommandKind.NetSim:
                ApplyNetSim(command.Address ?? "status", command.Password ?? string.Empty);
                break;

            case CoopCommandKind.Where:
                if (bridge.TryReadLocalState(out PlayerState local) && local.Module.Length > 0)
                {
                    Reply($"module {local.Module}  at {local.Position.X:F1} {local.Position.Y:F1} {local.Position.Z:F1}"
                        + $"  hp {local.Health}/{local.MaxHealth}");
                }
                else
                {
                    Reply("no player loaded (not in a module yet)");
                }

                break;

            // ---- staging cheats ---------------------------------------------

            case CoopCommandKind.God:
                SendCheat(new CheatRequest(CheatKinds.God, Amount: command.Amount));
                break;

            case CoopCommandKind.TopUp:
                SendCheat(new CheatRequest(CheatKinds.TopUp, Amount: command.Amount));
                break;

            case CoopCommandKind.StandUp:
                SendCheat(new CheatRequest(CheatKinds.StandUp, Amount: command.Amount));
                break;

            case CoopCommandKind.Slay:
                Slay(command.Address);
                break;

            case CoopCommandKind.Teleport:
                SendCheat(new CheatRequest(CheatKinds.Teleport, X: command.X, Y: command.Y, Z: command.Z));
                break;

            case CoopCommandKind.GoTo:
                GoTo(command.Address ?? string.Empty);
                break;

            case CoopCommandKind.DropItem:
                SendCheat(new CheatRequest(CheatKinds.DropItem, Text: command.Address ?? string.Empty));
                break;

            case CoopCommandKind.Cheats:
                foreach (string line in ConsoleCommand.CheatsText)
                {
                    Reply(line);
                }

                break;
        }
    }

    /// <summary>
    /// Arms (or clears) the transport fault injector on the live session, so a disconnect can
    /// be produced on the rig without unplugging anything and without killing a process — a
    /// killed process is a clean socket close, which is the one failure shape that was already
    /// known to recover. See <see cref="NetworkSim"/>.
    ///
    /// Deliberately applied to the LIVE session only: a fault outlives no session, so
    /// disconnecting is always a way out of one you set by mistake.
    /// </summary>
    private void ApplyNetSim(string sub, string args)
    {
        if (manager.Current?.Network is not { } sim)
        {
            Reply("netsim: not connected - nothing to break");
            return;
        }

        string[] parts = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        switch (sub)
        {
            case "off":
                sim.Clear();
                break;

            case "loss":
                sim.SetLoss(double.Parse(parts[0]));
                break;

            case "latency":
                sim.SetLatency(int.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 0);
                break;

            case "blackout":
                NetSimDirection direction = parts.Length > 1
                    ? parts[1].ToLowerInvariant() switch
                    {
                        "in" => NetSimDirection.Inbound,
                        "out" => NetSimDirection.Outbound,
                        _ => NetSimDirection.Both,
                    }
                    : NetSimDirection.Both;

                sim.StartBlackout((int)(double.Parse(parts[0]) * 1000), direction);
                break;
        }

        Reply(sim.Describe());
    }

    /// <summary>
    /// Files a staging cheat with the plugin and remembers its serial so the answer can be
    /// reported. Refuses out loud when there is no plugin: "nothing happened" is the one
    /// outcome a staging command must never produce silently.
    /// </summary>
    private void SendCheat(CheatRequest request)
    {
        if (!bridge.SupportsCheats)
        {
            Reply("staging cheats need the in-game plugin (no shared block found)");
            return;
        }

        if (bridge.TryRequestCheat(request, out uint serial))
        {
            _pendingCheatSerial = serial;
            _pendingCheatFiled = DateTime.UtcNow;
        }
        else
        {
            Reply("could not reach the plugin to run that");
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
    }

    /// <summary>
    /// Resolves what <c>slay</c> should aim at and hands it over. A bare <c>slay</c> means
    /// "the nearest hostile", which only the agent can answer — it is the side that runs the
    /// enemy census — so the nearest id is picked here and the plugin is given a number
    /// rather than a policy.
    ///
    /// The authority decision itself is deliberately NOT made here: whether this instance
    /// kills the creature or forwards the kill to its owner depends on state only the plugin
    /// can see (the phantom table, the party roster, the mirror's own pin), and splitting
    /// that rule across two processes is how it would drift.
    /// </summary>
    private void Slay(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            if (!TryFindNearestEnemy(out uint nearest, out float distance))
            {
                Reply("no living enemy in range - name one: slay <tag|id>");
                return;
            }

            Reply($"slaying the nearest enemy 0x{nearest:X} ({distance:F1}m away)");
            SendCheat(new CheatRequest(CheatKinds.Slay, Target: nearest));
            return;
        }

        if (ConsoleCommand.TryParseObjectId(token, out uint objectId))
        {
            SendCheat(new CheatRequest(CheatKinds.Slay, Target: objectId));
            return;
        }

        // A tag. Refuse one that names a PLAYER before it reaches the engine: the plugin
        // rejects a phantom by id anyway, but a peer's name is what a person actually types
        // and the reason is clearer said here.
        if (manager.Current is { } session)
        {
            foreach (RemotePeer peer in session.GetRemotePeers())
            {
                if (peer.Name.Equals(token, StringComparison.OrdinalIgnoreCase))
                {
                    Reply($"{peer.Name} is a player - their character is theirs, not yours to kill");
                    return;
                }
            }
        }

        SendCheat(new CheatRequest(CheatKinds.Slay, Text: token));
    }

    /// <summary>The nearest living enemy to the local player, from the census this agent
    /// already runs every tick for the enemy channel.</summary>
    private bool TryFindNearestEnemy(out uint objectId, out float distance)
    {
        objectId = 0;
        distance = 0f;

        if (!bridge.TryReadLocalState(out PlayerState local))
        {
            return false;
        }

        float best = float.MaxValue;
        foreach (EnemyState enemy in bridge.ReadEnemies([]))
        {
            if (enemy.Dead || enemy.Plot || enemy.CurrentHealth == 0)
            {
                continue;
            }

            float dx = enemy.Position.X - local.Position.X;
            float dy = enemy.Position.Y - local.Position.Y;
            float dz = enemy.Position.Z - local.Position.Z;
            float d = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));

            if (d < best)
            {
                best = d;
                objectId = enemy.ObjectId;
            }
        }

        distance = best;
        return objectId != 0;
    }

    /// <summary>
    /// Moves the local player to a peer's last known position.
    ///
    /// The module check is the whole point: peer positions are area coordinates, so the same
    /// numbers name a completely different place in a different module, and a cross-module
    /// <c>goto</c> would drop the player through the floor of the world they are in. Use
    /// <c>warp</c> to follow somebody to another module and <c>goto</c> once you are there.
    /// </summary>
    private void GoTo(string name)
    {
        if (manager.Current is not { } session)
        {
            Reply("not connected - nobody to go to");
            return;
        }

        IReadOnlyList<RemotePeer> peers = session.GetRemotePeers();
        RemotePeer? found = null;

        // "#n" picks the n-th of the `who` listing, for a name nobody wants to type.
        if (name.StartsWith('#') && int.TryParse(name[1..], out int index)
            && index >= 1 && index <= peers.Count)
        {
            found = peers[index - 1];
        }
        else
        {
            foreach (RemotePeer peer in peers)
            {
                if (peer.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    found = peer;
                    break;
                }
            }
        }

        if (found is not { } target)
        {
            Reply($"no player called '{name}' - run `who` to see who is here");
            return;
        }

        string localModule = bridge.TryReadLocalState(out PlayerState local) && local.Module.Length > 0
            ? local.Module
            : string.Empty;

        if (localModule.Length > 0 && target.State.Module.Length > 0
            && !localModule.Equals(target.State.Module, StringComparison.OrdinalIgnoreCase))
        {
            Reply($"{target.Name} is in {target.State.Module} and you are in {localModule} - warp there first");
            return;
        }

        SendCheat(new CheatRequest(
            CheatKinds.Teleport,
            X: target.State.Position.X,
            Y: target.State.Position.Y,
            Z: target.State.Position.Z));
    }

    /// <summary>Diffs the admitted roster each tick and posts joins/leaves through the same
    /// trio every reply uses (stdout, console line, chat overlay) — which is what puts the
    /// line on the HOST's screen when a player is admitted.</summary>
    private void AnnouncePeerChanges()
    {
        if (manager.Current is not { } session)
        {
            _peerAnnouncer.Reset();
            _kotor?.PublishSessionPeerCount(0);
            return;
        }

        IReadOnlyList<RemotePeer> peers = session.GetRemotePeers();
        _peerAnnouncer.Observe(peers, Reply);

        // The seamless-arrival gate's signal: written here, every tick, because THIS loop
        // never depends on the player resolve — the pump's RemoteCount does, and its 1 Hz
        // resolve is precisely why the load screen used to drop before the first sync.
        _kotor?.PublishSessionPeerCount(peers.Count);
    }

    /// <summary>
    /// Puts content-compatibility findings on the player's screen, through the same trio
    /// every reply uses. Without this the per-channel mod policy is invisible where it
    /// matters: a channel switched off for safety looks exactly like a channel that broke,
    /// and the one place the verdict was visible — the agent's console readout — is not
    /// where a player is looking.
    /// </summary>
    private void AnnounceCompatibilityChanges()
    {
        if (manager.Current is not { } session)
        {
            _compatibilityAnnouncer.Reset();
            return;
        }

        _compatibilityAnnouncer.Observe(session.Compatibility, Reply);
    }

    /// <summary>Posts the session roster: the local player first, then each remote peer with
    /// the module they were last seen in. Everything read here already exists per tick —
    /// <see cref="ICoopSession.GetRemotePeers"/> and the session stats.</summary>
    private void ListPlayers(ICoopSession session)
    {
        IReadOnlyList<RemotePeer> peers = session.GetRemotePeers();
        SessionStats stats = session.GetStats();

        string rtt = stats.RoundTripMs > 0 ? $" - rtt {stats.RoundTripMs:F0}ms" : string.Empty;
        Reply($"players ({peers.Count + 1}){rtt}:");

        string localModule = bridge.TryReadLocalState(out PlayerState state) && state.Module.Length > 0
            ? state.Module
            : "?";
        Reply($"  {session.LocalName} (you)  {localModule}");

        foreach (RemotePeer peer in peers)
        {
            string module = peer.State.Module.Length > 0 ? peer.State.Module : "?";
            string flags = peer.State.IsDowned ? "  [down]"
                : peer.State.IsInDialogue ? "  [talking]"
                : peer.State.IsInCombat ? "  [fighting]"
                : string.Empty;
            Reply($"  {peer.Name}  {module}{flags}");
        }
    }

    private void Join(string? address, string? password)
    {
        // "connect #n" dials the n-th server from the last `servers` listing.
        if (address is { Length: > 1 } && address[0] == '#'
            && int.TryParse(address[1..], out int index))
        {
            if (index < 1 || index > _lastServerList.Count)
            {
                Reply($"no server #{index} - run `servers` first");
                return;
            }

            JoinListed(index - 1, password);
            return;
        }

        CoopCommand command = new(CoopCommandKind.Connect, address, password);
        if (!command.TryResolveEndpoint(defaultPort, out IPEndPoint endpoint))
        {
            Reply($"could not resolve host '{address}'");
            return;
        }

        // A typed password wins; otherwise whatever is already held (coop.ini's, or one
        // offered ahead of the join by the in-browser prompt) rides along — the same rule
        // JoinListed applies.
        Dial(address, endpoint, password ?? _password, locked: false);
    }

    /// <summary>
    /// Joins the <paramref name="index"/>-th server of the last published listing — the path
    /// behind both <c>connect #n</c> and a row confirmed in the in-game browser panel. A typed
    /// <paramref name="password"/> wins; otherwise whatever the session is already carrying
    /// (coop.ini's, or one the player has answered a prompt with) is reused.
    /// </summary>
    private void JoinListed(int index, string? password = null)
    {
        if (index < 0 || index >= _lastServerList.Count)
        {
            Reply("that server is no longer listed");
            return;
        }

        ServerInfo chosen = _lastServerList[index];
        Reply($"connecting to {chosen.Name} ({chosen.Address})");
        Dial(chosen.Address, chosen.EndPoint, password ?? _password, chosen.Locked);
    }

    /// <summary>
    /// The one place a join is actually started. Records what a password prompt needs to retry
    /// it, and refuses to dial a server the beacon says is locked when nothing has supplied a
    /// password — prompting instead, because that attempt can only end in E_PASSWORD and a
    /// failed handshake is a slower, more confusing way to ask the same question.
    /// </summary>
    private void Dial(string? address, IPEndPoint endpoint, string password, bool locked)
    {
        _pendingAddress = address ?? endpoint.ToString();
        _pendingEndpoint = endpoint;
        _password = password;
        _promptedForPassword = false;

        if (locked && password.Length == 0)
        {
            PromptForPassword("this server is password protected");
            return;
        }

        manager.Connect(host: false, endpoint, settingsForPassword(password));
    }

    /// <summary>
    /// Answers a prompt: retry the waiting join with the typed password — or, when no join is
    /// waiting, HOLD the password for the one about to start. The unsolicited case is normal
    /// now, not an error: the in-browser prompt publishes <c>password &lt;pw&gt;</c> BEFORE
    /// the browser confirms the row, precisely so this agent already holds the secret when
    /// the connect request lands a moment later.
    /// </summary>
    private void AnswerPasswordPrompt(string password)
    {
        if (password.Length == 0)
        {
            Reply("that password was empty - type: password <the server's password>");
            return;
        }

        if (_pendingEndpoint is not { } endpoint)
        {
            // An offer: nothing to retry, so nothing is dialled — Dial picks this up via
            // _password when the join starts (the browser-confirm arrives in this same tick,
            // drained after the console line).
            _password = password;
            Reply("password saved for the next join");
            return;
        }

        _password = password;
        _promptedForPassword = false; // a wrong one must be able to ask again
        Reply($"retrying {_pendingAddress}...");
        manager.Connect(host: false, endpoint, settingsForPassword(password));
    }

    /// <summary>
    /// Asks the player for a password. The ask is handed to the PLUGIN, which force-opens the
    /// developer console pre-filled with <c>password&#160;</c> — the agent's own console is
    /// behind a fullscreen game and cannot take the keystroke. The written hint goes out too,
    /// for the headless case and because the overlay keeps it on screen after the console
    /// closes on Enter.
    /// </summary>
    private void PromptForPassword(string why)
    {
        _promptedForPassword = true;
        Reply($"{why} - type the password and press Enter (or: password <pw>)");

        // The ask has to reach the GAME. If it cannot, the player is left looking at a menu
        // with no prompt on it while we wait for an answer nobody was asked for — so say so
        // instead of failing silently, and name the manual way in.
        if (_kotor is null || !_kotor.RequestPasswordPrompt())
        {
            Console.WriteLine(
                "[coop] could not open the in-game prompt; press ~ in the game and type: password <pw>");
        }
    }

    /// <summary>
    /// Keeps the in-game browser panel fed with what the LAN listener has heard, for as long
    /// as the agent is alive — not only while it is sitting at the menu.
    ///
    /// The menu loop publishes this before you connect; nothing did afterwards, so the F9
    /// panel went empty the moment you joined a session and stayed that way, which reads as
    /// "the browser is broken" rather than "the list is not being refreshed". The publish
    /// itself is cheap: it signature-checks and returns without writing when the list has
    /// not changed, which is the common case.
    /// </summary>
    private void PublishBrowserList()
    {
        if (_kotor is null)
        {
            return;
        }

        _lastServerList = _browser.Servers;
        _kotor.PublishBrowserServers(
            [.. _lastServerList.Select(s =>
                (s.Name, s.Address, (byte)s.PlayerCount, (byte)s.MaxPlayers, s.Locked, s.Module, s.Faces))],
            passwordHeld: _password.Length > 0);
    }

    /// <summary>Posts the LAN server list to the overlay, numbered for `connect #n`.</summary>
    private void ListServers()
    {
        _lastServerList = _browser.Servers;
        if (_lastServerList.Count == 0)
        {
            Reply("no co-op servers found on the LAN (host one, or connect by IP)");
            return;
        }

        Reply($"servers on the LAN ({_lastServerList.Count}) - connect #n to join:");
        for (int i = 0; i < _lastServerList.Count; i++)
        {
            ServerInfo s = _lastServerList[i];
            Reply($"  {i + 1}) {s.Name}  {s.Address}  ({s.PlayerCount}/{s.MaxPlayers})");
        }
    }

    /// <summary>
    /// Posts a console reply when the session phase changes, and turns a password-related
    /// rejection into an actionable prompt that echoes the address to retype with a password.
    /// </summary>
    private void ReflectPhase()
    {
        SessionPhase phase = manager.Phase;
        if (phase == _lastPhase)
        {
            return;
        }

        _lastPhase = phase;

        switch (phase)
        {
            case SessionPhase.Connected:
                Reply($"connected - {manager.Detail}");
                FireMenuAutoLoadIfArmed();
                break;

            case SessionPhase.Failed:
                // The host answered E_PASSWORD: the one set in coop.ini was wrong, or there was
                // none. Ask, rather than leaving the player at a menu with a rejection they can
                // only see in the agent's window.
                if (!_promptedForPassword && _pendingEndpoint is not null
                    && manager.Detail.Contains("password", StringComparison.OrdinalIgnoreCase))
                {
                    PromptForPassword(_password.Length > 0 ? "that password was wrong" : "this server needs a password");
                }
                else
                {
                    Reply($"failed - {manager.Detail}");
                }

                // A refused join (wrong password, incompatible save...) must never load a
                // save out from under the menu.
                _menuAutoLoadArmed = false;
                break;

            case SessionPhase.Disconnected:
                Reply("disconnected");
                break;
        }
    }

    /// <summary>Surfaces chat received from peers on the in-game console and stdout.</summary>
    private void DrainChat()
    {
        if (manager.Current is not { } session)
        {
            return;
        }

        foreach (ChatMessage message in session.DrainChat())
        {
            Chat($"{message.Name}: {message.Text}");
        }
    }

    /// <summary>
    /// All co-op feedback — command replies and chat alike — goes to the always-on overlay as
    /// well as the console reply line, because the console closes on Enter (so a reply that
    /// arrives a tick later would never be seen there) and the overlay persists regardless.
    /// </summary>
    private void Reply(string text)
    {
        Console.WriteLine($"[coop] {text}");
        _kotor?.PostConsoleReply(text);
        _kotor?.PostChatLine(text);
    }

    /// <summary>Alias kept for call sites that specifically mean a chat line.</summary>
    private void Chat(string text) => Reply(text);
}
