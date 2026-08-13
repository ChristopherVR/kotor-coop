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

    private SessionPhase _lastPhase = SessionPhase.Idle;
    private bool _promptedForPassword;

    /// <summary>
    /// Armed by a connect/host that originated inside the game (Co-op button, browser
    /// panel, ~ console) — i.e. one made while the player could plausibly still be at the
    /// main menu. When such a session reaches its load condition the save auto-loads once,
    /// so "pick a server, arrive in the world" is one action.
    /// </summary>
    private bool _menuAutoLoadArmed;

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
                manager.Poll();
                DrainInputs();
                PublishBrowserList();
                ReflectPhase();
                DrainChat();
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
                _promptedForPassword = false;
                manager.Connect(host: true, endpoint: null, settingsForPassword(command.Password ?? string.Empty));
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

            ServerInfo chosen = _lastServerList[index - 1];
            _pendingAddress = chosen.Address;
            _promptedForPassword = false;
            Reply($"connecting to {chosen.Name} ({chosen.Address})");
            manager.Connect(host: false, chosen.EndPoint, settingsForPassword(password ?? string.Empty));
            return;
        }

        CoopCommand command = new(CoopCommandKind.Connect, address, password);
        if (!command.TryResolveEndpoint(defaultPort, out IPEndPoint endpoint))
        {
            Reply($"could not resolve host '{address}'");
            return;
        }

        _pendingAddress = address;
        _promptedForPassword = false;
        manager.Connect(host: false, endpoint, settingsForPassword(password ?? string.Empty));
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
                (s.Name, s.Address, (byte)s.PlayerCount, (byte)s.MaxPlayers, s.Locked, s.Module))]);
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
                if (!_promptedForPassword && _pendingAddress is { } address
                    && manager.Detail.Contains("password", StringComparison.OrdinalIgnoreCase))
                {
                    _promptedForPassword = true;
                    Reply($"password required. type: connect {address} <password>");
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
