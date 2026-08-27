using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure decode behind the persistent on-screen connection indicator (item 18,
/// roadmap-seamless.md): packed <c>SharedBlockView.ConnectStatus</c> uint -&gt; display text,
/// and the redraw decision. No SharedBlockView, no engine pointers, no session — see
/// <c>ConnectionIndicatorRule</c>'s own class comment for why the plugin cannot reference
/// <c>KotorCoop.Agent.ConnectionSnapshot</c> and duplicates its bit layout by value instead.
/// </summary>
public class ConnectionIndicatorRuleTests
{
    private static uint Pack(uint state, int peerCount) => state | ((uint)peerCount << 8);

    // ---- Text -----------------------------------------------------------------------

    [Fact]
    public void IdleShowsNothing()
    {
        Assert.Null(ConnectionIndicatorRule.Text(Pack(SharedBlock.ConnectStatusIdle, 0)));
    }

    [Fact]
    public void ConnectingShowsTheConnectingLine()
    {
        Assert.Equal(
            "co-op: connecting...",
            ConnectionIndicatorRule.Text(Pack(SharedBlock.ConnectStatusConnecting, 0)));
    }

    [Fact]
    public void FailedShowsTheFailedLine()
    {
        Assert.Equal(
            "co-op: connection failed",
            ConnectionIndicatorRule.Text(Pack(SharedBlock.ConnectStatusFailed, 0)));
    }

    [Fact]
    public void ReconnectingShowsTheReconnectingLine()
    {
        Assert.Equal(
            "co-op: reconnecting...",
            ConnectionIndicatorRule.Text(Pack(SharedBlock.ConnectStatusReconnecting, 2)));
    }

    [Theory]
    [InlineData(0, "co-op: connected, alone")]
    [InlineData(1, "co-op: connected, 1 player")]
    [InlineData(2, "co-op: connected, 2 players")]
    [InlineData(7, "co-op: connected, 7 players")]
    public void ConnectedShowsThePeerCount(int peerCount, string expected)
    {
        Assert.Equal(expected, ConnectionIndicatorRule.Text(Pack(SharedBlock.ConnectStatusConnected, peerCount)));
    }

    [Fact]
    public void AnUndefinedStateByteShowsNothingRatherThanGarbage()
    {
        Assert.Null(ConnectionIndicatorRule.Text(Pack(5, 3)));
    }

    [Fact]
    public void TheWordingMatchesTheChatToastExactlyForEveryState()
    {
        // The persistent HUD line and the fading chat/console toast
        // (KotorCoop.Agent.ConnectionStatusAnnouncer.Line) must never disagree about what the
        // connection currently looks like.
        Assert.Equal(
            KotorCoop.Agent.ConnectionStatusAnnouncer.Line(
                new KotorCoop.Agent.ConnectionSnapshot(KotorCoop.Agent.ConnectionIndicatorState.Connected, 3)),
            ConnectionIndicatorRule.Text(Pack(SharedBlock.ConnectStatusConnected, 3)));

        Assert.Equal(
            KotorCoop.Agent.ConnectionStatusAnnouncer.Line(
                new KotorCoop.Agent.ConnectionSnapshot(KotorCoop.Agent.ConnectionIndicatorState.Reconnecting, 0)),
            ConnectionIndicatorRule.Text(Pack(SharedBlock.ConnectStatusReconnecting, 0)));

        Assert.Equal(
            KotorCoop.Agent.ConnectionStatusAnnouncer.Line(
                new KotorCoop.Agent.ConnectionSnapshot(KotorCoop.Agent.ConnectionIndicatorState.Failed, 0)),
            ConnectionIndicatorRule.Text(Pack(SharedBlock.ConnectStatusFailed, 0)));
    }

    // ---- ShouldRedraw -----------------------------------------------------------------

    [Fact]
    public void UnchangedTextDoesNotNeedARedraw()
    {
        Assert.False(ConnectionIndicatorRule.ShouldRedraw("co-op: connected, alone", "co-op: connected, alone"));
    }

    [Fact]
    public void ChangedTextNeedsARedraw()
    {
        Assert.True(ConnectionIndicatorRule.ShouldRedraw("co-op: connected, 1 player", "co-op: connected, 2 players"));
    }

    [Fact]
    public void GoingFromNoLineToALineNeedsARedraw()
    {
        Assert.True(ConnectionIndicatorRule.ShouldRedraw(null, "co-op: connecting..."));
    }

    [Fact]
    public void GoingFromALineToNoLineNeedsARedraw()
    {
        Assert.True(ConnectionIndicatorRule.ShouldRedraw("co-op: connection failed", null));
    }

    [Fact]
    public void BothNullNeedsNoRedraw()
    {
        Assert.False(ConnectionIndicatorRule.ShouldRedraw(null, null));
    }
}
