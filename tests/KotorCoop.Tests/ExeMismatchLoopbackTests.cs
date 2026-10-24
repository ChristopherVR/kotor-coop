using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// coop.ini <c>exe_mismatch</c> over a real loopback socket: two installs whose running
/// executables differ (a widescreen exe patch on one side), refused by default and admitted
/// with a <c>W_EXE</c> warning on BOTH ends when the host allows it. The client half matters:
/// it re-judges compatibility from the host's Welcome and must not refuse what the host let in.
/// </summary>
[Trait("Category", "Slow")]
public class ExeMismatchLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static ContentDigest Install(uint exe) =>
        new((ushort)ContentDigest.Field.Exe, exe, 0, 0, 0, 0, 0, 0, 0, 0, 0);

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

    private static (CoopHost Host, CoopClient Client, CancellationTokenSource Cts) Start(bool hostAllows, bool clientAllows)
    {
        CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0, TickRate = 30, Digest = Install(0xC6962619),
            AllowExeMismatch = hostAllows,
        });
        CoopClient client = new(
            new SessionSettings
            {
                PlayerName = "Bastila", TickRate = 30, Digest = Install(0x12345678),
                AllowExeMismatch = clientAllows,
            },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));
        CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        _ = host.RunAsync(cts.Token);
        _ = client.RunAsync(cts.Token);
        return (host, client, cts);
    }

    [Fact]
    public async Task ADifferentExecutableIsRefusedByDefaultEvenIfTheJoinerAllowsIt()
    {
        (CoopHost host, CoopClient client, CancellationTokenSource cts) = Start(hostAllows: false, clientAllows: true);
        try
        {
            Assert.True(
                await WaitForAsync(() => client.GetStats().Status.Contains("E_EXE", StringComparison.Ordinal), Timeout),
                $"the joiner must be told E_EXE (status: {client.GetStats().Status})");
            Assert.False(client.IsConnected);
            Assert.Empty(host.GetRemotePeers());
        }
        finally
        {
            cts.Cancel();
            client.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public async Task AHostThatAllowsItAdmitsTheJoinerWithAWarningOnBothSides()
    {
        (CoopHost host, CoopClient client, CancellationTokenSource cts) = Start(hostAllows: true, clientAllows: false);
        try
        {
            Assert.True(
                await WaitForAsync(() => client.IsConnected && host.GetRemotePeers().Count == 1, Timeout),
                $"the joiner must be admitted (status: {client.GetStats().Status})");

            Assert.True(client.Compatibility?.Admitted);
            Assert.Contains(client.Compatibility!.Notes, n => n.Tag == "W_EXE");
            Assert.True(host.Compatibility?.Admitted);
            Assert.Contains(host.Compatibility!.Notes, n => n.Tag == "W_EXE");
        }
        finally
        {
            cts.Cancel();
            client.Dispose();
            host.Dispose();
        }
    }
}
