using System.Buffers.Binary;
using System.Text;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="GlobalFlagCraft"/> against a synthetic save directory built with the real
/// <see cref="GffWriter"/> — the same fixture discipline
/// <see cref="SaveDigestTests"/> uses. What matters: the source is never modified, only
/// the requested names' bits/bytes in the destination's GLOBALVARS.res change (everything
/// else round-trips byte-identical field for field), an unknown name is reported and left
/// alone rather than fabricated, and <see cref="GlobalFlagCraft.TryVerify"/> actually
/// catches a mismatch rather than always agreeing with itself.
/// </summary>
public sealed class GlobalFlagCraftTests : IDisposable
{
    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        foreach (string directory in _tempDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a leaked temp directory must not fail the suite.
            }
        }
    }

    [Fact]
    public void ForcingADeclaredBooleanTrueFlipsOnlyThatBitAndVerifies()
    {
        string source = BuildSaveDirectory(
            booleanNames: ["Tar_SwoopNew", "Tar_SwoopRace", "TAR_FIRST_SWOOP_USE"],
            numberNames: ["Tar_SwoopStatus"]);
        string dest = FreshDestinationPath();

        bool ok = GlobalFlagCraft.TryCraft(
            source, dest,
            new Dictionary<string, bool> { ["TAR_FIRST_SWOOP_USE"] = true },
            numberFlags: null,
            out IReadOnlyList<GlobalFlagCraft.FlagResult> results,
            out string diagnostic);

        Assert.True(ok, diagnostic);
        GlobalFlagCraft.FlagResult result = Assert.Single(results);
        Assert.True(result.Found);
        Assert.Equal(0, result.PreviousValue);
        Assert.Equal(1, result.NewValue);

        // Every other declared boolean stayed at its original (default 0) value.
        Gff globals = Gff.Parse(File.ReadAllBytes(Path.Combine(dest, "GLOBALVARS.res")));
        Assert.True(TryReadBoolean(globals, "Tar_SwoopNew", out int untouched));
        Assert.Equal(0, untouched);
        Assert.True(TryReadBoolean(globals, "TAR_FIRST_SWOOP_USE", out int forced));
        Assert.Equal(1, forced);

        Assert.True(GlobalFlagCraft.TryVerify(
            dest,
            new Dictionary<string, bool> { ["TAR_FIRST_SWOOP_USE"] = true, ["Tar_SwoopNew"] = false },
            expectedNumbers: null,
            out IReadOnlyList<string> mismatches,
            out _));
        Assert.Empty(mismatches);
    }

    [Fact]
    public void TheSourceDirectoryIsNeverModified()
    {
        string source = BuildSaveDirectory(
            booleanNames: ["Tar_SwoopNew"], numberNames: []);
        byte[] before = File.ReadAllBytes(Path.Combine(source, "GLOBALVARS.res"));
        string dest = FreshDestinationPath();

        Assert.True(GlobalFlagCraft.TryCraft(
            source, dest,
            new Dictionary<string, bool> { ["Tar_SwoopNew"] = true },
            numberFlags: null,
            out _, out _));

        byte[] after = File.ReadAllBytes(Path.Combine(source, "GLOBALVARS.res"));
        Assert.Equal(before, after);
    }

    [Fact]
    public void ANameNotInTheCatalogueIsReportedNotFabricated()
    {
        string source = BuildSaveDirectory(booleanNames: ["Tar_SwoopNew"], numberNames: []);
        string dest = FreshDestinationPath();

        Assert.True(GlobalFlagCraft.TryCraft(
            source, dest,
            new Dictionary<string, bool> { ["NOT_A_REAL_GLOBAL"] = true },
            numberFlags: null,
            out IReadOnlyList<GlobalFlagCraft.FlagResult> results,
            out _));

        GlobalFlagCraft.FlagResult result = Assert.Single(results);
        Assert.False(result.Found);

        // Nothing was rewritten — the copy's GLOBALVARS.res is untouched from the source's.
        byte[] sourceBytes = File.ReadAllBytes(Path.Combine(source, "GLOBALVARS.res"));
        byte[] destBytes = File.ReadAllBytes(Path.Combine(dest, "GLOBALVARS.res"));
        Assert.Equal(sourceBytes, destBytes);
    }

    [Fact]
    public void ForcingADeclaredNumberRoundTrips()
    {
        string source = BuildSaveDirectory(booleanNames: [], numberNames: ["Tar_SwoopStatus"]);
        string dest = FreshDestinationPath();

        Assert.True(GlobalFlagCraft.TryCraft(
            source, dest,
            booleanFlags: null,
            new Dictionary<string, int> { ["Tar_SwoopStatus"] = 2 },
            out IReadOnlyList<GlobalFlagCraft.FlagResult> results,
            out _));

        GlobalFlagCraft.FlagResult result = Assert.Single(results);
        Assert.True(result.Found);
        Assert.Equal(0, result.PreviousValue);
        Assert.Equal(2, result.NewValue);

        Assert.True(GlobalFlagCraft.TryVerify(
            dest, expectedBooleans: null,
            new Dictionary<string, int> { ["Tar_SwoopStatus"] = 2 },
            out IReadOnlyList<string> mismatches, out _));
        Assert.Empty(mismatches);
    }

    [Fact]
    public void VerifyCatchesAGenuineMismatch()
    {
        string source = BuildSaveDirectory(booleanNames: ["Tar_SwoopNew"], numberNames: []);
        string dest = FreshDestinationPath();

        Assert.True(GlobalFlagCraft.TryCraft(
            source, dest,
            new Dictionary<string, bool> { ["Tar_SwoopNew"] = true },
            numberFlags: null,
            out _, out _));

        // Asking to verify the OPPOSITE of what was actually set must fail, not agree.
        Assert.False(GlobalFlagCraft.TryVerify(
            dest,
            new Dictionary<string, bool> { ["Tar_SwoopNew"] = false },
            expectedNumbers: null,
            out IReadOnlyList<string> mismatches, out _));
        Assert.Single(mismatches);
    }

    [Fact]
    public void RefusesToCraftOverAnExistingNonEmptyDestination()
    {
        string source = BuildSaveDirectory(booleanNames: ["Tar_SwoopNew"], numberNames: []);
        string dest = FreshDestinationPath();
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "already-here.txt"), "do not clobber me");

        Assert.False(GlobalFlagCraft.TryCraft(
            source, dest,
            new Dictionary<string, bool> { ["Tar_SwoopNew"] = true },
            numberFlags: null,
            out _, out string diagnostic));
        Assert.Contains("already exists", diagnostic);
    }

    [Fact]
    public void ANonSaveDirectoryFailsCleanly()
    {
        string notASave = TempDirectory();
        string dest = FreshDestinationPath();

        Assert.False(GlobalFlagCraft.TryCraft(
            notASave, dest,
            new Dictionary<string, bool> { ["X"] = true },
            numberFlags: null,
            out _, out string diagnostic));
        Assert.Contains("GLOBALVARS.res", diagnostic);
    }

    // ---- fixture builders --------------------------------------------------------------

    private string TempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"kotorcoop-flagcraft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);
        return directory;
    }

    private string FreshDestinationPath()
    {
        // A path that does not exist yet — TryCraft creates it. Tracked for cleanup the
        // same as any other temp directory this suite makes.
        string path = Path.Combine(Path.GetTempPath(), $"kotorcoop-flagcraft-dest-{Guid.NewGuid():N}");
        _tempDirectories.Add(path);
        return path;
    }

    /// <summary>A save directory holding a real GLOBALVARS.res (CatBoolean/ValBoolean and
    /// CatNumber/ValNumber, every declared entry defaulting to 0) plus a placeholder
    /// SAVEGAME.sav so it looks like an ordinary save directory to anything that lists
    /// files, matching the real container's shape (savenfo.res/SAVEGAME.sav/GLOBALVARS.res
    /// sitting side by side, not nested).</summary>
    private string BuildSaveDirectory(IReadOnlyList<string> booleanNames, IReadOnlyList<string> numberNames)
    {
        string directory = TempDirectory();

        GffWriter globals = new("GVT ");

        GffWriterList catBoolean = globals.Root.AddList("CatBoolean");
        foreach (string name in booleanNames)
        {
            catBoolean.Add(0).SetString("Name", name);
        }

        globals.Root.SetVoid("ValBoolean", new byte[(booleanNames.Count + 7) / 8]);

        GffWriterList catNumber = globals.Root.AddList("CatNumber");
        foreach (string name in numberNames)
        {
            catNumber.Add(0).SetString("Name", name);
        }

        globals.Root.SetVoid("ValNumber", new byte[numberNames.Count]);

        globals.Root.AddList("CatLocation");
        globals.Root.SetVoid("ValLocation", []);
        globals.Root.AddList("CatString");
        globals.Root.AddList("ValString");

        File.WriteAllBytes(Path.Combine(directory, "GLOBALVARS.res"), globals.ToBytes());

        // A placeholder SAVEGAME.sav — GlobalFlagCraft copies every file verbatim and does
        // not parse this one, so its content only needs to exist, matching a real save
        // directory's own file list.
        File.WriteAllBytes(Path.Combine(directory, "SAVEGAME.sav"), BuildMinimalErf());
        File.WriteAllText(Path.Combine(directory, "savenfo.res"), "placeholder, not parsed by this tool");

        return directory;
    }

    private static bool TryReadBoolean(Gff globals, string name, out int value)
    {
        value = 0;
        IReadOnlyList<GffStruct> catalogue = globals.Root.GetList("CatBoolean");
        byte[] blob = globals.Root.Fields.TryGetValue("ValBoolean", out object? raw) && raw is byte[] b ? b : [];

        for (int i = 0; i < catalogue.Count; i++)
        {
            if (catalogue[i].TryGetString("Name", out string entryName)
                && string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
            {
                int byteIndex = i >> 3;
                int bit = 7 - (i & 7);
                value = byteIndex < blob.Length ? (blob[byteIndex] >> bit) & 1 : 0;
                return true;
            }
        }

        return false;
    }

    private static byte[] BuildMinimalErf()
    {
        // A trivially valid, empty "SAV " ERF (160-byte header, no members) — enough to
        // exist as a file GlobalFlagCraft copies without reading.
        byte[] file = new byte[160];
        Encoding.ASCII.GetBytes("SAV ").CopyTo(file, 0);
        Encoding.ASCII.GetBytes("V1.0").CopyTo(file, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x10, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x18, 4), 160);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x1C, 4), 160);
        return file;
    }
}
