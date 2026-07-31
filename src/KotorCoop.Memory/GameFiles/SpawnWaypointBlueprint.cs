namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// Generates <c>k_coop_spawn.utw</c>, a waypoint blueprint shipped in the game's Override so the
/// co-op layer has a deterministic, named place to put an arriving peer — a "spawn point" — that
/// exists without editing any module's <c>.git</c>. Waypoints are the cheapest object the engine
/// will place: they have no model, no collision and no AI, so an extra one is inert.
///
/// This is a pure data mod (no reverse engineering): the field set is copied from a stock
/// waypoint struct decoded out of a shipped module (<c>STUNT_06.rim</c> →
/// <c>stunt_levbridge.git</c>, WaypointList struct id 5), with the position/orientation left at
/// the origin because the spawner supplies real coordinates at placement time.
///
/// ⚠ The resref is deliberately in our <c>k_coop_</c> namespace: a raw Override drop wins over
/// everything, so colliding with a name a module already uses would replace that module's own
/// waypoint. See docs/research/no-re-features.md.
/// </summary>
public static class SpawnWaypointBlueprint
{
    /// <summary>The blueprint's resref — what the co-op spawner asks ResMan for.</summary>
    public const string ResRef = "k_coop_spawn";

    /// <summary>File name as dropped into Override.</summary>
    public const string FileName = ResRef + ".utw";

    /// <summary>Tag the placed waypoint carries, so it can be found by tag in-world.</summary>
    public const string Tag = "k_coop_spawn";

    /// <summary>
    /// Builds the .utw bytes. Deterministic — same output every call, so the installer can
    /// compare against what is already on disk instead of rewriting it.
    /// </summary>
    public static byte[] Build()
    {
        GffWriter writer = new("UTW ");
        GffWriterStruct root = writer.Root;

        // Field set and order follow the decoded stock waypoint. Every string field is present
        // even when empty, because the engine's loader reads them unconditionally.
        root.SetByte("Appearance", 1);
        root.SetString("LinkedTo", string.Empty);
        root.SetResRef("TemplateResRef", ResRef);
        root.SetString("Tag", Tag);

        // -1 (no StrRef) with no inline substring: the name is supplied by the plugin's own
        // name-plate write rather than shipped in dialog.tlk, which we deliberately never touch.
        root.SetLocString("LocalizedName", -1);
        root.SetLocString("Description", -1);

        // No map note: the co-op peer pin on the M screen is a separate, runtime-built waypoint
        // (see the map-marker controller); this blueprint must not add a second permanent pin.
        root.SetByte("HasMapNote", 0);
        root.SetLocString("MapNote", -1);
        root.SetByte("MapNoteEnabled", 0);

        // Placement coordinates are supplied when the waypoint is created; the blueprint's own
        // values are just a valid origin.
        root.SetFloat("XPosition", 0f);
        root.SetFloat("YPosition", 0f);
        root.SetFloat("ZPosition", 0f);
        root.SetFloat("XOrientation", 1f);
        root.SetFloat("YOrientation", 0f);

        return writer.ToBytes();
    }
}
