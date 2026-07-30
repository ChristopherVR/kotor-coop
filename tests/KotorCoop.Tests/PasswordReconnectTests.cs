using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Password admission and reconnection over real loopback UDP sessions.
/// </summary>
public class PasswordReconnectTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout, Action? each = null)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            each?.Invoke();
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    [Fact]
    public void PasswordHashIsStableAndOpenSessionsMatchAnything()
    {
        Assert.Equal(SessionPassword.None, SessionPassword.Hash(""));
        Assert.Equal(SessionPassword.None, SessionPassword.Hash(null));
        Assert.Equal(SessionPassword.Hash("hunter2"), SessionPassword.Hash("hunter2"));
        Assert.NotEqual(SessionPassword.Hash("hunter2"), SessionPassword.Hash("Hunter2"));
        Assert.NotEqual(SessionPassword.None, SessionPassword.Hash("hunter2"));
    }

    [Fact]
    public async Task RightPasswordJoinsWrongPasswordIsRejected()
    {
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0, TickRate = 30, Password = "swordfish",
        });

        using CoopClient good = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30, Password = "swordfish" },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CoopClient bad = new(
            new SessionSettings { PlayerName = "Bandon", TickRate = 30, Password = "guess" },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task goodTask = good.RunAsync(cts.Token);
        Task badTask = bad.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            good.PublishLocalState(state);
            bad.PublishLocalState(state);
        }

        Assert.True(await WaitForAsync(() => good.IsConnected, Timeout, Publish),
            "the client with the right password never connected");

        // The wrong-password client must be told why, and must not be connected.
        Assert.True(await WaitForAsync(
            () => bad.GetStats().Status.Contains("password", StringComparison.OrdinalIgnoreCase),
            Timeout,
            Publish));
        Assert.False(bad.IsConnected);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, goodTask, badTask);
    }

    [Fact]
    public async Task AClientReclaimsItsSlotWhenTheLinkReturns()
    {
        // A host that stops replying (crash / drop) then returns: the client should
        // reconnect on its own and land back in the SAME peer slot (matched by nonce),
        // not appear as a second player.
        int port;
        using (CoopHost firstHost = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 }))
        {
            port = firstHost.ListenEndPoint.Port;
        }

        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30, PeerTimeoutMs = 500 },
            new IPEndPoint(IPAddress.Loopback, port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(15));
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);

        // Host #1 on the fixed port.
        using (CoopHost host1 = new(new SessionSettings { PlayerName = "Revan", Port = port, TickRate = 30 }))
        {
            using CancellationTokenSource host1Cts = new();
            Task host1Task = host1.RunAsync(host1Cts.Token);

            Assert.True(await WaitForAsync(
                () => client.IsConnected,
                Timeout,
                () => { host1.PublishLocalState(state); client.PublishLocalState(state); }));
            uint firstId = client.LocalPeerId;

            await host1Cts.CancelAsync();
            await host1Task;

            // The client must notice the silence and drop back to reconnecting.
            Assert.True(await WaitForAsync(
                () => !client.IsConnected,
                Timeout,
                () => client.PublishLocalState(state)),
                "client did not detect the lost host");
            Assert.True(firstId != 0);
        }

        // Host #2 comes up on the same port. The client — same nonce — must rejoin.
        using CoopHost host2 = new(new SessionSettings { PlayerName = "Revan", Port = port, TickRate = 30 });
        using CancellationTokenSource host2Cts = new();
        Task host2Task = host2.RunAsync(host2Cts.Token);

        Assert.True(await WaitForAsync(
            () => client.IsConnected,
            Timeout,
            () => { host2.PublishLocalState(state); client.PublishLocalState(state); }),
            "client did not reconnect to the returned host");

        await host2Cts.CancelAsync();
        await host2Task;
        await cts.CancelAsync();
        await clientTask;
    }
}
