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

    /// <summary>Squared distance, for range tests that never need the actual distance.</summary>
    public float DistanceSquaredTo(Vector3f other)
    {
        float dx = X - other.X;
        float dy = Y - other.Y;
        float dz = Z - other.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);

    /// <summary>The largest |coordinate| any KOTOR area reaches, with room to spare: the biggest
    /// stock modules span a few hundred units. Mirrors the plugin's Vec3.MaxWorldCoordinate.</summary>
    public const float MaxWorldCoordinate = 10_000f;

    /// <summary>
    /// Finite AND inside any conceivable area. Finite alone let a real hang through: live
    /// 2026-09-02 (docs/research/session-save.md), the host's own-state read during its
    /// in-world reload copied (-4.15e34, 0, -1.06e37) out of the creature the engine was
    /// tearing down, published it, and the rejoining client's phantom spawner handed it to
    /// the engine, which searched for a walkmesh point near it forever. The own-state read
    /// and the receive path both gate on this, so such a frame is dropped at BOTH ends.
    /// </summary>
    public bool IsWorldPlausible =>
        IsFinite
        && MathF.Abs(X) < MaxWorldCoordinate
        && MathF.Abs(Y) < MaxWorldCoordinate
        && MathF.Abs(Z) < MaxWorldCoordinate;

    /// <summary>
    /// True only for the exact float-zero vector. Mirrors the plugin's <c>Vec3.IsOrigin</c> —
    /// deliberately NARROWER and separate from <see cref="IsWorldPlausible"/>, which accepts
    /// (0,0,0) as a real, if rare, in-world point. Live 2026-09-25
    /// (docs/research/walkmesh-guard.md §3): a joiner's phantom of the host crashed on
    /// danm14aa shortly after arriving there because the HOST's own agent read (0,0,0) off a
    /// player creature that existed but had not been placed yet during the host's own module
    /// load, and — since <see cref="IsWorldPlausible"/> alone does not catch it — republished
    /// it as a truthful-looking position. A creature genuinely at the area origin is possible
    /// in principle but indistinguishable from "never placed"; every consumer in this codebase
    /// that has had to make that call (PartyPresenceController, EnemyPresenceController,
    /// PhantomController's own spawn gate) already resolves the ambiguity the same way — treat
    /// exact origin as suspect and hold the last known state instead.
    /// </summary>
    public bool IsOrigin => X == 0f && Y == 0f && Z == 0f;

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
