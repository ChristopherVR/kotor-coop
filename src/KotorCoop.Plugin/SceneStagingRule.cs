namespace KotorCoop.Plugin;

/// <summary>
/// Where a stand-in stands during a scripted scene. A cutscene's script jumps "the player" to its
/// waypoint in both games, so the peer's published position lands on the local player's own
/// spot; the overlap push in <c>PhantomController.DriveDirect</c> then moved the stand-in 1.2 m
/// along world +X, wherever that happened to be, and in the Star Forge ceremony (STUNT_55a,
/// 2026-10-10) neither game showed the other player at all. While a scene runs, the stand-in
/// stands beside the scene's anchor (the local player, or the speaker when the player waits
/// off-stage) on the first candidate spot that is walkable and clear of every other body,
/// facing the way the anchor faces. Pure, so the tests compile it in.
/// </summary>
internal static class SceneStagingRule
{
    /// <summary>A peer this close to the local player during a scene is standing on the same
    /// scripted mark, not somewhere of their own choosing.</summary>
    public const float SameMarkRadius = 2.5f;

    /// <summary>How far to the side the stand-in is placed: a body's width plus a gap, so the
    /// two read as standing together rather than overlapping.</summary>
    public const float SideOffset = 1.4f;

    /// <summary>The least room a candidate spot needs from every other body. In the ceremony
    /// the spot beside Bastila was 0.7 m from a soldier and the stand-in vanished inside him.</summary>
    public const float Clearance = 1.0f;

    /// <summary>Candidates, in preference order, as (sideways, forward) multiples of
    /// <see cref="SideOffset"/> relative to the anchor's facing: back-left, back-right, left,
    /// right, front-left, front-right. Behind first: a scene's two-shot faces the player and
    /// whoever they talk to, and in the Star Forge ceremony the player's right was Bastila's own
    /// spot and the left was behind the parapet.</summary>
    private static readonly (float Side, float Forward)[] Candidates =
    [
        (0.7f, -0.8f), (-0.7f, -0.8f), (1f, 0f), (-1f, 0f), (0.7f, 0.8f), (-0.7f, 0.8f),
    ];

    public static int CandidateCount => Candidates.Length;

    /// <summary>
    /// True when the stand-in should be staged: a conversation or cutscene is running here and
    /// the peer is on (or next to) the local player's mark.
    /// </summary>
    public static bool ShouldStage(bool sceneActive, Vec3 peer, Vec3 local)
    {
        if (!sceneActive)
        {
            return false;
        }

        float dx = peer.X - local.X;
        float dy = peer.Y - local.Y;
        return (dx * dx) + (dy * dy) < SameMarkRadius * SameMarkRadius;
    }

    /// <summary>
    /// Candidate <paramref name="index"/>'s spot beside <paramref name="anchor"/>, same height,
    /// and the anchor's facing. "Left" is counter-clockwise from the way the anchor faces. A
    /// degenerate orientation (zero length) faces +X.
    /// </summary>
    public static (Vec3 Position, float Facing) Spot(Vec3 anchor, Vec3 orientation, int index)
    {
        (float side, float forward) = Candidates[Math.Clamp(index, 0, Candidates.Length - 1)];
        float length = MathF.Sqrt((orientation.X * orientation.X) + (orientation.Y * orientation.Y));
        float fx = length > 0.0001f ? orientation.X / length : 1f;
        float fy = length > 0.0001f ? orientation.Y / length : 0f;

        Vec3 spot = new()
        {
            X = anchor.X + (((fx * forward) - (fy * side)) * SideOffset),
            Y = anchor.Y + (((fy * forward) + (fx * side)) * SideOffset),
            Z = anchor.Z,
        };

        return (spot, MathF.Atan2(fy, fx));
    }

    /// <summary>True when <paramref name="spot"/> is at least <see cref="Clearance"/> from every
    /// body in <paramref name="others"/>.</summary>
    public static bool IsClear(Vec3 spot, ReadOnlySpan<Vec3> others)
    {
        foreach (Vec3 other in others)
        {
            float dx = other.X - spot.X;
            float dy = other.Y - spot.Y;
            if ((dx * dx) + (dy * dy) < Clearance * Clearance)
            {
                return false;
            }
        }

        return true;
    }
}
