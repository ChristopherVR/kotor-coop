using KotorCoop.Memory.GameFiles;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The GFF writer against the reader it must satisfy: every field type survives a
/// build → Parse round trip, and the generated k_coop_phantom.utc blueprint carries
/// exactly the fields docs/research/override-surfaces.md promises. The reader is the
/// correctness oracle throughout — byte-identity with BioWare's writer is not claimed.
/// </summary>
public sealed class GffWriterTests
{
    // ---- pure round trip -------------------------------------------------------

    [Fact]
    public void RoundTripsEveryFieldType()
    {
        GffWriter writer = new("TEST");
        GffWriterStruct root = writer.Root;

        root.SetByte("Byte", 200)
            .SetChar("Char", -5)
            .SetWord("Word", 40_000)
            .SetShort("Short", -1234)
            .SetDword("Dword", 0xDEADBEEF)
            .SetInt("Int", -5)
            .SetDword64("Dword64", 0xDEADBEEFCAFEBABE)
            .SetInt64("Int64", -9_000_000_000)
            .SetFloat("Float", 1.5f)
            .SetDouble("Double", -2.25)
            .SetString("Name", "Revan")
            .SetString("Empty", "")
            .SetResRef("Tag", "endar")
            .SetResRef("NoTag", "")
            .SetLocString("LocName", -1, (0, "Hello"))
            .SetLocString("LocRef", 5595)
            .SetVoid("Blob", [1, 2, 3])
            .SetStrRef("StrRef", 42);

        root.AddStruct("Child", 7).SetByte("Inner", 3);

        GffWriterList items = root.AddList("Items");
        items.Add(1).SetWord("Feat", 39);
        items.Add(1).SetWord("Feat", 40);
        root.AddList("Empties");

        Gff gff = Gff.Parse(writer.ToBytes());

        Assert.Equal("TEST", gff.FileType);

        // Simple inline values, each through the reader's own accessor.
        Assert.True(gff.Root.TryGetByte("Byte", out byte b));
        Assert.Equal(200, b);
        Assert.Equal((sbyte)-5, gff.Root.Fields["Char"]);
        Assert.True(gff.Root.TryGetWord("Word", out ushort w));
        Assert.Equal(40_000, w);
        Assert.True(gff.Root.TryGetShort("Short", out short s));
        Assert.Equal(-1234, s);
        Assert.True(gff.Root.TryGetDword("Dword", out uint dw));
        Assert.Equal(0xDEADBEEF, dw);
        Assert.True(gff.Root.TryGetInt("Int", out int i));
        Assert.Equal(-5, i);
        Assert.True(gff.Root.TryGetFloat("Float", out float f));
        Assert.Equal(1.5f, f);

        // 64-bit, double, void and strref have no dedicated accessor; the raw
        // dictionary shows the decoded CLR value.
        Assert.Equal(0xDEADBEEFCAFEBABE, gff.Root.Fields["Dword64"]);
        Assert.Equal(-9_000_000_000, gff.Root.Fields["Int64"]);
        Assert.Equal(-2.25, gff.Root.Fields["Double"]);
        Assert.Equal((byte[])[1, 2, 3], gff.Root.Fields["Blob"]);
        Assert.Equal(42u, gff.Root.Fields["StrRef"]);

        Assert.True(gff.Root.TryGetString("Name", out string name));
        Assert.Equal("Revan", name);
        Assert.True(gff.Root.TryGetString("Empty", out string empty));
        Assert.Equal(string.Empty, empty);

        Assert.True(gff.Root.TryGetResRef("Tag", out string tag));
        Assert.Equal("endar", tag);
        Assert.True(gff.Root.TryGetResRef("NoTag", out string noTag));
        Assert.Equal(string.Empty, noTag);

        // The written types survive, not just widened values: a ResRef stays a ResRef.
        Assert.False(gff.Root.TryGetResRef("Name", out _));
        Assert.True(gff.Root.TryGetFieldType("Tag", out GffFieldType tagType));
        Assert.Equal(GffFieldType.ResRef, tagType);

        GffLocString? loc = gff.Root.GetLocString("LocName");
        Assert.NotNull(loc);
        Assert.Equal(-1, loc.StrRef);
        Assert.Equal("Hello", loc.Text());
        GffLocString? locRef = gff.Root.GetLocString("LocRef");
        Assert.NotNull(locRef);
        Assert.Equal(5595, locRef.StrRef);
        Assert.Empty(locRef.Substrings);

        GffStruct? child = gff.Root.GetStruct("Child");
        Assert.NotNull(child);
        Assert.Equal(7u, child.StructId);
        Assert.True(child.TryGetByte("Inner", out byte inner));
        Assert.Equal(3, inner);

        IReadOnlyList<GffStruct> readItems = gff.Root.GetList("Items");
        Assert.Equal(2, readItems.Count);
        Assert.All(readItems, item => Assert.Equal(1u, item.StructId));
        Assert.True(readItems[0].TryGetWord("Feat", out ushort feat0));
        Assert.Equal(39, feat0);
        Assert.True(readItems[1].TryGetWord("Feat", out ushort feat1));
        Assert.Equal(40, feat1);
        Assert.Empty(gff.Root.GetList("Empties"));

        // Insertion order is file order — callers that iterate depend on it.
        Assert.Equal(
            ["Byte", "Char", "Word", "Short", "Dword", "Int", "Dword64", "Int64", "Float",
             "Double", "Name", "Empty", "Tag", "NoTag", "LocName", "LocRef", "Blob",
             "StrRef", "Child", "Items", "Empties"],
            gff.Root.FieldNames);
    }

    [Fact]
    public void SettingAFieldTwiceReplacesItInPlace()
    {
        GffWriter writer = new("TEST");
        writer.Root.SetByte("A", 1).SetByte("B", 2).SetWord("A", 300);

        Gff gff = Gff.Parse(writer.ToBytes());

        Assert.Equal(["A", "B"], gff.Root.FieldNames);
        Assert.True(gff.Root.TryGetWord("A", out ushort a));
        Assert.Equal(300, a);
    }

    [Fact]
    public void RejectsOversizedLabelsResRefsAndFileTypes()
    {
        Assert.Throws<ArgumentException>(() => new GffWriter("TOOLONG"));

        GffWriter writer = new("TEST");
        Assert.Throws<ArgumentException>(() => writer.Root.SetByte("SeventeenCharsBad", 0));
        Assert.Throws<ArgumentException>(() => writer.Root.SetResRef("Tag", "a_resref_over_16_chars"));
    }

    // ---- the generated blueprints ----------------------------------------------

    [Fact]
    public void SpawnWaypointBlueprintCarriesTheStockWaypointFieldSet()
    {
        byte[] bytes = SpawnWaypointBlueprint.Build();
        Gff utw = Gff.Parse(bytes);

        Assert.Equal("UTW ", utw.FileType);

        Assert.True(utw.Root.TryGetResRef("TemplateResRef", out string resref));
        Assert.Equal("k_coop_spawn", resref);
        Assert.True(utw.Root.TryGetString("Tag", out string tag));
        Assert.Equal("k_coop_spawn", tag);

        // Every field the stock waypoint struct carries must be present — the engine's loader
        // reads them unconditionally, so a missing one is a load failure, not a default.
        Assert.True(utw.Root.TryGetByte("Appearance", out byte appearance));
        Assert.Equal(1, appearance);
        Assert.True(utw.Root.TryGetString("LinkedTo", out string linkedTo));
        Assert.Equal(string.Empty, linkedTo);

        // No permanent map pin: the peer pin is a separate runtime waypoint.
        Assert.True(utw.Root.TryGetByte("HasMapNote", out byte hasMapNote));
        Assert.Equal(0, hasMapNote);
        Assert.True(utw.Root.TryGetByte("MapNoteEnabled", out byte mapNoteEnabled));
        Assert.Equal(0, mapNoteEnabled);

        foreach (string field in (string[])["XPosition", "YPosition", "ZPosition", "YOrientation"])
        {
            Assert.True(utw.Root.TryGetFloat(field, out float value), field);
            Assert.Equal(0f, value);
        }

        Assert.True(utw.Root.TryGetFloat("XOrientation", out float facing));
        Assert.Equal(1f, facing);

        // Deterministic: the installer compares bytes to decide whether to rewrite.
        Assert.Equal(bytes, SpawnWaypointBlueprint.Build());
    }

    [Fact]
    public void PhantomBlueprintCarriesTheCoopFieldSet()
    {
        Gff utc = Gff.Parse(PhantomBlueprint.Build());

        Assert.Equal("UTC ", utc.FileType);

        Assert.True(utc.Root.TryGetResRef("TemplateResRef", out string resref));
        Assert.Equal("k_coop_phantom", resref);
        Assert.True(utc.Root.TryGetString("Tag", out string tag));
        Assert.Equal("coop_phantom", tag);

        Assert.True(utc.Root.TryGetByte("Min1HP", out byte min1Hp));
        Assert.Equal(1, min1Hp);
        Assert.True(utc.Root.TryGetByte("Plot", out byte plot));
        Assert.Equal(0, plot);
        Assert.True(utc.Root.TryGetWord("FactionID", out ushort faction));
        Assert.Equal(5, faction);
        Assert.True(utc.Root.TryGetWord("Appearance_Type", out ushort appearance));
        Assert.Equal(114, appearance);
        Assert.True(utc.Root.TryGetWord("SoundSetFile", out ushort soundset));
        Assert.Equal(65535, soundset);
        Assert.True(utc.Root.TryGetByte("PerceptionRange", out byte perception));
        Assert.Equal(1, perception);

        foreach (string hp in (string[])["HitPoints", "CurrentHitPoints", "MaxHitPoints"])
        {
            Assert.True(utc.Root.TryGetShort(hp, out short value), hp);
            Assert.Equal(30, value);
        }

        // All fourteen event scripts present, typed ResRef, and empty: the whole point
        // of shipping the blueprint is deleting the stock commoner AI.
        Assert.Equal(14, PhantomBlueprint.ScriptFields.Count);
        foreach (string script in PhantomBlueprint.ScriptFields)
        {
            Assert.True(utc.Root.TryGetResRef(script, out string body), script);
            Assert.Equal(string.Empty, body);
        }

        Assert.True(utc.Root.TryGetResRef("Conversation", out string conversation));
        Assert.Equal(string.Empty, conversation);

        // FirstName ships blank (strref -1, no substrings); the plugin names the peer.
        GffLocString? firstName = utc.Root.GetLocString("FirstName");
        Assert.NotNull(firstName);
        Assert.Equal(-1, firstName.StrRef);
        Assert.Equal(string.Empty, firstName.Text());

        Assert.True(utc.Root.TryGetByte("NoPermDeath", out byte noPermDeath));
        Assert.Equal(0, noPermDeath);
        Assert.True(utc.Root.TryGetByte("PartyInteract", out byte partyInteract));
        Assert.Equal(0, partyInteract);

        // The eight proficiency feats, in n_commm's list shape: struct id 1, WORD Feat.
        IReadOnlyList<GffStruct> feats = utc.Root.GetList("FeatList");
        List<ushort> featRows = [];
        foreach (GffStruct entry in feats)
        {
            Assert.Equal(1u, entry.StructId);
            Assert.True(entry.TryGetWord("Feat", out ushort feat));
            Assert.True(entry.TryGetFieldType("Feat", out GffFieldType featType));
            Assert.Equal(GffFieldType.Word, featType);
            featRows.Add(feat);
        }

        Assert.Equal([4, 5, 6, 39, 40, 42, 43, 44], featRows);

        // Loader-required scaffolding copied from n_commm: a class level and skill table.
        IReadOnlyList<GffStruct> classes = utc.Root.GetList("ClassList");
        Assert.Single(classes);
        Assert.True(classes[0].TryGetInt("Class", out int cls));
        Assert.Equal(0, cls);
        Assert.True(classes[0].TryGetShort("ClassLevel", out short level));
        Assert.Equal(1, level);
        Assert.Equal(8, utc.Root.GetList("SkillList").Count);
    }

    [Fact]
    public void ShippedOverrideFileMatchesTheGenerator()
    {
        // profiles/override/k_coop_phantom.utc is a committed asset; it must stay in
        // lockstep with PhantomBlueprint.Build() or the installer ships stale bytes.
        Assert.Equal(PhantomBlueprint.Build(), File.ReadAllBytes(FindShipped("k_coop_phantom.utc")));
    }

    [Fact]
    public void ShippedSpawnWaypointMatchesTheGenerator()
    {
        Assert.Equal(SpawnWaypointBlueprint.Build(), File.ReadAllBytes(FindShipped(SpawnWaypointBlueprint.FileName)));
    }

    [Fact]
    public void ShippedJediClassDialogueMatchesTheGenerator()
    {
        // profiles/override/k_coop_jediclass.dlg is a committed asset (docs/research/
        // class-and-power-grants.md's 2026-09-25 section); it must stay in lockstep with
        // JediClassDialogueBlueprint.Build() or the installer ships a stale popup.
        Assert.Equal(
            JediClassDialogueBlueprint.Build(),
            File.ReadAllBytes(FindShipped(JediClassDialogueBlueprint.FileName)));
    }

    [Fact]
    public void TheJediClassDialogueHasNoScriptsAndTheRightShape()
    {
        Gff dlg = Gff.Parse(JediClassDialogueBlueprint.Build());

        IReadOnlyList<GffStruct> entries = dlg.Root.GetList("EntryList");
        Assert.Equal(4, entries.Count);

        IReadOnlyList<GffStruct> replies = dlg.Root.GetList("ReplyList");
        Assert.Equal(3, replies.Count);

        // No NCS anywhere (docs/research/class-and-power-grants.md's "no compiler" note): the
        // controller drives the class purely off which terminal ENTRY was reached.
        foreach (GffStruct entry in entries)
        {
            Assert.True(entry.TryGetResRef("Script", out string script));
            Assert.Equal(string.Empty, script);
        }

        foreach (GffStruct reply in replies)
        {
            Assert.True(reply.TryGetResRef("Script", out string script));
            Assert.Equal(string.Empty, script);
        }

        // The prompt entry offers exactly the three choices, in order, and each one's single
        // link lands on its own terminal entry with an EMPTY RepliesList (ends the conversation
        // the same way the shipped sample's own terminal reply does).
        IReadOnlyList<GffStruct> promptReplies = entries[0].GetList("RepliesList");
        Assert.Equal(3, promptReplies.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(promptReplies[i].TryGetDword("Index", out uint index));
            Assert.Equal((uint)i, index);
        }

        for (int terminal = JediClassDialogueBlueprint.TerminalEntryGuardian;
             terminal <= JediClassDialogueBlueprint.TerminalEntrySentinel;
             terminal++)
        {
            Assert.Empty(entries[terminal].GetList("RepliesList"));
        }
    }

    /// <summary>Locates a committed profiles/override asset by walking up from the test binary.</summary>
    private static string FindShipped(string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "profiles", "override", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail($"shipped asset not found: {fileName}");
        return string.Empty;
    }
}
