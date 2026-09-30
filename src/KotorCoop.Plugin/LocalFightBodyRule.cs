namespace KotorCoop.Plugin;

internal enum LocalFightBodyVerdict { Release, AwaitBirth, UseCreature }

/// <summary>A newly spawned unresolved body may arrive next frame; a positively
/// mismatched object must never keep its old owner's exclusion alive.</summary>
internal static class LocalFightBodyRule
{
    internal static bool IsCurrentCachedBody(nint recorded, nint current, bool readable,
        uint vtable, uint actualId, uint expectedId) => readable
        && LivenessRule.IsCurrentMaterialisedBody(recorded, current, vtable, actualId, expectedId);

    internal static LocalFightBodyVerdict Decide(bool resolved, uint vtable, uint actualId,
        uint expectedId, bool isLocalPlayer, bool isPeerPhantom, bool inBirthGrace)
    {
        if (!resolved) return inBirthGrace ? LocalFightBodyVerdict.AwaitBirth : LocalFightBodyVerdict.Release;
        return LivenessRule.CanReconcileBody(vtable, actualId, expectedId, isLocalPlayer, isPeerPhantom)
            ? LocalFightBodyVerdict.UseCreature : LocalFightBodyVerdict.Release;
    }
}
