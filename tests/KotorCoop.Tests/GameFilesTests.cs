using System.Buffers.Binary;
using System.Text;
using KotorCoop.Memory.GameFiles;
using Xunit;
using GameFilesReader = KotorCoop.Memory.GameFiles.GameFiles;

namespace KotorCoop.Tests;

/// <summary>
/// Marks a test that needs the real GOG install. Skipped — not failed — when the game
/// is not present, so the suite stays green on machines without KOTOR while still
/// exercising the genuine data files wherever they exist.
/// </summary>
public sealed class GogInstallFactAttribute : FactAttribute
{
    public GogInstallFactAttribute()
    {
        if (!File.Exists(Path.Combine(GameFilesTests.GogDirectory, "chitin.key")))
        {
            Skip = $"no KOTOR install at {GameFilesTests.GogDirectory}";
        }
    }
}

/// <summary>
/// The GameFiles readers against both synthetic bytes (always run) and the real GOG
/// install (skipped when absent). The observed reference numbers, verified against
/// tools/kotorfiles.py on the same install: appearance.2da is 509 rows x 80 columns,
/// global.jrl has 101 categories, dialog.tlk has 49265 entries.
/// </summary>
public sealed class GameFilesTests
{
    public const string GogDirectory = @"C:\GOG Games\Star Wars KOTOR (GOG)";

    // ---- real install ----------------------------------------------------------

    [GogInstallFact]
    public void ReadsAppearanceTwoDaFromTheRealInstall()
    {
        using GameFilesReader files = new(GogDirectory);

        Assert.True(files.IsUsable, "chitin.key exists but did not parse");
        Assert.True(files.TryReadResource("appearance", "2da", out byte[] data), "appearance.2da not found");

        TwoDa table = TwoDa.Parse(data);

        Assert.True(table.RowCount > 100, $"only {table.RowCount} rows");
        Assert.Contains("label", table.Columns);
        Assert.Contains("race", table.Columns);

        // Row 0's label column is empty in the shipped table; walkdist is not — a spot
        // check that cell addressing (not just the header) decodes correctly.
        Assert.Equal("1.7", table.Get(0, "walkdist"));
    }

    [GogInstallFact]
    public void ReadsTheGlobalJournalGff()
    {
        using GameFilesReader files = new(GogDirectory);

        // GOG-build quirk, verified against the Python reference: chitin.key indexes the
        // journal as "global" with restype 2056 (utw), but the bytes in _newbif.bif are
        // the "JRL " GFF. Ask for the honest name first, then the mislabelled one.
        if (!files.TryReadResource("global", "jrl", out byte[] data))
        {
            Assert.True(files.TryReadResource("global", "utw", out data), "global.jrl not found under either type");
        }

        Gff journal = Gff.Parse(data);

        Assert.Equal("JRL ", journal.FileType);

        IReadOnlyList<GffStruct> categories = journal.Root.GetList("Categories");
        Assert.NotEmpty(categories);

        foreach (GffStruct category in categories)
        {
            Assert.True(category.TryGetString("Tag", out string tag), "category without a Tag field");
            Assert.NotEqual(string.Empty, tag);
        }
    }

    [GogInstallFact]
    public void ReadsTheDialogTlkHeader()
    {
        using GameFilesReader files = new(GogDirectory);

        Assert.True(files.TryReadTlkHeader(out int entryCount, out long fileSize));

        // KOTOR 1 ships ~49k strings; anything drastically smaller means the header
        // was read at the wrong offset, not that the game shrank.
        Assert.True(entryCount > 10_000, $"implausible entry count {entryCount}");
        Assert.True(fileSize > entryCount * 40L, "file smaller than its own entry table");
    }

    [GogInstallFact]
    public void CensusesAreSortedLowercaseAndNonEmpty()
    {
        using GameFilesReader files = new(GogDirectory);

        IReadOnlyList<(string Name, long Size)> modules = files.ModulesCensus();
        Assert.NotEmpty(modules);

        for (int i = 0; i < modules.Count; i++)
        {
            Assert.Equal(modules[i].Name, modules[i].Name.ToLowerInvariant());

            if (i > 0)
            {
                Assert.True(string.CompareOrdinal(modules[i - 1].Name, modules[i].Name) < 0);
            }
        }
    }

    [Fact]
    public void MissingInstallDegradesInsteadOfThrowing()
    {
        using GameFilesReader files = new(@"C:\definitely\not\a\kotor\install");

        Assert.False(files.IsUsable);
        Assert.False(files.TryReadResource("appearance", "2da", out _));
        Assert.Empty(files.OverrideCensus());
        Assert.False(files.TryReadTlkHeader(out _, out _));
    }

    // ---- pure GFF round trip ---------------------------------------------------

    [Fact]
    public void ParsesAHandBuiltGff()
    {
        Gff gff = Gff.Parse(BuildTinyGff());

        Assert.Equal("TEST", gff.FileType);

        Assert.True(gff.Root.TryGetInt("Count", out int count));
        Assert.Equal(-5, count);

        Assert.True(gff.Root.TryGetString("Name", out string name));
        Assert.Equal("Revan", name);

        GffLocString? loc = gff.Root.GetLocString("LocName");
        Assert.NotNull(loc);
        Assert.Equal(-1, loc.StrRef);
        Assert.Equal("Hello", loc.Text());

        IReadOnlyList<GffStruct> items = gff.Root.GetList("Items");
        Assert.Single(items);
        Assert.Equal(7u, items[0].StructId);

        Assert.True(items[0].TryGetResRef("Tag", out string tag));
        Assert.Equal("endar", tag);

        // ResRef must not answer for a plain CExoString, or type confusion hides bugs.
        Assert.False(gff.Root.TryGetResRef("Name", out _));

        // Absent fields: false/empty, never a throw.
        Assert.False(gff.Root.TryGetInt("Nope", out _));
        Assert.Empty(gff.Root.GetList("Nope"));
    }

    [Fact]
    public void RejectsBadGffMagicAndTruncation()
    {
        byte[] good = BuildTinyGff();

        byte[] wrongVersion = (byte[])good.Clone();
        Encoding.ASCII.GetBytes("V9.9").CopyTo(wrongVersion, 4);
        Assert.Throws<InvalidDataException>(() => Gff.Parse(wrongVersion));

        Assert.Throws<InvalidDataException>(() => Gff.Parse(good[..40]));
        Assert.Throws<InvalidDataException>(() => Gff.Parse(good[..80]));
    }

    [Fact]
    public void ParsesAHandBuiltBinaryTwoDa()
    {
        TwoDa table = TwoDa.Parse(BuildTinyBinaryTwoDa());

        Assert.Equal(2, table.RowCount);
        Assert.Equal(new[] { "label", "value" }, table.Columns);
        Assert.Equal("alpha", table.Get(0, "label"));
        Assert.Equal("1", table.Get(0, 1));
        Assert.Equal("beta", table.Get(1, "LABEL")); // column lookup is case-insensitive
        Assert.Equal(string.Empty, table.Get(1, "value"));

        Assert.Throws<KeyNotFoundException>(() => table.Get(0, "nope"));
        Assert.Throws<InvalidDataException>(() => TwoDa.Parse("not a table"u8.ToArray()));
    }

    [Fact]
    public void ParsesAHandBuiltAsciiTwoDa()
    {
        byte[] data = Encoding.ASCII.GetBytes(
            "2DA V2.0\n\n label value\n0 \"alpha one\" ****\n1 beta 2\n");

        TwoDa table = TwoDa.Parse(data);

        Assert.Equal(2, table.RowCount);
        Assert.Equal(new[] { "label", "value" }, table.Columns);
        Assert.Equal("alpha one", table.Get(0, "label"));    // quoted cell keeps its space
        Assert.Equal(string.Empty, table.Get(0, "value"));   // **** reads as empty
        Assert.Equal("2", table.Get(1, "value"));
    }

    // ---- builders ----------------------------------------------------------------

    /// <summary>
    /// A minimal but complete GFF V3.2: a root with an INT, a CExoString, a
    /// CExoLocString and a List holding one child struct with a ResRef. Exercises the
    /// inline-value path, the field-data block, the field-indices block and the
    /// list-indices block — every table the format has.
    /// </summary>
    private static byte[] BuildTinyGff()
    {
        // Layout: header 56 | structs 24 @56 | fields 60 @80 | labels 80 @140
        //         | field data 40 @220 | field indices 16 @260 | list indices 8 @276
        byte[] data = new byte[284];
        Span<byte> s = data;

        Encoding.ASCII.GetBytes("TESTV3.2").CopyTo(s);
        WriteU32(s, 8, 56);    // structOffset
        WriteU32(s, 12, 2);    // structCount
        WriteU32(s, 16, 80);   // fieldOffset
        WriteU32(s, 20, 5);    // fieldCount
        WriteU32(s, 24, 140);  // labelOffset
        WriteU32(s, 28, 5);    // labelCount
        WriteU32(s, 32, 220);  // fieldDataOffset
        WriteU32(s, 36, 40);   // fieldDataCount
        WriteU32(s, 40, 260);  // fieldIndicesOffset
        WriteU32(s, 44, 16);   // fieldIndicesCount
        WriteU32(s, 48, 276);  // listIndicesOffset
        WriteU32(s, 52, 8);    // listIndicesCount

        // Structs: root (4 fields via indices at byte 0), child id=7 (1 field, index 3).
        WriteU32(s, 56, 0);
        WriteU32(s, 60, 0);
        WriteU32(s, 64, 4);
        WriteU32(s, 68, 7);
        WriteU32(s, 72, 3);
        WriteU32(s, 76, 1);

        // Fields: (type, labelIndex, dataOrDataOffset).
        WriteU32(s, 80, 5);              // INT "Count"
        WriteU32(s, 84, 0);
        WriteU32(s, 88, unchecked((uint)-5));
        WriteU32(s, 92, 10);             // CExoString "Name" -> field data 0
        WriteU32(s, 96, 1);
        WriteU32(s, 100, 0);
        WriteU32(s, 104, 15);            // List "Items" -> list indices 0
        WriteU32(s, 108, 2);
        WriteU32(s, 112, 0);
        WriteU32(s, 116, 11);            // ResRef "Tag" -> field data 9
        WriteU32(s, 120, 3);
        WriteU32(s, 124, 9);
        WriteU32(s, 128, 12);            // CExoLocString "LocName" -> field data 15
        WriteU32(s, 132, 4);
        WriteU32(s, 136, 15);

        // Labels: five 16-byte NUL-padded cells.
        string[] labels = ["Count", "Name", "Items", "Tag", "LocName"];
        for (int i = 0; i < labels.Length; i++)
        {
            Encoding.ASCII.GetBytes(labels[i]).CopyTo(s[(140 + (16 * i))..]);
        }

        // Field data: CExoString(len 5 "Revan") | ResRef(len 5 "endar")
        //             | LocString(total, strref -1, 1 substring: id 0, len 5, "Hello").
        WriteU32(s, 220, 5);
        Encoding.ASCII.GetBytes("Revan").CopyTo(s[224..]);
        s[229] = 5;
        Encoding.ASCII.GetBytes("endar").CopyTo(s[230..]);
        WriteU32(s, 235, 21);
        WriteU32(s, 239, 0xFFFFFFFF);
        WriteU32(s, 243, 1);
        WriteU32(s, 247, 0);
        WriteU32(s, 251, 5);
        Encoding.ASCII.GetBytes("Hello").CopyTo(s[255..]);

        // Field indices for the root: fields 0, 1, 2, 4.
        WriteU32(s, 260, 0);
        WriteU32(s, 264, 1);
        WriteU32(s, 268, 2);
        WriteU32(s, 272, 4);

        // List indices: one entry, struct 1.
        WriteU32(s, 276, 1);
        WriteU32(s, 280, 1);

        return data;
    }

    /// <summary>A two-column, two-row binary 2DA with one deliberately empty cell.</summary>
    private static byte[] BuildTinyBinaryTwoDa()
    {
        List<byte> b = [.. Encoding.ASCII.GetBytes("2DA V2.b\n")];
        b.AddRange(Encoding.ASCII.GetBytes("label\tvalue\t"));
        b.Add(0);
        b.AddRange(BitConverter.GetBytes(2u));                   // row count
        b.AddRange(Encoding.ASCII.GetBytes("0\t1\t"));           // row labels

        // Cell offsets into the data block, row-major: alpha, 1, beta, (empty).
        foreach (ushort offset in (ushort[])[0, 6, 8, 13])
        {
            b.AddRange(BitConverter.GetBytes(offset));
        }

        b.AddRange(BitConverter.GetBytes((ushort)14));           // data block size
        b.AddRange(Encoding.ASCII.GetBytes("alpha\01\0beta\0\0"));
        return [.. b];
    }

    private static void WriteU32(Span<byte> destination, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset, 4), value);
}
