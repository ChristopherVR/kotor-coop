using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// SessionManager.PhaseChanged: raised wherever the manager's phase already changes today
/// (Connect, Disconnect, Poll, Fail), and only when the value actually differs. Purely
/// additive — SessionManagerTests already exercises the underlying lifecycle this rides on.
/// </summary>
public class SessionManagerPhaseChangedTests
{
    private sealed class StubBridge : IGameBridge
    {
        public string Description => "phase-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public void Dispose()
        {
        }
    }

    [Fact]
    public void FailRaisesPhaseChangedExactlyOnceForAGenuineTransition()
    {
        StubBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });

        List<(SessionPhase Previous, SessionPhase Current)> transitions = [];
        manager.PhaseChanged += (previous, current) => transitions.Add((previous, current));

        // No endpoint on a client connect attempt fails immediately (Idle -> Failed), without
        // ever reaching Connecting.
        manager.Connect(host: false, endpoint: null, new SessionSettings { PlayerName = "Bastila" });

        Assert.Equal(SessionPhase.Failed, manager.Phase);
        Assert.Equal([(SessionPhase.Idle, SessionPhase.Failed)], transitions);
    }

    [Fact]
    public void ARepeatedFailureAtTheSamePhaseDoesNotReRaisePhaseChanged()
    {
        StubBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });

        // First failure: Idle -> Failed, a genuine change.
        manager.Connect(host: false, endpoint: null, new SessionSettings { PlayerName = "Bastila" });
        Assert.Equal(SessionPhase.Failed, manager.Phase);

        List<(SessionPhase Previous, SessionPhase Current)> transitions = [];
        manager.PhaseChanged += (previous, current) => transitions.Add((previous, current));

        // Reconnect with nothing to reconnect to also lands on Failed - Failed -> Failed is
        // not a change, so no second event.
        manager.Reconnect();

        Assert.Equal(SessionPhase.Failed, manager.Phase);
        Assert.Empty(transitions);
    }
}
