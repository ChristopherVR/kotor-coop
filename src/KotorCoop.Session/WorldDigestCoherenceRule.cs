using KotorCoop.Session.Pumps;

namespace KotorCoop.Session;

/// <summary>Rejects a retained PC label paired with a different native world.</summary>
public static class WorldDigestCoherenceRule
{
    public static bool ModuleMatches(string playerModule, bool moduleReadable, string nativeModule, uint nativeEpoch)
        => moduleReadable
            ? playerModule.Length > 0 && !ModuleNameGuard.IsFallbackModuleName(nativeModule)
                && string.Equals(playerModule, nativeModule, StringComparison.OrdinalIgnoreCase)
            // Bridges without native world support retain the legacy PC-only contract.
            : nativeEpoch == 0;
}
