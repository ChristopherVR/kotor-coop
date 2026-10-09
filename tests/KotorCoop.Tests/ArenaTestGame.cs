using System.Buffers.Binary;
using System.Text;
using KotorCoop.Memory.GameFiles;

namespace KotorCoop.Tests;

/// <summary>A synthetic Taris cantina (the module the arena is cut from), built with the same GFF
/// writer the generator uses, so tests need no KOTOR install. Shared by the generator tests and
/// the installer tests.</summary>
public static class ArenaTestGame
{
    public static ArenaSources Sources() => new(Ifo(), Are(), Git(), Pth(), Encoding.Latin1.GetBytes(Layout("\r\n")), CantinaVis());

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

    public static string Layout(string newline) => string.Join(
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

    /// <summary>Writes the cantina into an existing game folder the way the real one is laid out:
    /// the base rim holds module.ifo, the area and instance files; the _s rim the path file; the
    /// layout files stand in for the BIF copies as loose files in Override (which win, like a
    /// real mod).</summary>
    public static void WriteCantina(string game)
    {
        string modules = Directory.CreateDirectory(Path.Combine(game, "modules")).FullName;
        string overrides = Directory.CreateDirectory(Path.Combine(game, "Override")).FullName;
        ArenaSources s = Sources();

        File.WriteAllBytes(
            Path.Combine(modules, "tar_m02ae.rim"),
            BuildRim(("m02ae", 2012, s.Are), ("m02ae", 2023, s.Git), ("module", 2014, s.Ifo)));
        File.WriteAllBytes(Path.Combine(modules, "tar_m02ae_s.rim"), BuildRim(("m02ae", 3003, s.Pth!)));
        File.WriteAllBytes(Path.Combine(overrides, "m02ae.lyt"), s.Lyt);
        File.WriteAllBytes(Path.Combine(overrides, "m02ae.vis"), s.Vis);
    }

    public static byte[] BuildRim(params (string Resref, int Type, byte[] Data)[] members)
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
