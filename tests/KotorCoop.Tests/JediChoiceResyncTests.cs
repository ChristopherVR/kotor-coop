using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// docs/research/class-and-power-grants.md's 2026-09-25 "requirement 3": a player who joins
/// LATE (after the Jedi choice already happened AND the announcing packet already fully
/// retired, because every peer present at the time acked it) must still be checked against
/// their own character the next time they are in the world. <c>CoopHost.ResyncJediChoice</c> is
/// the fix, called from <c>HandleHello</c> on every (re)admission — the same shape
/// <see cref="MaterialisedLootResyncTests"/> already proves for materialised loot.
///
/// This only exercises the SESSION-layer delivery (a late joiner receives a fresh, unacked
/// <see cref="ClassGrantPacket"/>). Whether that joiner's OWN character then needs the
/// class-choice popup is <c>JediChoiceRule.Decide</c> (see <c>ClassGrantRelayTests</c>) and the
/// plugin's own <c>JediChoicePromptController</c>, neither reachable from a socket-only test.
/// </summary>
public class JediChoiceResyncTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

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

    private static PlayerState Somewhere => new(new Vector3f(10f, 20f, 0f), 1f, "danm13", 100, 0);

    [Fact]
    public async Task ALateJoinerReceivesAJediChoiceThatAlreadyFullyRetired()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient clientA = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task aTask = clientA.RunAsync(cts.Token);

        void PublishA()
        {
            host.PublishLocalState(Somewhere);
            clientA.PublishLocalState(Somewhere);
            clientA.DrainClassGrants(); // HandleClassGrant already acks on receipt
        }

        Assert.True(await WaitForAsync(() => clientA.IsConnected, Timeout, PublishA));

        // The host's own character made the Jedi choice.
        host.PublishClassGrant(3); // Guardian - the id no longer forces anything on the receiver

        Assert.True(
            await WaitForAsync(() => host.ClassGrantPendingCount == 0, Timeout, PublishA),
            "the Jedi-choice op never fully retired against the one peer already present");

        // B joins AFTER the op above already retired — the exact gap the resync exists to close.
        using CoopClient clientB = new(
            new SessionSettings { PlayerName = "Bastila", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));
        Task bTask = clientB.RunAsync(cts.Token);

        List<ClassGrantPacket> receivedByB = [];

        void PublishBoth()
        {
            PublishA();
            clientB.PublishLocalState(Somewhere);
            receivedByB.AddRange(clientB.DrainClassGrants());
        }

        Assert.True(await WaitForAsync(() => clientB.IsConnected, Timeout, PublishBoth));

        Assert.True(
            await WaitForAsync(() => receivedByB.Count > 0, Timeout, PublishBoth),
            "the late joiner never received the resynced Jedi-choice packet");

        Assert.Equal(CoopHost.HostPeerId, receivedByB[0].OriginPeerId);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, aTask, bTask);
    }

    /// <summary>No choice has happened yet: a fresh join must not manufacture one out of thin
    /// air (the resync is conditional on <c>_jediChoiceEverHappened</c>).</summary>
    [Fact]
    public async Task AJoinerBeforeAnyJediChoiceReceivesNothing()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        List<ClassGrantPacket> received = [];

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
            received.AddRange(client.DrainClassGrants());
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        for (int i = 0; i < 20; i++)
        {
            Publish();
            await Task.Delay(25);
        }

        Assert.Empty(received);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }
}
