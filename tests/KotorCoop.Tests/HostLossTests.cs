using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// End-to-end host-loss behaviour over real sockets: a real host, a real client, and either a
/// real link failure (the transport fault injector) or a real clean shutdown — see
/// <see cref="DisconnectRecoveryTests"/>'s own remarks for why "real link failure" and "killed
/// process" are different bugs. This file is specifically about the NEW <c>HostLost</c> signal
/// (<see cref="ICoopSession.HostLost"/>) both triggers feed, not about the pre-existing
/// reconnect machinery <see cref="DisconnectRecoveryTests"/> already covers.
///
/// Also covers the host's own side of "a client vanishes mid-dialogue/mid-lock" (task 2 of
/// docs/research/disconnect-reconnect.md's host-loss section) — <see cref="CoopHost.Tick"/>'s
/// timeout sweep and <see cref="CoopHost.HandleBye"/> already both call
/// <c>ReleaseDialogueIfOwnedBy</c> and <c>InteractionLockStore.Forget</c>; these tests are the
/// integration-level proof that combination genuinely releases both together, over the wire,
/// rather than only in the two separate unit tests that already existed for each store alone.
/// </summary>
[Trait("Category", "Slow")]
public class HostLossTests
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

    private static PlayerState Somewhere => new(new Vector3f(10f, 20f, 0f), 1f, "end_m01aa", 100, 0);

    /// <summary>
    /// The headline client-side case: a client holding a dialogue token and an interaction lock
    /// — the two locks this file's own doc comment names as the stand-ins for "everything the
    /// plugin's ReleaseSession list would tear down" — loses its host to silence. HostLost must
    /// latch in the SAME tick IsConnected does (host_loss_timeout_ms defaults to PeerTimeoutMs),
    /// and clear again the moment the host comes back — this is exactly the edge
    /// <c>CoopAgent.PumpOnce</c> reacts to.
    /// </summary>
    [Fact]
    public async Task WhenTheHostGoesSilentTheClientDeclaresHostLostAndRecoversOnRejoin()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
            client.PublishDialogue(true, "Malak", "You have failed me.", partnerTag: "trask");
            client.PublishInteractionClaim("end_m01aa", "trask", InteractionKind.Conversation, active: true);

            // Mirrors what CoopAgent.PumpOnce's PumpInteractionLocks does every tick on both
            // ends: even an idle claim is sent so the host learns which module the locks it
            // broadcasts are scoped to (WorldStatePump.PumpInteractionLocks's own remarks).
            // Without this the host's own _interactionModule never advances past empty, and its
            // broadcast (scoped to _interactionModule) never includes anything a client claimed
            // for a real module.
            host.PublishInteractionClaim("end_m01aa", string.Empty, InteractionKind.Conversation, active: false);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));
        Assert.False(client.HostLost);

        Assert.True(
            await WaitForAsync(() => client.GetDialogue().IsOwnedBy(client.LocalPeerId), Timeout, Publish),
            "the client never got the token it claimed");
        Assert.True(
            await WaitForAsync(() => client.GetInteractionLocks().Any(l => l.Tag == "trask"), Timeout, Publish),
            "the client's own lock never came back on the broadcast");

        client.Network.StartBlackout(10_000);

        Assert.True(
            await WaitForAsync(() => !client.IsConnected && client.HostLost, TimeSpan.FromSeconds(10), Publish),
            "HostLost never latched when the client gave up on the link");

        // The reconnect loop keeps running regardless — nothing about it is gated on HostLost.
        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish), "the client never rejoined");
        Assert.True(
            await WaitForAsync(() => !client.HostLost, TimeSpan.FromSeconds(5), Publish),
            "HostLost never cleared after the host came back");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// A clean host shutdown (not a crash) must be noticed far faster than the silence timeout —
    /// that speed is the entire reason <see cref="PacketType.HostBye"/> exists. Two independent
    /// cancellation tokens so the host can be torn down on its own while the client keeps
    /// running to observe the effect.
    /// </summary>
    [Fact]
    public async Task AnExplicitHostByeDeclaresHostLossFarFasterThanTheSilenceTimeout()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource hostCts = new(TimeSpan.FromSeconds(60));
        using CancellationTokenSource clientCts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(hostCts.Token);
        Task clientTask = client.RunAsync(clientCts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        DateTime shutdownAt = DateTime.UtcNow;
        await hostCts.CancelAsync();
        await hostTask; // returns once BroadcastHostBye has already been sent

        Assert.True(
            await WaitForAsync(() => client.HostLost, TimeSpan.FromSeconds(3), Publish),
            "the client never reacted to the host's clean shutdown");

        TimeSpan elapsed = DateTime.UtcNow - shutdownAt;
        Assert.True(
            elapsed < TimeSpan.FromSeconds(3),
            $"HostBye should be near-instant, took {elapsed.TotalMilliseconds}ms "
            + $"(silence timeout is {ProtocolConstants.PeerTimeoutMs}ms)");

        // The reconnect loop starts immediately too, regardless of host_loss_release.
        Assert.False(client.IsConnected);

        await clientCts.CancelAsync();
        await clientTask;
    }

    /// <summary>
    /// <c>host_loss_release = 0</c> must restore the exact pre-existing behaviour: the client
    /// still notices the link is down and still keeps re-Helloing (nothing about reconnecting
    /// itself is gated on the flag), it simply never raises <see cref="ICoopSession.HostLost"/>
    /// — so <c>CoopAgent.PumpOnce</c> never runs the extra release pass and every lock stays
    /// held until a manual disconnect, same as before this feature existed.
    /// </summary>
    [Fact]
    public async Task DisablingHostLossReleaseNeverDeclaresHostLostEvenPastTheTimeout()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings
            {
                PlayerName = "Malak", TickRate = 30, HostLossReleaseEnabled = false,
            },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        client.Network.StartBlackout(10_000);

        Assert.True(
            await WaitForAsync(() => !client.IsConnected, TimeSpan.FromSeconds(10), Publish),
            "the client never noticed the link had gone");

        // Give HostLost every chance to wrongly latch.
        for (int i = 0; i < 40; i++)
        {
            Publish();
            await Task.Delay(25);
        }

        Assert.False(client.HostLost);

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish), "the client never rejoined");
        Assert.False(client.HostLost);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// Task 2: the host's own side. A client holding both the conversation token AND an
    /// interaction lock vanishes (a real blackout, so this is the timeout sweep in
    /// <see cref="CoopHost.Tick"/>, not <see cref="CoopHost.HandleBye"/>) — both must release,
    /// together, not just whichever one an isolated unit test happened to cover.
    /// </summary>
    [Fact]
    public async Task AClientThatVanishesMidDialogueAndMidLockReleasesBothOnTheHost()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
            client.PublishDialogue(true, "Malak", "...", partnerTag: "trask");
            client.PublishInteractionClaim("end_m01aa", "trask", InteractionKind.Conversation, active: true);

            // Mirrors what CoopAgent.PumpOnce's PumpInteractionLocks does every tick on both
            // ends: even an idle claim is sent so the host learns which module the locks it
            // broadcasts are scoped to (WorldStatePump.PumpInteractionLocks's own remarks).
            // Without this the host's own _interactionModule never advances past empty, and its
            // broadcast (scoped to _interactionModule) never includes anything a client claimed
            // for a real module.
            host.PublishInteractionClaim("end_m01aa", string.Empty, InteractionKind.Conversation, active: false);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));
        uint clientId = client.LocalPeerId;

        Assert.True(
            await WaitForAsync(() => host.GetDialogue().IsOwnedBy(clientId), Timeout, Publish),
            "the host never granted the token");
        Assert.True(
            await WaitForAsync(() => host.GetInteractionLocks().Any(l => l.PeerId == clientId), Timeout, Publish),
            "the host never recorded the lock");

        // Silence in both directions - the host must expire the peer on its own timeout sweep,
        // not because the client asked to leave.
        client.Network.StartBlackout(10_000);

        Assert.True(
            await WaitForAsync(() => !host.GetDialogue().IsOwnedBy(clientId), TimeSpan.FromSeconds(10), Publish),
            "the host kept the vanished client's dialogue token");
        Assert.True(
            await WaitForAsync(
                () => !host.GetInteractionLocks().Any(l => l.PeerId == clientId), TimeSpan.FromSeconds(10), Publish),
            "the host kept the vanished client's interaction lock");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>Same combination, but through <see cref="CoopHost.HandleBye"/> instead of the
    /// timeout sweep — a player who quits cleanly must not have to wait out any timeout at
    /// all for the NPC/token they were holding to free up for the host or a third player.</summary>
    [Fact]
    public async Task AClientThatSaysByeMidDialogueAndMidLockReleasesBothOnTheHostImmediately()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource hostCts = new(TimeSpan.FromSeconds(60));
        using CancellationTokenSource clientCts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(hostCts.Token);
        Task clientTask = client.RunAsync(clientCts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
            client.PublishDialogue(true, "Malak", "...", partnerTag: "trask");
            client.PublishInteractionClaim("end_m01aa", "trask", InteractionKind.Conversation, active: true);

            // Mirrors what CoopAgent.PumpOnce's PumpInteractionLocks does every tick on both
            // ends: even an idle claim is sent so the host learns which module the locks it
            // broadcasts are scoped to (WorldStatePump.PumpInteractionLocks's own remarks).
            // Without this the host's own _interactionModule never advances past empty, and its
            // broadcast (scoped to _interactionModule) never includes anything a client claimed
            // for a real module.
            host.PublishInteractionClaim("end_m01aa", string.Empty, InteractionKind.Conversation, active: false);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));
        uint clientId = client.LocalPeerId;

        Assert.True(await WaitForAsync(() => host.GetDialogue().IsOwnedBy(clientId), Timeout, Publish));
        Assert.True(
            await WaitForAsync(() => host.GetInteractionLocks().Any(l => l.PeerId == clientId), Timeout, Publish));

        // Client leaves cleanly (RunAsync sends Bye during its own shutdown, exactly like a
        // player-initiated disconnect).
        await clientCts.CancelAsync();
        await clientTask;

        Assert.True(
            await WaitForAsync(() => !host.GetDialogue().IsOwnedBy(clientId), TimeSpan.FromSeconds(3), Publish),
            "the host kept the departed client's dialogue token past its Bye");
        Assert.True(
            await WaitForAsync(
                () => !host.GetInteractionLocks().Any(l => l.PeerId == clientId), TimeSpan.FromSeconds(3), Publish),
            "the host kept the departed client's interaction lock past its Bye");

        await hostCts.CancelAsync();
        await hostTask;
    }
}
