using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>CScriptLocation, 0x18 bytes: position then a NORMALISED direction vector.</summary>
[StructLayout(LayoutKind.Sequential, Size = 0x18)]
internal struct ScriptLocation
{
    public Vec3 Position;
    public Vec3 Orientation;
}

/// <summary>
/// What one call to the engine's walk step actually did. The step function's second and
/// third outputs and its return value are undocumented, so they are captured verbatim
/// rather than guessed at.
/// </summary>
internal readonly record struct StepDiagnostics(
    float RequestedDistance,
    int WalkResult,
    int SetPositionResult,
    Vec3 Before,
    Vec3 Stepped,
    Vec3 SpareVector,
    int SpareInt,
    Vec3 AfterCommit);

/// <summary>CExoString, 0x08 bytes: { char* c_string; uint length }.</summary>
[StructLayout(LayoutKind.Sequential, Size = 0x08)]
internal struct ExoString
{
    public nint CString;
    public uint Length;
}

/// <summary>
/// Bindings to swkotor.exe v1.0.3's own engine functions.
///
/// Every address is absolute and hardcoded, which is safe here for two specific reasons:
/// the image has no ASLR (base is always 0x400000), and the public Ghidra symbol database
/// these came from is sha256-matched to this exact executable. See docs/re/spawn.md,
/// docs/re/engine-movement.md and docs/re/object-model.md.
///
/// EVERYTHING HERE IS MAIN-THREAD ONLY. The engine is not reentrant and the plugin's worker
/// thread must never call into it; that is what the frame hook exists for.
///
/// On x86, `unmanaged[Thiscall]` puts the first argument in ECX, pushes the rest
/// right-to-left and lets the callee pop, which is exactly the retail convention.
/// </summary>
internal static unsafe partial class Odyssey
{
}
