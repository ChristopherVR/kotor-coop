using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// A real host and two real clients over loopback UDP, exercising the explicit arbitration
/// channel that closes the container-loot race (docs/roadmap-seamless.md Tier 1 item 4): two
/// clicks on the same chest within one RTT used to both proceed locally, because the ordinary
/// per-tick claim/lock pair (<see cref="InteractionLockStoreTests"/>) is eventually consistent
/// by design. <see cref="ICoopSession.RequestContainerLock"/>/<see cref="ICoopSession.TryGetLockResponse"/>
/// add a synchronous question on top of the SAME <c>InteractionLockStore.Claim</c> decision, so
/// a loser finds out before its own engine ever opens anything.
/// </summary>
public class ContainerLockArbitrationTests
{
    private const string Module = "end_m01aa";
    private const uint TargetId = 0x131;

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

    private static async Task SwallowCancel(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The headline scenario from the task brief: two clients request the SAME container
    /// within about 1ms of each other (sent back-to-back on this thread, with no delay between
    /// the two calls) and exactly one is granted. After the winner closes (releases through the
    /// ordinary per-tick claim, <c>Active: false</c>), the loser can be granted.
    /// </summary>
    [Fact]
    public async Task TwoClientsRequestingTheSameContainerWithinOneMillisecondExactlyOneIsGranted()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        IPEndPoint hostEndPoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);

        using CoopClient one = new(new SessionSettings { PlayerName = "Malak", TickRate = 30 }, hostEndPoint);
        using CoopClient two = new(new SessionSettings { PlayerName = "Bastila", TickRate = 30 }, hostEndPoint);

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(cts.Token);
        Task oneTask = one.RunAsync(cts.Token);
        Task twoTask = two.RunAsync(cts.Token);

        PlayerState state = new(new Vector3f(10.8f, 21.87f, 0f), 0f, Module, 100, 0);

        void Publish()
        {
            host.PublishLocalState(state);
            one.PublishLocalState(state);
            two.PublishLocalState(state);
        }

        Assert.True(
            await WaitForAsync(() => one.IsConnected && two.IsConnected, Timeout, Publish),
            "both clients must be admitted");

        // Back-to-back on this thread: as close to simultaneous as a real two-click race
        // within one RTT, and both name the SAME serial (1) — each client's own request-serial
        // numbering is independent, keyed by peer id on the wire, so there is no collision.
        const uint FirstRequestSerial = 1;
        one.RequestContainerLock(Module, TargetId, FirstRequestSerial);
        two.RequestContainerLock(Module, TargetId, FirstRequestSerial);

        bool? oneGranted = null;
        bool? twoGranted = null;

        Assert.True(
            await WaitForAsync(
                () =>
                {
                    if (oneGranted is null && one.TryGetLockResponse(FirstRequestSerial, out bool og))
                    {
                        oneGranted = og;
                    }

                    if (twoGranted is null && two.TryGetLockResponse(FirstRequestSerial, out bool tg))
                    {
                        twoGranted = tg;
                    }

                    return oneGranted is not null && twoGranted is not null;
                },
                Timeout,
                Publish),
            $"expected both clients to hear back; one={oneGranted}, two={twoGranted}");

        // Exactly one — never both (the race this test exists to close) and never neither
        // (arbitration must always resolve a request one way or the other).
        Assert.True(oneGranted!.Value ^ twoGranted!.Value, $"expected exactly one grant; one={oneGranted}, two={twoGranted}");

        (CoopClient winner, CoopClient loser) = oneGranted.Value ? (one, two) : (two, one);

        // The winner closes: the ordinary per-tick claim carries the release (Active: false),
        // exactly what a real close does — see InteractionLockController.PollLocalContainerUse.
        winner.PublishInteractionClaim(Module, string.Empty, InteractionKind.Container, active: false, TargetId);

        // The loser retries with a fresh serial each poll — the same behaviour the plugin's own
        // ContainerLockArbiter drives on a fresh click — until the release has actually landed
        // and a request is finally granted.
        uint loserSerial = FirstRequestSerial;
        bool loserEventuallyGranted = false;

        Assert.True(
            await WaitForAsync(
                () =>
                {
                    if (loser.TryGetLockResponse(loserSerial, out bool granted) && granted)
                    {
                        loserEventuallyGranted = true;
                        return true;
                    }

                    return false;
                },
                Timeout,
                () =>
                {
                    Publish();
                    loserSerial++;
                    loser.RequestContainerLock(Module, TargetId, loserSerial);
                }),
            "the loser must be grantable once the winner releases");

        Assert.True(loserEventuallyGranted);

        await cts.CancelAsync();
        await Task.WhenAll(SwallowCancel(hostTask), SwallowCancel(oneTask), SwallowCancel(twoTask));
    }

    /// <summary>
    /// The host's own local clicks go through the identical in-process arbiter — no network,
    /// no round trip — so a request the host makes of ITSELF resolves synchronously, on the
    /// same call. This is what "the host's own clicks go through the same arbiter in-process"
    /// means concretely: <see cref="ICoopSession.TryGetLockResponse"/> already has the answer
    /// the instant <see cref="ICoopSession.RequestContainerLock"/> returns.
    /// </summary>
    [Fact]
    public void TheHostsOwnRequestResolvesSynchronouslyInProcess()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });

        host.RequestContainerLock(Module, TargetId, requestSerial: 7);

        Assert.True(host.TryGetLockResponse(7, out bool granted));
        Assert.True(granted, "the first request for an unheld container must be granted");
    }

    /// <summary>A second host-local request for the SAME container while the first hold is
    /// still fresh SUCCEEDS — re-entrancy (<c>ContainerUseRule.MayClaim</c>'s second rule): the
    /// host re-clicking a container it already holds must never be refused by its own lock.
    /// This pins that behaviour for the host-local, in-process path specifically (the pure
    /// version, independent of any session, already lives in
    /// <c>InteractionLockStoreTests</c>/<c>ContainerUseRuleTests</c>).</summary>
    [Fact]
    public void AHostLocalReRequestForItsOwnHeldContainerIsIdempotent()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });

        host.RequestContainerLock(Module, TargetId, requestSerial: 1);
        Assert.True(host.TryGetLockResponse(1, out bool firstGranted) && firstGranted);

        host.RequestContainerLock(Module, TargetId, requestSerial: 2);
        Assert.True(host.TryGetLockResponse(2, out bool secondGranted) && secondGranted);
    }

    /// <summary>
    /// A CLIENT'S request for a container the HOST already holds (via its own in-process
    /// request) is denied — the host's local clicks are not a silent exception to the arbiter
    /// just because they never touch the network.
    /// </summary>
    [Fact]
    public async Task AClientIsDeniedAContainerTheHostAlreadyHoldsLocally()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        IPEndPoint hostEndPoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);
        using CoopClient client = new(new SessionSettings { PlayerName = "Malak", TickRate = 30 }, hostEndPoint);

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(new Vector3f(10.8f, 21.87f, 0f), 0f, Module, 100, 0);

        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        host.RequestContainerLock(Module, TargetId, requestSerial: 1);
        Assert.True(host.TryGetLockResponse(1, out bool hostGranted) && hostGranted);

        uint clientSerial = 1;
        bool? clientGranted = null;

        Assert.True(
            await WaitForAsync(
                () =>
                {
                    if (client.TryGetLockResponse(clientSerial, out bool granted))
                    {
                        clientGranted = granted;
                        return true;
                    }

                    return false;
                },
                Timeout,
                () =>
                {
                    Publish();
                    if (clientGranted is null)
                    {
                        client.RequestContainerLock(Module, TargetId, clientSerial);
                    }
                }),
            "the client must hear back");

        Assert.False(clientGranted, "the host already holds this container; the client must be denied");

        await cts.CancelAsync();
        await Task.WhenAll(SwallowCancel(hostTask), SwallowCancel(clientTask));
    }
}
