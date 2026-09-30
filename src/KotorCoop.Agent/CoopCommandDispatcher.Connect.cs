using System.Net;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// The connect/host/reconnect/password-prompt flow: everything that decides WHICH endpoint
/// this instance is dialling and what to do about a locked server.
/// </summary>
public sealed partial class CoopCommandDispatcher
{
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
    /// Armed by a connect/host that originated inside the game (Co-op button, browser
    /// panel, ~ console) — i.e. one made while the player could plausibly still be at the
    /// main menu. When such a session reaches its load condition the save auto-loads once,
    /// so "pick a server, arrive in the world" is one action.
    /// </summary>
    private bool _menuAutoLoadArmed;

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
    ///
    /// A host fires this immediately after <see cref="Dispatch"/> returns, before anything
    /// else gets a chance to look at the outcome — so it must check <c>manager.Current</c>
    /// itself. A bind that failed (port already in use) now returns from Connect cleanly
    /// instead of throwing, and without this guard a host session that never actually
    /// started would still load a save into a game with nobody driving it.
    /// </summary>
    private void FireMenuAutoLoadIfArmed()
    {
        if (!_menuAutoLoadArmed)
        {
            return;
        }

        _menuAutoLoadArmed = false;

        if (menuAutoLoad is not null && manager.Current is not null && !bridge.TryReadLocalState(out _))
        {
            menuAutoLoad();
        }
    }

    private void HostCommand(CoopCommand command)
    {
        CancelRejoinForExternalSessionChange();
        _pendingAddress = null;
        _pendingEndpoint = null;
        _promptedForPassword = false;
        _password = command.Password ?? string.Empty;
        manager.Connect(host: true, endpoint: null, settingsForPassword(_password));
    }

    private void ReconnectCommand(CoopCommand command)
    {
        if (command.Address == "world")
        {
            BeginManualWorldRefresh();
            return;
        }
        CancelRejoinForExternalSessionChange();
        _promptedForPassword = false;
        manager.Reconnect();
    }

    private void DisconnectCommand()
    {
        CancelRejoinForExternalSessionChange();
        manager.Disconnect();
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
        CancelRejoinForExternalSessionChange();
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
        CancelRejoinForExternalSessionChange();
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

            case SessionPhase.Idle:
                // The only two things that move the phase back to Idle after it has left
                // Idle once are a port-in-use bind failure and a connect timeout
                // (SessionManager.Fail(..., SessionPhase.Idle)) — both already carry a
                // complete, player-facing sentence in Detail. Deliberately its own case,
                // not folded into Failed: a silent host and a wrong password are different
                // situations, and the password-prompt retry above must never fire for either.
                Reply(manager.Detail);
                _menuAutoLoadArmed = false;
                break;
        }
    }
}
