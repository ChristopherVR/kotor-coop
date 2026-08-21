namespace KotorCoop.Plugin;

/// <summary>
/// The few settings the PLUGIN itself needs from <c>coop.ini</c>, read in-process.
///
/// Deliberately a tiny private parser rather than a shared one: the plugin is NativeAOT x86
/// and cannot reference the agent's assemblies. Same file and same shape the agent's reader
/// accepts, so there is one file for the player to edit.
/// </summary>
internal static class PluginConfig
{
    private static string _directory = ".";

    public static void UseDirectory(string gameDirectory) => _directory = gameDirectory;

    /// <summary>Reads one key, or null when the file or key is absent.</summary>
    public static string? Read(string key)
    {
        try
        {
            string path = Path.Combine(_directory, "coop.ini");
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';' or '[')
                {
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals <= 0 || !line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = line[(equals + 1)..].Trim();
                int comment = value.IndexOfAny([';', '#']);
                return comment >= 0 ? value[..comment].Trim() : value;
            }
        }
        catch
        {
            // A missing or unreadable coop.ini simply means defaults.
        }

        return null;
    }

    public static bool ReadBool(string key, bool fallback)
    {
        string? value = Read(key);
        if (value is null)
        {
            return fallback;
        }

        return value == "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    public static float ReadFloat(string key, float fallback) =>
        float.TryParse(Read(key), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float value)
            ? value
            : fallback;

    public static int ReadInt(string key, int fallback) =>
        int.TryParse(Read(key), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
}
