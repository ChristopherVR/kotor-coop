namespace KotorCoop.Plugin;

/// <summary>
/// The decision half of fog-of-war sync (roadmap item 19), separated from
/// <c>MapRevealController</c> so the throttle can be unit-tested without the engine call it
/// guards — the same split <c>ControlReturnRule</c>, <c>SpawnDrainRule</c> and
/// <c>PartyFreezeRule</c> use.
///
/// The question it answers, once per remote slot per pass: has enough happened since the
/// last reveal at this slot to justify another engine call? Two independent gates, both
/// required — see docs/research/map-reveal.md for why <c>SetWorldPointExplored</c> is cheap
/// enough to call this often but not so cheap it should run at the raw hook rate:
///
///   * TIME — no more than a few times a second. The engine's own party fog reveal
///     (<c>CServerExoAppInternal::UpdateMapData</c>) runs every server tick; a peer's trail
///     does not need that, and a small cadence keeps this call off the hot path.
///   * DISTANCE — a peer standing still (talking, looting, waiting at a door) must not
///     re-fire the call every throttle interval forever; only a real step through the world
///     is worth another reveal.
/// </summary>
internal static class MapRevealRule
{
    /// <summary>Minimum world-unit displacement since the last reveal before another one is
    /// worth making. KOTOR's world unit is ~1 metre; 2 is a stride, not a shuffle.</summary>
    public const float MinMoveDistance = 2.0f;

    /// <summary>Minimum milliseconds between two reveals for the SAME peer slot — 4 Hz.</summary>
    public const int MinIntervalMs = 250;

    /// <summary>
    /// True if this peer's position should be revealed now. <paramref name="hasRevealedBefore"/>
    /// false always reveals (there is nothing to compare against, and a peer who just
    /// appeared in this module should show up on the map without waiting for a 2 m walk).
    /// </summary>
    public static bool ShouldReveal(
        bool hasRevealedBefore,
        long lastRevealedAtMs,
        long nowMs,
        Vec3 lastRevealedPosition,
        Vec3 currentPosition)
    {
        if (!hasRevealedBefore)
        {
            return true;
        }

        if (nowMs - lastRevealedAtMs < MinIntervalMs)
        {
            return false;
        }

        float dx = currentPosition.X - lastRevealedPosition.X;
        float dy = currentPosition.Y - lastRevealedPosition.Y;
        float dz = currentPosition.Z - lastRevealedPosition.Z;
        float distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);

        return distanceSquared >= MinMoveDistance * MinMoveDistance;
    }
}
