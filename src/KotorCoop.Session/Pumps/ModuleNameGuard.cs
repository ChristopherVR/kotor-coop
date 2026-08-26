namespace KotorCoop.Session.Pumps;

/// <summary>The "is this module name trustworthy right now" check every pump that reads or
/// writes module-labelled state needs — split out of CoopAgent because a dozen pumps across
/// this folder share the exact same guard.</summary>
internal static class ModuleNameGuard
{
    /// <summary>True for "area1F"-style names: the bridge's stand-in when the real module
    /// name was unreadable, built from an object id that names nothing on disk. Seen mid
    /// transition; treating it as a real module (filtering by it, travelling to it, labelling
    /// world data with it) mixes state from two different worlds.</summary>
    public static bool IsFallbackModuleName(string module)
    {
        if (module.Length <= 4 || !module.StartsWith("area", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int i = 4; i < module.Length; i++)
        {
            if (!Uri.IsHexDigit(module[i]))
            {
                return false;
            }
        }

        return true;
    }
}
