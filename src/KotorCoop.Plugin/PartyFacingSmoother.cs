using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// Smooths a party member's owner-facing angle on the same time constant as the mirrored
/// position. Angles wrap at 0/2*pi, so interpolation must follow the short arc or a companion
/// turning through north would spin almost a full revolution. Stationary callers can snap the
/// state with <see cref="Snap"/> to preserve the owner's exact combat/turn-in-place heading.
/// </summary>
internal sealed class PartyFacingSmoother(int capacity, float smoothingTauSeconds)
{
    private readonly float[] _shown = new float[capacity];
    private readonly double[] _shownAt = new double[capacity];
    private readonly bool[] _hasShown = new bool[capacity];

    public void Reset(int slot)
    {
        _shown[slot] = 0f;
        _shownAt[slot] = 0;
        _hasShown[slot] = false;
    }

    /// <summary>Stores the exact owner angle for a stationary or combat pose.</summary>
    public void Snap(int slot, float target, double now = 0)
    {
        if (!float.IsFinite(target))
        {
            return;
        }

        _shown[slot] = Angles.Normalize(target);
        _shownAt[slot] = now;
        _hasShown[slot] = true;
    }

    /// <summary>
    /// Advances one moving sample. Returns false for a nonfinite target so callers never hand
    /// NaN/infinity to the engine's orientation setter.
    /// </summary>
    public bool TryStep(int slot, float target, double now, out float facing)
    {
        if (!float.IsFinite(target))
        {
            facing = 0f;
            return false;
        }

        float normalized = Angles.Normalize(target);
        if (!_hasShown[slot] || smoothingTauSeconds <= 0f)
        {
            _shown[slot] = normalized;
            _shownAt[slot] = now;
            _hasShown[slot] = true;
            facing = normalized;
            return true;
        }

        float dt = (float)Math.Clamp(now - _shownAt[slot], 0.0, 0.25);
        float alpha = 1f - MathF.Exp(-dt / smoothingTauSeconds);
        _shown[slot] = Angles.Lerp(_shown[slot], normalized, alpha);
        _shownAt[slot] = now;
        facing = _shown[slot];
        return true;
    }
}
