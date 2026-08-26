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
