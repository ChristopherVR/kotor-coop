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
}
