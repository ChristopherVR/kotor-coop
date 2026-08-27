using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="WelcomePacket.TickRate"/> used to be written by <see cref="CoopHost"/> and read
/// by nobody: <see cref="CoopClient.RunAsync"/> built its tick timer from the local
/// <see cref="SessionSettings.TickRate"/> and never looked at the host's announcement, so a
/// host running at a different rate had no effect on a joining client at all.
/// </summary>
public class WelcomeTickRateTests
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

    private static PlayerState Somewhere => new(new Vector3f(10f, 20f, 0f), 1f, "end_m01aa", 100, 0);

    [Fact]
    public async Task TheClientAdoptsTheHostsAnnouncedTickRate()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 20 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        // Before the handshake, the client is still running at its own local setting.
        Assert.Equal(30, client.EffectiveTickRate);

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // Once Welcome lands, the host's 20 Hz wins over the client's own 30 Hz setting.
        Assert.True(
            await WaitForAsync(() => client.EffectiveTickRate == 20, Timeout, Publish),
            $"the client never adopted the host's tick rate; effective was {client.EffectiveTickRate}");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task MatchingTickRatesNeedNoAdoption()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));
        Assert.Equal(30, client.EffectiveTickRate);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }
}
