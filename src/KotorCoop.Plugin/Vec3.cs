using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

// Its own file, not Odyssey.cs, because the test project compiles MotionSmoother in
// directly and this is its one dependency — see KotorCoop.Tests.csproj.
[StructLayout(LayoutKind.Sequential)]
internal struct Vec3
{
    public float X;
    public float Y;
    public float Z;

    public float Length => MathF.Sqrt((X * X) + (Y * Y) + (Z * Z));

    /// <summary>The largest |coordinate| any KOTOR area reaches, with room to spare: the biggest
    /// stock modules span a few hundred units, so 10,000 is a bad read, not a far corner.</summary>
    public const float MaxWorldCoordinate = 10_000f;

    /// <summary>
    /// A point that could be somewhere in a loaded area: finite AND bounded. Finite alone is
    /// not enough — live 2026-09-02 (session-save.md), a client rejoining a host reload read
    /// the host's slot position as (-4.15e34, 0, -1.06e37): three finite floats copied out of
    /// a creature the host's engine was tearing down. The phantom spawner passed that to
    /// CreateObject, and CSWSArea::ComputeSafeLocation searched outward from it for a walkmesh
    /// point FOREVER, freezing the client on the game thread. Every consumer of a peer or
    /// player position that hands it to the engine screens with this.
    /// </summary>
    public static bool IsWorldPlausible(Vec3 p) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)
        && MathF.Abs(p.X) < MaxWorldCoordinate
        && MathF.Abs(p.Y) < MaxWorldCoordinate
        && MathF.Abs(p.Z) < MaxWorldCoordinate;

    /// <summary>Straight-line distance between two points — shared by every nearest-door/
    /// nearest-phantom sweep instead of each caller carrying its own copy.</summary>
    public static float Distance(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}
