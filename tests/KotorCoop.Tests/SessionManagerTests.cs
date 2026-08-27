using System.Net;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Exercises the runtime lifecycle layer (connect / disconnect / reconnect) against a real
/// host over loopback, with a stub bridge standing in for the game. This is the layer the
/// in-game ~ console and the dashboard drive.
/// </summary>
public class SessionManagerTests
{
    private sealed class LifecycleBridge : IGameBridge
    {
        public int ClearCount { get; private set; }

        public string Description => "lifecycle-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public void ClearCoopState() => ClearCount++;

        public void Dispose()
        {
        }
    }

    private static async Task<bool> WaitForPhase(SessionManager manager, SessionPhase phase, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            manager.Poll();
            if (manager.Phase == phase)
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    [Fact]
    public async Task ConnectDisconnectReconnectDrivesTheLifecycle()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(40));
        Task hostTask = host.RunAsync(cts.Token);

        LifecycleBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });

        IPEndPoint endpoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);
        SessionSettings settings = new() { PlayerName = "Malak", TickRate = 30 };

        // Connect: the manager stands up a client + agent and reaches Connected.
        manager.Connect(host: false, endpoint, settings);
        Assert.True(
            await WaitForPhase(manager, SessionPhase.Connected, TimeSpan.FromSeconds(15)),
            "the manager never reached Connected");
        Assert.NotNull(manager.Current);
        Assert.NotNull(manager.Agent);

        ICoopSession firstSession = manager.Current!;

        // Disconnect: the session is gone, the game's co-op state is cleared.
        manager.Disconnect();
        Assert.Equal(SessionPhase.Disconnected, manager.Phase);
        Assert.Null(manager.Current);
        Assert.Null(manager.Agent);
        Assert.True(bridge.ClearCount >= 1, "disconnect did not clear the game's co-op state");

        // Reconnect: no arguments; the last connect parameters are reused, and a FRESH
        // session/agent is stood up (not the disposed one).
        manager.Reconnect();
        Assert.True(
            await WaitForPhase(manager, SessionPhase.Connected, TimeSpan.FromSeconds(15)),
            "the manager never reconnected");
        Assert.NotNull(manager.Current);
        Assert.NotSame(firstSession, manager.Current);

        await cts.CancelAsync();
        manager.Disconnect();
        try
        {
            await hostTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public void ReconnectWithNothingToReconnectToFails()
    {
        LifecycleBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });

        manager.Reconnect();

        Assert.Equal(SessionPhase.Failed, manager.Phase);
    }

    [Fact]
    public void JoinWithNoAddressFails()
    {
        LifecycleBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });

        manager.Connect(host: false, endpoint: null, new SessionSettings { PlayerName = "Malak" });

        Assert.Equal(SessionPhase.Failed, manager.Phase);
        Assert.Null(manager.Current);
    }

    /// <summary>
    /// A second co-op agent (or a leftover one from a crashed previous session) already
    /// holding the port used to throw a bare SocketException straight out of Connect,
    /// leaving the manager in whatever phase it happened to be in — never reported, never
    /// idle. Item 6c: this must fail cleanly to Idle with a specific, actionable line, and
    /// distinctly from the ordinary "connect: no host address" Failed case above.
    /// </summary>
    [Fact]
    public void HostingOnAPortAlreadyInUseFailsCleanlyToIdle()
    {
        using UdpChannel blocker = new(0);
        int busyPort = blocker.LocalEndPoint.Port;

        LifecycleBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });

        manager.Connect(
            host: true, endpoint: null,
            new SessionSettings { PlayerName = "Malak", Port = busyPort, TickRate = 30 });

        Assert.Equal(SessionPhase.Idle, manager.Phase);
        Assert.Contains("already in use", manager.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(manager.Current);
        Assert.Null(manager.Agent);
    }

    /// <summary>
    /// Item 6d: a host that never answers at all (down, firewalled, or simply the wrong
    /// address) used to leave the manager in Connecting forever — Poll kept re-deriving the
    /// same "connecting to X..." status from a CoopClient that was, correctly, still trying.
    /// A configurable deadline must give up and return to Idle with a message distinct from
    /// the wrong-password Failed path (which the host DOES answer, immediately).
    /// </summary>
    [Fact]
    public async Task JoiningAHostThatNeverAnswersTimesOutToIdle()
    {
        // A bound socket nothing ever reads from: Hello packets land and vanish, exactly
        // like a firewalled or dead host, without needing a second process.
        using UdpChannel deadEnd = new(0);
        IPEndPoint endpoint = new(IPAddress.Loopback, deadEnd.LocalEndPoint.Port);

        LifecycleBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });

        manager.Connect(host: false, endpoint, new SessionSettings
        {
            PlayerName = "Malak",
            TickRate = 30,
            ConnectTimeoutMs = 300,
        });

        Assert.Equal(SessionPhase.Connecting, manager.Phase);

        Assert.True(
            await WaitForPhase(manager, SessionPhase.Idle, TimeSpan.FromSeconds(5)),
            "the manager never gave up and returned to idle");
        Assert.Contains("no reply from", manager.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(endpoint.Port.ToString(), manager.Detail);
        Assert.Null(manager.Current);
        Assert.Null(manager.Agent);

        // A LAN/loopback address must NOT get roadmap item 24's port-forwarding hint: that
        // question only makes sense for an address that had to cross a router.
        Assert.DoesNotContain("forwarded", manager.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Roadmap item 24: a silent connect attempt against an address that had to cross a router
    /// (anything outside loopback/RFC 1918/link-local) gets an actionable hint appended, since
    /// "no reply" from a real internet address is disproportionately likely to mean the UDP
    /// port was never forwarded.
    /// </summary>
    [Fact]
    public async Task JoiningANonLanAddressThatNeverAnswersHintsAtPortForwarding()
    {
        // 203.0.113.0/24 is TEST-NET-3 (RFC 5737): reserved for documentation, never routed,
        // so nothing will ever answer this - exactly the "silent host" case, without depending
        // on any real external service.
        IPEndPoint endpoint = new(IPAddress.Parse("203.0.113.5"), 27015);

        LifecycleBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });

        manager.Connect(host: false, endpoint, new SessionSettings
        {
            PlayerName = "Malak",
            TickRate = 30,
            ConnectTimeoutMs = 300,
        });

        Assert.True(
            await WaitForPhase(manager, SessionPhase.Idle, TimeSpan.FromSeconds(5)),
            "the manager never gave up and returned to idle");

        Assert.Contains("no reply from", manager.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("forwarded", manager.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("27015", manager.Detail);
    }

    /// <summary>A host that answers within the deadline must reach Connected as normal —
    /// the timeout must not fire on its way there.</summary>
    [Fact]
    public async Task AQuickConnectBeatsAShortTimeout()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(cts.Token);

        LifecycleBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });

        IPEndPoint endpoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);
        manager.Connect(host: false, endpoint, new SessionSettings
        {
            PlayerName = "Malak",
            TickRate = 30,
            ConnectTimeoutMs = 500, // short, but the loopback handshake is much faster
        });

        Assert.True(
            await WaitForPhase(manager, SessionPhase.Connected, TimeSpan.FromSeconds(5)),
            "the connect timeout fired before a real handshake could land");

        manager.Disconnect();
        await cts.CancelAsync();
        try
        {
            await hostTask;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
