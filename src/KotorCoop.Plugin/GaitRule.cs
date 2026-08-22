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
///
/// ROUND 68 (docs/research/phantom-locomotion.md §14): the walk/run split used to be a bare
/// <c>speed &gt; 2.0f</c> with no memory of the previous decision — round 65/67's own fix
/// notes (§13) flagged this as a plausible flicker source under measurement jitter, the same
/// failure shape "moving" itself was already hardened against (see
/// <see cref="MotionSmoother"/>'s remarks). It is now a two-threshold hysteresis band: entering
/// Run needs <see cref="RunEnterThreshold"/>, leaving it needs to drop under
/// <see cref="RunExitThreshold"/>, and a speed sampled anywhere between the two keeps whatever
/// gait was already showing. The low-speed side gets the same treatment: a "moving" body under
/// <see cref="IdleSpeedThreshold"/> only drops to idle after <see cref="IdleDwellSeconds"/> of
/// staying there, via <see cref="AdvanceBelowIdle"/>, so a one-sample dip near a stop-start
/// does not flash idle and back.
/// </summary>
internal static class GaitRule
{
    /// <summary>CSWSObject +0xD4 values (docs/research/realtime-creature-movement.md §10).</summary>
    public const int AnimIdle = 0x2710;

    public const int AnimWalk = 0x2712;
    public const int AnimRun = 0x2714;

    /// <summary>
    /// Planar speed (units/second) a WALKING body must exceed to become a RUN. Only reachable
    /// from Walk — a body already running does not need to reach this again, it only has to
    /// stay above <see cref="RunExitThreshold"/> (see <see cref="IsRun"/>).
    ///
    /// Appearance row 114 (the PC's row, and every phantom's blueprint) walks at 1.6 u/s and
    /// runs at 3.96 u/s (<c>RunSpeedReference</c>/<c>WalkSpeedReference</c> in Odyssey.cs), so
    /// 2.4 sits with clear margin above the walk speed and below the run speed.
    /// </summary>
    public const float RunEnterThreshold = 2.4f;

    /// <summary>
    /// Planar speed a RUNNING body must drop under to fall back to WALK. Only reachable from
    /// Run — a body already walking needs <see cref="RunEnterThreshold"/> to start running
    /// instead, not this.
    ///
    /// The dead band between this and <see cref="RunEnterThreshold"/> (1.6 .. 2.4) is the
    /// hysteresis: a speed measured anywhere inside it — including exactly at the old bare
    /// 2.0 u/s cutoff this replaces — keeps whichever gait was already showing, so
    /// measurement jitter that used to cross a single threshold several times a second
    /// (docs/research/phantom-locomotion.md §13's flagged risk) can no longer flip the gait
    /// every sample.
    /// </summary>
    public const float RunExitThreshold = 1.6f;

    /// <summary>
    /// Below this planar speed, a body the caller has already decided is "moving" (see
    /// <see cref="SelectAnimation"/>) is close enough to a dead stop that idle reads better
    /// than a walk cycle covering almost no ground — jostling in place at a dialogue trigger,
    /// or the last sliver of a MotionSmoother chase settling in. Gated behind
    /// <see cref="IdleDwellSeconds"/> rather than applied instantly, for the same reason the
    /// run/walk band has hysteresis: an instant drop would flash idle on a one-sample dip
    /// during a stop-start and flash back on the next sample.
    /// </summary>
    public const float IdleSpeedThreshold = 0.3f;

    /// <summary>
    /// How long the speed must stay continuously under <see cref="IdleSpeedThreshold"/>
    /// before <see cref="SelectAnimation"/> actually returns idle for a "moving" body. Short
    /// on purpose — long enough to absorb one bad sample, short enough that a genuine stop
    /// still reads as a stop almost immediately, well under the 0.35 s dwell
    /// <see cref="MotionSmoother"/> already uses to decide "moving" itself.
    /// </summary>
    public const float IdleDwellSeconds = 0.15f;

    /// <summary>True when <paramref name="speed"/> counts as a run rather than a walk, given
    /// whether the body was already classified as running. <paramref name="wasRunning"/>
    /// defaults to false — a caller with no memory of the previous decision (or none yet, for
    /// a body just starting to be driven) gets the same one-shot "> threshold" comparison the
    /// non-hysteresis version always had, using the enter threshold.</summary>
    public static bool IsRun(float speed, bool wasRunning = false) =>
        wasRunning ? speed >= RunExitThreshold : speed > RunEnterThreshold;

    /// <summary>
    /// The looping animation id a body with this speed, moving or not, should be playing.
    /// <paramref name="moving"/> is the hysteresis-gated signal, not a raw threshold on
    /// <paramref name="speed"/> — see the type remarks for why the distinction matters. A
    /// non-finite or negative speed is treated as a walk while moving: neither is a value the
    /// measured stream should ever produce, and refusing to answer would be worse than a
    /// slightly wrong gait for one frame.
    ///
    /// <paramref name="wasRunning"/> and <paramref name="belowIdleSeconds"/> both default to
    /// "no history yet" (false / 0), so an existing caller that only wants the moving/not-moving
    /// split keeps working unchanged; a caller that wants the full round-68 hysteresis passes
    /// its own per-body memory (see <see cref="IsRun"/> and <see cref="AdvanceBelowIdle"/>).
    /// </summary>
    public static int SelectAnimation(float speed, bool moving, bool wasRunning = false, float belowIdleSeconds = 0f)
    {
        if (!moving)
        {
            return AnimIdle;
        }

        if (speed < IdleSpeedThreshold && belowIdleSeconds >= IdleDwellSeconds)
        {
            return AnimIdle;
        }

        return IsRun(speed, wasRunning) ? AnimRun : AnimWalk;
    }

    /// <summary>
    /// Advances the "how long has this body been continuously under
    /// <see cref="IdleSpeedThreshold"/>" accumulator by one step: an instant recovery above
    /// the threshold resets it to zero, otherwise <paramref name="dt"/> (clamped to
    /// non-negative, so a caller's clock hiccup cannot wind it backwards) is added.
    ///
    /// Pure and clock-agnostic on purpose, so the dwell timer is unit-testable without a wall
    /// clock. The caller (<c>Odyssey.DriveBody</c>'s per-creature gait slots) supplies real
    /// frame dt and stores the returned value back per creature, then passes it into
    /// <see cref="SelectAnimation"/> as <c>belowIdleSeconds</c> next step.
    /// </summary>
    public static float AdvanceBelowIdle(float previousAccum, float speed, float dt) =>
        speed < IdleSpeedThreshold ? previousAccum + MathF.Max(dt, 0f) : 0f;

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
