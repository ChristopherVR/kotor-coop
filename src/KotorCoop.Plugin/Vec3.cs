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
}
