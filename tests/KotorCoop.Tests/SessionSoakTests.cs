using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The long one: a host and two clients, over real sockets, for ten minutes of the link a
/// player actually has — loss between 5 and 30 percent, reordering, duplicates, one client
/// dropping out and rejoining twice, and the host itself vanishing for eight seconds — while
/// the world keeps changing underneath all of it.
///
/// ⚠ OPT-IN, AND DELIBERATELY NOT PART OF THE ORDINARY SUITE. Ten minutes is not a unit test
/// and must never become one; CI filters <c>Category=Soak</c> out, and even a direct run does
/// nothing unless <c>KOTORCOOP_SOAK=1</c> is set. Run it:
///
/// <code>
/// $env:KOTORCOOP_SOAK=1
/// dotnet test tests/KotorCoop.Tests --filter "Category=Soak"
/// # shorter, for a quick look:
/// $env:KOTORCOOP_SOAK_SECONDS=120
/// </code>
///
/// WHAT IT IS FOR. Every other test in this repository asserts one property over one fault.
/// The failures this one exists to catch are the ones that need TIME and REPETITION: a pending
/// queue that grows a little on every outage, a serial that wraps or restarts, a store that
/// converges after each individual incident but drifts a little each time, a peer identity
/// that is reclaimed correctly once and not the second time. None of those are visible in a
/// twenty-second test, and all of them are what a four-hour playthrough over the internet is.
///
/// WHAT IT ASSERTS AT THE END. The link is repaired, the churn stops, and then every
/// replicated store on all three ends has to agree — by CONTENT DIGEST, not by "roughly the
/// same number of entries" — and the identity bookkeeping has to be intact: each client still
/// holds the peer id it was first given, the host's roster holds exactly those two ids with no
/// ghost beside them, and a change minted after the last rejoin carries a serial that outranks
/// everything from before it (see docs/research/disconnect-reconnect.md and the rejoin-identity
/// note: a rejoin reclaims the peer id, and a per-peer serial that restarted at 1 would let a
/// stale op from before the outage outrank a fresh one after it).
/// </summary>
[Trait("Category", "Soak")]
public class SessionSoakTests
{
    /// <summary>How long the churn phase runs. Ten minutes by default — the point of the test —
    /// but overridable so a change to the soak itself can be smoke-tested in two minutes
    /// without editing the file.</summary>
    private static TimeSpan ChurnDuration =>
        int.TryParse(Environment.GetEnvironmentVariable("KOTORCOOP_SOAK_SECONDS"), out int seconds)
            && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromMinutes(10);

    private static bool Enabled => Environment.GetEnvironmentVariable("KOTORCOOP_SOAK") == "1";

    private const string Module = "end_m01aa";

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
    public async Task ASessionSurvivesTenMinutesOfBadInternet()
    {
        if (!Enabled)
        {
            // Not a silent pass by accident: see the class remarks for how to turn it on.
            return;
        }

        TimeSpan churn = ChurnDuration;

        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        IPEndPoint hostEnd = new(IPAddress.Loopback, host.ListenEndPoint.Port);
        using CoopClient alpha = new(new SessionSettings { PlayerName = "Malak", TickRate = 30 }, hostEnd);
        using CoopClient beta = new(new SessionSettings { PlayerName = "Bastila", TickRate = 30 }, hostEnd);

        // Generous: the churn plus the settle plus the slack a loaded machine needs. The test
        // asserts its own deadlines; this token only exists so nothing outlives the run.
        using CancellationTokenSource cts = new(churn + TimeSpan.FromMinutes(5));
        Task hostTask = host.RunAsync(cts.Token);
        Task alphaTask = alpha.RunAsync(cts.Token);

        // Beta runs on its own token so the "disconnects and rejoins" leg can stop and restart
        // it for real — a Bye and a fresh RunAsync on the same object, same nonce, exactly what
        // quitting to the menu and rejoining does.
        CancellationTokenSource betaRun = new();
        Task betaTask = beta.RunAsync(betaRun.Token);

        float tick = 0;

        void PublishPositions()
        {
            tick += 0.05f;
            host.PublishLocalState(new PlayerState(new Vector3f(10f + tick, 20f, 0f), tick % 6.28f, Module, 100, 0));
            alpha.PublishLocalState(new PlayerState(new Vector3f(30f, 20f + tick, 0f), 1f, Module, 95, 0));
            beta.PublishLocalState(new PlayerState(new Vector3f(50f, 20f, tick), 2f, Module, 90, 0));
        }

        Assert.True(
            await WaitForAsync(
                () => alpha.IsConnected && beta.IsConnected && host.GetRemotePeers().Count == 2,
                TimeSpan.FromSeconds(30),
                PublishPositions),
            "the three-way session never came up");

        uint alphaId = alpha.LocalPeerId;
        uint betaId = beta.LocalPeerId;
        Assert.NotEqual(alphaId, betaId);

        // ---- The link a player actually has ------------------------------------------------
        //
        // Reordering comes from OUTBOUND jitter specifically: an outbound delay hands the
        // datagram to a fire-and-forget task, so packets held for different lengths of time
        // genuinely swap places. Inbound delay is awaited inside the single receive loop and
        // would only be head-of-line blocking. See NetworkSim.SetLatency.
        foreach (NetworkSim sim in new[] { host.Network, alpha.Network, beta.Network })
        {
            sim.SetLoss(5);
            sim.SetLatency(15, 25);
            sim.SetDuplication(5);
        }

        // ---- Churn -------------------------------------------------------------------------

        DateTime start = DateTime.UtcNow;
        DateTime until = start + churn;

        // Scheduled incidents, as a fraction of the run, so they land in the right places
        // whatever the duration is set to.
        DateTime betaDropOne = start + (churn * 0.15);
        DateTime betaDropTwo = start + (churn * 0.55);
        DateTime hostBlackout = start + (churn * 0.35);
        bool betaDroppedOnce = false;
        bool betaDroppedTwice = false;
        bool hostDropped = false;

        uint doorFlag = 100;
        uint questSerial = 0;
        uint container = 0x1000;
        uint lootItem = 0x2000;
        List<uint> alphaItems = [];
        List<uint> hostItems = [];
        int step = 0;

        while (DateTime.UtcNow < until)
        {
            PublishPositions();
            step++;

            // Every ~200 ms of churn: a door, a quest step, an emptied container, an item
            // dropped and (later) picked back up — from both ends, so nothing under test is
            // only ever exercised in one direction.
            if (step % 10 == 0)
            {
                host.SetFlag(doorFlag, (sbyte)(step % 2));
                alpha.SetFlag(doorFlag + 1000, (sbyte)((step / 2) % 3));
                if (beta.IsConnected)
                {
                    beta.SetFlag(doorFlag + 2000, 1);
                }

                doorFlag = doorFlag % 400 == 0 ? 100 : doorFlag + 1;

                host.SetJournalEntry($"k_soak_{questSerial % 24}", (int)(questSerial % 50));
                questSerial++;

                host.PublishContainerLoot(container, lootItem);
                if (++lootItem % 8 == 0)
                {
                    container++;
                }
            }

            // Ground items churn slower and are picked back up, so the store stays bounded
            // (GroundItemStore has a hard cap) rather than growing for ten minutes.
            if (step % 50 == 0)
            {
                hostItems.Add(host.DropItem("g_w_blstrpstl001", Module, new Vector3f(step, 0, 0)));
                if (alpha.IsConnected)
                {
                    uint dropped = alpha.DropItem("g_i_credits001", Module, new Vector3f(0, step, 0));
                    if (dropped != 0)
                    {
                        alphaItems.Add(dropped);
                    }
                }

                if (hostItems.Count > 6)
                {
                    host.RemoveItem(hostItems[0]);
                    hostItems.RemoveAt(0);
                }

                if (alphaItems.Count > 6)
                {
                    alpha.RemoveItem(alphaItems[0]);
                    alphaItems.RemoveAt(0);
                }
            }

            // The link gets worse and better, the way a real one does: 5% to 30% and back on a
            // slow sweep, so no single loss rate is the only one ever tested.
            if (step % 100 == 0)
            {
                double loss = 5 + (12.5 * (1 + Math.Sin(step / 300.0)));
                foreach (NetworkSim sim in new[] { host.Network, alpha.Network, beta.Network })
                {
                    sim.SetLoss(loss);
                }
            }

            // ---- The incidents --------------------------------------------------------------

            if (!betaDroppedOnce && DateTime.UtcNow >= betaDropOne)
            {
                betaDroppedOnce = true;
                await betaRun.CancelAsync();
                await betaTask;
                betaRun.Dispose();

                // Away long enough for the host to expire the slot, so the return really is a
                // rejoin and not a blip the keepalives cover.
                await Task.Delay(TimeSpan.FromSeconds(8));

                betaRun = new CancellationTokenSource();
                betaTask = beta.RunAsync(betaRun.Token);
                Assert.True(
                    await WaitForAsync(() => beta.IsConnected, TimeSpan.FromSeconds(30), PublishPositions),
                    "beta never rejoined after its first departure");
                Assert.Equal(betaId, beta.LocalPeerId);
            }

            if (!hostDropped && DateTime.UtcNow >= hostBlackout)
            {
                hostDropped = true;

                // The host vanishes — not a process restart, a cable pull: its socket stays
                // open and both clients keep talking into nothing for eight seconds, which is
                // past the peer timeout on every end.
                host.Network.StartBlackout(8_000);
                Assert.True(
                    await WaitForAsync(
                        () => !alpha.IsConnected, TimeSpan.FromSeconds(20), PublishPositions),
                    "alpha never noticed the host had gone: "
                    + $"status={alpha.GetStats().Status} "
                    + $"lastInboundAgeMs={alpha.GetStats().LastInboundAgeMs} "
                    + $"hostSim={host.Network.Describe()} alphaSim={alpha.Network.Describe()}");
                Assert.True(
                    await WaitForAsync(
                        () => alpha.IsConnected && beta.IsConnected && host.GetRemotePeers().Count == 2,
                        TimeSpan.FromSeconds(40),
                        PublishPositions),
                    "the session never re-formed after the host came back");
                Assert.Equal(alphaId, alpha.LocalPeerId);
                Assert.Equal(betaId, beta.LocalPeerId);
            }

            if (!betaDroppedTwice && DateTime.UtcNow >= betaDropTwo)
            {
                betaDroppedTwice = true;
                await betaRun.CancelAsync();
                await betaTask;
                betaRun.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(8));

                betaRun = new CancellationTokenSource();
                betaTask = beta.RunAsync(betaRun.Token);
                Assert.True(
                    await WaitForAsync(() => beta.IsConnected, TimeSpan.FromSeconds(30), PublishPositions),
                    "beta never rejoined after its second departure");
                Assert.Equal(betaId, beta.LocalPeerId);
            }

            await Task.Delay(20);
        }

        // ---- Settle --------------------------------------------------------------------------
        //
        // The link is repaired and the world stops changing. Everything reliable that is still
        // owed has to be delivered from here, with no new work arriving to hide a gap.
        foreach (NetworkSim sim in new[] { host.Network, alpha.Network, beta.Network })
        {
            sim.Clear();
        }

        bool converged = await WaitForAsync(
            () => Converged(host, alpha) && Converged(host, beta),
            TimeSpan.FromSeconds(90),
            PublishPositions);

        Assert.True(
            converged,
            "the world never converged after the link was repaired:\n"
            + Describe("host ", host.Flags, host.Journal, host.Items, host.Loot)
            + Describe("alpha", alpha.Flags, alpha.Journal, alpha.Items, alpha.Loot)
            + Describe("beta ", beta.Flags, beta.Journal, beta.Items, beta.Loot));

        // ---- Identity ---------------------------------------------------------------------

        Assert.Equal(alphaId, alpha.LocalPeerId);
        Assert.Equal(betaId, beta.LocalPeerId);

        List<RemotePeer> roster = [.. host.GetRemotePeers()];
        Assert.Equal(2, roster.Count);
        Assert.Contains(roster, p => p.PeerId == alphaId);
        Assert.Contains(roster, p => p.PeerId == betaId);
        Assert.Equal(roster.Count, roster.Select(p => p.PeerId).Distinct().Count());

        // ⚠ AND THE SERIALS DID NOT RESTART. A rejoin reclaims the peer id, so a per-peer serial
        // that began again at 1 would let an op from before the outage outrank one made after
        // it — the whole Lamport comparison in EventFlagStore is keyed on (Serial, OriginPeerId)
        // and would silently prefer the stale value. A change minted now, after two rejoins and
        // a host outage, must outrank everything the session has ever stamped.
        uint highWater = Math.Max(host.Flags.Version, Math.Max(alpha.Flags.Version, beta.Flags.Version));
        beta.SetFlag(6_001, 1);
        Assert.True(beta.Flags.TryGetStamp(6_001, out uint serial, out uint origin));
        Assert.True(
            serial > highWater,
            $"a post-rejoin change was stamped {serial}, which does not outrank the session's {highWater}");
        Assert.Equal(betaId, origin);

        // And it still lands, which is the only proof the reclaimed identity is the one the
        // host is actually keying on.
        Assert.True(
            await WaitForAsync(
                () => host.Flags.TryGet(6_001, out sbyte v) && v == 1,
                TimeSpan.FromSeconds(30),
                PublishPositions),
            "a flag set after the last rejoin never reached the host");

        await betaRun.CancelAsync();
        await betaTask;
        betaRun.Dispose();
        await cts.CancelAsync();
        await Task.WhenAll(hostTask, alphaTask);
    }

    /// <summary>Content equality across all four replicated stores, by digest rather than by
    /// count — two stores that diverged by the same NUMBER of changes tie on every counter and
    /// only the content fingerprint tells them apart.</summary>
    private static bool Converged(CoopHost host, CoopClient client) =>
        host.Flags.Digest == client.Flags.Digest
        && host.Journal.Digest == client.Journal.Digest
        && host.Items.Digest == client.Items.Digest
        && host.Loot.Digest == client.Loot.Digest;

    private static string Describe(
        string who, EventFlagStore flags, JournalStore journal, GroundItemStore items, ContainerLootStore loot) =>
        $"  {who}: flags v{flags.Version}/{flags.Digest:x16} ({flags.Count}) "
        + $"journal v{journal.Version}/{journal.Digest:x16} ({journal.Count}) "
        + $"items v{items.Version}/{items.Digest:x16} "
        + $"loot v{loot.Version}/{loot.Digest:x16}\n";
}
