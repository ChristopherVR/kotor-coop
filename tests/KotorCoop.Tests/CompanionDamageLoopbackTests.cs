using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// A real host and client over loopback UDP, exercising the wire half of the companion-damage
/// channel (docs/research/companion-state-sync.md §13): a client's report of what its own
/// engine actually did to a <c>companion_local_combat</c>-released companion reaches the host's
/// <see cref="ICoopSession.GetCompanionDamage"/>, which is what
/// <c>Pumps.CombatPump.PumpCompanionDamage</c> banks into a <see cref="CompanionDamageLedger"/>
/// and spends as real damage. See <see cref="CompanionDamageLedgerTests"/> for the pure
/// arithmetic this wire hop feeds.
/// </summary>
public class CompanionDamageLoopbackTests
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
    public async Task AClientsCompanionDamageReportReachesTheHost()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        IPEndPoint hostEndPoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);

        using CoopClient client = new(new SessionSettings { PlayerName = "Bastila", TickRate = 30 }, hostEndPoint);

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(new Vector3f(10f, 20f, 0f), 0f, "end_m01aa", 100, 0);

        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
            WorldDigest world = new(1, 1, state.Module, 1, NativeWorldEpoch: 1);
            host.PublishWorldDigest(world);
            client.PublishWorldDigest(world);
            host.PublishPartyRoster(new PartyRoster(1, 1, HostNativeEpoch: 1));
        }

        Assert.True(await WaitForAsync(() => client.IsConnected && client.RemoteWorldDigest.NativeWorldEpoch == 1, Timeout, Publish), "client must be admitted");

        // The companion released to slot 0 (roster slot 1) has taken 12, then 27 — a rising
        // cumulative total, exactly the shape PumpCompanionDamage reports every tick.
        client.ReportCompanionDamage(1, [new CompanionDamageEntry(1, 12)]);

        Assert.True(
            await WaitForAsync(
                () => host.GetCompanionDamage().Any(d => d.RosterSlotPlusOne == 1 && d.CumulativeDamage == 12),
                Timeout,
                Publish),
            "the host must see the client's first report");

        client.ReportCompanionDamage(1, [new CompanionDamageEntry(1, 27)]);

        Assert.True(
            await WaitForAsync(
                () => host.GetCompanionDamage().Any(d => d.RosterSlotPlusOne == 1 && d.CumulativeDamage == 27),
                Timeout,
                Publish),
            "a rising report must supersede the last one");
    }

    [Fact]
    public async Task TheHostNeverReportsToItself()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });

        // A no-op — the host's own companions are already authoritative, so there is nothing
        // to forward to itself. See CoopHost.CompanionDamage.cs's own doc comment.
        host.ReportCompanionDamage(1, [new CompanionDamageEntry(1, 99)]);

        Assert.Empty(host.GetCompanionDamage());
    }

    [Fact]
    public async Task AnExpiredCompanionReportIsRemovedFromTheHostRetentionMap()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        IPEndPoint hostEndPoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);

        using CoopClient client = new(new SessionSettings { PlayerName = "Bastila", TickRate = 30 }, hostEndPoint);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(new Vector3f(10f, 20f, 0f), 0f, "end_m01aa", 100, 0);

        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
            WorldDigest world = new(1, 1, state.Module, 1, NativeWorldEpoch: 1);
            host.PublishWorldDigest(world);
            client.PublishWorldDigest(world);
            host.PublishPartyRoster(new PartyRoster(1, 1, HostNativeEpoch: 1));
        }

        Assert.True(await WaitForAsync(() => client.IsConnected && client.RemoteWorldDigest.NativeWorldEpoch == 1, Timeout, Publish), "client must be admitted");

        client.ReportCompanionDamage(1, [new CompanionDamageEntry(1, 12)]);
        Assert.True(
            await WaitForAsync(() => host.CompanionDamageReportCount == 1, Timeout, Publish),
            "the host must retain a fresh companion report");

        // The report is intentionally not refreshed. GetCompanionDamage is the host's normal
        // read path, so it should both return no stale rows and reclaim the transport snapshot.
        await Task.Delay(CompanionDamageFreshnessWindow().Add(TimeSpan.FromMilliseconds(100)));
        Assert.Empty(host.GetCompanionDamage());
        Assert.Equal(0, host.CompanionDamageReportCount);
    }

    private static TimeSpan CompanionDamageFreshnessWindow() => TimeSpan.FromSeconds(2);
}
