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

    /// <summary>The address of the last join attempt, so a password prompt can echo it back.</summary>
    private string? _pendingAddress;

    private SessionPhase _lastPhase = SessionPhase.Idle;
    private bool _promptedForPassword;

    /// <summary>Polls the block for console/menu commands and reflects status until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(150));

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                manager.Poll();
                DrainInputs();
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
                    Reply($"you: {message}"); // echo locally; peers get it over the wire
                }
                else
                {
                    Reply("not connected - nothing to say to");
                }

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
            Reply($"{message.Name}: {message.Text}");
        }
    }

    private void Reply(string text)
    {
        Console.WriteLine($"[coop] {text}");
        _kotor?.PostConsoleReply(text);
    }
}
