namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// The Aurora/Odyssey resource-type registry: the numeric type ids the KEY/BIF/ERF/RIM
/// container formats use, mapped to the file extensions everyone actually talks in.
/// Ported from RES_TYPES / EXT_TO_TYPE in tools/kotorfiles.py; the table itself comes
/// from the engine, so it must not be "cleaned up" — a wrong id silently resolves the
/// wrong resource.
/// </summary>
public static class ResourceTypes
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [0] = "res", [1] = "bmp", [2] = "mve", [3] = "tga", [4] = "wav", [6] = "plt", [7] = "ini",
        [8] = "mp3", [9] = "mpg", [10] = "txt", [11] = "xml",
        [2000] = "plh", [2001] = "tex", [2002] = "mdl", [2003] = "thg", [2005] = "fnt",
        [2007] = "lua", [2008] = "slt", [2009] = "nss", [2010] = "ncs", [2011] = "mod",
        [2012] = "are", [2013] = "set", [2014] = "ifo", [2015] = "bic", [2016] = "wok",
        [2017] = "2da", [2018] = "tlk", [2022] = "txi", [2023] = "git", [2024] = "bti",
        [2025] = "uti", [2026] = "btc", [2027] = "utc", [2029] = "dlg", [2030] = "itp",
        [2031] = "btt", [2032] = "utt", [2033] = "dds", [2034] = "bts", [2035] = "uts",
        [2036] = "ltr", [2037] = "gff", [2038] = "fac", [2039] = "bte", [2040] = "ute",
        [2041] = "btd", [2042] = "utd", [2043] = "btp", [2044] = "utp", [2045] = "dft",
        [2046] = "gic", [2047] = "gui", [2048] = "css", [2049] = "ccs", [2050] = "btm",
        [2051] = "utm", [2052] = "dwk", [2053] = "pwk", [2054] = "jrl", [2055] = "sav",
        [2056] = "utw", [2057] = "4pc", [2058] = "ssf", [2059] = "hak", [2060] = "nwm",
        [2061] = "bik", [2062] = "ndb", [2063] = "ptm", [2064] = "ptt", [2065] = "bak",
        [3000] = "lyt", [3001] = "vis", [3002] = "rim", [3003] = "pth", [3004] = "lip",
        [3005] = "bwm", [3006] = "txb", [3007] = "tpc", [3008] = "mdx", [3009] = "rsv",
        [3010] = "sig", [3011] = "mab", [3012] = "qst", [3013] = "npc", [3014] = "spn",
        [3015] = "utx", [3016] = "mmd", [3017] = "smm", [3018] = "uta", [3019] = "mde",
        [3020] = "mdv", [3021] = "mdx", [3022] = "hqm", [3033] = "erf",
    };

    private static readonly Dictionary<string, int> ExtensionToType = BuildReverseMap();

    private static Dictionary<string, int> BuildReverseMap()
    {
        Dictionary<string, int> map = [];

        foreach ((int type, string extension) in Names)
        {
            // First-wins, like the Python setdefault loop: two ids map to "mdx" and the
            // dictionary's iteration order must not decide which one an extension means.
            map.TryAdd(extension, type);
        }

        // The one duplicate is resolved explicitly: 3008 is the real MDX geometry type.
        map["mdx"] = 3008;
        return map;
    }

    /// <summary>The extension for a type id, or "t{id}" for one nothing recognises.</summary>
    public static string NameFor(int resourceType) =>
        Names.TryGetValue(resourceType, out string? name) ? name : $"t{resourceType}";

    /// <summary>Maps "2da"/".2DA" style extensions to the container type id.</summary>
    public static bool TryGetType(string extension, out int resourceType) =>
        ExtensionToType.TryGetValue(
            extension.TrimStart('.').ToLowerInvariant(), out resourceType);
}
