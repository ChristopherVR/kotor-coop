namespace KotorCoop.Session;

/// <summary>
/// Display names for the galaxy map's planet indices — the row order of <c>planetary.2da</c>
/// (17 rows; `python tools/kotorfiles.py 2da planetary.2da`), which is what
/// <c>GetSelectedPlanet()</c> / <c>CSWPartyTable+0xE0</c> hold and what the departure relay
/// carries on the wire (<see cref="Protocol.GalaxyDeparturePacket.Planet"/>). Used for the
/// on-screen lines only ("setting course for Tatooine"); nothing decides on a name.
///
/// The 2da's own <c>name</c> column is a dialog.tlk StrRef, which is where these strings come
/// from — resolved once by hand rather than at runtime, because the agent's TLK reader is a
/// per-install lookup this file has no business depending on for a chat line. Rows 11–16 are
/// the retail "Live_Planet" placeholders (never unlocked by shipped scripts); they fall back to
/// the index.
/// </summary>
public static class GalaxyPlanetNames
{
    private static readonly string[] Names =
    [
        "the Endar Spire",  // 0  Endar_Spire   (never a departure destination)
        "Taris",            // 1
        "the Ebon Hawk",    // 2  Ebon_Hawk     (never a destination either)
        "Dantooine",        // 3
        "Tatooine",         // 4
        "Kashyyyk",         // 5
        "Manaan",           // 6
        "Korriban",         // 7
        "the Leviathan",    // 8
        "the Unknown World", // 9
        "the Star Forge",   // 10
    ];

    /// <summary>The display name for <paramref name="planet"/>, or "planet n" for an index
    /// with no retail name (the Live_Planet rows, or anything out of range).</summary>
    public static string Display(int planet) =>
        planet >= 0 && planet < Names.Length ? Names[planet] : $"planet {planet}";
}
