using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The four player-count scaling settings on the wire (docs/research/player-count-scaling.md,
/// protocol version 53 / <see cref="BehaviourDigest.CurrentScheme"/> 2): they ride the
/// behaviour digest, compare as host-authoritative keys, survive the Hello/Welcome codec, and
/// a client adopts the host's value over real loopback the way <see
/// cref="BehaviourNegotiationTests"/> already proves for the boolean keys.
/// </summary>
public class PlayerCountScalingDigestTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static BehaviourDigest Digest(
        int enemyHealth = PlayerCountScalingRule.Auto, int enemyDamage = PlayerCountScalingRule.Auto,
        int xpRate = PlayerCountScalingRule.Auto, int storeStock = PlayerCountScalingRule.Auto) =>
        BehaviourDigest.Compute(
            localCombat: true, companionLocalCombat: false, partyFastPump: true, partySync: true,
            clientDoorRefusal: true, transitionAnnounce: false, clientLoadRejoin: true, hostLoadReadopt: true,
            effectSync: true, triggerRelay: true, clientEncounters: true, dialogueItemSync: true,
            dialogueRecruitSync: false, scriptItemSync: false, movieRelay: true, livenessSync: false,
            mapRevealSync: false, requireIdenticalSave: false, interpolationMs: 80, followLead: "any",
            enemyHealth: enemyHealth, enemyDamage: enemyDamage, xpRate: xpRate, storeStock: storeStock);

    [Fact]
    public void ComputeCarriesTheSettingsAndTheHashFoldsThem()
    {
        BehaviourDigest a = Digest(enemyHealth: 150, storeStock: 200);
        Assert.Equal(150, a.EnemyHealth);
        Assert.Equal(PlayerCountScalingRule.Auto, a.EnemyDamage);
        Assert.Equal(200, a.StoreStock);

        BehaviourDigest same = Digest(enemyHealth: 150, storeStock: 200);
        Assert.Equal(a, same);

        BehaviourDigest b = Digest(enemyHealth: 151, storeStock: 200);
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void FromFieldsNormalisesAnOutOfRangeWireValue()
    {
        BehaviourDigest d = BehaviourDigest.FromFields(0, 80, "any", enemyHealth: 9999, xpRate: -1);
        Assert.Equal(PlayerCountScalingRule.MaxPercent, d.EnemyHealth);
        Assert.Equal(PlayerCountScalingRule.MinPercent, d.XpRate);
        Assert.Equal(PlayerCountScalingRule.Auto, d.EnemyDamage);
    }

    [Fact]
    public void EveryScalingKeyComparesAsHostAuthoritativeWithReadableValues()
    {
        BehaviourComparisonResult result = BehaviourCompatibility.Compare(
            host: Digest(enemyHealth: PlayerCountScalingRule.Auto, enemyDamage: 120, xpRate: 100, storeStock: 250),
            peer: Digest(enemyHealth: 150, enemyDamage: 120, xpRate: 100, storeStock: PlayerCountScalingRule.Auto));

        Assert.Equal(2, result.Differences.Count);

        BehaviourDifference health = Assert.Single(result.Differences, d => d.Key == "enemy_health");
        Assert.True(health.HostAuthoritative);
        Assert.Equal("auto", health.HostValue);
        Assert.Equal("150", health.YourValue);
        Assert.Equal("enemy_health host=auto you=150", health.Text);

        BehaviourDifference stock = Assert.Single(result.Differences, d => d.Key == "store_stock");
        Assert.True(stock.HostAuthoritative);
        Assert.Equal("250", stock.HostValue);
        Assert.Equal("auto", stock.YourValue);
    }

    [Fact]
    public void EqualSettingsAddNoDifference()
    {
        Assert.True(BehaviourCompatibility.Compare(Digest(enemyHealth: 150), Digest(enemyHealth: 150)).IsEmpty);
    }

    [Fact]
    public void HelloAndWelcomeCarryTheFourSettings()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        BehaviourDigest behaviour = Digest(enemyHealth: 150, enemyDamage: 115, xpRate: 200, storeStock: 300);

        HelloPacket hello = new("Revan", "end_m01aa", 12345, ContentDigest.Unknown, 0xABCD, behaviour);
        int length = PacketCodec.Write(buffer, hello);
        HelloPacket? decodedHello = PacketCodec.ReadHello(buffer[..length]);

        Assert.NotNull(decodedHello);
        Assert.Equal(150, decodedHello.Behaviour.EnemyHealth);
        Assert.Equal(115, decodedHello.Behaviour.EnemyDamage);
        Assert.Equal(200, decodedHello.Behaviour.XpRate);
        Assert.Equal(300, decodedHello.Behaviour.StoreStock);
        Assert.Equal(behaviour.Hash, decodedHello.Behaviour.Hash);
        Assert.Equal(behaviour, decodedHello.Behaviour);
    }

    [Fact]
    public async Task AClientAdoptsTheHostsScalingSettingsOverLoopback()
    {
        // Host: enemy_health auto, store_stock 250. Client: enemy_health 150 (fixed), everything
        // else auto. Both differences are host-authoritative and both are adopted; the two
        // equal keys stay unadopted (null = "read the local value").
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0, Behaviour = Digest(storeStock: 250),
        });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", Behaviour = Digest(enemyHealth: 150) },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState somewhere = new(new Vector3f(10f, 20f, 0f), 1f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(somewhere);
            client.PublishLocalState(somewhere);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));
        Assert.True(
            await WaitForAsync(() => client.BehaviourWarnings is not null, Timeout, Publish),
            "the client never compared its behaviour digest against the host's Welcome");

        BehaviourComparisonResult result = client.BehaviourWarnings!;
        Assert.Equal(2, result.Differences.Count);
        Assert.Contains(result.Differences, d => d.Key == "enemy_health" && d.HostAuthoritative);
        Assert.Contains(result.Differences, d => d.Key == "store_stock" && d.HostAuthoritative);

        Assert.Equal(PlayerCountScalingRule.Auto, client.Overrides.EnemyHealth);
        Assert.Equal(250, client.Overrides.StoreStock);
        Assert.Null(client.Overrides.EnemyDamage);
        Assert.Null(client.Overrides.XpRate);

        // Fed through the resolved state the agent keeps: two players, host's auto -> 150%.
        PlayerCountScaling scaling = new(new PlayerCountScalingSettings(150, 100, 100, 100));
        scaling.Update(client.Overrides, 2);
        Assert.Equal(150, scaling.EnemyHealthPercent);
        Assert.Equal(250, scaling.StoreStockPercent);
        Assert.Equal(100, scaling.EnemyDamagePercent);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

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
}
