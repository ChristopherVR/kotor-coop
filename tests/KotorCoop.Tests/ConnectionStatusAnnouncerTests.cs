using KotorCoop.Agent;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The persistent connection indicator (item 18, roadmap-seamless.md). Before this the ONLY
/// way to see connection state in-game was typing <c>status</c> at the <c>~</c> console.
/// These pin the pure "what changed -&gt; which line" decision and the on-change diffing —
/// no tick loop, no session, no clock.
/// </summary>
public class ConnectionStatusAnnouncerTests
{
    // ---- the pure function: Line ------------------------------------------------

    [Theory]
    [InlineData(ConnectionIndicatorState.Disconnected, 0, "co-op: disconnected")]
    [InlineData(ConnectionIndicatorState.Connecting, 0, "co-op: connecting...")]
    [InlineData(ConnectionIndicatorState.Reconnecting, 0, "co-op: reconnecting...")]
    [InlineData(ConnectionIndicatorState.Failed, 0, "co-op: connection failed")]
    [InlineData(ConnectionIndicatorState.Connected, 0, "co-op: connected, alone")]
    [InlineData(ConnectionIndicatorState.Connected, 1, "co-op: connected, 1 player")]
    [InlineData(ConnectionIndicatorState.Connected, 2, "co-op: connected, 2 players")]
    [InlineData(ConnectionIndicatorState.Connected, 7, "co-op: connected, 7 players")]
    public void LineMatchesTheStateAndPeerCount(ConnectionIndicatorState state, int peerCount, string expected)
    {
        Assert.Equal(expected, ConnectionStatusAnnouncer.Line(new ConnectionSnapshot(state, peerCount)));
    }

    // ---- diffing: Observe ---------------------------------------------------------

    [Fact]
    public void AStableSnapshotIsAnnouncedOnceOnly()
    {
        ConnectionStatusAnnouncer announcer = new();
        List<string> lines = [];

        ConnectionSnapshot snapshot = new(ConnectionIndicatorState.Connected, 1);
        announcer.Observe(snapshot, lines.Add);
        announcer.Observe(snapshot, lines.Add);
        announcer.Observe(snapshot, lines.Add);

        Assert.Equal(["co-op: connected, 1 player"], lines);
    }

    [Fact]
    public void APeerJoiningWhileConnectedIsAChangeEvenThoughTheStateByteDidNotMove()
    {
        ConnectionStatusAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(new ConnectionSnapshot(ConnectionIndicatorState.Connected, 1), lines.Add);
        announcer.Observe(new ConnectionSnapshot(ConnectionIndicatorState.Connected, 2), lines.Add);

        Assert.Equal(["co-op: connected, 1 player", "co-op: connected, 2 players"], lines);
    }

    [Fact]
    public void ADropAndRecoveryAnnouncesBothEdges()
    {
        ConnectionStatusAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(new ConnectionSnapshot(ConnectionIndicatorState.Connected, 1), lines.Add);
        announcer.Observe(new ConnectionSnapshot(ConnectionIndicatorState.Reconnecting, 1), lines.Add);
        announcer.Observe(new ConnectionSnapshot(ConnectionIndicatorState.Connected, 1), lines.Add);

        Assert.Equal(
            [
                "co-op: connected, 1 player",
                "co-op: reconnecting...",
                "co-op: connected, 1 player",
            ],
            lines);
    }

    [Fact]
    public void NoSessionAnnouncesNothingByItself()
    {
        // Null is "nothing to show", not "disconnected" — the caller decides whether a torn
        // down session is worth its own line (it is: AnnounceConnectionStatus posts
        // Disconnected explicitly before resetting). Observe(null, ...) alone must not invent
        // a line for a session that never existed at all (e.g. before the first connect).
        ConnectionStatusAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(null, lines.Add);

        Assert.Empty(lines);
    }

    [Fact]
    public void ResetForgetsTheLastLineSoAFreshSessionAnnouncesItsOwnState()
    {
        ConnectionStatusAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(new ConnectionSnapshot(ConnectionIndicatorState.Connected, 1), lines.Add);
        announcer.Reset();
        announcer.Observe(new ConnectionSnapshot(ConnectionIndicatorState.Connected, 1), lines.Add);

        // Without Reset the second Observe would be a no-op (identical to the last one seen).
        Assert.Equal(["co-op: connected, 1 player", "co-op: connected, 1 player"], lines);
    }

    // ---- the wire packing ---------------------------------------------------------

    [Fact]
    public void PackPutsStateInTheLowByteAndPeerCountInTheNext()
    {
        ConnectionSnapshot snapshot = new(ConnectionIndicatorState.Connected, 3);

        uint packed = snapshot.Pack();

        Assert.Equal(2u, packed & 0xFF);
        Assert.Equal(3u, (packed >> 8) & 0xFF);
    }

    [Fact]
    public void PackReproducesTheOriginalFourSentinelsWhenAlone()
    {
        // The four values that predate this pass (Idle/Connecting/Connected/Failed) are all
        // entered with no peer to count, so packing must reproduce the bare original uint
        // exactly - anything that ever compared the whole field against one of them still
        // works.
        Assert.Equal(0u, new ConnectionSnapshot(ConnectionIndicatorState.Disconnected, 0).Pack());
        Assert.Equal(1u, new ConnectionSnapshot(ConnectionIndicatorState.Connecting, 0).Pack());
        Assert.Equal(2u, new ConnectionSnapshot(ConnectionIndicatorState.Connected, 0).Pack());
        Assert.Equal(3u, new ConnectionSnapshot(ConnectionIndicatorState.Failed, 0).Pack());
    }

    [Fact]
    public void PackClampsAnOutOfRangePeerCountToAByte()
    {
        ConnectionSnapshot snapshot = new(ConnectionIndicatorState.Connected, 999);

        uint packed = snapshot.Pack();

        Assert.Equal(255u, (packed >> 8) & 0xFF);
    }

    // ---- Pack / Unpack round trip (item 18's on-screen indicator, 2026-08-27) -------------

    [Theory]
    [InlineData(ConnectionIndicatorState.Disconnected, 0)]
    [InlineData(ConnectionIndicatorState.Connecting, 0)]
    [InlineData(ConnectionIndicatorState.Connected, 0)]
    [InlineData(ConnectionIndicatorState.Connected, 1)]
    [InlineData(ConnectionIndicatorState.Connected, 254)]
    [InlineData(ConnectionIndicatorState.Failed, 0)]
    [InlineData(ConnectionIndicatorState.Reconnecting, 3)]
    public void UnpackReversesPackForEveryState(ConnectionIndicatorState state, int peerCount)
    {
        ConnectionSnapshot original = new(state, peerCount);

        ConnectionSnapshot roundTripped = ConnectionSnapshot.Unpack(original.Pack());

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void UnpackClampsAnOutOfRangePeerCountTheSameWayPackDid()
    {
        ConnectionSnapshot original = new(ConnectionIndicatorState.Connected, 999);

        ConnectionSnapshot roundTripped = ConnectionSnapshot.Unpack(original.Pack());

        Assert.Equal(255, roundTripped.PeerCount);
    }

    [Fact]
    public void UnpackOfAnUndefinedStateByteFallsBackToDisconnected()
    {
        // Byte value 5 has never been assigned to a ConnectionIndicatorState. A block this
        // same process wrote should never carry it, but Unpack must not throw on one.
        ConnectionSnapshot decoded = ConnectionSnapshot.Unpack(5u);

        Assert.Equal(ConnectionIndicatorState.Disconnected, decoded.State);
    }
}
