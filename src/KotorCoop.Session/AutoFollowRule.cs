using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>What one <c>autofollow</c> tick should do.</summary>
public enum AutoFollowAction
{
    /// <summary>Close enough, or an order is still fresh — leave the player alone.</summary>
    None,

    /// <summary>Queue a real walk (walkmesh-guarded, crosses subarea triggers) to the destination.</summary>
    Walk,

    /// <summary>Left far behind (a spawn point after a load, a failed path): snap to the leader.</summary>
    Snap,

    /// <summary>Not followable right now — a different module, a load, a conversation. The reason says which.</summary>
    Hold,
}

public readonly record struct AutoFollowDecision(AutoFollowAction Action, Vector3f Destination, string Reason = "");

/// <summary>
/// The pure half of the <c>autofollow</c> console verb: given where the local player and the
/// leader are, whether to walk, snap, or wait. The dispatcher owns timing state and the cheat
/// channel; this owns the thresholds, so they can be tested without a game.
///
/// Module changes are deliberately NOT carried here: a cross-module warp mid-transition is the
/// retail engine's known crash (see the warp-crash lesson), and the door/galaxy-map relays
/// already move a follower under <c>follow_lead</c>. Auto-follow resumes once both players
/// report the same module.
/// </summary>
public static class AutoFollowRule
{
    /// <summary>Inside this the follower stands still — roughly a companion's own trailing gap.</summary>
    public const float StopDistance = 3.0f;

    /// <summary>Beyond this a walk is pointless (a load's spawn point, a stuck path): snap instead.</summary>
    public const float SnapDistance = 30f;

    /// <summary>How far short of the leader to aim, so the follower does not path into them.</summary>
    public const float TrailGap = 1.5f;

    /// <summary>Re-order once the leader has moved this far from the last destination.</summary>
    public const float RetargetDistance = 1.5f;

    /// <summary>Re-order a stale walk after this long even if the leader stood still (a walk the
    /// engine dropped — combat, a bump — leaves the follower parked otherwise).</summary>
    public static readonly TimeSpan StaleOrder = TimeSpan.FromSeconds(3);

    /// <summary>A snap is a big hammer; never two inside this window.</summary>
    public static readonly TimeSpan SnapCooldown = TimeSpan.FromSeconds(5);

    public static AutoFollowDecision Decide(
        PlayerState local,
        PlayerState leader,
        Vector3f? lastDestination,
        TimeSpan sinceLastOrder,
        TimeSpan sinceLastSnap)
    {
        if (local.Module.Length == 0 || leader.Module.Length == 0)
        {
            return Hold("waiting for a module to finish loading");
        }

        if (!string.Equals(local.Module, leader.Module, StringComparison.OrdinalIgnoreCase))
        {
            return Hold($"leader is in {leader.Module}, you are in {local.Module}");
        }

        if (!local.IsPlausible || !leader.IsPlausible)
        {
            return Hold("position not readable yet");
        }

        if (local.IsInDialogue || leader.IsInDialogue)
        {
            return Hold("in a conversation");
        }

        if (local.IsDowned || local.IsInMinigame)
        {
            return Hold(local.IsDowned ? "you are down" : "in a minigame");
        }

        Vector3f here = local.Position;
        Vector3f there = leader.Position;
        float distance = here.DistanceTo(there);

        if (distance <= StopDistance)
        {
            return new AutoFollowDecision(AutoFollowAction.None, there);
        }

        if (distance > SnapDistance && sinceLastSnap >= SnapCooldown)
        {
            return new AutoFollowDecision(AutoFollowAction.Snap, there, $"{distance:F0}m behind");
        }

        Vector3f destination = TrailPoint(here, there, distance);

        bool leaderMoved = lastDestination is not { } last || last.DistanceTo(destination) > RetargetDistance;
        if (leaderMoved || sinceLastOrder >= StaleOrder)
        {
            return new AutoFollowDecision(AutoFollowAction.Walk, destination);
        }

        return new AutoFollowDecision(AutoFollowAction.None, destination);
    }

    /// <summary>The point <see cref="TrailGap"/> short of the leader on the line back to the
    /// follower. Z is the leader's: they are standing on the walkmesh there.</summary>
    public static Vector3f TrailPoint(Vector3f here, Vector3f there, float distance)
    {
        if (distance <= TrailGap)
        {
            return there;
        }

        float t = TrailGap / distance;
        return new Vector3f(
            there.X + ((here.X - there.X) * t),
            there.Y + ((here.Y - there.Y) * t),
            there.Z);
    }

    private static AutoFollowDecision Hold(string reason) => new(AutoFollowAction.Hold, Vector3f.Zero, reason);
}
