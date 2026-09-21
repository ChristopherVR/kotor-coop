using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// What a clean <c>Bye</c> does to a peer's IDENTITY, as opposed to what it does to its
/// connection.
///
/// ⚠ THIS FILE REPLACES AN ASSERTION THAT WENT THE OTHER WAY. A reasonable-looking invariant
/// says a farewell should be final: the session announced "so-and-so left", everyone tore
/// down, and a returning player is a new player who must be dealt a new peer id. The premise
/// is false, and the cost of acting on it is a double-counted fight.
///
/// What a departing player actually tears down is on THEIR machine — SessionManager.Disconnect
/// disposes their session and calls ClearCoopState. On the HOST, nothing keyed on their peer
/// id is discarded by the announcement, and two of the tables that matter are never discarded
/// at all by design: the incoming phantom-damage rows (dropping one walks a monotonic total
/// backwards) and CoopAgent's PeerDamageLedger, which is scoped to the module and knows
/// nothing about sessions.
///
/// And the departing player's own counters survive too. The running total of damage they have
/// dealt lives in the plugin's DamageDealtTable; ClearCoopState does not touch it, and the
/// only thing that does is PluginHost.InvalidateAll — world unload, module change, flushed
/// transition. Never a disconnect. Its generation is bumped in the same place, so that does
/// not move either.
///
/// So a player who quits and rejoins in the same module reports the SAME cumulative total
/// under the SAME generation. Give them a fresh id and the ledger sees no prior row, banks all
/// of it as new damage, and the enemy takes their whole historic contribution twice — with the
/// generation rule unable to help, because the generation is exactly what did not change.
///
/// The real invariant: AN IDENTITY MUST LIVE AS LONG AS THE COUNTERS KEYED ON IT. When a
/// departure genuinely does reset them, the returning player carries a NEW nonce (a fresh
/// CoopClient mints one), nothing is reclaimed, and every cumulative channel correctly treats
/// them as first sight — which is also why only a client object that SURVIVED can reach the
/// reclaim at all, and that is precisely the one whose counters survived with it. That is the
/// case these tests construct.
/// </summary>
public class ByeIdentityTests
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
    /// A client says Bye, the host drops it at once, and then the SAME client comes back —
    /// which is the only way a nonce that has said farewell can ever be presented again, and
    /// therefore the only way the reclaim is reachable at all.
    ///
    /// It must come back as itself, and the fight it was in the middle of must not be counted
    /// twice. The damage assertion is the one that matters: the peer id is only interesting
    /// because of what is keyed on it.
    /// </summary>
    [Fact]
    public async Task AReturningFarewellReclaimsItsIdentityAndItsFightIsNotCountedTwice()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource hostCts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(hostCts.Token);

        // The client runs on its own token so it can be stopped — which is what sends the Bye
        // — and then started again on the same object, nonce and counters intact.
        CancellationTokenSource firstRun = new();
        Task clientTask = client.RunAsync(firstRun.Token);

        // The plugin's ledger survives a disconnect, so the returning client reports exactly
        // this again, under exactly this generation. Both are held constant on purpose.
        PeerDamageLedger ledger = new();
        List<EnemyDamageEntry> report = [new(0x14, 40)];

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
            client.ReportEnemyDamage("end_m01aa", generation: 7, report);
            ledger.Merge("end_m01aa", host.GetEnemyDamage("end_m01aa"));
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));
        uint originalId = client.LocalPeerId;

        Assert.True(
            await WaitForAsync(() => host.GetRemotePeers().Count == 1, Timeout, Publish),
            "the host never saw the client");
        Assert.True(
            await WaitForAsync(() => ledger.OwedFor(0x14) == 40, Timeout, Publish),
            "the client's damage never reached the host's ledger");

        // A clean, deliberate departure: RunAsync sends Bye on its way out.
        await firstRun.CancelAsync();
        await clientTask;
        firstRun.Dispose();

        Assert.True(
            await WaitForAsync(
                () => host.GetRemotePeers().Count == 0,
                Timeout,
                () => host.PublishLocalState(Somewhere)),
            "the host did not drop a peer that said Bye");

        // The same player comes back. Same object, so the same nonce — and, in the real
        // system, the same plugin-side totals that never went anywhere.
        using CancellationTokenSource secondRun = new(TimeSpan.FromSeconds(45));
        Task rejoinTask = client.RunAsync(secondRun.Token);

        Assert.True(
            await WaitForAsync(() => client.IsConnected, Timeout, Publish),
            "the client never rejoined after saying Bye");

        // ⚠ THE SAME ID. Not because continuity is tidy, but because of the assertion below.
        Assert.Equal(originalId, client.LocalPeerId);
        Assert.True(
            await WaitForAsync(() => host.GetRemotePeers().Count == 1, Timeout, Publish),
            "the host never saw the returning client");
        Assert.Equal(originalId, host.GetRemotePeers()[0].PeerId);

        // ⚠ AND THE FIGHT IS NOT RE-BANKED. Under a fresh id the ledger would find no prior
        // row for the returning peer, read its unchanged running total of 40 as forty points
        // of brand new damage, and kill the enemy twice over.
        for (int i = 0; i < 60; i++)
        {
            Publish();
            await Task.Delay(20);
        }

        Assert.Equal(40u, ledger.OwedFor(0x14));

        // Caller-thread reports must not race the tick encoder: a torn damage packet can
        // lower a cumulative baseline and make the next valid report bank damage twice.
        Assert.Equal(0, host.GetStats().PacketsDropped);

        await secondRun.CancelAsync();
        await rejoinTask;
        await hostCts.CancelAsync();
        await hostTask;
    }

    /// <summary>
    /// The other half of the rule, and the reason the first half is safe: a genuinely new
    /// client — a restarted agent, a new session, a player who really has torn everything down
    /// — carries a new nonce and is dealt a NEW id. Nothing is reclaimed, and every cumulative
    /// channel treats it as first sight, which is exactly right because its counters restarted
    /// too.
    ///
    /// So "a farewell is final" is still true for every departure that actually resets
    /// anything. It is enforced by the nonce, which is the thing that knows, rather than by
    /// the Bye, which does not.
    /// </summary>
    [Fact]
    public async Task ARebuiltClientIsANewPlayerEvenFromTheSameAddress()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });

        using CancellationTokenSource hostCts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(hostCts.Token);

        IPEndPoint hostEnd = new(IPAddress.Loopback, host.ListenEndPoint.Port);
        uint firstId;

        using (CoopClient first = new(new SessionSettings { PlayerName = "Malak", TickRate = 30 }, hostEnd))
        {
            using CancellationTokenSource firstCts = new(TimeSpan.FromSeconds(30));
            Task firstTask = first.RunAsync(firstCts.Token);

            Assert.True(await WaitForAsync(
                () => first.IsConnected,
                Timeout,
                () =>
                {
                    host.PublishLocalState(Somewhere);
                    first.PublishLocalState(Somewhere);
                }));

            firstId = first.LocalPeerId;

            await firstCts.CancelAsync();
            await firstTask;
        }

        Assert.True(
            await WaitForAsync(
                () => host.GetRemotePeers().Count == 0,
                Timeout,
                () => host.PublishLocalState(Somewhere)),
            "the host did not drop the departed peer");

        // A brand new client object: new nonce, and in the real system a new agent whose
        // ledgers all start from zero.
        using CoopClient second = new(new SessionSettings { PlayerName = "Malak", TickRate = 30 }, hostEnd);
        using CancellationTokenSource secondCts = new(TimeSpan.FromSeconds(30));
        Task secondTask = second.RunAsync(secondCts.Token);

        Assert.True(await WaitForAsync(
            () => second.IsConnected,
            Timeout,
            () =>
            {
                host.PublishLocalState(Somewhere);
                second.PublishLocalState(Somewhere);
            }));

        Assert.NotEqual(firstId, second.LocalPeerId);

        await secondCts.CancelAsync();
        await secondTask;
        await hostCts.CancelAsync();
        await hostTask;
    }
}
