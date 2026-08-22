namespace KotorCoop.Plugin;

/// <summary>
/// The gait DECISION only: given a body's measured planar speed and whether it counts as
/// moving at all (that half is decided upstream, WITH HYSTERESIS — see
/// <see cref="MotionSmoother"/> and <see cref="PhantomController"/>'s own copy of the same
/// math — a raw threshold on an instantaneous speed estimate flickers several times a
/// second, and because the engine only pushes an animation to the render side ON CHANGE, a
/// flickering decision mostly arrives as idle: the body glides with no run cycle at all,
/// which is the exact defect this rule exists to keep fixed), which looping animation should
/// play, and — given what is already asserted — whether it needs to be (re)issued this step.
///
/// Extracted from <c>Odyssey.DrivePhantom</c>/<c>Odyssey.DriveEnemy</c>, which used to inline
/// <c>speed &gt; 2.0f</c> twice (once per caller) with no test of either copy, and from
/// <c>Odyssey.GaitChanged</c>'s equality check, so the threshold and the edge-trigger rule
/// are each a single named, unit-tested decision instead of logic only ever exercised live.
/// The engine calls themselves (<c>SetAnimation</c> through the animbase vtable slot, the
/// twin's <c>ClientServerSyncHandler_Animation</c> at vtable +0x134 — see
/// docs/research/realtime-creature-movement.md §9 and the memory note on vtable-slot
/// dispatch) stay exactly where they were, in <c>Odyssey.DriveBody</c>: this type has no
/// engine dependency and no unsafe code, so it links into the test project the same way
/// every other <c>*Rule</c> file in this project does.
/// </summary>
internal static class GaitRule
{
    /// <summary>CSWSObject +0xD4 values (docs/research/realtime-creature-movement.md §10).</summary>
    public const int AnimIdle = 0x2710;

    public const int AnimWalk = 0x2712;
    public const int AnimRun = 0x2714;

    /// <summary>
    /// Planar speed (units/second) above which the gait is a run rather than a walk.
    /// Appearance row 114 (the PC's row, and every phantom's blueprint) walks at 1.6 u/s and
    /// runs at 3.96 u/s (<c>RunSpeedReference</c>/<c>WalkSpeedReference</c> in Odyssey.cs), so
    /// 2.0 sits cleanly between the two: a walking peer never crosses it, a running one
    /// always does, and the threshold has almost a full unit of margin on either side against
    /// measurement noise.
    /// </summary>
    public const float RunSpeedThreshold = 2.0f;

    /// <summary>True when <paramref name="speed"/> counts as a run rather than a walk.
    /// Exactly at the threshold is a walk — this is a run cycle, not "at least a walk",
    /// so the boundary should not require crossing it to still count.</summary>
    public static bool IsRun(float speed) => speed > RunSpeedThreshold;

    /// <summary>
    /// The looping animation id a body with this speed, moving or not, should be playing.
    /// <paramref name="moving"/> is the hysteresis-gated signal, not a raw threshold on
    /// <paramref name="speed"/> — see the type remarks for why the distinction matters. A
    /// non-finite or negative speed is treated as a walk while moving: neither is a value the
    /// measured stream should ever produce, and refusing to answer would be worse than a
    /// slightly wrong gait for one frame.
    /// </summary>
    public static int SelectAnimation(float speed, bool moving) =>
        !moving ? AnimIdle : IsRun(speed) ? AnimRun : AnimWalk;

    /// <summary>
    /// Whether the loop animation needs to be (re)issued to the engine this step: only when
    /// the desired id actually differs from what was last asserted on this body. Calling the
    /// engine's animation-sync entry every frame regardless of change is itself a documented
    /// failure mode (a one-shot playing on the body — a weapon flourish, a stim — gets
    /// visibly cancelled mid-play, see <c>Odyssey.DriveBody</c>'s remarks on twin+0x48) as
    /// well as needless work on the game's frame thread. Edge-triggered, not per-frame.
    /// </summary>
    public static bool ShouldReissue(int lastAsserted, int want) => lastAsserted != want;
}
