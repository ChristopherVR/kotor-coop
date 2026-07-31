using System.Text;
using KotorCoop.Memory.GameFiles;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The 2DA writer against the reader it must satisfy, and the DataModInstaller merge on
/// top of both. As with the GFF writer, <see cref="TwoDa.Parse"/> is the correctness
/// oracle — the claim is cell-for-cell fidelity after a round trip, not byte-identity
/// with BioWare's writer (whose string-pool ordering is its own). Installer tests run
/// against a synthetic install in a temp directory; the real GOG install is exercised
/// read-only and only where present.
/// </summary>
public sealed class TwoDaWriterTests : IDisposable
{
    private readonly string _tempDirectory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"kotorcoop-2da-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing the suite over.
        }
    }

    // ---- pure round trip ---------------------------------------------------------

    [Fact]
    public void RoundTripsASyntheticTableCellForCell()
    {
        // Empty cells included deliberately: the binary format stores them as real
        // zero-length pool entries, and they must come back as "" like the reader
        // normalises everything else to.
        TwoDaWriter builder = new(["label", "value", "note"]);
        builder.AddRow("0", ["alpha", "1", ""]);
        builder.AddRow("1", ["beta", "", "shared"]);
        builder.AddRow("2", ["gamma", "1", "shared"]);

        TwoDa first = TwoDa.Parse(builder.ToBytes());
        TwoDa second = TwoDa.Parse(TwoDaWriter.Write(first));

        AssertSameTable(first, second);
        Assert.Equal("", second.Get(0, "note"));
        Assert.Equal("", second.Get(1, "value"));
        Assert.Equal("shared", second.Get(2, "note"));
    }

    [Fact]
    public void DeduplicatesRepeatedCellsLikeTheStockFilesDo()
    {
        // 200 rows of identical cells: undeduplicated this is ~1.4 KB of pool text,
        // deduplicated it is two strings. The uint16 offsets make deduplication a
        // correctness requirement (stock appearance.2da overflows without it), so the
        // pool size is asserted, not just eyeballed.
        TwoDaWriter builder = new(["a", "b"]);
        for (int i = 0; i < 200; i++)
        {
            builder.AddRow(i.ToString(), ["same", "50"]);
        }

        byte[] bytes = builder.ToBytes();
        TwoDa table = TwoDa.Parse(bytes);

        Assert.Equal(200, table.RowCount);
        Assert.Equal("same", table.Get(199, "a"));

        // The pool is the file's tail and the uint16 dataBlockSize sits just before it:
        // "same\0" + "50\0" = 8 bytes, whatever the row count.
        byte[] pool = bytes[^8..];
        Assert.Equal(Encoding.ASCII.GetBytes("same\0" + "50\0"), pool);
        Assert.Equal(8, bytes[^10] | (bytes[^9] << 8));
    }

    [Fact]
    public void RefusesAPoolThatCannotFitUint16Offsets()
    {
        // ~10k unique 9-byte entries = ~90 KB of pool, past what uint16 can address.
        TwoDaWriter builder = new(["value"]);
        for (int i = 0; i < 10_000; i++)
        {
            builder.AddRow(i.ToString(), [$"cell{i:D5}"]);
        }

        Assert.Throws<InvalidOperationException>(() => builder.ToBytes());
    }

    [Fact]
    public void RejectsFramingBytesInLabelsAndCells()
    {
        TwoDaWriter tabbed = new(["a\tb"]);
        tabbed.AddRow("0", ["x"]);
        Assert.Throws<ArgumentException>(() => tabbed.ToBytes());

        TwoDaWriter nulled = new(["a"]);
        nulled.AddRow("0", ["x\0y"]);
        Assert.Throws<ArgumentException>(() => nulled.ToBytes());

        TwoDaWriter builder = new(["a"]);
        Assert.Throws<ArgumentException>(() => builder.AddRow("0", ["x", "extra"]));
    }

    // ---- append and edit ---------------------------------------------------------

    [Fact]
    public void AppendedRowsAndColumnsSurviveAReRead()
    {
        TwoDaWriter builder = new(["label", "value"]);
        builder.AddRow("0", ["alpha", "1"]);
        TwoDa original = TwoDa.Parse(builder.ToBytes());

        TwoDaWriter merged = new(original);
        int column = merged.AddColumn("extra", "50");
        int row = merged.AddRow("1", ["beta", "2", "100"]);
        merged.SetCell(0, "EXTRA", "75"); // column addressing is case-insensitive

        Assert.Equal(2, column);
        Assert.Equal(1, row);

        TwoDa table = TwoDa.Parse(merged.ToBytes());

        Assert.Equal(new[] { "label", "value", "extra" }, table.Columns);
        Assert.Equal(new[] { "0", "1" }, table.RowLabels);
        Assert.Equal("alpha", table.Get(0, "label")); // pre-existing cells untouched
        Assert.Equal("1", table.Get(0, "value"));
        Assert.Equal("75", table.Get(0, "extra"));
        Assert.Equal("beta", table.Get(1, "label"));
        Assert.Equal("100", table.Get(1, "extra"));

        Assert.Throws<KeyNotFoundException>(() => merged.SetCell(0, "nope", "1"));
    }

    // ---- the installer against a synthetic install -------------------------------

    [Fact]
    public void InstallerMergesBothTablesAndReportsTheNewRows()
    {
        string overrideDirectory = WriteSyntheticInstall();

        DataModInstallResult result = DataModInstaller.Install(_tempDirectory);

        Assert.Equal(overrideDirectory, result.OverrideDirectory);
        Assert.True(result.ReputeWritten);
        Assert.True(result.AppearanceWritten);
        Assert.Equal(4, result.FactionRowIndex);     // after Player..Neutral (0..3)
        Assert.Equal(2, result.AppearanceRowIndex);  // after the two synthetic rows

        TwoDa repute = TwoDa.Parse(
            File.ReadAllBytes(Path.Combine(overrideDirectory, "repute.2da")));

        // The faction is both a row and a column: the row covers coop-vs-others, the
        // column covers others-vs-coop — critically Player, which has no column of its
        // own in the stock layout.
        Assert.Equal(5, repute.RowCount);
        Assert.Contains("coop_player", repute.Columns);
        Assert.Equal("coop_player", repute.Get(4, "label"));
        Assert.Equal("100", repute.Get(4, "friendly_1")); // friendly to player-aligned
        Assert.Equal("50", repute.Get(4, "hostile_1"));   // neutral, never provoking
        Assert.Equal("100", repute.Get(4, "coop_player")); // itself, like the diagonal
        Assert.Equal("100", repute.Get(0, "coop_player")); // Player likes the phantom
        Assert.Equal("50", repute.Get(1, "coop_player"));  // hostiles ignore it
        Assert.Equal("100", repute.Get(2, "coop_player")); // Friendly_1 likes it

        // Pre-existing cells survive the merge byte-for-value.
        Assert.Equal("Player", repute.Get(0, "label"));
        Assert.Equal("100", repute.Get(0, "friendly_1"));
        Assert.Equal("0", repute.Get(1, "friendly_1"));

        TwoDa appearance = TwoDa.Parse(
            File.ReadAllBytes(Path.Combine(overrideDirectory, "appearance.2da")));

        // coop_p2 is a clone of the donor PC row with only the label changed.
        Assert.Equal(3, appearance.RowCount);
        Assert.Equal("coop_p2", appearance.Get(2, "label"));
        Assert.Equal("PMBMM", appearance.Get(2, "modela"));
        Assert.Equal("PMBBM05", appearance.Get(2, "texa"));
        Assert.Equal("B", appearance.Get(2, "modeltype"));
        Assert.Equal("n_minion", appearance.Get(0, "modela")); // untouched neighbour
    }

    [Fact]
    public void InstallerIsIdempotentAcrossRuns()
    {
        string overrideDirectory = WriteSyntheticInstall();

        DataModInstallResult first = DataModInstaller.Install(_tempDirectory);
        byte[] reputeAfterFirst = File.ReadAllBytes(Path.Combine(overrideDirectory, "repute.2da"));
        byte[] appearanceAfterFirst = File.ReadAllBytes(Path.Combine(overrideDirectory, "appearance.2da"));

        DataModInstallResult second = DataModInstaller.Install(_tempDirectory);

        // Same rows reported, nothing rewritten, bytes untouched: label detection, not
        // "did I run before" bookkeeping, is what makes it safe.
        Assert.Equal(first.FactionRowIndex, second.FactionRowIndex);
        Assert.Equal(first.AppearanceRowIndex, second.AppearanceRowIndex);
        Assert.False(second.ReputeWritten);
        Assert.False(second.AppearanceWritten);
        Assert.Equal(reputeAfterFirst, File.ReadAllBytes(Path.Combine(overrideDirectory, "repute.2da")));
        Assert.Equal(appearanceAfterFirst, File.ReadAllBytes(Path.Combine(overrideDirectory, "appearance.2da")));
    }

    [Fact]
    public void InstallerRefusesAnInstallWithoutTheTables()
    {
        // No Override tables and no chitin.key: refusing beats guessing.
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "Override"));

        Assert.Throws<InvalidOperationException>(() => DataModInstaller.Install(_tempDirectory));
    }

    // ---- the real install, read-only ---------------------------------------------

    [GogInstallFact]
    public void RoundTripsTheStockTablesFromTheRealInstall()
    {
        using KotorCoop.Memory.GameFiles.GameFiles files = new(GameFilesTests.GogDirectory);

        foreach (string resref in (string[])["repute", "appearance"])
        {
            Assert.True(files.TryReadResource(resref, "2da", out byte[] data), $"{resref}.2da not found");

            TwoDa original = TwoDa.Parse(data);
            TwoDa rewritten = TwoDa.Parse(TwoDaWriter.Write(original));

            AssertSameTable(original, rewritten);
        }
    }

    // ---- fixtures ----------------------------------------------------------------

    /// <summary>
    /// A miniature install: an Override holding shrunken but structurally faithful
    /// repute.2da and appearance.2da (real column names, stock-shaped rows, one modded
    /// extra to prove merges preserve it). No chitin.key — the installer must work from
    /// Override alone, which is also the precedence the engine gives these files.
    /// </summary>
    private string WriteSyntheticInstall()
    {
        string overrideDirectory = Directory.CreateDirectory(
            Path.Combine(_tempDirectory, "Override")).FullName;

        TwoDaWriter repute = new(["label", "hostile_1", "friendly_1", "neutral"]);
        repute.AddRow("0", ["Player", "0", "100", "50"]);
        repute.AddRow("1", ["Hostile_1", "100", "0", "50"]);
        repute.AddRow("2", ["Friendly_1", "0", "100", "50"]);
        repute.AddRow("3", ["Neutral", "50", "50", "100"]);
        File.WriteAllBytes(Path.Combine(overrideDirectory, "repute.2da"), repute.ToBytes());

        TwoDaWriter appearance = new(["label", "modeltype", "modela", "texa", "race"]);
        appearance.AddRow("0", ["Commoner", "B", "n_minion", "n_minion01", ""]);
        appearance.AddRow("1", ["P_MAL_B_MED_05", "B", "PMBMM", "PMBBM05", "PMBBM"]);
        File.WriteAllBytes(Path.Combine(overrideDirectory, "appearance.2da"), appearance.ToBytes());

        return overrideDirectory;
    }

    private static void AssertSameTable(TwoDa expected, TwoDa actual)
    {
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.RowLabels, actual.RowLabels);
        Assert.Equal(expected.RowCount, actual.RowCount);

        for (int r = 0; r < expected.RowCount; r++)
        {
            for (int c = 0; c < expected.Columns.Count; c++)
            {
                Assert.Equal(expected.Get(r, c), actual.Get(r, c));
            }
        }
    }
}
