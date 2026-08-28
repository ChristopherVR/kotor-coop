using System.Net;
using KotorCoop.Agent;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Real host, real client, real sockets, real <see cref="CoopAgent"/> pump loop on each side —
/// the full production shape, minus only the actual game process (a <see cref="FakeBridge"/>
/// stands in for <c>KotorGameBridge</c>) — proving the claim
/// <see cref="MinigameAnnouncer"/>'s own doc comment makes: under the shipped
/// <c>minigame_hide_peer=1</c> default, the agent-side announcer still sees a peer's
/// <see cref="PlayerState.FlagInMinigame"/> edge, because it reads
/// <c>ICoopSession.GetRemotePeers()</c> — the RAW admitted roster the wire buffer holds — not
/// <c>CoopAgent.PumpOnce</c>'s own filtered <c>visible</c> list that the hide setting trims
/// before <c>TryWriteRemotePeers</c> writes it into the plugin's shared block.
///
/// This is the exact mechanism <c>CoopCommandDispatcher.Announcers.cs</c>'s
/// <c>AnnouncePeerChanges</c> uses in production (<c>session.GetRemotePeers()</c> fed straight
/// into <see cref="MinigameAnnouncer.Observe"/>); this test drives the identical call from its
/// own loop rather than standing up the full <c>CoopCommandDispatcher</c> (which needs a real
/// <c>KotorGameBridge</c> cast for several unrelated concerns), matching
/// <c>PartyPoolLoopbackTests.DispatcherLoop</c>'s own precedent of modelling
/// <c>CoopCommandDispatcher.RunAsync</c>'s tick as a separate, independently-scheduled task.
///
/// docs/research/playthrough-taris.md's dated follow-up (2026-08-28) traces why a live round
/// reported "the partner gets ZERO notice": it read the PLUGIN's own log file
/// (<c>kotorcoop-plugin.log</c>), but the agent-side toast never touches that file — it posts
/// through <c>KotorGameBridge.PostChatLine</c> into the shared block, which
/// <c>ChatOverlayController</c> renders straight onto the frame as queued text, and is recorded
/// only in the AGENT's own log (<c>agent2.log</c>). A prior live pass
/// (docs/research/minigames.md §23's "Live pass (2026-08-28)") already caught this exact line
/// firing for real, in the correct log: `CLIENT [03:28:08] * Lanik is in a minigame - waiting`.
/// This test is the permanent regression pin for that same wiring.
/// </summary>
public class MinigameAnnouncerLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const string Module = "tar_m02ae";

    /// <summary>
    /// Stands in for <c>KotorGameBridge</c>: <see cref="InMinigame"/> drives whether the
    /// published local state carries <see cref="PlayerState.FlagInMinigame"/> — the same bit a
    /// real `pazaak`/swoop/turret entry sets — and <see cref="LastWrittenPeers"/> records what
    /// <c>CoopAgent.PumpOnce</c> actually wrote into "the block" (its filtered <c>visible</c>
    /// list), so this test can assert the hide filter's OTHER half — the write side really did
    /// park the peer — held at the exact same moment the toast fired.
    /// </summary>
    private sealed class FakeBridge : IGameBridge
    {
        public bool InMinigame { get; set; }

        public IReadOnlyList<RemotePeer> LastWrittenPeers { get; private set; } = [];

        public string Description => "minigame-announcer-loopback-fake";

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
    public async Task TheClientsMinigameAnnouncerSeesTheHostsFlagWhileHidePeersParksThePhantom()
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

        // HidePeersInMinigame is left at its default (true) - the shipped coop.ini default
        // (minigame_hide_peer = 1) docs/research/playthrough-taris.md's own "shipped defaults"
        // framing used, deliberately not overridden here.
        AgentSettings agentSettings = new() { HeavyPumpIntervalMs = 0 };
        Assert.True(agentSettings.HidePeersInMinigame, "test assumes the shipped default");

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

        // The exact call CoopCommandDispatcher.Announcers.cs's AnnouncePeerChanges makes every
        // tick, driven from its own separately-scheduled loop (production: CoopCommandDispatcher
        // .RunAsync's PeriodicTimer, entirely independent of CoopAgent.RunAsync's own loop).
        MinigameAnnouncer clientAnnouncer = new();
        List<string> clientLines = [];
        using CancellationTokenSource announceCts = new();
        Task announceTask = Task.Run(async () =>
        {
            while (!announceCts.IsCancellationRequested)
            {
                lock (clientLines)
                {
                    clientAnnouncer.Observe(client.GetRemotePeers(), clientLines.Add);
                }

                await Task.Delay(15, CancellationToken.None).ConfigureAwait(false);
            }
        });

        try
        {
            Assert.True(
                await WaitForAsync(() => client.IsConnected && host.GetRemotePeers().Count == 1, Timeout),
                "host/client never connected");

            // Let the baseline (not-in-minigame) reading settle first, so the announcer's own
            // "first sight is silent" rule does not eat the edge below.
            await Task.Delay(300);

            // The HOST enters a minigame - the exact live repro (`pazaak` on the host, under
            // the shipped minigame_hide_peer=1 default) docs/research/playthrough-taris.md's
            // dated follow-up describes.
            hostBridge.InMinigame = true;

            Assert.True(
                await WaitForAsync(
                    () => { lock (clientLines) { return clientLines.Contains("Revan is in a minigame - waiting"); } },
                    Timeout),
                "the client's MinigameAnnouncer never announced the host's edge; saw: "
                + string.Join(" | ", clientLines));

            // The complementary half of the same claim: the hide filter really did park the
            // phantom on the WRITE side at the same time - a peer excluded from what
            // CoopAgent.PumpOnce actually writes into the client's own bridge
            // (TryWriteRemotePeers), proving the hide filter and the toast are not the same
            // gate and one does not block the other.
            Assert.True(
                await WaitForAsync(() => clientBridge.LastWrittenPeers.Count == 0, Timeout),
                "the hide filter never excluded the host from the client's own block write");

            hostBridge.InMinigame = false;

            Assert.True(
                await WaitForAsync(
                    () => { lock (clientLines) { return clientLines.Contains("Revan is back"); } },
                    Timeout),
                "the client's MinigameAnnouncer never announced the host leaving the minigame; saw: "
                + string.Join(" | ", clientLines));
        }
        finally
        {
            await announceCts.CancelAsync();
            await announceTask;

            pumpCts.Cancel();
            await Task.WhenAll(hostPump, clientPump);

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }
}
