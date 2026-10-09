using System.Globalization;
using System.Text;

namespace KotorCoop.Memory.GameFiles;

/// <summary>One fighting position in the arena: a team and slot, where it stands, and which way
/// it faces (a unit vector on the floor).</summary>
public readonly record struct ArenaSpawn(string Tag, int Team, int Slot, float X, float Y, float Z, float FaceX, float FaceY);

/// <summary>The raw files the arena module is built from, all read out of the player's own
/// game (never shipped). <see cref="Pth"/> is optional: an install without it still gets an
/// arena, just without the path file.</summary>
public sealed record ArenaSources(byte[] Ifo, byte[] Are, byte[] Git, byte[]? Pth, byte[] Lyt, byte[] Vis);

/// <summary>
/// The PvP arena module (<c>k_coop_arena.mod</c>, docs/research/pvp-arenas.md section 2): the
/// Taris duel ring, cut out of the Upper City Cantina (<c>tar_m02ae</c>, room <c>m02ae_07a</c>)
/// and stripped to a clean room.
///
/// WHY GENERATED, NOT SHIPPED. Everything in it that is not ours is BioWare's: the area
/// settings, the room layout, the ring's path points. The package carries none of it. The
/// installer reads the player's own <c>tar_m02ae.rim</c> and writes a module made only of
/// field-for-field copies and deletions of those files, the same way
/// <see cref="DataModInstaller"/> writes a merged 2DA. The room's model and walkmesh stay in
/// the game's own archives, found by room name.
///
/// WHAT IS KEPT. The area's look and sound, the single ring room, the path points inside the
/// ring, and the waypoints inside the ring. WHAT IS DROPPED: every creature, trigger, door,
/// placeable, sound, store and camera, every area and module script except the stock player
/// death ones, and the other nine cantina rooms. The door in the ring's wall is dropped too (its
/// blueprint lives in the cantina's own script archive), so the doorway is a gap in the wall
/// with no floor beyond it: the walkmesh ends at the wall, nobody can leave.
///
/// A NEW AREA NAME. The module, the area and the layout are all <c>k_coop_arena</c>, so nothing
/// in the cantina's own resource names is shadowed. That needs the layout (<c>.lyt</c>) and
/// visibility (<c>.vis</c>) files under the new name; they are generated here from the
/// cantina's, cut down to the one room. NEEDS-LIVE: whether the engine is happy with a module
/// whose layout names rooms of another area; if not, <see cref="AreaResref"/> can go back to
/// the cantina's own name with no other change.
///
/// DETERMINISTIC. Same game files in, same bytes out (no dates, no random ids), because the
/// content digest hashes the file and two players' arenas must agree.
/// </summary>
public static class ArenaModule
{
    /// <summary>The module name a warp or a travel request uses.</summary>
    public const string ModuleName = "k_coop_arena";

    /// <summary>The area inside it.</summary>
    public const string AreaResref = "k_coop_arena";

    /// <summary>The file in the game's modules folder.</summary>
    public const string FileName = "k_coop_arena.mod";

    public const string SourceModule = "tar_m02ae";
    public const string SourceArea = "m02ae";
    public const string RoomName = "m02ae_07a";

    // The ring room's floor, from m02ae_07a.wok (x 185-215, y 85-117) plus a metre of margin:
    // a path point or waypoint inside this box belongs to the ring.
    private const float RingMinX = 184f;
    private const float RingMaxX = 216f;
    private const float RingMinY = 84f;
    private const float RingMaxY = 118f;

    private static readonly byte[] ModId = Encoding.ASCII.GetBytes("kotor-coop-arena");

    // Spawn positions. All four are the cantina's own duel waypoints (tar02_wppcarena,
    // tar02_wppcarena2, tar02_wpopparena, tar02_wpopparena2), known to stand on the floor:
    // team A at the east end facing west, team B at the west end facing east, 27 units apart,
    // a partner four units behind each leader. The agent teleports a duellist to these, so the
    // numbers live here and the module is written from the same table.
    private static readonly ArenaSpawn[] SpawnTable =
    [
        new("k_coop_arena_a1", 0, 0, 213.588f, 100.004f, 0f, -1f, 0f),
        new("k_coop_arena_a2", 0, 1, 209.589f, 100.005f, 0f, -1f, 0f),
        new("k_coop_arena_b1", 1, 0, 186.583f, 99.774f, 0f, 1f, 0f),
        new("k_coop_arena_b2", 1, 1, 190.471f, 99.885f, 0f, 1f, 0f),
    ];

    public static IReadOnlyList<ArenaSpawn> Spawns => SpawnTable;

    /// <summary>The spawn for a team (0 or 1) and slot (0 or 1); falls back to the team's
    /// first slot when the slot is out of range.</summary>
    public static ArenaSpawn SpawnFor(int team, int slot)
    {
        int t = team == 0 ? 0 : 1;
        int s = slot is 0 or 1 ? slot : 0;
        return SpawnTable[(t * 2) + s];
    }

    /// <summary>Builds the module archive. Throws <see cref="InvalidDataException"/> when a
    /// source file is not what the cantina's is (the ring room or its waypoint template is
    /// missing), so a modded or damaged install gets no arena instead of a broken one.</summary>
    public static byte[] Build(ArenaSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        List<(string, int, byte[])> members =
        [
            ("module", ResourceType("ifo"), BuildIfo(sources.Ifo)),
            (AreaResref, ResourceType("are"), BuildAre(sources.Are)),
            (AreaResref, ResourceType("git"), BuildGit(sources.Git)),
        ];

        (byte[] lyt, byte[] vis) = BuildLayout(sources.Lyt);
        members.Add((AreaResref, ResourceType("lyt"), lyt));
        members.Add((AreaResref, ResourceType("vis"), vis));

        if (sources.Pth is { } pth && BuildPth(pth) is { } trimmed)
        {
            members.Add((AreaResref, ResourceType("pth"), trimmed));
        }

        return ErfWriter.Build("MOD ", members);
    }

    private static int ResourceType(string extension) =>
        ResourceTypes.TryGetType(extension, out int type)
            ? type
            : throw new InvalidOperationException($"no resource type for .{extension}");

    // -- module.ifo ---------------------------------------------------------------------

    private static byte[] BuildIfo(byte[] source)
    {
        GffStruct root = Gff.Parse(source).Root;
        GffWriter writer = new("IFO ");
        GffCopy.CopyInto(root, writer.Root);

        ArenaSpawn entry = SpawnTable[0];
        writer.Root
            .SetVoid("Mod_ID", ModId)
            .SetString("Mod_VO_ID", ModuleName)
            .SetLocString("Mod_Name", -1, (0, ModuleName))
            .SetResRef("Mod_Entry_Area", AreaResref)
            .SetFloat("Mod_Entry_X", entry.X)
            .SetFloat("Mod_Entry_Y", entry.Y)
            .SetFloat("Mod_Entry_Z", entry.Z)
            .SetFloat("Mod_Entry_Dir_X", entry.FaceX)
            .SetFloat("Mod_Entry_Dir_Y", entry.FaceY);

        // No module script runs here except the stock player-death ones, which the engine
        // wants set on every module (a duellist cannot die under the duel's pin, but a forced
        // down still has to go somewhere sensible).
        foreach (string label in root.FieldNames)
        {
            if (label.StartsWith("Mod_On", StringComparison.Ordinal)
                && root.TryGetFieldType(label, out GffFieldType type) && type == GffFieldType.ResRef
                && label is not ("Mod_OnPlrDeath" or "Mod_OnPlrDying" or "Mod_OnSpawnBtnDn"))
            {
                writer.Root.SetResRef(label, string.Empty);
            }
        }

        // A one-area module. The source's list entry (struct id 6) is copied so the id stays
        // whatever the engine's own modules use.
        GffWriterList areas = writer.Root.AddList("Mod_Area_list");
        uint areaStructId = root.GetList("Mod_Area_list") is { Count: > 0 } sourceAreas ? sourceAreas[0].StructId : 6u;
        areas.Add(areaStructId).SetResRef("Area_Name", AreaResref);

        return writer.ToBytes();
    }

    // -- area (.are) --------------------------------------------------------------------

    private static byte[] BuildAre(byte[] source)
    {
        GffStruct root = Gff.Parse(source).Root;
        GffWriter writer = new("ARE ");
        GffCopy.CopyInto(root, writer.Root);

        writer.Root
            .SetString("Tag", ModuleName)
            .SetLocString("Name", -1, (0, "Co-op Arena"))
            .SetResRef("OnEnter", string.Empty)
            .SetResRef("OnExit", string.Empty)
            .SetResRef("OnHeartbeat", string.Empty)
            .SetResRef("OnUserDefined", string.Empty);

        GffWriterList rooms = writer.Root.AddList("Rooms");
        bool found = false;
        foreach (GffStruct room in root.GetList("Rooms"))
        {
            if (room.TryGetString("RoomName", out string name)
                && string.Equals(name, RoomName, StringComparison.OrdinalIgnoreCase))
            {
                GffCopy.CopyInto(room, rooms.Add(room.StructId));
                found = true;
            }
        }

        if (!found)
        {
            throw new InvalidDataException($"the cantina's area has no room '{RoomName}'");
        }

        return writer.ToBytes();
    }

    // -- instances (.git) ---------------------------------------------------------------

    private static readonly string[] EmptiedLists =
    [
        "Creature List", "Door List", "Encounter List", "List", "SoundList", "StoreList",
        "TriggerList", "Placeable List", "CameraList",
    ];

    private static byte[] BuildGit(byte[] source)
    {
        GffStruct root = Gff.Parse(source).Root;
        GffWriter writer = new("GIT ");
        GffCopy.CopyInto(root, writer.Root);

        foreach (string list in EmptiedLists)
        {
            if (root.Contains(list))
            {
                writer.Root.AddList(list);
            }
        }

        GffStruct? template = null;
        GffWriterList waypoints = writer.Root.AddList("WaypointList");
        foreach (GffStruct waypoint in root.GetList("WaypointList"))
        {
            if (!InRing(waypoint, "XPosition", "YPosition"))
            {
                continue;
            }

            GffCopy.CopyInto(waypoint, waypoints.Add(waypoint.StructId));
            if (waypoint.TryGetString("Tag", out string tag)
                && tag.Equals("tar02_wppcarena", StringComparison.OrdinalIgnoreCase))
            {
                template = waypoint;
            }
        }

        if (template is null)
        {
            throw new InvalidDataException("the cantina's area has no 'tar02_wppcarena' waypoint inside the ring");
        }

        foreach (ArenaSpawn spawn in SpawnTable)
        {
            GffWriterStruct added = waypoints.Add(template.StructId);
            GffCopy.CopyInto(template, added);
            added.SetString("Tag", spawn.Tag)
                .SetLocString("LocalizedName", -1, (0, spawn.Tag))
                .SetByte("HasMapNote", 0)
                .SetFloat("XPosition", spawn.X)
                .SetFloat("YPosition", spawn.Y)
                .SetFloat("ZPosition", spawn.Z)
                .SetFloat("XOrientation", spawn.FaceX)
                .SetFloat("YOrientation", spawn.FaceY);
        }

        return writer.ToBytes();
    }

    private static bool InRing(GffStruct item, string xLabel, string yLabel) =>
        item.TryGetFloat(xLabel, out float x) && item.TryGetFloat(yLabel, out float y)
        && x >= RingMinX && x <= RingMaxX && y >= RingMinY && y <= RingMaxY;

    // -- path points (.pth) -------------------------------------------------------------

    /// <summary>Keeps the path points inside the ring and the links between them, renumbering.
    /// Null when none are inside (the module then ships without a path file).</summary>
    private static byte[]? BuildPth(byte[] source)
    {
        GffStruct root = Gff.Parse(source).Root;
        IReadOnlyList<GffStruct> points = root.GetList("Path_Points");
        IReadOnlyList<GffStruct> links = root.GetList("Path_Conections");

        Dictionary<int, int> renumbered = [];
        List<int> kept = [];
        for (int i = 0; i < points.Count; i++)
        {
            if (InRing(points[i], "X", "Y"))
            {
                renumbered[i] = kept.Count;
                kept.Add(i);
            }
        }

        if (kept.Count == 0)
        {
            return null;
        }

        GffWriter writer = new("PTH ");
        GffWriterList pointList = writer.Root.AddList("Path_Points");
        GffWriterList linkList = writer.Root.AddList("Path_Conections");
        uint linkStructId = links.Count > 0 ? links[0].StructId : 3u;

        foreach (int index in kept)
        {
            GffStruct point = points[index];
            point.TryGetDword("First_Conection", out uint first);
            point.TryGetDword("Conections", out uint count);

            int firstNew = linkList.Count;
            for (uint c = 0; c < count && first + c < links.Count; c++)
            {
                if (links[(int)(first + c)].TryGetDword("Destination", out uint destination)
                    && renumbered.TryGetValue((int)destination, out int mapped))
                {
                    linkList.Add(linkStructId).SetDword("Destination", (uint)mapped);
                }
            }

            GffWriterStruct entry = pointList.Add(point.StructId);
            GffCopy.CopyInto(point, entry);
            entry.SetDword("Conections", (uint)(linkList.Count - firstNew));
            entry.SetDword("First_Conection", (uint)firstNew);
        }

        return writer.ToBytes();
    }

    // -- layout (.lyt) and visibility (.vis) --------------------------------------------

    /// <summary>The cantina's layout cut to the one ring room (and its door hook), and a
    /// visibility file that says that room sees nothing else (which is what the cantina's own
    /// says about it).</summary>
    private static (byte[] Lyt, byte[] Vis) BuildLayout(byte[] source)
    {
        string text = Encoding.Latin1.GetString(source);
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        List<string> output = [];
        string? roomSpelling = null;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 2 && parts[0] is "roomcount" or "doorhookcount")
            {
                int count = int.Parse(parts[1], CultureInfo.InvariantCulture);
                List<string> keep = [];
                for (int c = 1; c <= count && i + c < lines.Length; c++)
                {
                    string[] member = lines[i + c].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (member.Length > 0 && member[0].Equals(RoomName, StringComparison.OrdinalIgnoreCase))
                    {
                        keep.Add(lines[i + c]);
                        roomSpelling ??= member[0];
                    }
                }

                output.Add($"   {parts[0]} {keep.Count}");
                output.AddRange(keep);
                i += count;
                continue;
            }

            if (parts.Length == 2 && parts[0] is "trackcount" or "obstaclecount"
                && parts[1] != "0")
            {
                throw new InvalidDataException($"the cantina's layout has {parts[0]} {parts[1]}; expected none");
            }

            output.Add(line);
        }

        if (roomSpelling is null)
        {
            throw new InvalidDataException($"the cantina's layout has no room '{RoomName}'");
        }

        // The file ends with a newline; the split leaves one empty trailing element, which the
        // join below turns back into exactly one.
        string lyt = string.Join(newline, output);
        string vis = $"{roomSpelling} 0{newline}";
        return (Encoding.Latin1.GetBytes(lyt), Encoding.Latin1.GetBytes(vis));
    }
}

/// <summary>What <see cref="ArenaModuleInstaller.Check"/> found.</summary>
public enum ArenaStatus
{
    /// <summary>The module is in the modules folder and matches what this game's files generate.</summary>
    Current,

    /// <summary>It is there but differs from what would be generated now (the game's files
    /// changed, or the build that made it did). Setup again refreshes it.</summary>
    Stale,

    /// <summary>Not installed.</summary>
    Missing,

    /// <summary>Not installed, and this game's files cannot make one (no cantina module).</summary>
    CannotGenerate,
}

/// <summary>What <see cref="ArenaModuleInstaller.Install"/> did.</summary>
public enum ArenaInstallOutcome
{
    Installed,
    Refreshed,
    UpToDate,
    Skipped,
}

public sealed record ArenaInstallResult(ArenaInstallOutcome Outcome, string Path, string Message);

/// <summary>
/// Generates, installs, checks and removes <c>modules\k_coop_arena.mod</c> for one game folder.
/// Idempotent: a run that finds the same bytes already there writes nothing. It only ever
/// touches that one file, and never anything the game or another mod owns.
/// </summary>
public static class ArenaModuleInstaller
{
    /// <summary>True when the arena module file is in the game's modules folder (cheap: no
    /// parsing). What the running agent asks before offering arena fights.</summary>
    public static bool IsInstalled(string gameDirectory) => FindInstalled(gameDirectory) is not null;

    /// <summary>Builds the module from this game's own files. False (with the reason) when the
    /// cantina module or a file in it is missing or not as expected.</summary>
    public static bool TryGenerate(string gameDirectory, out byte[] module, out string error)
    {
        module = [];
        error = string.Empty;

        try
        {
            using GameFiles files = new(gameDirectory);
            if (!files.TryReadModuleMember(ArenaModule.SourceModule, "module", "ifo", out byte[] ifo)
                || !files.TryReadModuleMember(ArenaModule.SourceModule, ArenaModule.SourceArea, "are", out byte[] are)
                || !files.TryReadModuleMember(ArenaModule.SourceModule, ArenaModule.SourceArea, "git", out byte[] git))
            {
                error = $"{ArenaModule.SourceModule}.rim (the Taris cantina) was not found in the game's modules folder";
                return false;
            }

            if (!files.TryReadResource(ArenaModule.SourceArea, "lyt", out byte[] lyt)
                || !files.TryReadResource(ArenaModule.SourceArea, "vis", out byte[] vis))
            {
                error = "the cantina's layout files were not found in the game's data";
                return false;
            }

            byte[]? pth = files.TryReadModuleMember(
                ArenaModule.SourceModule + "_s", ArenaModule.SourceArea, "pth", out byte[] pathFile) ? pathFile : null;

            module = ArenaModule.Build(new ArenaSources(ifo, are, git, pth, lyt, vis));
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Writes the module if it is not already current. Never throws for a game that
    /// cannot make one: that is <see cref="ArenaInstallOutcome.Skipped"/> with the reason.</summary>
    public static ArenaInstallResult Install(string gameDirectory)
    {
        string? existing = FindInstalled(gameDirectory);

        if (!TryGenerate(gameDirectory, out byte[] module, out string error))
        {
            return new ArenaInstallResult(ArenaInstallOutcome.Skipped, existing ?? string.Empty, error);
        }

        if (existing is not null && SameBytes(existing, module))
        {
            return new ArenaInstallResult(ArenaInstallOutcome.UpToDate, existing, "already current");
        }

        string modules = ModulesDirectory(gameDirectory)
            ?? Directory.CreateDirectory(System.IO.Path.Combine(gameDirectory, "modules")).FullName;
        string target = existing ?? System.IO.Path.Combine(modules, ArenaModule.FileName);
        File.WriteAllBytes(target, module);

        return new ArenaInstallResult(
            existing is null ? ArenaInstallOutcome.Installed : ArenaInstallOutcome.Refreshed, target, "written");
    }

    /// <summary>Reports whether the module is installed and current. Changes nothing.</summary>
    public static ArenaStatus Check(string gameDirectory, out string path)
    {
        string? existing = FindInstalled(gameDirectory);
        path = existing ?? string.Empty;
        bool canGenerate = TryGenerate(gameDirectory, out byte[] module, out _);

        if (existing is null)
        {
            return canGenerate ? ArenaStatus.Missing : ArenaStatus.CannotGenerate;
        }

        // A module we cannot regenerate (the game files moved on) is still the one in use.
        return !canGenerate || SameBytes(existing, module) ? ArenaStatus.Current : ArenaStatus.Stale;
    }

    /// <summary>Removes the module file. False when there was none.</summary>
    public static bool Uninstall(string gameDirectory, out string path)
    {
        string? existing = FindInstalled(gameDirectory);
        path = existing ?? string.Empty;
        if (existing is null)
        {
            return false;
        }

        File.Delete(existing);
        return true;
    }

    private static bool SameBytes(string path, byte[] expected)
    {
        try
        {
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? ModulesDirectory(string gameDirectory)
    {
        if (!Directory.Exists(gameDirectory))
        {
            return null;
        }

        foreach (string directory in Directory.EnumerateDirectories(gameDirectory))
        {
            if (string.Equals(System.IO.Path.GetFileName(directory), "modules", StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }
        }

        return null;
    }

    private static string? FindInstalled(string gameDirectory)
    {
        string? modules = ModulesDirectory(gameDirectory);
        if (modules is null)
        {
            return null;
        }

        foreach (string file in Directory.EnumerateFiles(modules))
        {
            if (string.Equals(System.IO.Path.GetFileName(file), ArenaModule.FileName, StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }
}
