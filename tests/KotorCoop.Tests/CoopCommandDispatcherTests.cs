using KotorCoop.Agent;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The dispatcher's menu-connect surface: a bare Co-op button press resolved from coop.ini
/// (mode=host hosts, host=&lt;address&gt; joins, neither points at the server browser), and
/// the one-shot auto-load for sessions started from the main menu — fires on the host
/// immediately and on a joiner at Connected, never mid-world, never on a reconnect, never
/// after a refused join.
/// </summary>
public class CoopCommandDispatcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Stands in for the game. <see cref="InWorld"/> false models the main menu: no
    /// readable player state, which is exactly the probe the auto-load trigger uses.
    /// </summary>
    private sealed class MenuBridge : IGameBridge
    {
        public volatile bool InWorld;

        public string Description => "menu-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
            return InWorld;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public void ClearCoopState()
        {
        }

        public void Dispose()
        {
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    private static SessionSettings Settings(string password) => new()
    {
        PlayerName = "MenuPlayer",
        Port = 0,
        TickRate = 30,
        Password = password,
    };

    [Fact]
    public void BareButtonPressWithHostModeConfigHostsAndAutoLoadsOnce()
    {
        MenuBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        int loads = 0;
        string? lastPassword = null;
        CoopConfig config = new() { Host = true, Password = "swordfish" };
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, ProtocolConstants.DefaultPort,
            p => { lastPassword = p; return Settings(p); },
            () => config,
            () => loads++);

        Assert.False(manager.CanReconnect);

        dispatcher.OnConnectRequest(string.Empty);

        // mode=host in coop.ini: the press hosts, carries the file's password, and the
        // save auto-loads immediately (a host has nothing to wait for).
        Assert.NotNull(manager.Current);
        Assert.Equal("swordfish", lastPassword);
        Assert.Equal(1, loads);
        Assert.True(manager.CanReconnect);

        // A second bare press now means "reconnect" — and a reconnect must NOT re-fire
        // the auto-load.
        dispatcher.OnConnectRequest(string.Empty);
        Assert.NotNull(manager.Current);
        Assert.Equal(1, loads);

        manager.Disconnect();
    }

    [Fact]
    public void BareButtonPressMidWorldHostsButNeverLoadsASave()
    {
        MenuBridge bridge = new() { InWorld = true };
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        int loads = 0;
        CoopConfig config = new() { Host = true };
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, ProtocolConstants.DefaultPort, Settings, () => config, () => loads++);

        dispatcher.OnConnectRequest(string.Empty);

        // The session starts, but the game is already in a world — loading a save out
        // from under the player would be a disaster, so the trigger must hold its fire.
        Assert.NotNull(manager.Current);
        Assert.Equal(0, loads);

        manager.Disconnect();
    }

    [Fact]
    public void BareButtonPressWithNothingConfiguredRepliesAndStartsNothing()
    {
        MenuBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        int loads = 0;
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, ProtocolConstants.DefaultPort, Settings,
            () => null, // no coop.ini
            () => loads++);

        dispatcher.OnConnectRequest(string.Empty);

        // No crash, no session, no load — the player is told to pick a server instead.
        Assert.Equal(SessionPhase.Idle, manager.Phase);
        Assert.Null(manager.Current);
        Assert.False(manager.CanReconnect);
        Assert.Equal(0, loads);
    }

    [Fact]
    public async Task BareButtonPressWithConfiguredAddressJoinsAndLoadsOnConnectedExactlyOnce()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CancellationTokenSource hostCts = new(Timeout + TimeSpan.FromSeconds(25));
        Task hostTask = host.RunAsync(hostCts.Token);

        MenuBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        int loads = 0;
        CoopConfig config = new()
        {
            HasHostAddress = true,
            HostAddress = "127.0.0.1",
            Port = host.ListenEndPoint.Port,
        };
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, ProtocolConstants.DefaultPort, Settings,
            () => config,
            () => Interlocked.Increment(ref loads));

        // The press dials the configured host; the load waits for Connected.
        dispatcher.OnConnectRequest(string.Empty);
        Assert.Equal(0, loads);

        using (CancellationTokenSource dispatchCts = new())
        {
            Task dispatchTask = dispatcher.RunAsync(dispatchCts.Token);
            Assert.True(
                await WaitForAsync(() => Volatile.Read(ref loads) == 1, Timeout),
                "the menu-initiated join never fired the auto-load");
            Assert.Equal(SessionPhase.Connected, manager.Phase);
            await dispatchCts.CancelAsync();
            await dispatchTask;
        }

        // A bare press with a session behind it is a reconnect, which must not re-fire
        // the load even though the session passes through Connected again.
        dispatcher.OnConnectRequest(string.Empty);
        using (CancellationTokenSource dispatchCts = new())
        {
            Task dispatchTask = dispatcher.RunAsync(dispatchCts.Token);
            Assert.True(
                await WaitForAsync(() => manager.Phase == SessionPhase.Connected, Timeout),
                "the reconnect never reached Connected");
            await Task.Delay(300); // give a wrongful second fire the chance to happen
            Assert.Equal(1, Volatile.Read(ref loads));
            await dispatchCts.CancelAsync();
            await dispatchTask;
        }

        manager.Disconnect();
        await hostCts.CancelAsync();
        try
        {
            await hostTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task RefusedJoinNeverAutoLoads()
    {
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0, TickRate = 30, Password = "swordfish",
        });
        using CancellationTokenSource hostCts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(hostCts.Token);

        MenuBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        int loads = 0;
        CoopConfig config = new()
        {
            HasHostAddress = true,
            HostAddress = "127.0.0.1",
            Port = host.ListenEndPoint.Port,
            // No password: the join is rejected with E_PASSWORD.
        };
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, ProtocolConstants.DefaultPort, Settings,
            () => config,
            () => Interlocked.Increment(ref loads));

        dispatcher.OnConnectRequest(string.Empty);

        using CancellationTokenSource dispatchCts = new();
        Task dispatchTask = dispatcher.RunAsync(dispatchCts.Token);
        Assert.True(
            await WaitForAsync(() => manager.Phase == SessionPhase.Failed, Timeout),
            "the passworded host never refused the join");
        await Task.Delay(300); // a late fire would land here
        Assert.Equal(0, Volatile.Read(ref loads));

        await dispatchCts.CancelAsync();
        await dispatchTask;
        manager.Disconnect();
        await hostCts.CancelAsync();
        try
        {
            await hostTask;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
