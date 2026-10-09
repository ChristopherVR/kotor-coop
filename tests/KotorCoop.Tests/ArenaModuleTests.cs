using System.Buffers.Binary;
using System.Text;
using KotorCoop.Memory.GameFiles;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The PvP arena module generator (docs/research/pvp-arenas.md section 2) against a synthetic
/// "cantina" built from the same GFF writer the generator uses, so nothing here needs a KOTOR
/// install; one test also runs against the real GOG files (read-only, skipped when absent).
/// </summary>
public sealed class ArenaModuleTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string path in _temp)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---- ErfWriter.Build ---------------------------------------------------------------

    [Fact]
    public void BuiltArchiveReadsBackThroughTheGameReader()
    {
        byte[] erf = ErfWriter.Build("MOD ", [("alpha", 2012, [1, 2, 3]), ("beta", 2023, [9, 8, 7, 6, 5])]);

        string dir = Temp();
        string path = Path.Combine(dir, "x.mod");
        File.WriteAllBytes(path, erf);
        ErfArchive archive = ErfArchive.Open(path);

        Assert.Equal("MOD ", archive.FileType);
        Assert.Equal(2, archive.Resources.Count);
        Assert.True(archive.TryRead("alpha", "are", out byte[] alpha));
        Assert.Equal(new byte[] { 1, 2, 3 }, alpha);
        Assert.True(archive.TryRead("beta", "git", out byte[] beta));
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, beta);
        Assert.True(ErfArchive.TryFindMember(erf, 2012, "ALPHA", out byte[] viaMemory));
        Assert.Equal(alpha, viaMemory);
    }

    [Fact]
    public void BuiltArchiveCarriesNoDateSoTwoBuildsAreIdentical()
    {
        (string, int, byte[])[] members = [("a", 2012, [1]), ("b", 2013, [2, 2])];
        byte[] first = ErfWriter.Build("MOD ", members);
        byte[] second = ErfWriter.Build("MOD ", members);

        Assert.Equal(first, second);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(0x20, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(0x24, 4)));
    }

    [Fact]
    public void BuiltArchiveRejectsBadMembers()
    {
        Assert.Throws<ArgumentException>(() => ErfWriter.Build("MOD ", [("a", 2012, [1]), ("A", 2012, [2])]));
        Assert.Throws<ArgumentException>(() => ErfWriter.Build("MOD ", [("seventeen_chars_xx", 2012, [1])]));
        Assert.Throws<ArgumentException>(() => ErfWriter.Build("MODULE", []));
        Assert.Equal(160, ErfWriter.Build("ERF ", []).Length);
    }

    [Fact]
    public void BuiltArchiveCanBeRewrittenByReplaceMember()
    {
        byte[] erf = ErfWriter.Build("MOD ", [("a", 2012, [1, 2]), ("b", 2023, [3])]);
        byte[] changed = ErfWriter.ReplaceMember(erf, "b", 2023, [7, 7, 7]);

        Assert.True(ErfArchive.TryFindMember(changed, 2023, "b", out byte[] b));
        Assert.Equal(new byte[] { 7, 7, 7 }, b);
        Assert.True(ErfArchive.TryFindMember(changed, 2012, "a", out byte[] a));
        Assert.Equal(new byte[] { 1, 2 }, a);
    }

    // ---- the generator -----------------------------------------------------------------

    [Fact]
    public void ModuleInfoIsStrippedAndEntersAtTheFirstSpawn()
    {
        GffStruct ifo = Member("module", "ifo");

        Assert.Equal("k_coop_arena", Text(ifo, "Mod_Entry_Area"));
        Assert.Equal("k_coop_arena", Text(ifo, "Mod_VO_ID"));
        ArenaSpawn entry = ArenaModule.SpawnFor(0, 0);
        Assert.True(ifo.TryGetFloat("Mod_Entry_X", out float x));
        Assert.True(ifo.TryGetFloat("Mod_Entry_Y", out float y));
        Assert.Equal(entry.X, x);
        Assert.Equal(entry.Y, y);

        // Every script slot is blank except the stock player-death trio.
        foreach (string label in new[] { "Mod_OnHeartbeat", "Mod_OnModLoad", "Mod_OnClientEntr", "Mod_OnUsrDefined" })
        {
            Assert.Equal(string.Empty, Text(ifo, label));
        }

        Assert.Equal("nw_o0_death", Text(ifo, "Mod_OnPlrDeath"));
        Assert.Equal("nw_o0_dying", Text(ifo, "Mod_OnPlrDying"));
        Assert.Equal("nw_o0_respawn", Text(ifo, "Mod_OnSpawnBtnDn"));

        IReadOnlyList<GffStruct> areas = ifo.GetList("Mod_Area_list");
        Assert.Single(areas);
        Assert.Equal("k_coop_arena", Text(areas[0], "Area_Name"));
        Assert.Equal(6u, areas[0].StructId);

        // Everything else is carried over as it was.
        Assert.True(ifo.TryGetByte("Mod_DawnHour", out byte dawn));
        Assert.Equal(6, dawn);
    }

    [Fact]
    public void AreaKeepsOnlyTheRingRoomAndLosesItsScripts()
    {
        GffStruct are = Member("k_coop_arena", "are");

        foreach (string label in new[] { "OnEnter", "OnExit", "OnHeartbeat", "OnUserDefined" })
        {
            Assert.Equal(string.Empty, Text(are, label));
        }

        IReadOnlyList<GffStruct> rooms = are.GetList("Rooms");
        Assert.Single(rooms);
        Assert.Equal("M02ae_07a", Text(rooms[0], "RoomName"));
        Assert.True(are.TryGetByte("Unescapable", out byte unescapable));
        Assert.Equal(1, unescapable);
    }

    [Fact]
    public void InstancesKeepOnlyWaypointsInsideTheRingPlusTheFourSpawns()
    {
        GffStruct git = Member("k_coop_arena", "git");

        foreach (string list in new[] { "Creature List", "Door List", "TriggerList", "Placeable List", "SoundList", "CameraList" })
        {
            Assert.Empty(git.GetList(list));
        }

        string[] tags = [.. git.GetList("WaypointList").Select(w => Text(w, "Tag"))];
        Assert.Contains("tar02_wppcarena", tags);
        Assert.Contains("bp_fight1_wp", tags.Select(t => t.ToLowerInvariant()));
        Assert.DoesNotContain("tar02_wpangrywoman", tags);
        Assert.DoesNotContain("wp_serverdroid_01", tags);

        foreach (ArenaSpawn spawn in ArenaModule.Spawns)
        {
            GffStruct waypoint = git.GetList("WaypointList").Single(w => Text(w, "Tag") == spawn.Tag);
            Assert.True(waypoint.TryGetFloat("XPosition", out float x));
            Assert.True(waypoint.TryGetFloat("YPosition", out float y));
            Assert.True(waypoint.TryGetFloat("XOrientation", out float faceX));
            Assert.Equal(spawn.X, x);
            Assert.Equal(spawn.Y, y);
            Assert.Equal(spawn.FaceX, faceX);
        }

        Assert.Equal(2 + 4, tags.Length); // tar02_wppcarena + BP_FIGHT1_WP in the ring, then the four
        Assert.True(git.TryGetByte("UseTemplates", out byte useTemplates));
        Assert.Equal(1, useTemplates);
        Assert.NotNull(git.GetStruct("AreaProperties"));
    }

    [Fact]
    public void SpawnsFaceEachOtherAndSitInsideTheRing()
    {
        ArenaSpawn a = ArenaModule.SpawnFor(0, 0);
        ArenaSpawn b = ArenaModule.SpawnFor(1, 0);

        Assert.True(a.X > b.X);
        Assert.Equal(-1f, a.FaceX);
        Assert.Equal(1f, b.FaceX);
        Assert.Equal(4, ArenaModule.Spawns.Count);
        Assert.All(ArenaModule.Spawns, s => Assert.InRange(s.X, 185f, 215f));
        Assert.All(ArenaModule.Spawns, s => Assert.InRange(s.Y, 85f, 117f));
        Assert.Equal(ArenaModule.SpawnFor(1, 1), ArenaModule.Spawns[3]);

        // An out-of-range slot or team falls back instead of throwing.
        Assert.Equal(ArenaModule.SpawnFor(0, 0), ArenaModule.SpawnFor(0, 7));
        Assert.Equal(ArenaModule.SpawnFor(1, 0), ArenaModule.SpawnFor(5, 0));
    }

    [Fact]
    public void PathPointsAreCutToTheRingAndRenumbered()
    {
        GffStruct pth = Member("k_coop_arena", "pth");
        IReadOnlyList<GffStruct> points = pth.GetList("Path_Points");

        // Source points 1 and 2 are inside the ring; 0 and 3 are in the cantina. 1 -> 2 and 2 -> 1
        // stay linked, the links to the outside are dropped.
        Assert.Equal(2, points.Count);
        Assert.True(points[0].TryGetDword("Conections", out uint firstCount));
        Assert.Equal(1u, firstCount);

        IReadOnlyList<GffStruct> links = pth.GetList("Path_Conections");
        Assert.Equal(2, links.Count);
        Assert.True(links[0].TryGetDword("Destination", out uint toSecond));
        Assert.True(links[1].TryGetDword("Destination", out uint toFirst));
        Assert.Equal(1u, toSecond);
        Assert.Equal(0u, toFirst);
        Assert.True(points[1].TryGetDword("First_Conection", out uint secondFirst));
        Assert.Equal(1u, secondFirst);
    }

    [Fact]
    public void ModuleWithoutAPathFileOrWithNoPointsInTheRingStillBuilds()
    {
        ArchiveSet none = BuildArchive(Sources() with { Pth = null });
        Assert.False(none.Has("k_coop_arena", "pth"));

        GffWriter farAway = new("PTH ");
        GffWriterList list = farAway.Root.AddList("Path_Points");
        list.Add(2).SetDword("Conections", 0).SetDword("First_Conection", 0).SetFloat("X", 10f).SetFloat("Y", 10f);
        farAway.Root.AddList("Path_Conections");
        ArchiveSet outside = BuildArchive(Sources() with { Pth = farAway.ToBytes() });
        Assert.False(outside.Has("k_coop_arena", "pth"));
    }

    [Fact]
    public void LayoutIsCutToTheRingRoomAndItsDoorHook()
    {
        ArchiveSet set = BuildArchive(Sources());

        string lyt = Encoding.Latin1.GetString(set.Read("k_coop_arena", "lyt"));
        Assert.Contains("roomcount 1", lyt);
        Assert.Contains("M02ae_07a 200.0 70.0 0.0", lyt);
        Assert.DoesNotContain("M02ae_09a", lyt);
        Assert.Contains("doorhookcount 1", lyt);
        Assert.Contains("M02ae_07a door_07", lyt);
        Assert.Contains("beginlayout", lyt);
        Assert.Contains("donelayout", lyt);

        string vis = Encoding.Latin1.GetString(set.Read("k_coop_arena", "vis"));
        Assert.Equal("M02ae_07a 0\r\n", vis);
    }

    [Fact]
    public void LayoutKeepsTheSourceLineEndings()
    {
        ArchiveSet lf = BuildArchive(Sources() with { Lyt = Encoding.Latin1.GetBytes(Layout("\n")) });

        Assert.DoesNotContain('\r', Encoding.Latin1.GetString(lf.Read("k_coop_arena", "lyt")));
        Assert.Equal("M02ae_07a 0\n", Encoding.Latin1.GetString(lf.Read("k_coop_arena", "vis")));
    }

    [Fact]
    public void SameSourcesAlwaysGiveTheSameBytes()
    {
        Assert.Equal(ArenaModule.Build(Sources()), ArenaModule.Build(Sources()));
    }

    [Fact]
    public void ArchiveListsTheSixMembersAClassicModuleNeeds()
    {
        ArchiveSet set = BuildArchive(Sources());

        Assert.Equal("MOD ", set.Archive.FileType);
        Assert.True(set.Has("module", "ifo"));
        foreach (string extension in new[] { "are", "git", "lyt", "vis", "pth" })
        {
            Assert.True(set.Has("k_coop_arena", extension), extension);
        }

        Assert.Equal(6, set.Archive.Resources.Count);
    }

    [Fact]
    public void CantinaWithoutTheRingIsRefusedRatherThanHalfBuilt()
    {
        GffWriter are = new("ARE ");
        are.Root.SetResRef("OnEnter", "x");
        are.Root.AddList("Rooms").Add(0).SetResRef("RoomName", "M02ae_01a");
        Assert.Throws<InvalidDataException>(() => ArenaModule.Build(Sources() with { Are = are.ToBytes() }));

        string noRoomLayout = "beginlayout\n   roomcount 1\n      M02ae_01a 1.0 1.0 0.0\n   doorhookcount 0\ndonelayout\n";
        Assert.Throws<InvalidDataException>(() =>
            ArenaModule.Build(Sources() with { Lyt = Encoding.Latin1.GetBytes(noRoomLayout) }));

        GffWriter git = new("GIT ");
        git.Root.AddList("WaypointList");
        Assert.Throws<InvalidDataException>(() => ArenaModule.Build(Sources() with { Git = git.ToBytes() }));
    }

    // ---- the installer -----------------------------------------------------------------

    [Fact]
    public void InstallWritesCheckSeesAndUninstallRemovesTheModule()
    {
        string game = FakeGame();

        Assert.False(ArenaModuleInstaller.IsInstalled(game));
        Assert.Equal(ArenaStatus.Missing, ArenaModuleInstaller.Check(game, out _));

        ArenaInstallResult first = ArenaModuleInstaller.Install(game);
        Assert.Equal(ArenaInstallOutcome.Installed, first.Outcome);
        Assert.True(File.Exists(Path.Combine(game, "modules", "k_coop_arena.mod")));
        Assert.True(ArenaModuleInstaller.IsInstalled(game));
        Assert.Equal(ArenaStatus.Current, ArenaModuleInstaller.Check(game, out string path));
        Assert.EndsWith("k_coop_arena.mod", path, StringComparison.OrdinalIgnoreCase);

        // Run again: nothing to write.
        ArenaInstallResult second = ArenaModuleInstaller.Install(game);
        Assert.Equal(ArenaInstallOutcome.UpToDate, second.Outcome);

        // The ring room is not in the archive's room list for any other module's file.
        Assert.Single(Directory.GetFiles(Path.Combine(game, "modules"), "k_coop_*"));

        Assert.True(ArenaModuleInstaller.Uninstall(game, out _));
        Assert.False(ArenaModuleInstaller.IsInstalled(game));
        Assert.False(ArenaModuleInstaller.Uninstall(game, out _));
        Assert.True(File.Exists(Path.Combine(game, "modules", "tar_m02ae.rim")));
    }

    [Fact]
    public void TwoInstallsOfTheSameGameFilesProduceTheSameFile()
    {
        string one = FakeGame();
        string two = FakeGame();
        ArenaModuleInstaller.Install(one);
        ArenaModuleInstaller.Install(two);

        Assert.Equal(
            File.ReadAllBytes(Path.Combine(one, "modules", "k_coop_arena.mod")),
            File.ReadAllBytes(Path.Combine(two, "modules", "k_coop_arena.mod")));
    }

    [Fact]
    public void ChangedGameFilesMakeTheInstalledModuleStaleAndInstallRefreshesIt()
    {
        string game = FakeGame();
        ArenaModuleInstaller.Install(game);

        // A mod replaces the layout in Override: the module the game would now generate differs.
        string changed = Layout("\r\n").Replace("200.0 70.0", "201.0 70.0", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(game, "Override", "m02ae.lyt"), changed, Encoding.Latin1);

        Assert.Equal(ArenaStatus.Stale, ArenaModuleInstaller.Check(game, out _));
        Assert.Equal(ArenaInstallOutcome.Refreshed, ArenaModuleInstaller.Install(game).Outcome);
        Assert.Equal(ArenaStatus.Current, ArenaModuleInstaller.Check(game, out _));
    }

    [Fact]
    public void GameWithoutTheCantinaSkipsWithAReasonAndWritesNothing()
    {
        string game = Temp();
        Directory.CreateDirectory(Path.Combine(game, "modules"));

        ArenaInstallResult result = ArenaModuleInstaller.Install(game);

        Assert.Equal(ArenaInstallOutcome.Skipped, result.Outcome);
        Assert.Contains("tar_m02ae", result.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(game, "modules")));
        Assert.Equal(ArenaStatus.CannotGenerate, ArenaModuleInstaller.Check(game, out _));
        Assert.False(ArenaModuleInstaller.IsInstalled(Path.Combine(game, "nope")));
    }

    [GogInstallFact]
    public void RealCantinaMakesAModuleWithTheRingOnly()
    {
        Assert.True(ArenaModuleInstaller.TryGenerate(GameFilesTests.GogDirectory, out byte[] module, out string error), error);

        Assert.True(ErfArchive.TryFindMember(module, 2014, "module", out byte[] ifoBytes));
        GffStruct ifo = Gff.Parse(ifoBytes).Root;
        Assert.Equal("k_coop_arena", Text(ifo, "Mod_Entry_Area"));

        Assert.True(ErfArchive.TryFindMember(module, 2023, "k_coop_arena", out byte[] gitBytes));
        GffStruct git = Gff.Parse(gitBytes).Root;
        Assert.Empty(git.GetList("Creature List"));
        Assert.Empty(git.GetList("TriggerList"));
        Assert.Empty(git.GetList("Door List"));
        Assert.Empty(git.GetList("Placeable List"));
        Assert.Contains(git.GetList("WaypointList"), w => Text(w, "Tag") == "k_coop_arena_a1");

        Assert.True(ErfArchive.TryFindMember(module, 3000, "k_coop_arena", out byte[] lyt));
        Assert.Contains("roomcount 1", Encoding.Latin1.GetString(lyt), StringComparison.Ordinal);

        Assert.True(ArenaModuleInstaller.TryGenerate(GameFilesTests.GogDirectory, out byte[] again, out _));
        Assert.Equal(module, again);
    }

    // ---- synthetic cantina -------------------------------------------------------------

    private static string Text(GffStruct item, string label) =>
        item.TryGetResRef(label, out string resref) ? resref
        : item.TryGetString(label, out string text) ? text
        : string.Empty;

    private sealed record ArchiveSet(ErfArchive Archive)
    {
        public bool Has(string resref, string extension) => Archive.Find(resref, extension) is not null;

        public byte[] Read(string resref, string extension) =>
            Archive.TryRead(resref, extension, out byte[] data) ? data : throw new InvalidOperationException($"{resref}.{extension}");
    }

    private GffStruct Member(string resref, string extension)
    {
        ArchiveSet set = BuildArchive(Sources());
        return Gff.Parse(set.Read(resref, extension)).Root;
    }

    private ArchiveSet BuildArchive(ArenaSources sources)
    {
        string path = Path.Combine(Temp(), "arena.mod");
        File.WriteAllBytes(path, ArenaModule.Build(sources));
        return new ArchiveSet(ErfArchive.Open(path));
    }

    private string Temp()
    {
        string path = Path.Combine(Path.GetTempPath(), "arena-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _temp.Add(path);
        return path;
    }

    private static ArenaSources Sources() => new(Ifo(), Are(), Git(), Pth(), Encoding.Latin1.GetBytes(Layout("\r\n")), CantinaVis());

    private static byte[] Ifo()
    {
        GffWriter w = new("IFO ");
        w.Root.SetVoid("Mod_ID", new byte[16]);
        w.Root.SetString("Mod_VO_ID", "m02ae");
        w.Root.SetLocString("Mod_Name", -1, (0, "tar_m02ae"));
        w.Root.SetResRef("Mod_Entry_Area", "m02ae");
        w.Root.SetFloat("Mod_Entry_X", 123.175f);
        w.Root.SetFloat("Mod_Entry_Y", 110.001f);
        w.Root.SetFloat("Mod_Entry_Z", 0f);
        w.Root.SetFloat("Mod_Entry_Dir_X", 0f);
        w.Root.SetFloat("Mod_Entry_Dir_Y", 1f);
        w.Root.SetByte("Mod_DawnHour", 6);
        w.Root.SetResRef("Mod_OnHeartbeat", "cantina_beat");
        w.Root.SetResRef("Mod_OnModLoad", "cantina_load");
        w.Root.SetResRef("Mod_OnClientEntr", "cantina_enter");
        w.Root.SetResRef("Mod_OnUsrDefined", "cantina_user");
        w.Root.SetResRef("Mod_OnPlrDeath", "nw_o0_death");
        w.Root.SetResRef("Mod_OnPlrDying", "nw_o0_dying");
        w.Root.SetResRef("Mod_OnSpawnBtnDn", "nw_o0_respawn");
        w.Root.AddList("Mod_Area_list").Add(6).SetResRef("Area_Name", "m02ae");
        return w.ToBytes();
    }

    private static byte[] Are()
    {
        GffWriter w = new("ARE ");
        w.Root.SetString("Tag", "Untitled");
        w.Root.SetLocString("Name", 35872);
        w.Root.SetByte("Unescapable", 1);
        GffWriterList rooms = w.Root.AddList("Rooms");
        foreach (string room in new[] { "M02ae_09a", "M02ae_07a", "M02ae_01a" })
        {
            rooms.Add(0).SetResRef("RoomName", room).SetInt("EnvAudio", 77).SetFloat("AmbientScale", 0.5f);
        }

        w.Root.SetResRef("OnEnter", "k_ptar_a02ae_en");
        w.Root.SetResRef("OnExit", "k_ptar_02ae_ex");
        w.Root.SetResRef("OnHeartbeat", "heartbeat");
        w.Root.SetResRef("OnUserDefined", "user");
        return w.ToBytes();
    }

    private static byte[] Git()
    {
        GffWriter w = new("GIT ");
        w.Root.SetByte("UseTemplates", 1);
        GffWriterStruct props = w.Root.AddStruct("AreaProperties", 100);
        props.SetInt("MusicDay", 27);

        GffWriterList creatures = w.Root.AddList("Creature List");
        creatures.Add(4).SetResRef("TemplateResRef", "tar02_bendak");
        creatures.Add(4).SetResRef("TemplateResRef", "tar02_ajuur");
        w.Root.AddList("Door List").Add(8).SetResRef("TemplateResRef", "tar02_arenadoor");
        w.Root.AddList("TriggerList").Add(1).SetResRef("TemplateResRef", "tar02_strdue");
        w.Root.AddList("Placeable List").Add(9).SetResRef("TemplateResRef", "tar02_bar");
        w.Root.AddList("SoundList").Add(6).SetResRef("TemplateResRef", "cantina_music");
        w.Root.AddList("CameraList").Add(14).SetInt("CameraID", 1);

        GffWriterList waypoints = w.Root.AddList("WaypointList");
        Waypoint(waypoints, "tar02_wppcarena", 213.588f, 100.004f);
        Waypoint(waypoints, "BP_FIGHT1_WP", 197.945f, 94.2269f);
        Waypoint(waypoints, "tar02_wpangrywoman", 107.497f, 117.885f);
        Waypoint(waypoints, "wp_serverdroid_01", 102.43f, 114.781f);
        return w.ToBytes();
    }

    private static void Waypoint(GffWriterList list, string tag, float x, float y) =>
        list.Add(5)
            .SetByte("Appearance", 1)
            .SetString("LinkedTo", string.Empty)
            .SetResRef("TemplateResRef", "sw_waypoint001")
            .SetString("Tag", tag)
            .SetLocString("LocalizedName", 23827)
            .SetByte("HasMapNote", 1)
            .SetFloat("XPosition", x)
            .SetFloat("YPosition", y)
            .SetFloat("ZPosition", 0f)
            .SetFloat("XOrientation", -1f)
            .SetFloat("YOrientation", 0f);

    // Four points: 0 cantina, 1 and 2 in the ring, 3 cantina. 0->1, 1->0, 1->2, 2->1, 2->3, 3->2.
    private static byte[] Pth()
    {
        GffWriter w = new("PTH ");
        GffWriterList points = w.Root.AddList("Path_Points");
        GffWriterList links = w.Root.AddList("Path_Conections");
        (float X, float Y, int[] To)[] source =
        [
            (100f, 100f, [1]),
            (190f, 90f, [0, 2]),
            (200f, 100f, [1, 3]),
            (110f, 110f, [2]),
        ];

        int linkIndex = 0;
        foreach ((float x, float y, int[] to) in source)
        {
            points.Add(2).SetDword("Conections", (uint)to.Length).SetDword("First_Conection", (uint)linkIndex).SetFloat("X", x).SetFloat("Y", y);
            foreach (int destination in to)
            {
                links.Add(3).SetDword("Destination", (uint)destination);
                linkIndex++;
            }
        }

        return w.ToBytes();
    }

    private static string Layout(string newline) => string.Join(
        newline,
        "#MAXLAYOUT ASCII",
        "filedependancy M02ae.max",
        "beginlayout",
        "   roomcount 3",
        "      M02ae_09a 110.0 110.0 0.0",
        "      M02ae_07a 200.0 70.0 0.0",
        "      M02ae_01a 140.0 120.0 0.0",
        "   trackcount 0",
        "   obstaclecount 0",
        "   doorhookcount 3",
        "      M02ae_09a door_03 0 123.25 110.0 0.0 0.707107 0.0 0.0 -0.707107",
        "      M02ae_05a door_01 0 141.726 110.0 0.0 -0.707107 0.0 0.0 0.707107",
        "      M02ae_07a door_07 0 200.0 116.85 0.0 0.0 0.0 0.0 1.0",
        "donelayout",
        string.Empty);

    private static byte[] CantinaVis() => Encoding.Latin1.GetBytes("M02ae_07a 0\r\n");

    /// <summary>A game folder with the cantina module as the real one lays it out: the base rim
    /// holds module.ifo, the area and instance files; the _s rim the path file; the layout files
    /// stand in for the BIF copies as loose files in Override (which win, like a real mod).</summary>
    private string FakeGame()
    {
        string game = Temp();
        string modules = Directory.CreateDirectory(Path.Combine(game, "modules")).FullName;
        string overrides = Directory.CreateDirectory(Path.Combine(game, "Override")).FullName;
        ArenaSources s = Sources();

        File.WriteAllBytes(
            Path.Combine(modules, "tar_m02ae.rim"),
            BuildRim(("m02ae", 2012, s.Are), ("m02ae", 2023, s.Git), ("module", 2014, s.Ifo)));
        File.WriteAllBytes(Path.Combine(modules, "tar_m02ae_s.rim"), BuildRim(("m02ae", 3003, s.Pth!)));
        File.WriteAllBytes(Path.Combine(overrides, "m02ae.lyt"), s.Lyt);
        File.WriteAllBytes(Path.Combine(overrides, "m02ae.vis"), s.Vis);
        return game;
    }

    private static byte[] BuildRim(params (string Resref, int Type, byte[] Data)[] members)
    {
        int count = members.Length;
        int tableOffset = 0x78;
        int dataOffset = tableOffset + (32 * count);

        byte[] file = new byte[dataOffset + members.Sum(m => m.Data.Length)];
        Encoding.ASCII.GetBytes("RIM V1.0").CopyTo(file, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x0C, 4), (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x10, 4), (uint)tableOffset);

        int offset = dataOffset;
        for (int i = 0; i < count; i++)
        {
            (string resref, int type, byte[] data) = members[i];
            int entry = tableOffset + (32 * i);
            Encoding.ASCII.GetBytes(resref).CopyTo(file, entry);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 16, 4), (uint)type);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 20, 4), (uint)i);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 24, 4), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 28, 4), (uint)data.Length);
            data.CopyTo(file, offset);
            offset += data.Length;
        }

        return file;
    }
}
