using KotorCoop.Memory.GameFiles;

namespace KotorCoop.Memory;

/// <summary>
/// Selects the module-state member of a saved <c>SAVEGAME.sav</c> without trusting ERF
/// enumeration order. Transition autosaves can retain the module being left as the first
/// nested member while <c>savenfo.res:LASTMODULE</c> already names the module being entered.
/// When that metadata is absent, a single module member is the only unambiguous fallback;
/// multiple members fail closed.
/// </summary>
internal static class SavedModuleSelection
{
    private const int ModuleSavTypeThisBuild = 2057;
    private const int ModuleSavTypeClassic = 2055;

    public static bool TrySelect(
        string saveDirectory,
        ErfArchive outer,
        out ArchiveResource module)
    {
        ArchiveResource[] candidates = outer.Resources
            .Where(resource => resource.ResourceType is ModuleSavTypeThisBuild or ModuleSavTypeClassic)
            .ToArray();

        if (candidates.Length == 0)
        {
            module = null!;
            return false;
        }

        if (PluginMenuLoader.TryReadSaveLastModuleName(saveDirectory, out string lastModule))
        {
            ArchiveResource[] matches = candidates
                .Where(resource => resource.Resref.Equals(lastModule, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
            {
                module = null!;
                return false;
            }

            module = matches[0];
            return true;
        }

        if (candidates.Length != 1)
        {
            module = null!;
            return false;
        }

        module = candidates[0];
        return true;
    }
}
