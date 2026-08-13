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
    Func<string, SessionSettings> settingsForPassword)
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
                    DrainChat();
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
            Dispatch(ConsoleCommand.Parse(line));
        }

        // The main-menu Connect button. It carries an optional typed host; empty means "use
        // the configured host", which we model as a bare reconnect/connect with no address.
        if (_kotor.TryTakeConnectRequest(out string typedHost))
        {
            Dispatch(typedHost.Length > 0
                ? new CoopCommand(CoopCommandKind.Connect, Address: typedHost)
                : new CoopCommand(CoopCommandKind.Reconnect));
        }

        // A row confirmed in the in-game browser panel. The menu loop consumes these until the
        // first session starts and nothing did afterwards, so a SECOND pick — which is the
        // ordinary case, because a refused join leaves the player back at the menu looking at
        // the same panel — did nothing at all, beside a list this class was already keeping
        // up to date.
        if (_kotor.TryTakeBrowserConnect(out int picked))
        {
            JoinListed(picked);
        }

        if (_kotor.TryTakeBrowserHost())
        {
            Dispatch(new CoopCommand(CoopCommandKind.Host));
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
        }
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

        Dial(address, endpoint, password ?? string.Empty, locked: false);
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
    /// Answers a prompt: retry the waiting join with the typed password. The address comes from
    /// the attempt that asked, so the player supplies only the secret.
    /// </summary>
    private void AnswerPasswordPrompt(string password)
    {
        if (_pendingEndpoint is not { } endpoint)
        {
            Reply("nothing is waiting for a password - pick a server first");
            return;
        }

        if (password.Length == 0)
        {
            Reply("that password was empty - type: password <the server's password>");
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
                (s.Name, s.Address, (byte)s.PlayerCount, (byte)s.MaxPlayers, s.Locked, s.Module, s.Faces))]);
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
