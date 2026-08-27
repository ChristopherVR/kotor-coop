using KotorCoop.Agent;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// ConnectionPhaseMapping.Resolve: the fix for the 2026-08-27 live finding
/// (docs/research/connect-flow.md) that ConnectionIndicatorState.Reconnecting was dead code —
/// SessionManager.Poll sets SessionPhase.Connecting the instant a client's live link drops for
/// ANY reason, including the ordinary give-up-and-re-Hello that follows a genuine outage, so a
/// naive phase-only switch could never tell "first handshake" from "reconnecting" apart.
/// </summary>
public class ConnectionPhaseMappingTests
{
    private static SessionStats Stats(bool connected, bool reconnecting, int peerCount = 0) => new()
    {
        Connected = connected,
        Reconnecting = reconnecting,
        PeerCount = peerCount,
    };

    [Fact]
    public void AFreshClientsFirstHandshakeIsConnectingNotReconnecting()
    {
        // Never yet connected: Reconnecting is false (CoopClient's field default), Connected is
        // false, phase is Connecting. Must NOT be confused with a real reconnect.
        Assert.Equal(
            ConnectionIndicatorState.Connecting,
            ConnectionPhaseMapping.Resolve(SessionPhase.Connecting, Stats(connected: false, reconnecting: false)));
    }

    [Fact]
    public void AClientGivingUpAndReHelloingIsReconnectingEvenThoughPhaseReadsConnecting()
    {
        // The exact live-observed shape: SessionManager.Poll already flipped phase to
        // Connecting (stats.Connected went false), but CoopClient.ForgetSession also set
        // Reconnecting true in the same step because this client HAD a session before.
        Assert.Equal(
            ConnectionIndicatorState.Reconnecting,
            ConnectionPhaseMapping.Resolve(SessionPhase.Connecting, Stats(connected: false, reconnecting: true)));
    }

    [Fact]
    public void AConnectedSessionReadsConnected()
    {
        Assert.Equal(
            ConnectionIndicatorState.Connected,
            ConnectionPhaseMapping.Resolve(SessionPhase.Connected, Stats(connected: true, reconnecting: false)));
    }

    [Fact]
    public void AHostIsNeverReconnectingForItself()
    {
        // CoopHost.GetStats() always sets Reconnecting = false; losing one peer only ever
        // moves the peer count, never the host's own indicator state.
        Assert.Equal(
            ConnectionIndicatorState.Connected,
            ConnectionPhaseMapping.Resolve(
                SessionPhase.Connected, Stats(connected: true, reconnecting: false, peerCount: 0)));
    }

    [Fact]
    public void AFailedPhaseWinsEvenIfSomehowStillMarkedReconnecting()
    {
        // A definitive rejection (e.g. the host's password changed mid-outage) must show
        // Failed, not linger as Reconnecting.
        Assert.Equal(
            ConnectionIndicatorState.Failed,
            ConnectionPhaseMapping.Resolve(SessionPhase.Failed, Stats(connected: false, reconnecting: true)));
    }

    [Fact]
    public void ADisconnectedPhaseWinsEvenIfSomehowStillMarkedReconnecting()
    {
        Assert.Equal(
            ConnectionIndicatorState.Disconnected,
            ConnectionPhaseMapping.Resolve(SessionPhase.Disconnected, Stats(connected: false, reconnecting: true)));
    }

    [Fact]
    public void AConnectedPhaseWithALiveLinkDropStillReadsReconnectingAsADefensiveFallback()
    {
        // Belt-and-suspenders: even if phase somehow lagged behind (still Connected) while the
        // live flag already says the link is down and not yet reconnecting, trust the live
        // flag rather than claim a stale Connected.
        Assert.Equal(
            ConnectionIndicatorState.Reconnecting,
            ConnectionPhaseMapping.Resolve(SessionPhase.Connected, Stats(connected: false, reconnecting: false)));
    }
}
