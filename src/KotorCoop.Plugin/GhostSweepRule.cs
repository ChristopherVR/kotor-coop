namespace KotorCoop.Plugin;

/// <summary>
/// Pure classification half of <see cref="GhostSweepController"/>: which objects found in a
/// freshly loaded world are co-op session spawns that a save baked in, and so must go.
/// </summary>
public static class GhostSweepRule
{
    /// <summary>The tag every co-op stand-in creature carries: peer phantoms AND materialised
    /// enemy bodies are both spawned from <c>k_coop_phantom</c>
    /// (<see cref="KotorCoop.Memory.GameFiles.PhantomBlueprint.Tag"/>). No shipped module uses it.</summary>
    public const string CreatureTag = "coop_phantom";

    /// <summary>The tag <see cref="Odyssey.SpawnMapMarker"/> stamps on a peer's map-note pin
    /// since 2026-09-18, so a pin a save serialised can be found again by tag.</summary>
    public const string MapMarkerTag = "coop_mapnote";

    /// <summary>A creature is a ghost when it carries the stand-in tag. Case-insensitive, the
    /// way the engine compares tags.</summary>
    public static bool IsGhostCreatureTag(string tag) =>
        string.Equals(tag, CreatureTag, StringComparison.OrdinalIgnoreCase);

    /// <summary>A waypoint is a ghost pin when it carries our tag, OR — for pins written by
    /// builds before the tag existed — when it is an ENABLED map note with an EMPTY tag: every
    /// shipped map note has a tag (measured: `sw_waypoint001` on the Endar Spire), and the
    /// only untagged enabled map notes are the ones this plugin used to spawn.</summary>
    public static bool IsGhostMapMarker(string tag, bool hasMapNote, bool mapNoteEnabled) =>
        string.Equals(tag, MapMarkerTag, StringComparison.OrdinalIgnoreCase)
        || (string.IsNullOrEmpty(tag) && hasMapNote && mapNoteEnabled);
}
