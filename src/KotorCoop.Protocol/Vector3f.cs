namespace KotorCoop.Protocol;

/// <summary>
/// A position in game world space. KOTOR (Odyssey) stores object positions as three
/// consecutive 32-bit floats, so this maps directly onto game memory.
/// </summary>
public readonly record struct Vector3f(float X, float Y, float Z)
{
    public static readonly Vector3f Zero = new(0f, 0f, 0f);

    public static Vector3f Lerp(Vector3f a, Vector3f b, float t) => new(
        a.X + ((b.X - a.X) * t),
        a.Y + ((b.Y - a.Y) * t),
        a.Z + ((b.Z - a.Z) * t));

    public float DistanceTo(Vector3f other)
    {
        float dx = X - other.X;
        float dy = Y - other.Y;
        float dz = Z - other.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);

    public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";
}

/// <summary>Angle helpers for facing values expressed in radians.</summary>
public static class Angles
{
    public const float TwoPi = MathF.PI * 2f;

    /// <summary>Normalises an angle into [0, 2*pi).</summary>
    public static float Normalize(float radians)
    {
        if (!float.IsFinite(radians))
        {
            return 0f;
        }

        float wrapped = radians % TwoPi;
        return wrapped < 0f ? wrapped + TwoPi : wrapped;
    }

    /// <summary>
    /// Interpolates between two angles along the shortest arc, so a peer turning
    /// through north does not spin the long way round on the remote view.
    /// </summary>
    public static float Lerp(float from, float to, float t)
    {
        float a = Normalize(from);
        float b = Normalize(to);
        float delta = b - a;

        if (delta > MathF.PI)
        {
            delta -= TwoPi;
        }
        else if (delta < -MathF.PI)
        {
            delta += TwoPi;
        }

        return Normalize(a + (delta * t));
    }
}
