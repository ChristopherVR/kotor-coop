namespace KotorCoop.Session;

/// <summary>K1's <c>classes.2da</c> rows by display name, for the class-grant relay's own
/// log and chat lines (docs/research/class-and-power-grants.md). The table is fixed game data,
/// identical on every install, so it is baked in rather than read from the 2DA.</summary>
public static class CreatureClassNames
{
    private static readonly string[] Names =
    [
        "Soldier",        // 0
        "Scout",          // 1
        "Scoundrel",      // 2
        "Jedi Guardian",  // 3
        "Jedi Consular",  // 4
        "Jedi Sentinel",  // 5
        "Combat Droid",   // 6
        "Expert Droid",   // 7
        "Minion",         // 8
    ];

    public static string Display(int classId) =>
        classId >= 0 && classId < Names.Length ? Names[classId] : $"class {classId}";
}
