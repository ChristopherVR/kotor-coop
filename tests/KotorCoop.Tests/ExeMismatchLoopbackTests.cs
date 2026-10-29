using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Memory;
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

    private static (CoopHost Host, CoopClient Client, CancellationTokenSource Cts) Start(bool hostAllows, bool clientAllows,
        ContentDigest? hostDigest = null, ContentDigest? clientDigest = null)
    {
        CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0, TickRate = 30, Digest = hostDigest ?? Install(0xC6962619),
            AllowExeMismatch = hostAllows,
        });
        CoopClient client = new(
            new SessionSettings
            {
                PlayerName = "Bastila", TickRate = 30, Digest = clientDigest ?? Install(0x12345678),
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KmrpJoinUsesMatchingFilesWithoutEnablingExeMismatch(bool changedPatch)
    {
        string root = Path.Combine(Path.GetTempPath(), "kc-kmrp-join-" + Guid.NewGuid().ToString("N"));
        try
        {
            string a = KmrpInstallIdentityTests.CreateInstall(Path.Combine(root, "host"));
            string b = KmrpInstallIdentityTests.CreateInstall(Path.Combine(root, "client"));
            if (changedPatch) File.AppendAllText(Path.Combine(b, "patches/kmrp.dll"), "different version");
            var hostDigest = InstallDigest.Compute(a, Path.Combine(a, "swkotor.exe"), 1);
            var clientDigest = InstallDigest.Compute(b, Path.Combine(b, "swkotor.exe"), 2);
            var (host, client, cts) = Start(false, false, hostDigest, clientDigest);
            try
            {
                if (changedPatch)
                {
                    Assert.True(await WaitForAsync(() => client.GetStats().Status.Contains("E_EXE", StringComparison.Ordinal), Timeout));
                    Assert.False(client.IsConnected);
                    Assert.Empty(host.GetRemotePeers());
                }
                else
                {
                    Assert.True(await WaitForAsync(() => client.IsConnected && host.GetRemotePeers().Count == 1, Timeout));
                    Assert.Contains(client.Compatibility!.Notes, n => n.Tag == "W_KMRP");
                    Assert.Contains(host.Compatibility!.Notes, n => n.Tag == "W_KMRP");
                }
            }
            finally { cts.Cancel(); client.Dispose(); host.Dispose(); cts.Dispose(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
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
