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

/// <summary>A fetched world plus the manager lifecycle generation that owns its pending load.</summary>
public sealed record RejoinAdoption(AdoptedWorld World, long Generation);

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
    private long _lifecycleGeneration;
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

    /// <summary>Identity of the current manager lifecycle, used by background operations that
    /// must not outlive the session they were triggered from.</summary>
    public long LifecycleGeneration
    {
        get
        {
            lock (_gate)
            {
                return _lifecycleGeneration;
            }
        }
    }

    /// <summary>Captures the generation only while <paramref name="expectedSession"/> is
    /// still the current session, for work queued from a particular pump snapshot.</summary>
    public bool TryGetCurrentGeneration(ICoopSession expectedSession, out long generation)
    {
        lock (_gate)
        {
            generation = _lifecycleGeneration;
            return ReferenceEquals(_session, expectedSession);
        }
    }

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
        lock (_gate)
        {
            _lifecycleGeneration++;
            ConnectCore(host, endpoint, settings);
        }
    }

    private void ConnectCore(bool host, IPEndPoint? endpoint, SessionSettings settings)
    {
        if (!host && endpoint is null)
        {
            Fail("connect: no host address");
            return;
        }

        DisconnectCore();

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

    /// <summary>
    /// Cancels and disposes the live session/agent, if any, without deciding what phase or
    /// message comes after — that is the caller's call, because <see cref="Disconnect"/>
    /// and a connect timeout land in different places. Returns false when there was
    /// nothing to tear down (mirrors the old <see cref="Disconnect"/> early-out exactly).
    /// </summary>
    private bool TearDownSession()
    {
        // Caller holds _gate for the full teardown, including ClearCoopState(), so another
        // connect cannot install a new session between ownership release and native cleanup.
        CancellationTokenSource? cts = _cts;
        ICoopSession? session = _session;
        Task running = _running;
        ServerDiscovery? discovery = _discovery;
        _cts = null;
        _session = null;
        _agent = null;
        _discovery = null;

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
        lock (_gate)
        {
            _lifecycleGeneration++;
            DisconnectCore();
        }
    }

    private void DisconnectCore()
    {
        if (_session is not null)
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
        lock (_gate)
        {
            IPEndPoint? endpoint = _lastEndpoint;

            TearDownSession();
            _connectDeadlineMs = 0;

            // A silent LAN address usually means a typo or the host simply isn't up; a silent
            // address that had to cross a router is much more likely to mean the port was never
            // forwarded, so name that possibility explicitly rather than leaving a player who
            // typed a real internet address stuck guessing what "no reply" could mean.
            string hint = endpoint is not null && !IsLikelyLan(endpoint.Address)
                ? $" - is UDP port {endpoint.Port} forwarded on the host?"
                : string.Empty;

            Fail($"no reply from {(endpoint is null ? "the host" : endpoint.ToString())}{hint}", SessionPhase.Idle);
        }
    }

    /// <summary>
    /// Whether <paramref name="address"/> looks like it stays on a local network (loopback,
    /// link-local, or one of the three RFC 1918 private ranges) — used only to decide whether
    /// <see cref="TimeoutConnect"/>'s port-forwarding hint is worth printing, since that
    /// question only makes sense for an address that had to cross a router in the first place.
    /// IPv6 always answers false (no hint either way): NAT/forwarding over IPv6 is untested by
    /// this project (docs/research/network-topology.md's "Internet play" section), so guessing
    /// either way here would be worse than saying nothing.
    /// </summary>
    private static bool IsLikelyLan(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254); // link-local
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
    /// <see cref="CompleteRejoin"/> once it settles (mirrors how <see cref="Poll"/>/<see cref="Connect"/>
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
    /// <param name="carriedIsAuthoritative">coop.ini <c>join_mode = auto</c> (the default): the
    /// sidecar IS the co-op character, full stop — carry it without consulting this install's
    /// own saves at all (<see cref="WorldAdoption.TryAdoptWithCharacterAsync"/>). A fresh
    /// install that joined through character creation may have no own save to resolve, and a
    /// single-player save written since must never outrank the co-op character on age. Ignored
    /// when <paramref name="carriedCharacter"/> is null.</param>
    public async Task<RejoinAdoption?> RejoinAfterLoadAsync(
        IGameBridge bridge,
        string gameDirectory,
        (GffStruct Player, SidecarMetadata Metadata)? carriedCharacter,
        long expectedGeneration,
        CancellationToken cancellationToken,
        bool carriedIsAuthoritative = false,
        bool worldTransferUdpFallback = false)
    {
        if (!TryBeginRejoin(expectedGeneration, cancellationToken, out IPEndPoint endpoint,
                out SessionSettings settings, out long generation))
        {
            return null;
        }

        AdoptedWorld? adopted;
        if (carriedIsAuthoritative && carriedCharacter is { } coop)
        {
            adopted = await WorldAdoption.TryAdoptWithCharacterAsync(
                gameDirectory,
                coop.Player,
                $"the co-op character (captured {coop.Metadata.CapturedAtUtc:u})",
                endpoint,
                SessionPassword.Hash(settings.Password),
                expectedSaveIdentity: 0,
                Log,
                cancellationToken,
                worldTransferUdpFallback).ConfigureAwait(false);
        }
        else
        {
            if (!PluginMenuLoader.TryResolveSaveDirectory(gameDirectory, slotIndex: 0, out string ownSave, out string which))
            {
                Log?.Invoke($"rejoin: {which}");
                return null;
            }

            adopted = await WorldAdoption.TryAdoptAsync(
                gameDirectory,
                ownSave,
                carriedCharacter,
                endpoint,
                SessionPassword.Hash(settings.Password),
                expectedSaveIdentity: 0,
                Log,
                cancellationToken,
                worldTransferUdpFallback).ConfigureAwait(false);
        }

        if (adopted is null)
        {
            return null;
        }

        // The transfer may complete after an external reset cancelled this generation. Do not
        // ask the native engine to load a late world after ownership has already been revoked.
        if (!TryRequestAdoptedWorldLoad(bridge, generation, cancellationToken, out string diagnostic))
        {
            return null;
        }

        // inSession: true — a rejoin can start from either side of a world. Parked at the
        // game-over menu after a wipe (WipeRecoveryRule) there is no server and the plugin
        // takes its main-menu route regardless; standing in a world of our own (the host
        // reloaded while we played on) the mid-session replay is the only one that loads at
        // all — the main-menu-only request is acked and mooted the instant a server exists.
        // The mid-session replay itself is the path join_mode=newcharacter live-verified
        // 2026-08-29 (docs/research/world-adoption.md).
        Log?.Invoke($"rejoin: {diagnostic}");
        return new RejoinAdoption(adopted.Value, generation);
    }

    private bool TryBeginRejoin(
        long expectedGeneration,
        CancellationToken cancellationToken,
        out IPEndPoint endpoint,
        out SessionSettings settings,
        out long generation)
    {
        lock (_gate)
        {
            endpoint = null!;
            settings = null!;
            generation = _lifecycleGeneration;
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            if (expectedGeneration != _lifecycleGeneration)
            {
                return false;
            }
            if (_lastHost || _lastEndpoint is null || _lastSettings is null)
            {
                Log?.Invoke("rejoin: nothing to rejoin (not a joined client, or never connected)");
                return false;
            }

            endpoint = _lastEndpoint;
            settings = _lastSettings;
            DisconnectCore();
            return true;
        }
    }

    private bool TryRequestAdoptedWorldLoad(
        IGameBridge targetBridge,
        long generation,
        CancellationToken cancellationToken,
        out string diagnostic)
    {
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested || generation != _lifecycleGeneration)
            {
                diagnostic = "rejoin cancelled by a session change";
                return false;
            }

            if (!targetBridge.TryRequestAdoptedWorldLoad(inSession: true, out diagnostic))
            {
                Log?.Invoke($"rejoin: {diagnostic}");
                return false;
            }

            return true;
        }
    }

    /// <summary>Runs settled-load bookkeeping and reopens the session only if no user session
    /// change superseded the adoption while the game was loading. The callback executes under
    /// the same lifecycle lock as connect/disconnect, before the replacement socket starts.</summary>
    public bool CompleteRejoin(
        long generation,
        CancellationToken cancellationToken,
        Action onAdoptedLoadSettled)
    {
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested || generation != _lifecycleGeneration)
            {
                return false;
            }

            bool host = _lastHost;
            IPEndPoint? endpoint = _lastEndpoint;
            SessionSettings? settings = _lastSettings;
            if (settings is null)
            {
                return false;
            }

            onAdoptedLoadSettled();
            _lifecycleGeneration++;
            Log?.Invoke("reconnecting...");
            ConnectCore(host, endpoint, settings);
            return true;
        }
    }

    /// <summary>
    /// The new-character join's adoption — coop.ini <c>join_mode = newcharacter</c>.
    ///
    /// Same two pieces as <see cref="RejoinAfterLoadAsync"/> (adopt the host's world, ask the
    /// plugin to load it carrying our character), with the two differences the first-time
    /// joiner forces:
    /// <list type="number">
    /// <item>The character is HANDED IN rather than lifted out of a save directory, because
    /// this player has no save — it is what the game's own character creation just wrote (see
    /// <see cref="WorldAdoption.TryAdoptWithCharacterAsync"/>).</item>
    /// <item>The load is requested with <c>inSession: true</c>. Retail chargen finishes by
    /// LOADING A MODULE — the player is standing in the Endar Spire by the time their
    /// character exists — so the adoption is mid-session by construction, and a main-menu-only
    /// request would be acked and mooted the instant <c>MenuLoadController</c> saw a server,
    /// leaving the player in single-player believing they had joined.</item>
    /// </list>
    ///
    /// Like <see cref="RejoinAfterLoadAsync"/>, this does NOT reopen the co-op socket: the
    /// caller polls the load and calls <see cref="Reconnect"/> once it settles.
    /// </summary>
    public async Task<AdoptedWorld?> AdoptWithNewCharacterAsync(
        IGameBridge bridge,
        string gameDirectory,
        GffStruct carriedPlayer,
        string characterSource,
        CancellationToken cancellationToken,
        bool worldTransferUdpFallback = false)
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
            Log?.Invoke("new character: nothing to join (not a joined client, or never connected)");
            return null;
        }

        Disconnect();

        AdoptedWorld? adopted = await WorldAdoption.TryAdoptWithCharacterAsync(
            gameDirectory,
            carriedPlayer,
            characterSource,
            endpoint,
            SessionPassword.Hash(settings.Password),
            expectedSaveIdentity: 0,
            Log,
            cancellationToken,
            worldTransferUdpFallback).ConfigureAwait(false);

        if (adopted is null)
        {
            return null;
        }

        if (!bridge.TryRequestAdoptedWorldLoad(inSession: true, out string diagnostic))
        {
            Log?.Invoke($"new character: {diagnostic}");
            return null;
        }

        Log?.Invoke($"new character: {diagnostic}");
        return adopted;
    }

    /// <summary>Reconnects with the parameters of the last <see cref="Connect"/>.</summary>
    public void Reconnect()
    {
        lock (_gate)
        {
            if (_lastSettings is null)
            {
                Fail("reconnect: nothing to reconnect to");
                return;
            }

            _lifecycleGeneration++;
            Log?.Invoke("reconnecting...");
            ConnectCore(_lastHost, _lastEndpoint, _lastSettings);
        }
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
