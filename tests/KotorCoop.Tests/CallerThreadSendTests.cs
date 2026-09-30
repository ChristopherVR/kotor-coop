using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Who owns the encode buffer.
///
/// ⚠ A CoopClient HAS THREE THREADS TOUCHING IT AND ONLY TWO OF THEM ARE ITS OWN.
/// <c>RunAsync</c>'s tick loop encodes into <c>_sendBuffer</c>; the receive loop encodes its
/// replies into <c>_replyBuffer</c>; and then there is everyone else — <c>CoopAgent.PumpOnce</c>
/// on the agent pump thread, the console dispatcher, the plugin pump — calling
/// <see cref="CoopClient.SetFlag"/>, <see cref="CoopClient.SetJournalEntry"/>,
/// <see cref="CoopClient.DropItem"/>, <see cref="CoopClient.ReportEnemyDamage"/> and friends,
/// all of which used to encode into the TICK thread's buffer from whatever thread the caller
/// happened to be.
///
/// Two threads sharing one array do not produce a dropped packet, which would be harmless on a
/// channel built for loss. They produce a datagram carrying one packet's header over another
/// packet's body: it has the right magic and the right version, so the far end parses it as
/// whatever type won the header byte and then either rejects it as malformed or — the case
/// that actually cost time — accepts a plausible wrong number. It was first found as a
/// container-lock request the host dropped as malformed, and again as a peer's cumulative
/// damage total decoding LOWER than it had ever been, which <see cref="PeerDamageLedger"/>
/// takes as a new baseline and then re-banks the whole fight against.
///
/// This test hammers the caller-thread API from a foreign thread while the tick loop runs. The
/// evidence is the host's malformed-datagram counter: on a clean loopback session with no
/// fault injection, nothing else can raise it.
/// </summary>
public class CallerThreadSendTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

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

    [Fact]
    public async Task HammeringTheClientApiFromAnotherThreadNeverTearsADatagram()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        Assert.True(
            await WaitForAsync(
                () => client.IsConnected,
                Timeout,
                () =>
                {
                    host.PublishLocalState(Somewhere);
                    client.PublishLocalState(Somewhere);
                }));

        // A foreign thread, standing in for the agent pump, calling exactly the methods
        // CoopAgent.PumpOnce calls — while the tick loop below keeps encoding its own state
        // updates, pings and retransmits.
        using CancellationTokenSource stop = new();
        Task hammer = Task.Run(
            () =>
            {
                uint flag = 5000;
                uint serial = 1;

                while (!stop.IsCancellationRequested)
                {
                    client.SetFlag(flag++, 1);
                    client.SetJournalEntry($"k_hammer_{serial % 16}", (int)(serial % 40));
                    host.PublishWorldDigest(new WorldDigest(1, 1, "end_m01aa", 1, NativeWorldEpoch: 1));
                    client.PublishWorldDigest(new WorldDigest(1, 1, "end_m01aa", 1, NativeWorldEpoch: 1));
                    client.ReportEnemyDamage(
                        "end_m01aa", generation: 7, [new EnemyDamageEntry(0x14, serial)]);
                    client.ReportProgression(
                        generation: 7, goldEarned: serial, goldSpent: 0, experience: serial);
                    serial++;

                    // Paced so the reliable senders' pending tables drain normally (a clean
                    // loopback link acks within a tick) rather than piling into their caps —
                    // the race this test is looking for needs concurrency, not volume.
                    Thread.Sleep(1);
                }
            },
            CancellationToken.None);

        // Long enough for thousands of encodes to race; the old code fails inside the first
        // few hundred.
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until)
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
            await Task.Delay(10);
        }

        await stop.CancelAsync();
        await hammer;

        // Give the last few datagrams time to land before reading the counter.
        await Task.Delay(200);

        SessionStats hostStats = host.GetStats();
        Assert.Equal(0, hostStats.PacketsDropped);
        Assert.Equal(0, hostStats.VersionMismatches);

        // ...and the session was genuinely busy, so a zero above means "nothing tore", not
        // "nothing was sent".
        Assert.True(hostStats.PacketsReceived > 500, $"only {hostStats.PacketsReceived} packets arrived");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }
}
