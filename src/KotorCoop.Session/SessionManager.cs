using System.Net;
using System.Net.Sockets;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;

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

    /// <summary>While hosting, broadcasts this server's presence so LAN browsers can list it.</summary>
    private ServerDiscovery? _discovery;

    // The last connect parameters, so reconnect needs no arguments.
    private bool _lastHost;
    private IPEndPoint? _lastEndpoint;
    private SessionSettings? _lastSettings;

    /// <summary>
    /// When a CLIENT's current connect attempt must be given up on if nothing has happened
    /// by then (<see cref="SessionClock.NowMs"/> units). 0 = no deadline armed — a host has
    /// nobody to be admitted by, so it never sets one.
    /// </summary>
    private long _connectDeadlineMs;

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

    /// <summary>
    /// Whether <see cref="Reconnect"/> has parameters to reuse — i.e. any
    /// <see cref="Connect"/> has ever happened in this process. A bare Co-op button press
    /// means "reconnect" when this is true and "consult coop.ini" when it is not.
    /// </summary>
    public bool CanReconnect
    {
        get
        {
            lock (_gate)
            {
                return _lastSettings is not null;
            }
        }
    }

    public string Detail => _detail;

    /// <summary>
    /// The settings the CURRENT (or most recent) <see cref="Connect"/> used — the same values
    /// <see cref="Reconnect"/> and <see cref="RejoinAfterLoadAsync"/> already read internally
    /// (<c>_lastSettings</c>), exposed so a caller that needs the live session's own port and
    /// (unhashed) password — <see cref="KotorCoop.Agent.CoopCommandDispatcher"/>'s world-serve
    /// listener for roadmap item 14's rejoin gates — does not have to re-derive or duplicate
    /// them. Null before the first <see cref="Connect"/> this process.
    /// </summary>
    public SessionSettings? CurrentSettings
    {
        get
        {
            lock (_gate)
            {
                return _lastSettings;
            }
        }
    }

    /// <summary>Optional sink for status changes; surfaced by the console and dashboard.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Fires whenever <see cref="Phase"/> actually changes (Idle/Connecting/Connected/
    /// Disconnected/Failed) — raised synchronously, wherever the transition already happens
    /// today (<see cref="Connect"/>, <see cref="Disconnect"/>, <see cref="Poll"/>,
    /// <see cref="TimeoutConnect"/>/<see cref="Fail"/>), through the shared <see cref="SetPhase"/>
    /// helper. Additive: no existing phase logic moved onto this, only a notification layered
    /// on top of it.</summary>
    public event Action<SessionPhase, SessionPhase>? PhaseChanged;

    /// <summary>Sets <see cref="_phase"/> and raises <see cref="PhaseChanged"/> when the value
    /// actually differs — the single place every "_phase = ..." assignment in this class goes
    /// through, so the event can never miss a transition or double-fire a no-op one.</summary>
    private void SetPhase(SessionPhase phase)
    {
        SessionPhase previous = _phase;
        _phase = phase;
        if (previous != phase)
        {
            PhaseChanged?.Invoke(previous, phase);
        }
    }

    /// <summary>
    /// Fires immediately before a live session is torn down — a clean disconnect, a
    /// reconnect's implicit teardown, or a fresh <see cref="Connect"/> replacing one already
    /// running. The last chance to read anything off <see cref="Current"/>/<see cref="Agent"/>
    /// while it is still alive. Consumer: roadmap item 14 phase 1's "resume tomorrow" —
    /// capturing a client's progressed character before the session object (and the world it
    /// was standing in) goes away. Never invoked when there was nothing to tear down.
    /// </summary>
    public Action? OnDisconnecting { get; set; }

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

            ICoopSession session;
            try
            {
                session = host ? new CoopHost(settings) : new CoopClient(settings, endpoint!);
            }
            catch (PortInUseException ex)
            {
                // Distinct, actionable line rather than a generic bind failure - and Idle,
                // not Failed: nothing here ever half-started (the socket that would have
                // backed it never came up), so there is nothing for Disconnect to unwind.
                Fail(
                    $"port {ex.Port} is already in use - is another co-op agent already running?",
                    SessionPhase.Idle);
                return;
            }
            catch (SocketException ex)
            {
                Fail($"could not start the session: {ex.Message}", SessionPhase.Idle);
                return;
            }

            CoopAgent agent = new(bridge, session, agentSettings);

            // Through a lambda so a sink assigned to this manager AFTER the session started
            // still hears the agent — the dashboard wires itself up later in startup.
            agent.Log = line => Log?.Invoke(line);

            CancellationTokenSource cts = new();
            _session = session;
            _agent = agent;
            _cts = cts;
            _running = agent.RunAsync(cts.Token);

            // A host makes itself discoverable on the LAN so a browsing client can list it
            // without being told an IP. The player count and current module are read live,
            // so a browser shows a game in progress (and where) rather than a stale lobby;
            // the locked flag tells a browsing player a password prompt is coming.
            if (host)
            {
                _discovery = new ServerDiscovery();
                _discovery.StartAdvertising(
                    settings.AdvertisedName,
                    settings.Port,
                    (byte)KotorCoop.Protocol.ProtocolConstants.MaxPeers,
                    () => (_session?.GetStats().PeerCount ?? 0) + 1,
                    locked: settings.Password.Length > 0,
                    module: () => bridge.TryReadLocalState(out var state) ? state.Module : string.Empty,
                    // Read live, like the count and the module: a browsing player sees the
                    // party as it is now, not as it was when the session started.
                    portraits: bridge.ReadPartyPortraits);
            }

            // A host has nobody to wait for; only a client's attempt can go unanswered
            // forever, so only a client arms the timeout.
            _connectDeadlineMs = host
                ? 0
                : SessionClock.NowMs + Math.Max(1, settings.ConnectTimeoutMs);

            SetPhase(SessionPhase.Connecting);
            _detail = host ? $"hosting on port {settings.Port}" : $"connecting to {endpoint}";
            Log?.Invoke(_detail);
        }
    }

    /// <summary>
    /// Cancels and disposes the live session/agent, if any, without deciding what phase or
    /// message comes after — that is the caller's call, because <see cref="Disconnect"/>
    /// and a connect timeout land in different places. Returns false when there was
    /// nothing to tear down (mirrors the old <see cref="Disconnect"/> early-out exactly).
    /// </summary>
    private bool TearDownSession()
    {
        CancellationTokenSource? cts;
        ICoopSession? session;
        Task running;

        ServerDiscovery? discovery;
        lock (_gate)
        {
            cts = _cts;
            session = _session;
            running = _running;
            discovery = _discovery;
            _cts = null;
            _session = null;
            _agent = null;
            _discovery = null;
        }

        discovery?.Dispose();

        if (cts is null)
        {
            return false;
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
        }

        return true;
    }

    /// <summary>
    /// Stops the current session. A client sends its Bye during the session's own shutdown,
    /// so the host drops it cleanly. Blocks briefly for the loops to unwind, then clears the
    /// game's co-op state so no phantoms or mirrored peers are left behind.
    /// </summary>
    public void Disconnect()
    {
        if (Current is not null)
        {
            OnDisconnecting?.Invoke();
        }

        if (!TearDownSession())
        {
            return;
        }

        _connectDeadlineMs = 0;
        SetPhase(SessionPhase.Disconnected);
        _detail = "disconnected";
        Log?.Invoke(_detail);
    }

    /// <summary>
    /// A client's connect attempt went unanswered past its deadline: the host is down, the
    /// port is firewalled, or the address was wrong — total silence looks the same from
    /// here either way. Distinct from a wrong-password rejection (which the host DOES
    /// answer, and which <see cref="Poll"/> resolves to <see cref="SessionPhase.Failed"/>
    /// immediately, never through this path): lands on <see cref="SessionPhase.Idle"/>
    /// instead, so a silent host does not read as the same kind of failure as a refused one.
    /// </summary>
    private void TimeoutConnect()
    {
        IPEndPoint? endpoint;
        lock (_gate)
        {
            endpoint = _lastEndpoint;
        }

        TearDownSession();
        _connectDeadlineMs = 0;
        Fail($"no reply from {(endpoint is null ? "the host" : endpoint.ToString())}", SessionPhase.Idle);
    }

    /// <summary>
    /// Roadmap item 14 phase 2 (docs/research/session-save.md's Phase 2 section): the rejoin
    /// action a mid-session load edge triggers, for a CLIENT only. Tears the current session
    /// down, re-runs world adoption against the SAME host this connection's last
    /// <see cref="Connect"/> used, and requests the plugin replay the adopted-world load —
    /// exactly the two pieces <c>agent adopt</c> (Program.cs's <c>AdoptAsync</c>) already
    /// composes for the FIRST join, reused rather than re-derived. Does NOT itself reopen the
    /// co-op socket: the local engine load this kicks off is asynchronous, so the caller polls
    /// <paramref name="bridge"/>'s <c>TryGetAdoptedWorldLoadStatus</c> and calls
    /// <see cref="Reconnect"/> once it settles (mirrors how <see cref="Poll"/>/<see cref="Connect"/>
    /// are already split for exactly this "cannot block on the game" reason).
    ///
    /// ⚠ NEEDS-LIVE: see <c>KotorGameBridge.TryRequestAdoptedWorldLoad</c>'s own doc comment
    /// for the one real gap this reuses rather than papers over — whether the local engine
    /// load request lands while <c>Odyssey.HasServer</c> is still true from the player's own
    /// just-completed Load Game action has never been measured.
    ///
    /// Only valid for a CLIENT — a host has nothing to "rejoin" (see the host mid-session-load
    /// path in <c>SessionSaveWatch</c>/<c>CoopCommandDispatcher</c> instead, which reuses THIS
    /// same method on every connected CLIENT once the host's own reload settles). Returns null
    /// with nothing torn down when there is no prior client connection to rejoin from.
    /// </summary>
    public async Task<AdoptedWorld?> RejoinAfterLoadAsync(
        IGameBridge bridge,
        string gameDirectory,
        (GffStruct Player, SidecarMetadata Metadata)? carriedCharacter,
        CancellationToken cancellationToken)
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

        if (host || endpoint is null || settings is null)
        {
            Log?.Invoke("rejoin: nothing to rejoin (not a joined client, or never connected)");
            return null;
        }

        Disconnect();

        if (!PluginMenuLoader.TryResolveSaveDirectory(gameDirectory, slotIndex: 0, out string ownSave, out string which))
        {
            Log?.Invoke($"rejoin: {which}");
            return null;
        }

        AdoptedWorld? adopted = await WorldAdoption.TryAdoptAsync(
            gameDirectory,
            ownSave,
            carriedCharacter,
            endpoint,
            SessionPassword.Hash(settings.Password),
            expectedSaveIdentity: 0,
            Log,
            cancellationToken).ConfigureAwait(false);

        if (adopted is null)
        {
            return null;
        }

        if (!bridge.TryRequestAdoptedWorldLoad(out string diagnostic))
        {
            Log?.Invoke($"rejoin: {diagnostic}");
            return null;
        }

        Log?.Invoke($"rejoin: {diagnostic}");
        return adopted;
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

        // Checked before reading stats: a host that never answers Hello produces no status
        // change at all, so nothing below would ever notice on its own.
        if (_phase == SessionPhase.Connecting && !_lastHost
            && _connectDeadlineMs != 0 && SessionClock.NowMs >= _connectDeadlineMs)
        {
            TimeoutConnect();
            return;
        }

        SessionStats stats = session.GetStats();
        if (stats.Connected)
        {
            _connectDeadlineMs = 0;
            SetPhase(SessionPhase.Connected);
            _detail = stats.Status;
        }
        else if (stats.Status.Contains("rejected", StringComparison.OrdinalIgnoreCase))
        {
            _connectDeadlineMs = 0;
            SetPhase(SessionPhase.Failed);
            _detail = stats.Status;
        }
        else
        {
            SetPhase(SessionPhase.Connecting);
            _detail = stats.Status;
        }
    }

    /// <summary>Lands the manager on <paramref name="phase"/> (Failed by default) with a
    /// message for the console/overlay. Also used with <see cref="SessionPhase.Idle"/> for
    /// failures that never half-started anything — a port-in-use bind, a connect
    /// timeout — which must not read as the same "Failed" state a wrong password does.</summary>
    private void Fail(string reason, SessionPhase phase = SessionPhase.Failed)
    {
        SetPhase(phase);
        _detail = reason;
        Log?.Invoke(reason);
    }

    public void Dispose() => Disconnect();
}
