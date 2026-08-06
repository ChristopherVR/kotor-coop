namespace KotorCoop.Plugin;

/// <summary>
/// The measure-smooth-decide half of driving a creature from a position stream, shared by
/// the enemy and party presence controllers (PhantomController carries its own tuned copy;
/// fold it in only with a live check).
///
/// Three jobs per tracked slot, all learned the hard way on phantoms
/// (docs/research/realtime-creature-movement.md, Handoff.md §0i):
///
/// - SPEED is measured from consumed displacements, update-to-update. The stream changes
///   per network tick while this runs per frame; dividing a tick's displacement by a
///   frame's dt read as double speed and the legs churned.
/// - The SHOWN position chases the received one with a short time constant, so discrete
///   arrivals become continuous motion. A gap past <see cref="SnapGapSquared"/> is a
///   teleport and cuts instead of gliding.
/// - MOVING is decided with hysteresis, not a threshold on the speed estimate: the engine
///   only pushes an animation on change, so a flickering decision mostly lands as idle and
///   the body glides with no run cycle.
/// </summary>
internal sealed class MotionSmoother(int capacity, float smoothingTauSeconds)
{
    /// <summary>Movement below this between updates is jitter, not walking.</summary>
    private const float MovementEpsilon = 0.02f;

    /// <summary>How long the stream must be still before the gait drops to idle. Longer
    /// than any plausible gap between updates, so a late packet cannot look like a stop.</summary>
    private const double StillnessSeconds = 0.35;

    /// <summary>Past this the body did not walk, it was moved — cut instead of gliding.</summary>
    private const float SnapGapSquared = 8f * 8f;

    private readonly Vec3[] _lastGoal = new Vec3[capacity];
    private readonly float[] _speed = new float[capacity];
    private readonly double[] _speedFromTime = new double[capacity];
    private readonly Vec3[] _shown = new Vec3[capacity];
    private readonly double[] _shownAtTime = new double[capacity];
    private readonly bool[] _smoothing = new bool[capacity];
    private readonly double[] _lastMovedAt = new double[capacity];

    public void Reset(int slot)
    {
        _lastGoal[slot] = default;
        _speed[slot] = 0f;
        _speedFromTime[slot] = 0;
        _shown[slot] = default;
        _shownAtTime[slot] = 0;
        _smoothing[slot] = false;
        _lastMovedAt[slot] = 0;
    }

    public (Vec3 Shown, float Speed, bool Moving) Step(int slot, Vec3 target, double now)
    {
        float mdx = target.X - _lastGoal[slot].X;
        float mdy = target.Y - _lastGoal[slot].Y;
        float moved = MathF.Sqrt((mdx * mdx) + (mdy * mdy));

        if (moved > MovementEpsilon)
        {
            if (_speedFromTime[slot] > 0)
            {
                float dt = (float)(now - _speedFromTime[slot]);
                if (dt > 0.001f)
                {
                    float instant = MathF.Min(moved / dt, 12f);
                    _speed[slot] = _speed[slot] <= 0f
                        ? instant
                        : (_speed[slot] * 0.7f) + (instant * 0.3f);
                }
            }

            _speedFromTime[slot] = now;
            _lastMovedAt[slot] = now;
        }

        Vec3 shown = _shown[slot];
        float jdx = target.X - shown.X;
        float jdy = target.Y - shown.Y;
        float gapSquared = (jdx * jdx) + (jdy * jdy);

        if (!_smoothing[slot] || gapSquared > SnapGapSquared)
        {
            _smoothing[slot] = true;
            shown = target;
        }
        else
        {
            // Frame-rate independent: the same fraction of the gap closes per unit of
            // time whatever the step rate happens to be.
            float dt = (float)Math.Clamp(now - _shownAtTime[slot], 0.0, 0.25);
            float alpha = smoothingTauSeconds <= 0f ? 1f : 1f - MathF.Exp(-dt / smoothingTauSeconds);

            shown.X += jdx * alpha;
            shown.Y += jdy * alpha;
            shown.Z = target.Z;
        }

        _shown[slot] = shown;
        _shownAtTime[slot] = now;
        _lastGoal[slot] = target;

        bool moving = _lastMovedAt[slot] > 0 && (now - _lastMovedAt[slot]) < StillnessSeconds;
        return (shown, _speed[slot], moving);
    }
}
