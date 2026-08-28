using System.Net;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Round 103 (docs/research/party-reconciliation.md): the agent-side half of "reconnect does
/// not reliably re-establish companion mirroring" — round 102's rig finding. Real host, real
/// client, real sockets — <c>SessionManager.Disconnect()</c>/<c>Connect()</c> (the console
/// <c>disconnect</c>/<c>reconnect</c> verbs' own path) is reproduced by hand here, tearing down
/// and rebuilding the client and its <see cref="CoopAgent"/> exactly as
/// <c>SessionManager.TearDownSession()</c>/<c>Connect()</c> do — including the
/// <c>bridge.ClearCoopState()</c> call, and keeping the SAME bridge instance across the whole
/// cycle (SessionManager's own "one bridge across every reconnect" contract).
///
/// <see cref="PartyPresenceBridge"/> stands in for <c>KotorGameBridge.Party.cs</c>, faithfully:
/// it delegates every skip/write decision to the REAL, shared
/// <see cref="KotorCoop.Memory.PartyPresenceWriteCache"/> production class (not a
/// reimplementation), and its <see cref="IGameBridge.ClearCoopState"/> override mirrors what
/// <c>KotorGameBridge.ClearCoopState()</c> now does: zero the "block" level directly (the raw
/// memory write <c>SessionScopedBlockLevels</c> performs) AND reset the cache.
///
/// ⚠ WHY <see cref="CoopAgent.PumpOnce"/> IS CALLED BY HAND, NOT THROUGH <c>RunAsync</c>. An
/// earlier version of this test drove the client through <c>SessionManager</c>'s own background
/// pump loop and found it did NOT reproduce the bug: a freshly reconnected <c>CoopClient</c>'s
/// own party view starts EMPTY until its first <c>PartySnapshotPacket</c> arrives, and pumping
/// through that empty-then-full transition flushes the stale cache "for free" via an ordinary
/// content difference (0 companions, then 1) — regardless of whether <c>ClearCoopState</c>
/// resets anything. That masks the exact bug round 102 hit: in production,
/// <c>KotorGameBridge.SupportsPartySync</c> (<c>_player is not null</c>) gates
/// <c>PumpParty</c> shut until the LOCAL player object resolves — independent of the session
/// reconnect, and typically already true from before the disconnect — so the very FIRST call
/// <c>TryWritePartyMembers</c> ever sees after a reconnect already carries the host's full,
/// unchanged companion list, never an intervening empty one. This test reproduces that ordering
/// directly: it waits for the reconnected client to already hold the full companion state
/// BEFORE pumping the agent even once, so the first (and only) pump's call is the exact
/// byte-identical write round 102 needed to land.
/// </summary>
public sealed class PartyPresenceReconnectTests
{
    private const string Module = "end_m01aa";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static PartyMemberState Companion(float x = 12f) =>
        new(SlotIndex: 0, new Vector3f(x, 34f, 0f), Facing: 0.25f, Animation: 3u, Health: 40);

    /// <summary>
    /// A faithful stand-in for <c>KotorGameBridge</c>'s party-presence write path — see the
    /// class doc comment above for exactly what "faithful" means here.
    /// </summary>
    private sealed class PartyPresenceBridge : IGameBridge
    {
        private readonly PartyPresenceWriteCache _cache = new(capacity: 4);

        public string Description => "party-presence-reconnect-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsPartySync => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, Module, 100, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        /// <summary>What is CURRENTLY latched as the shared block's own
        /// <c>BlockPartyPresenceCount</c> level: 0 whenever nothing has (yet) been mirrored
        /// since the last reset, the occupied slot count once a write actually lands.</summary>
        public uint LatchedPartyPresenceCount { get; private set; }

        /// <summary>How many times <see cref="TryWritePartyMembers(IReadOnlyList{PartyMemberState})"/>
        /// actually wrote the count level (not skipped) — the direct measurement this test's
        /// core assertion needs: did a pass land a write, or silently skip it.</summary>
        public int CountWritesLanded { get; private set; }

        public int TryWritePartyMembers(IReadOnlyList<PartyMemberState> members)
        {
            bool moduleChanged = _cache.NoteModule(Module);

            uint written = 0;
            for (int i = 0; i < 4; i++)
            {
                PartyMemberState? candidate = i < members.Count ? members[i] : null;
                if (candidate.HasValue)
                {
                    written++;
                }

                if (_cache.CanSkipSlot(i, candidate, moduleChanged))
                {
                    continue;
                }

                _cache.RecordSlot(i, candidate);
            }

            if (!_cache.CanSkipCount(written, moduleChanged))
            {
                LatchedPartyPresenceCount = written;
                _cache.RecordCount(written);
                CountWritesLanded++;
            }

            return (int)written;
        }

        /// <summary>
        /// Mirrors <c>KotorGameBridge.ClearCoopState()</c> exactly: the raw block zero
        /// (<c>SessionScopedBlockLevels</c>'s own write, here just the latched field) AND —
        /// round 103's fix — the cache reset.
        /// </summary>
        public void ClearCoopState()
        {
            LatchedPartyPresenceCount = 0;
            _cache.Reset();
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

    /// <summary>
    /// ⚠ THE REGRESSION PIN FOR ROUND 102's FINDING. A client mirrors a companion, disconnects,
    /// and reconnects while the host's own published companion state has not changed at all
    /// (the ordinary case — nothing moved while nobody was watching, and the reconnected
    /// client's own view of the party is already fully populated by the time this agent's first
    /// pump runs — see the class doc comment for why that ordering matters). The "block"'s own
    /// PartyPresenceCount level must rise above 0 again on the reconnect, even though every byte
    /// the host republishes is identical to what was mirrored before the disconnect.
    /// </summary>
    [Fact]
    public async Task DisconnectThenReconnectRewritesTheBlockEvenWithIdenticalCompanionState()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CancellationTokenSource hostCts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(hostCts.Token);

        // The host publishes ONE companion, unchanging — a level, broadcast every tick
        // regardless of who is currently connected (CoopHost.Party.cs's own "sent even when
        // nothing changed" design), exactly matching the round-102 recipe (nothing moved while
        // the client was disconnected).
        PartyMemberState companion = Companion();
        host.PublishParty([companion]);

        PartyPresenceBridge bridge = new();
        IPEndPoint endpoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);
        SessionSettings settings = new() { PlayerName = "Malak", TickRate = 30 };
        AgentSettings agentSettings = new() { PartyFastPump = true };

        // ---- first connect ----
        CoopClient client1 = new(settings, endpoint);
        using CancellationTokenSource clientCts1 = new(TimeSpan.FromSeconds(30));
        Task clientTask1 = client1.RunAsync(clientCts1.Token);

        Assert.True(
            await WaitForAsync(() => client1.IsConnected && client1.GetPartyMembers().Count == 1, Timeout),
            "the first connect never received the host's companion");

        CoopAgent agent1 = new(bridge, client1, agentSettings);
        agent1.PumpOnce();

        Assert.Equal(1u, bridge.LatchedPartyPresenceCount);
        int writesBeforeDisconnect = bridge.CountWritesLanded;

        // ---- disconnect: SessionManager.TearDownSession()'s own shape (cancel, dispose,
        // ClearCoopState) ----
        await clientCts1.CancelAsync();
        try
        {
            await clientTask1;
        }
        catch (OperationCanceledException)
        {
        }

        client1.Dispose();
        bridge.ClearCoopState();

        Assert.Equal(0u, bridge.LatchedPartyPresenceCount);

        // ---- reconnect: a FRESH CoopClient/CoopAgent, the SAME bridge instance
        // (SessionManager never rebuilds it) ----
        CoopClient client2 = new(settings, endpoint);
        using CancellationTokenSource clientCts2 = new(TimeSpan.FromSeconds(30));
        Task clientTask2 = client2.RunAsync(clientCts2.Token);

        // Wait until the SECOND client already holds the (byte-identical) companion state
        // BEFORE this agent's first pump — see the class doc comment for why this ordering,
        // not an incidental empty-then-full transition, is what actually happens in production.
        Assert.True(
            await WaitForAsync(() => client2.IsConnected && client2.GetPartyMembers().Count == 1, Timeout),
            "the reconnect never received the host's companion");

        CoopAgent agent2 = new(bridge, client2, agentSettings);
        agent2.PumpOnce();

        // THE ASSERTION ROUND 102 NEEDED: the block's own count must rise back to 1 on THIS
        // very first post-reconnect pass. Before round 103's fix, it silently stays at 0
        // forever — the cache's stale, pre-disconnect idea of "already wrote this" survives the
        // reconnect (the same bridge instance), and byte-identical content is skipped.
        Assert.Equal(1u, bridge.LatchedPartyPresenceCount);
        Assert.True(
            bridge.CountWritesLanded > writesBeforeDisconnect,
            "round 102's finding: reconnect does not reliably re-establish companion mirroring "
            + "— the write was silently skipped instead of landing");

        await clientCts2.CancelAsync();
        try
        {
            await clientTask2;
        }
        catch (OperationCanceledException)
        {
        }

        client2.Dispose();

        await hostCts.CancelAsync();
        try
        {
            await hostTask;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
