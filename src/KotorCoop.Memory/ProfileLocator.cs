namespace KotorCoop.Memory;

/// <summary>
/// Finds an offset profile by name. Profiles live in the repository rather than beside
/// the binaries so they can be edited and reloaded without a rebuild, which matters when
/// the workflow is "scan, guess an offset, try it, adjust".
/// </summary>
public static class ProfileLocator
{
    public static string Resolve(string nameOrPath)
    {
        if (File.Exists(nameOrPath))
        {
            return Path.GetFullPath(nameOrPath);
        }

        string fileName = nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? nameOrPath
            : nameOrPath + ".json";

        List<string> searched = [];

        foreach (string root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            DirectoryInfo? directory = new(root);

            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, "profiles", fileName);
                searched.Add(candidate);

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException(
            $"Could not find profile '{nameOrPath}'. Looked for '{fileName}' in a 'profiles' directory "
            + $"walking up from the binary and the working directory ({searched.Count} locations checked).");
    }

    public static IReadOnlyList<string> ListAvailable()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string profiles = Path.Combine(directory.FullName, "profiles");
            if (Directory.Exists(profiles))
            {
                return [.. Directory.GetFiles(profiles, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order()];
            }

            directory = directory.Parent;
        }

        return [];
    }
}
