using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// <c>autofollow</c>: keeps the local player walking after a peer (by default the first one —
/// the host, on a joiner). Built as a live-test driver: one instance is played by hand, the
/// other trails it, and the session runs until something breaks.
///
/// Every order rides the existing staging-cheat channel (<c>walkto</c>'s walkmesh-guarded
/// <c>ActionMoveToLocation</c>, <c>tp</c>'s snap when left far behind), filed quiet so the
/// console is not flooded. An order is only filed when the single cheat slot is idle — a
/// follow walk must never queue behind (or ahead of) a player's own command. The thresholds
/// live in <see cref="AutoFollowRule"/>.
/// </summary>
public sealed partial class CoopCommandDispatcher
{
    /// <summary>Who to follow: a name, "#n", or empty for "the first peer". Null = off.</summary>
    private string? _followTarget;

    private Vector3f? _followLastDestination;
    private DateTime _followLastOrder = DateTime.MinValue;
    private DateTime _followLastSnap = DateTime.MinValue;

    /// <summary>The last hold reason put on screen, so a hold is announced once, not per tick.</summary>
    private string _followHoldReason = string.Empty;

    /// <summary>Set when the plugin refused a trail-point walk (off the walkmesh); the next
    /// order aims at the leader's own feet, which are on it by definition.</summary>
    private bool _followAimAtLeader;

    private void AutoFollowCommand(string? argument)
    {
        string arg = argument?.Trim() ?? string.Empty;

        if (arg.Equals("off", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            if (_followTarget is null)
            {
                Reply("autofollow is already off");
                return;
            }

            _followTarget = null;
            Reply("autofollow off");
            Log?.Invoke("autofollow: off");
            return;
        }

        if (arg.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            Reply(_followTarget is null
                ? "autofollow is off"
                : $"autofollow on ({FollowTargetLabel()}){(_followHoldReason.Length > 0 ? " - holding: " + _followHoldReason : string.Empty)}");
            return;
        }

        if (manager.Current is not { } session)
        {
            Reply("not connected - nobody to follow");
            return;
        }

        IReadOnlyList<RemotePeer> peers = session.GetRemotePeers();
        RemotePeer? leader = arg.Length == 0
            ? (peers.Count > 0 ? peers[0] : null)
            : FindPeer(peers, arg);

        if (leader is null)
        {
            Reply(arg.Length == 0
                ? "nobody else is here to follow"
                : $"no player called '{arg}' - run `who` to see who is here");
            return;
        }

        // Pin the resolved name rather than "#1": the listing order can change on a rejoin.
        _followTarget = leader.Name;
        _followLastDestination = null;
        _followLastOrder = DateTime.MinValue;
        _followHoldReason = string.Empty;
        _followAimAtLeader = false;
        Reply($"autofollow on - following {leader.Name} (`autofollow off` to stop)");
        Log?.Invoke($"autofollow: following {leader.Name}");
    }

    private string FollowTargetLabel() => _followTarget is { Length: > 0 } name ? name : "the host";

    /// <summary>Once per dispatcher tick (150 ms). Cheap when off.</summary>
    private void PumpAutoFollow()
    {
        if (_followTarget is null)
        {
            return;
        }

        if (manager.Current is not { } session)
        {
            FollowHold("not connected");
            return;
        }

        if (FindPeer(session.GetRemotePeers(), _followTarget) is not { } leader)
        {
            FollowHold($"{FollowTargetLabel()} is not in the session");
            return;
        }

        if (!bridge.TryReadLocalState(out PlayerState local))
        {
            FollowHold("no player loaded");
            return;
        }

        // The slot holds one request at a time; a follow order waits its turn rather than
        // queueing, so a typed command is never stuck behind a stream of walks.
        if (_pendingCheatSerial != 0)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        AutoFollowDecision decision = AutoFollowRule.Decide(
            local,
            leader.State,
            _followLastDestination,
            now - _followLastOrder,
            now - _followLastSnap);

        switch (decision.Action)
        {
            case AutoFollowAction.Hold:
                FollowHold(decision.Reason);
                return;

            case AutoFollowAction.None:
                FollowResume();
                return;

            case AutoFollowAction.Snap:
                FollowResume();
                _followLastSnap = now;
                _followLastOrder = now;
                _followLastDestination = decision.Destination;
                Log?.Invoke($"autofollow: snapping to {leader.Name} ({decision.Reason})");
                SendCheat(new CheatRequest(
                    CheatKinds.Teleport,
                    X: decision.Destination.X,
                    Y: decision.Destination.Y,
                    Z: decision.Destination.Z), quiet: true);
                return;

            case AutoFollowAction.Walk:
                FollowResume();
                Vector3f aim = _followAimAtLeader ? leader.State.Position : decision.Destination;
                _followLastOrder = now;
                _followLastDestination = decision.Destination;
                SendCheat(
                    new CheatRequest(CheatKinds.WalkTo, X: aim.X, Y: aim.Y, Z: aim.Z),
                    onApplied: () => _followAimAtLeader = false,
                    onRefused: () => _followAimAtLeader = !_followAimAtLeader,
                    quiet: true);
                return;
        }
    }

    private void FollowHold(string reason)
    {
        if (reason == _followHoldReason)
        {
            return;
        }

        _followHoldReason = reason;
        Reply($"autofollow holding: {reason}");
        Log?.Invoke($"autofollow: holding ({reason})");
    }

    private void FollowResume()
    {
        if (_followHoldReason.Length == 0)
        {
            return;
        }

        _followHoldReason = string.Empty;
        _followLastDestination = null;
        Reply($"autofollow resumed - following {FollowTargetLabel()}");
        Log?.Invoke("autofollow: resumed");
    }
}
