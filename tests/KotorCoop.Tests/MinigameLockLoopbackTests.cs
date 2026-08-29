using System.Net;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Real host, real client, real sockets, real <see cref="CoopAgent"/> pump on each side — the
/// same full-production shape <see cref="MinigameAnnouncerLoopbackTests"/> uses — pinning the
/// one ordering claim the minigame peer lock rests on and that a unit test of
/// <see cref="MinigameLockRule"/> alone cannot reach:
///
/// <c>CoopAgent.PumpOnce</c> evaluates the lock against the UNFILTERED peer roster, BEFORE its
/// own <c>minigame_hide_peer</c> filter runs. That filter (default ON) removes a racing peer
/// from what is written into the plugin's shared block in the very tick their flag turns true,
/// so a lock computed from the filtered list would never begin at all — the identical trap
/// <see cref="KotorCoop.Agent.MinigameAnnouncer"/>'s own doc comment records for the HUD notice.
/// Both halves are asserted at once here: the lock is asserted AND the phantom is parked, in
/// the same window.
///
/// The three release paths (<see cref="MinigameLockRuleTests"/> covers each in isolation) are
/// represented here by the one that actually happens in play: the racer's flag clears.
/// </summary>
public class MinigameLockLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const string Module = "tar_m03af";

    /// <summary>
    /// Stands in for <c>KotorGameBridge</c>. <see cref="InMinigame"/> drives the published
    /// <see cref="PlayerState.FlagInMinigame"/>; <see cref="LockAsserted"/>/
    /// <see cref="LockOwnerName"/> record what <c>CoopAgent</c> pushed through
    /// <see cref="IPlayerBridge.TryWriteMinigameLock"/> — the real shared-block write, minus
    /// the process boundary.
    /// </summary>
    private sealed class FakeBridge : IGameBridge
    {
        public bool InMinigame { get; set; }

        public bool LockAsserted { get; private set; }

        public string LockOwnerName { get; private set; } = string.Empty;

        public IReadOnlyList<RemotePeer> LastWrittenPeers { get; private set; } = [];

        public string Description => "minigame-lock-loopback-fake";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(
                Vector3f.Zero, 0f, Module, 100, 0,
                Flags: InMinigame ? PlayerState.FlagInMinigame : 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers)
        {
            LastWrittenPeers = peers;
            return true;
        }

        public bool TryWriteMinigameLock(bool locked, string ownerName)
        {
            LockAsserted = locked;
            LockOwnerName = ownerName;
            return true;
        }

        public void Dispose()
        {
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(15).ConfigureAwait(false);
        }

        return false;
    }

    [Fact]
    public async Task TheHostRacingLocksTheClientAndNamesTheRacer()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        FakeBridge hostBridge = new();
        FakeBridge clientBridge = new();

        // Shipped defaults on both keys, deliberately not overridden: minigame_hide_peer = 1
        // is exactly what makes the ordering claim above load-bearing.
        AgentSettings agentSettings = new() { HeavyPumpIntervalMs = 0 };
        Assert.True(agentSettings.HidePeersInMinigame, "test assumes the shipped default");
        Assert.True(agentSettings.LockPeersDuringMinigame, "test assumes the shipped default");

        CoopAgent hostAgent = new(hostBridge, host, agentSettings);
        CoopAgent clientAgent = new(clientBridge, client, agentSettings);

        using CancellationTokenSource pumpCts = new();
        Task hostPump = Task.Run(async () =>
        {
            while (!pumpCts.IsCancellationRequested)
            {
                hostAgent.PumpOnce();
                await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
            }
        });
        Task clientPump = Task.Run(async () =>
        {
            while (!pumpCts.IsCancellationRequested)
            {
                clientAgent.PumpOnce();
                await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
            }
        });

        try
        {
            Assert.True(
                await WaitForAsync(() => client.IsConnected && host.GetRemotePeers().Count == 1, Timeout),
                "host/client never connected");

            Assert.False(clientBridge.LockAsserted, "nothing is racing yet");

            hostBridge.InMinigame = true;

            Assert.True(
                await WaitForAsync(() => clientBridge.LockAsserted, Timeout),
                "the client never locked for the host's minigame");

            Assert.Equal("Revan", clientBridge.LockOwnerName);

            // Both halves of the ordering claim in the same window: the peer really was
            // filtered out of the block write at the same moment the lock stood.
            Assert.True(
                await WaitForAsync(() => clientBridge.LastWrittenPeers.Count == 0, Timeout),
                "the hide filter never excluded the racing host from the client's block write");

            // And the racer is never locked by their own race.
            Assert.False(hostBridge.LockAsserted, "the racer locked itself");

            hostBridge.InMinigame = false;

            Assert.True(
                await WaitForAsync(() => !clientBridge.LockAsserted, Timeout),
                "the client's lock never released when the host left the minigame");
        }
        finally
        {
            pumpCts.Cancel();
            await Task.WhenAll(hostPump, clientPump);

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    [Fact]
    public async Task TheKillSwitchLeavesTheClientUnlocked()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        FakeBridge hostBridge = new() { InMinigame = true };
        FakeBridge clientBridge = new();

        AgentSettings off = new() { HeavyPumpIntervalMs = 0, LockPeersDuringMinigame = false };
        CoopAgent hostAgent = new(hostBridge, host, off);
        CoopAgent clientAgent = new(clientBridge, client, off);

        using CancellationTokenSource pumpCts = new();
        Task hostPump = Task.Run(async () =>
        {
            while (!pumpCts.IsCancellationRequested)
            {
                hostAgent.PumpOnce();
                await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
            }
        });
        Task clientPump = Task.Run(async () =>
        {
            while (!pumpCts.IsCancellationRequested)
            {
                clientAgent.PumpOnce();
                await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
            }
        });

        try
        {
            Assert.True(
                await WaitForAsync(() => client.IsConnected && host.GetRemotePeers().Count == 1, Timeout),
                "host/client never connected");

            // Give the lock every chance to appear, then assert it did not.
            await Task.Delay(500);
            Assert.False(clientBridge.LockAsserted, "minigame_lock_peers = 0 still locked the client");
        }
        finally
        {
            pumpCts.Cancel();
            await Task.WhenAll(hostPump, clientPump);

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }
}
