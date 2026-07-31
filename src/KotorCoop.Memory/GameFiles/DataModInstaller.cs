using System.Globalization;

namespace KotorCoop.Memory.GameFiles;

/// <summary>What one install run did: the row indices co-op content should reference,
/// and whether each file actually had to be written this time.</summary>
public sealed record DataModInstallResult(
    string OverrideDirectory,
    int FactionRowIndex,
    bool ReputeWritten,
    int AppearanceRowIndex,
    bool AppearanceWritten);

/// <summary>
/// The no-RE 2DA merge step from docs/research/no-re-features.md items 1–3: gives the
/// co-op phantom a faction that never trips auto-hostility (repute.2da "coop_player")
/// and a second-player body distinct from the host's (appearance.2da "coop_p2").
///
/// Merge semantics, in the TSLPatcher spirit rather than the raw-drop one:
///
///   The source of each table is whatever the ENGINE would load — the user's own
///   Override copy when one exists (a faction or appearance mod, typically), else the
///   stock copy out of chitin.key/BIF. Every existing row, column and cell is carried
///   over verbatim and our additions are appended after them, so a modded table keeps
///   its mods and our row indices adapt to whatever the user's file already holds.
///
///   Idempotent by label: a run that finds "coop_player"/"coop_p2" already present
///   reports the existing indices and writes nothing, so re-running (or running after
///   another mod re-merged the table) never duplicates rows.
///
///   The merged file is written to Override, where it outranks the BIF copy — the same
///   precedence the readers model. dialog.tlk is never touched: the new rows carry no
///   StrRef of ours, display names stay in the plugin (research §4 note).
/// </summary>
public static class DataModInstaller
{
    /// <summary>The repute.2da label of the co-op faction row (and its mirror column).</summary>
    public const string FactionLabel = "coop_player";

    /// <summary>The appearance.2da label of the second player's body row.</summary>
    public const string AppearanceLabel = "coop_p2";

    /// <summary>
    /// repute.2da labels that mean "on the player's side". Semantics of the stock table
    /// (verified against the GOG copy; matches the Deadly Stream 2DA write-up and reone's
    /// reputes implementation): rows and columns are both factions — rows 0..20 are
    /// Player..Sacrifice, the columns are the same factions minus Player — and the cell
    /// at (row, column) is the standing between the two factions: 0 hostile, 50 neutral,
    /// 100 friendly. Row 0 ("Player") reads: Hostile_1/2 are 0 toward the player,
    /// Friendly_1/2 are 100, Neutral 50, glb_xor 100. The stock file itself misspells
    /// faction 8: its row label is "glb_xor", its column label "gbl_xor" — both are
    /// listed here so the match survives either spelling.
    /// </summary>
    private static readonly string[] PlayerAlignedFactions =
        ["player", "friendly_1", "friendly_2", "glb_xor", "gbl_xor"];

    /// <summary>
    /// Donor rows for the P2 body, most preferred first: stock human PC appearance rows,
    /// labelled P_&lt;sex&gt;_&lt;head class A..C&gt;_&lt;SML/MED/LRG build&gt;_&lt;texture
    /// variant 01..05&gt; (rows 91–180 in the stock table). Texture variant 05 is chosen
    /// precisely because character generation defaults to variant 01 — the clone reads as
    /// a different person next to a stock host without needing any new art. Several
    /// fallbacks in case an appearance overhaul renamed the first choice.
    /// </summary>
    private static readonly string[] AppearanceDonors =
        ["P_MAL_B_MED_05", "P_MAL_B_MED_04", "P_FEM_B_MED_05", "P_MAL_C_MED_01", "P_MAL_A_MED_01"];

    /// <summary>
    /// Merges both tables into <paramref name="gameDirectory"/>'s Override. Throws
    /// <see cref="InvalidOperationException"/> when a table cannot be resolved or is too
    /// mangled to merge safely (no label column, no recognisable donor row) — refusing
    /// beats writing a guess into the user's install.
    /// </summary>
    public static DataModInstallResult Install(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameDirectory);

        if (!Directory.Exists(gameDirectory))
        {
            throw new InvalidOperationException($"game directory not found: {gameDirectory}");
        }

        using GameFiles files = new(gameDirectory);
        string overrideDirectory = EnsureOverrideDirectory(gameDirectory);

        (int factionRow, bool reputeWritten) = MergeRepute(files, overrideDirectory);
        (int appearanceRow, bool appearanceWritten) = MergeAppearance(files, overrideDirectory);

        return new DataModInstallResult(
            overrideDirectory, factionRow, reputeWritten, appearanceRow, appearanceWritten);
    }

    // -- repute.2da ----------------------------------------------------------------

    /// <summary>
    /// Adds the co-op faction as BOTH a row and a column, because the standing between
    /// two factions is looked up with one of them as the row and the other as the
    /// column: the row alone covers "coop_player vs each stock faction", but the reverse
    /// direction — and above all Player vs coop_player, since Player is a row with no
    /// column of its own — needs a coop_player column for row 0 to hold a 100 in.
    /// Appending the column also keeps the positional model consistent (column i pairs
    /// with faction i+1 in the stock layout, so new faction N+1 gets column N).
    ///
    /// Values: 100 mutual with the player-aligned factions and itself, 50 (neutral)
    /// with everything else — including hostiles and any column another mod added that
    /// this code has never heard of. Neutral is the safety default: the phantom neither
    /// provokes nor is provoked, which is the entire point of the faction (research §5
    /// item 1 — deletes the plugin's per-tick faction patch once live-verified).
    /// </summary>
    private static (int Row, bool Written) MergeRepute(GameFiles files, string overrideDirectory)
    {
        TwoDa table = ReadTable(files, "repute");
        int labelColumn = RequireLabelColumn(table, "repute.2da");

        int existingRow = FindRowByLabel(table, labelColumn, FactionLabel);
        bool hasColumn = table.TryGetColumnIndex(FactionLabel, out _);

        if (existingRow >= 0 && hasColumn)
        {
            return (existingRow, false);
        }

        TwoDaWriter writer = new(table);

        if (!hasColumn)
        {
            writer.AddColumn(FactionLabel, "50");

            for (int r = 0; r < table.RowCount; r++)
            {
                if (IsPlayerAligned(table.Get(r, labelColumn)))
                {
                    writer.SetCell(r, FactionLabel, "100");
                }
            }
        }

        int row = existingRow;

        if (existingRow < 0)
        {
            // One cell per column of the MERGED table (including the coop_player column
            // just added, where the faction is 100 with itself like every stock diagonal).
            List<string> cells = new(writer.Columns.Count);

            foreach (string column in writer.Columns)
            {
                cells.Add(
                    string.Equals(column, "label", StringComparison.OrdinalIgnoreCase) ? FactionLabel
                    : IsPlayerAligned(column) || string.Equals(column, FactionLabel, StringComparison.OrdinalIgnoreCase) ? "100"
                    : "50");
            }

            row = writer.AddRow(table.RowCount.ToString(CultureInfo.InvariantCulture), cells);
        }
        else
        {
            // Partial earlier state: the row existed without its column. Finish the job.
            writer.SetCell(existingRow, FactionLabel, "100");
        }

        WriteTable(writer, overrideDirectory, "repute.2da");
        return (row, true);
    }

    private static bool IsPlayerAligned(string factionLabel) =>
        Array.Exists(
            PlayerAlignedFactions,
            aligned => string.Equals(aligned, factionLabel, StringComparison.OrdinalIgnoreCase));

    // -- appearance.2da ------------------------------------------------------------

    /// <summary>
    /// Appends "coop_p2" as a verbatim clone of a stock human PC row with only the label
    /// changed: same models, same head, same walk/run rates, just a texture variant the
    /// host's default character is not wearing. Cloning the whole row (all 80 stock
    /// columns, or however many the user's modded table has) is deliberate — a PC row is
    /// known-good for every column the engine reads, so nothing is guessed.
    /// </summary>
    private static (int Row, bool Written) MergeAppearance(GameFiles files, string overrideDirectory)
    {
        TwoDa table = ReadTable(files, "appearance");
        int labelColumn = RequireLabelColumn(table, "appearance.2da");

        int existingRow = FindRowByLabel(table, labelColumn, AppearanceLabel);
        if (existingRow >= 0)
        {
            return (existingRow, false);
        }

        int donor = FindAppearanceDonor(table, labelColumn);

        TwoDaWriter writer = new(table);
        List<string> cells = new(table.Columns.Count);

        for (int c = 0; c < table.Columns.Count; c++)
        {
            cells.Add(c == labelColumn ? AppearanceLabel : table.Get(donor, c));
        }

        int row = writer.AddRow(table.RowCount.ToString(CultureInfo.InvariantCulture), cells);

        WriteTable(writer, overrideDirectory, "appearance.2da");
        return (row, true);
    }

    private static int FindAppearanceDonor(TwoDa table, int labelColumn)
    {
        foreach (string donor in AppearanceDonors)
        {
            int row = FindRowByLabel(table, labelColumn, donor);
            if (row >= 0)
            {
                return row;
            }
        }

        // Overhauled table: settle for any male/female PC row (the P_ prefix is the
        // stock naming for the player-selectable bodies).
        for (int r = 0; r < table.RowCount; r++)
        {
            string label = table.Get(r, labelColumn);
            if (label.StartsWith("P_MAL_", StringComparison.OrdinalIgnoreCase)
                || label.StartsWith("P_FEM_", StringComparison.OrdinalIgnoreCase))
            {
                return r;
            }
        }

        throw new InvalidOperationException(
            "appearance.2da has no recognisable player-appearance row to clone "
            + $"(looked for {string.Join(", ", AppearanceDonors)} and any P_MAL_*/P_FEM_* label)");
    }

    // -- shared plumbing -----------------------------------------------------------

    private static TwoDa ReadTable(GameFiles files, string resref)
    {
        if (!files.TryReadResource(resref, "2da", out byte[] data))
        {
            throw new InvalidOperationException(
                $"{resref}.2da not found in {files.GameDirectory} (neither Override nor chitin.key/BIF)");
        }

        return TwoDa.Parse(data);
    }

    private static int RequireLabelColumn(TwoDa table, string fileName) =>
        table.TryGetColumnIndex("label", out int index)
            ? index
            : throw new InvalidOperationException(
                $"{fileName} has no 'label' column; cannot merge without duplicating rows");

    private static int FindRowByLabel(TwoDa table, int labelColumn, string label)
    {
        for (int r = 0; r < table.RowCount; r++)
        {
            if (string.Equals(table.Get(r, labelColumn), label, StringComparison.OrdinalIgnoreCase))
            {
                return r;
            }
        }

        return -1;
    }

    private static void WriteTable(TwoDaWriter writer, string overrideDirectory, string fileName)
    {
        byte[] merged = writer.ToBytes();

        // Read-back self-check before touching the user's install: if these bytes do not
        // parse, the bug is ours and nothing gets written.
        TwoDa.Parse(merged);

        File.WriteAllBytes(Path.Combine(overrideDirectory, fileName), merged);
    }

    /// <summary>The install's override directory, located case-insensitively the way
    /// <see cref="GameFiles"/> does (GOG ships "Override") and created when absent — a
    /// fresh install may not have one, and the engine picks it up without complaint.</summary>
    private static string EnsureOverrideDirectory(string gameDirectory)
    {
        foreach (string path in Directory.EnumerateDirectories(gameDirectory))
        {
            if (string.Equals(Path.GetFileName(path), "override", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return Directory.CreateDirectory(Path.Combine(gameDirectory, "Override")).FullName;
    }
}
