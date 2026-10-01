namespace KotorCoop.Plugin;

/// <summary>A phantom cache owns one concrete current server creature, not an ID
/// which the next world or a script may have reused.</summary>
internal static class PhantomBodyIdentityRule
{
    internal static bool IsCurrent(nint cached, nint current, bool headerReadable,
        bool appearanceReadable, uint vtable, uint actualId, uint expectedId, nint localPlayer) =>
        cached != localPlayer && headerReadable && appearanceReadable
        && LivenessRule.IsCurrentMaterialisedBody(cached, current, vtable, actualId, expectedId);
}
