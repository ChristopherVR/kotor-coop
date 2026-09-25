namespace KotorCoop.Plugin;

/// <summary>
/// The outcome of validating a move destination — shared by <see cref="Odyssey.TryValidateMoveDestination"/>
/// (which adds the two engine calls: <c>CSWSObject::GetArea</c> then <c>CSWSArea::PositionWalkable</c>)
/// and <see cref="WalkmeshGuardRule"/> (the pure sanity part, below). See docs/research/walkmesh-guard.md.
/// </summary>
internal enum MoveRejectReason
{
    /// <summary>Accepted; the move may be issued.</summary>
    None = 0,

    /// <summary>NaN/infinite, or magnitude beyond <see cref="Vec3.MaxWorldCoordinate"/> — a bad
    /// read or a stale cross-world value, never a real destination.</summary>
    NotPlausible = 1,

    /// <summary>The exact zero vector — the same poison sentinel <c>Teleport</c>/<c>WalkTo</c>
    /// already refused ad hoc; a resolved-but-never-placed object, a stale/zeroed publish, or
    /// the area origin nobody actually stands on.</summary>
    Origin = 2,

    /// <summary>The mover does not resolve to a live object, or its own
    /// <c>CSWSObject::GetArea()</c> returned 0 (no loaded world to validate against —
    /// mid-transition).</summary>
    NoArea = 3,

    /// <summary>The engine's own <c>CSWSArea::PositionWalkable</c> said no: inside a room's
    /// bounding box but off the real walkmesh triangle (the <c>walkto 187 100</c> shape), or
    /// outside every room entirely.</summary>
    NotWalkable = 4,
}

/// <summary>
/// The pure half of the walkmesh guard (docs/research/walkmesh-guard.md): the sanity checks
/// that need no running engine — NaN/infinity/out-of-world magnitude and the exact-origin
/// poison sentinel. Split out exactly the way <see cref="MoveNearDestinationRule"/> and every
/// other <c>*Rule</c> class in this project is, so this half can be unit-tested without a live
/// game process; the two engine calls (<c>GetArea</c>, <c>PositionWalkable</c>) that decide
/// <see cref="MoveRejectReason.NoArea"/>/<see cref="MoveRejectReason.NotWalkable"/> stay in
/// <see cref="Odyssey.TryValidateMoveDestination"/>, which has no test double for the engine.
/// </summary>
internal static class WalkmeshGuardRule
{
    /// <summary>
    /// Classifies a destination using only arithmetic on the three floats — no pointer, no
    /// engine call. Returns <see cref="MoveRejectReason.None"/> when both pure checks pass
    /// (the caller still owes the walkability check itself); otherwise the first reason that
    /// failed, checked in this order: plausibility (NaN/inf/magnitude) before origin, matching
    /// <see cref="Odyssey.TryValidateMoveDestination"/>'s own order.
    /// </summary>
    public static MoveRejectReason ClassifyDestination(Vec3 destination)
    {
        if (!Vec3.IsWorldPlausible(destination))
        {
            return MoveRejectReason.NotPlausible;
        }

        if (Vec3.IsOrigin(destination))
        {
            return MoveRejectReason.Origin;
        }

        return MoveRejectReason.None;
    }

    /// <summary>
    /// True when a REMOTE (peer-published) position is safe to drive a body toward THIS frame
    /// — the source-side half of the guard (docs/research/walkmesh-guard.md §3): a joiner's
    /// phantom of the host crashed on danm14aa because the host's own agent republished
    /// (0,0,0) while the host's module was still loading, and PhantomController's per-frame
    /// drive loop (unlike its own spawn path, <c>PhantomController.Lifecycle.IsPlausiblePosition</c>)
    /// had nothing screening <c>remote.Position</c> before feeding it to SnapTo/MoveToLocation/
    /// DriveBody every frame. Deliberately the SAME two pure checks as
    /// <see cref="ClassifyDestination"/> (no walkability call: this runs on a hot per-frame
    /// path for potentially several driven bodies, and the engine call is reserved for the
    /// low-frequency choke points — MoveToLocation/SnapTo — that actually risk
    /// TestDirectLine's crash).
    /// </summary>
    public static bool IsRemotePositionDriveable(Vec3 remotePosition) =>
        ClassifyDestination(remotePosition) == MoveRejectReason.None;
}
