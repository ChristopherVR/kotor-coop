namespace KotorCoop.Plugin;

/// <summary>
/// The pure destination arithmetic for <c>movenear</c> (see <c>CheatController.MoveNear</c>'s
/// own doc comment for the live fault this replaces): the verb used to snap its target to a
/// hardcoded point that only made sense in END_M01AA — measured live 2026-09-02 in tar_m10aa,
/// <c>movenear 0x46</c> teleported a live Vulkar to (29.0, 143.5, 0.0), off the map entirely.
/// The default destination is module-agnostic now: next to the LOCAL PLAYER, a fixed offset
/// along +X, deliberately with no facing math to keep it trivially predictable from the log.
/// An explicit <c>movenear &lt;id&gt; &lt;x&gt; &lt;y&gt; [z]</c> overrides it.
///
/// Separated from <c>CheatController.MoveNear</c> so the "which destination, and was it named
/// explicitly" decision can be unit-tested without a live engine — the engine reads themselves
/// (creature liveness, the actual snap and read-back) stay in the controller, which has no test
/// double for the engine.
/// </summary>
internal static class MoveNearDestinationRule
{
    /// <summary>How far along +X the default "near the player" destination sits. A fixed,
    /// trivially predictable offset — not a facing-relative point — so a log reader can check
    /// the math in their head.</summary>
    public const float DefaultOffsetX = 2.0f;

    /// <summary>
    /// Picks the destination and reports which form produced it.
    /// </summary>
    /// <param name="playerPosition">The local player's current position — the anchor for the
    /// default form.</param>
    /// <param name="explicitPosition">The request's own X/Y/Z fields
    /// (<see cref="SharedBlockView.CheatPosition"/>). Read as "no explicit destination named"
    /// when it is the exact origin — the same sentinel <c>tp</c>/<c>walkto</c> already use for
    /// a request that sent no coordinates, since a real explicit destination is never exactly
    /// (0,0,0) either (<see cref="Vec3.IsOrigin"/>).</param>
    /// <param name="isExplicit">True when <paramref name="explicitPosition"/> was used as-is;
    /// false when the default near-the-player point was computed instead.</param>
    public static Vec3 Resolve(Vec3 playerPosition, Vec3 explicitPosition, out bool isExplicit)
    {
        if (!Vec3.IsOrigin(explicitPosition))
        {
            isExplicit = true;
            return explicitPosition;
        }

        isExplicit = false;
        return new Vec3
        {
            X = playerPosition.X + DefaultOffsetX,
            Y = playerPosition.Y,
            Z = playerPosition.Z,
        };
    }
}
