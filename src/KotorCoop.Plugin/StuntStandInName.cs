namespace KotorCoop.Plugin;

/// <summary>
/// The name of the stand-in's sideways copy of a dialogue stunt track, as the agent writes it
/// into override (KotorCoop.Memory.GameFiles.StuntStandInModels.NameFor; a test compares the
/// two): "kcs", the variant, and an FNV-1a hash of the stunt's own resref. Pure, so the tests
/// compile it in.
/// </summary>
internal static class StuntStandInName
{
    public static string For(string stuntResref, int variant)
    {
        uint hash = 2166136261;
        foreach (char c in stuntResref.ToLowerInvariant())
        {
            hash = (hash ^ (byte)c) * 16777619;
        }

        return $"kcs{variant}_{hash:x8}";
    }
}
