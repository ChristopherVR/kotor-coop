using System.Net;
using KotorCoop.Memory;

namespace KotorCoop.Session;

/// <summary>Where a managed session is in its lifecycle, for a status line.</summary>
public enum SessionPhase
{
    Idle,
    Connecting,
    Connected,
    Disconnected,
    Failed,
}

/// <summary>
/// Owns a co-op session's lifecycle so it can be started, stopped and restarted at runtime
/// rather than living for the whole process. This is what lets the in-game console (and the
/// dashboard) offer <c>connect</c>, <c>disconnect</c> and <c>reconnect</c>: each command
/// tears down the current session and agent and, for connect, stands up a fresh pair.
///
/// One <see cref="CoopAgent"/> is created per session (they share the process-lived bridge),
/// which keeps the pump and the session strictly paired — a torn-down session leaves no
/// half-connected agent writing stale peers into the game.
/// </summary>
public sealed class SessionManager(IGameBridge bridge, AgentSettings agentSettings) : IDisposable
{
    private readonly Lock _gate = new();
    private ICoopSession? _session;
    private CoopAgent? _agent;
    private CancellationTokenSource? _cts;
    private Task _running = Task.CompletedTask;

    // The last connect parameters, so reconnect needs no arguments.
    private bool _lastHost;
    private IPEndPoint? _lastEndpoint;
    private SessionSettings? _lastSettings;

    private volatile SessionPhase _phase = SessionPhase.Idle;
    private string _detail = "not connected";

    /// <summary>The live session, or null when disconnected. The pump reads this.</summary>
    public ICoopSession? Current
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    /// <summary>The agent driving the live session, or null.</summary>
    public CoopAgent? Agent
    {
        get
        {
            lock (_gate)
            {
                return _agent;
            }
        }
    }

    public SessionPhase Phase => _phase;

    public string Detail => _detail;

    /// <summary>Optional sink for status changes; surfaced by the console and dashboard.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Tears down any current session and starts a new one. Host binds and waits; a client
    /// begins the join handshake to <paramref name="endpoint"/>.
    /// </summary>
    public void Connect(bool host, IPEndPoint? endpoint, SessionSettings settings)
    {
        if (!host && endpoint is null)
        {
            Fail("connect: no host address");
            return;
        }

        Disconnect();

        lock (_gate)
        {
            _lastHost = host;
            _lastEndpoint = endpoint;
            _lastSettings = settings;

            ICoopSession session = host ? new CoopHost(settings) : new CoopClient(settings, endpoint!);
            CoopAgent agent = new(bridge, session, agentSettings);

            CancellationTokenSource cts = new();
            _session = session;
            _agent = agent;
            _cts = cts;
            _running = agent.RunAsync(cts.Token);

            _phase = SessionPhase.Connecting;
            _detail = host ? $"hosting on port {settings.Port}" : $"connecting to {endpoint}";
            Log?.Invoke(_detail);
        }
    }

    /// <summary>
    /// Stops the current session. A client sends its Bye during the session's own shutdown,
    /// so the host drops it cleanly. Blocks briefly for the loops to unwind, then clears the
    /// game's co-op state so no phantoms or mirrored peers are left behind.
    /// </summary>
    public void Disconnect()
    {
        CancellationTokenSource? cts;
        ICoopSession? session;
        Task running;

        lock (_gate)
        {
            cts = _cts;
            session = _session;
            running = _running;
            _cts = null;
            _session = null;
            _agent = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
            running.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Teardown is best-effort; a stuck loop must not wedge the console.
        }
        finally
        {
            session?.Dispose();
            cts.Dispose();
            bridge.ClearCoopState();
            _phase = SessionPhase.Disconnected;
            _detail = "disconnected";
            Log?.Invoke(_detail);
        }
    }

    /// <summary>Reconnects with the parameters of the last <see cref="Connect"/>.</summary>
    public void Reconnect()
    {
        bool host;
        IPEndPoint? endpoint;
        SessionSettings? settings;

        lock (_gate)
        {
            host = _lastHost;
            endpoint = _lastEndpoint;
            settings = _lastSettings;
        }

        if (settings is null)
        {
            Fail("reconnect: nothing to reconnect to");
            return;
        }

        Log?.Invoke("reconnecting...");
        Connect(host, endpoint, settings);
    }

    /// <summary>Refreshes the phase from the live session each tick (Connecting -&gt;
    /// Connected once the handshake lands, or the reject reason if it failed).</summary>
    public void Poll()
    {
        ICoopSession? session = Current;
        if (session is null)
        {
            return;
        }

        SessionStats stats = session.GetStats();
        if (stats.Connected)
        {
            _phase = SessionPhase.Connected;
            _detail = stats.Status;
        }
        else if (stats.Status.Contains("rejected", StringComparison.OrdinalIgnoreCase))
        {
            _phase = SessionPhase.Failed;
            _detail = stats.Status;
        }
        else
        {
            _phase = SessionPhase.Connecting;
            _detail = stats.Status;
        }
    }

    private void Fail(string reason)
    {
        _phase = SessionPhase.Failed;
        _detail = reason;
        Log?.Invoke(reason);
    }

    public void Dispose() => Disconnect();
}
