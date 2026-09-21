namespace KotorCoop.Protocol;

/// <summary>Stable, case-insensitive identity for a loaded module in shared memory.</summary>
public static class ModuleIdentity
{
    public static uint Hash(string module)
    {
        uint hash = 2166136261u;
        foreach (char c in module)
        {
            byte b = (byte)char.ToUpperInvariant(c);
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }

    public static bool Matches(string expected, string actual) =>
        expected.Length > 0 && actual.Length > 0
        && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
}
