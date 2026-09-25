using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// End-to-end (real loopback UDP, real Hello/Welcome) coverage for the behaviour-flag
/// negotiation — docs/roadmap-seamless.md Tier 1 item 7, docs/research/mod-compatibility.md's
/// "Behaviour-flag negotiation" section. Mirrors <see cref="WelcomeTickRateTests"/>'s shape,
/// which already proved this exact pattern (host announces something in Welcome, client
/// adopts it) for the tick rate.
/// </summary>
public class BehaviourNegotiationTests
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

    private static BehaviourDigest Digest(
        bool effectSync = true, bool localCombat = true, string followLead = "any",
        bool inventoryPersonal = true, bool creditsPersonal = true) =>
        BehaviourDigest.Compute(
            localCombat: localCombat, companionLocalCombat: false, partyFastPump: true, partySync: true,
            clientDoorRefusal: true, transitionAnnounce: false, clientLoadRejoin: true, hostLoadReadopt: true,
            effectSync: effectSync, triggerRelay: true, clientEncounters: true, dialogueItemSync: true,
            dialogueRecruitSync: false, scriptItemSync: false, movieRelay: true, livenessSync: false,
            mapRevealSync: false, requireIdenticalSave: false, interpolationMs: 80, followLead: followLead,
            inventoryPersonal: inventoryPersonal, creditsPersonal: creditsPersonal);

    [Fact]
    public async Task ClientWarnsOnBothDifferencesAndAdoptsOnlyTheHostAuthoritativeOne()
    {
        // Two differences: effect_sync (host-authoritative -> adopted) and local_combat (not
        // host-authoritative -> warned only, client keeps running its own value).
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0, Behaviour = Digest(effectSync: true, localCombat: true),
        });
        using CoopClient client = new(
            new SessionSettings
            {
                PlayerName = "Malak", Behaviour = Digest(effectSync: false, localCombat: false),
            },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        Assert.Null(client.BehaviourWarnings);
        Assert.Null(client.Overrides.EffectSync);

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));
        Assert.True(
            await WaitForAsync(() => client.BehaviourWarnings is not null, Timeout, Publish),
            "the client never compared its behaviour digest against the host's Welcome");

        BehaviourComparisonResult result = client.BehaviourWarnings!;
        Assert.Equal(2, result.Differences.Count);
        Assert.Contains(result.Differences, d => d.Key == "effect_sync" && d.HostAuthoritative);
        Assert.Contains(result.Differences, d => d.Key == "local_combat" && !d.HostAuthoritative);

        // The host-authoritative key is adopted...
        Assert.True(client.Overrides.EffectSync);

        // ...and only that one - local_combat has no override slot at all (it is warn-only:
        // no channel in KotorCoop.Session is gated on it), so nothing here claims to adopt it.
        Assert.Null(client.Overrides.PartySync);
        Assert.Null(client.Overrides.MovieRelay);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task IdenticalBehaviourProducesNoWarningsAndNoOverrides()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, Behaviour = Digest() });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", Behaviour = Digest() },
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
        Assert.True(
            await WaitForAsync(() => client.BehaviourWarnings is not null, Timeout, Publish),
            "the client never compared its behaviour digest against the host's Welcome");

        Assert.True(client.BehaviourWarnings!.IsEmpty);
        Assert.Equal(SessionOverrides.None, client.Overrides);

        // The host's own comparison (against the client's Hello) agrees too.
        Assert.True(
            await WaitForAsync(() => host.BehaviourWarnings is { IsEmpty: true }, Timeout, Publish),
            "the host never compared its behaviour digest against the client's Hello");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task HostNeverAdoptsAnythingFromAPeer()
    {
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0, Behaviour = Digest(effectSync: true),
        });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", Behaviour = Digest(effectSync: false) },
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
        Assert.True(
            await WaitForAsync(() => host.BehaviourWarnings is { IsEmpty: false }, Timeout, Publish));

        // A host is authoritative by definition: it always reports SessionOverrides.None,
        // regardless of what its peers' Hellos disagree on. Overrides is a default interface
        // member (CoopHost never overrides it), so it is only reachable through the interface.
        Assert.Equal(SessionOverrides.None, ((ICoopSession)host).Overrides);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>docs/research/personal-inventory.md's negotiation: inventory_mode/credits_mode
    /// are host-authoritative, exactly like enemy_health/effect_sync — a client running a
    /// different mode than the host adopts the host's at runtime.</summary>
    [Fact]
    public async Task ClientAdoptsTheHostsInventoryAndCreditsMode()
    {
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0,
            Behaviour = Digest(inventoryPersonal: true, creditsPersonal: true),
        });
        using CoopClient client = new(
            new SessionSettings
            {
                PlayerName = "Malak",
                Behaviour = Digest(inventoryPersonal: false, creditsPersonal: false),
            },
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
        Assert.True(
            await WaitForAsync(() => client.BehaviourWarnings is not null, Timeout, Publish));

        Assert.Contains(client.BehaviourWarnings!.Differences,
            d => d.Key == "inventory_mode" && d.HostAuthoritative);
        Assert.Contains(client.BehaviourWarnings!.Differences,
            d => d.Key == "credits_mode" && d.HostAuthoritative);
        Assert.True(client.Overrides.InventoryPersonal);
        Assert.True(client.Overrides.CreditsPersonal);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }
}
