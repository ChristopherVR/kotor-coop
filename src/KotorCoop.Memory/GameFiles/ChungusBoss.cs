using System.Globalization;

namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// The arena boss's creature blueprint, <c>k_coop_chungus.utc</c>
/// (docs/research/chungus-boss.md). His model, texture and sounds are the mod's own files and
/// ship as they are (<c>Override\k_coop_chungus.mdl/.mdx/.tga</c>, <c>k_coop_ch*.wav</c>, built by
/// tools/chungus/build.py). The blueprint cannot ship ready-made, because it names a row of
/// appearance.2da and that row's number depends on the player's own table (a mod may have added
/// rows): <see cref="ChungusInstaller"/> adds the row and writes this file with its number.
/// (Two textures: the body's, and the mineral seams' with a .txi beside it that tells the engine to
/// mirror its stock metal reflection in it, as its droids' textures do.)
///
/// The field set follows the game's own hostile creatures (g_kathhound01, g_rancor01): the
/// stock default combat scripts by name, so the engine's ordinary AI walks him at the party and
/// swings; a stock creature weapon in a claw slot; twenty levels of soldier for his attack
/// bonus. Nothing here is a BioWare file: the scripts and the weapon are named, not copied.
/// </summary>
public static class ChungusBlueprint
{
    public const string ResRef = "k_coop_chungus";

    public const string FileName = ResRef + ".utc";

    public const string Tag = "k_coop_chungus";

    public const string Name = "Cairn Sentinel";

    public const short HitPoints = 800;

    /// <summary>No borrowed creature voice; the resonator uses original synthesized audio.</summary>
    public const ushort SoundSet = ushort.MaxValue;

    /// <summary>repute.2da row 1, Hostile_1: hostile to the player, as every stock enemy is.</summary>
    public const ushort Faction = 1;

    /// <summary>The stock default creature scripts, field by field, as g_kathhound01 carries them.</summary>
    public static readonly IReadOnlyList<(string Field, string Script)> Scripts =
    [
        ("ScriptHeartbeat", "k_def_heartbt01"),
        ("ScriptOnNotice", "k_def_percept01"),
        ("ScriptSpellAt", "k_def_spellat01"),
        ("ScriptAttacked", "k_def_attacked01"),
        ("ScriptDamaged", "k_def_damage01"),
        ("ScriptDisturbed", "k_def_disturb01"),
        ("ScriptEndRound", "k_def_combend01"),
        ("ScriptEndDialogu", "k_def_endconv"),
        ("ScriptDialogue", "k_def_dialogue01"),
        ("ScriptSpawn", "k_def_spawn01"),
        ("ScriptRested", ""),
        ("ScriptDeath", "k_def_death01"),
        ("ScriptUserDefine", "k_def_userdef01"),
        ("ScriptOnBlocked", "k_def_blocked01"),
    ];

    /// <summary>Builds the .utc bytes for a given appearance.2da row. Deterministic.</summary>
    public static byte[] Build(ushort appearanceRow)
    {
        GffWriter writer = new("UTC ");
        GffWriterStruct root = writer.Root;

        root.SetResRef("TemplateResRef", ResRef);
        root.SetByte("Race", 6);
        root.SetByte("SubraceIndex", 0);
        root.SetLocString("FirstName", -1, (0, Name));   // in the file itself: dialog.tlk is never touched
        root.SetLocString("LastName", -1);
        root.SetWord("Appearance_Type", appearanceRow);
        root.SetByte("Gender", 0);
        root.SetInt("Phenotype", 0);
        root.SetWord("PortraitId", 0);
        root.SetLocString("Description", -1);
        root.SetString("Tag", Tag);
        root.SetResRef("Conversation", "");
        root.SetByte("IsPC", 0);
        root.SetWord("FactionID", Faction);
        root.SetByte("Disarmable", 0);
        root.SetString("Subrace", "");
        root.SetString("Deity", "");
        root.SetWord("SoundSetFile", SoundSet); // no stock voice set
        root.SetByte("Plot", 0);               // a plot creature is never mirrored to joiners
        root.SetByte("Interruptable", 1);
        root.SetByte("NoPermDeath", 0);
        root.SetByte("BodyBag", 0);
        root.SetByte("BodyVariation", 0);
        root.SetByte("TextureVar", 0);
        root.SetByte("Min1HP", 0);
        root.SetByte("PartyInteract", 0);
        root.SetByte("Str", 26);
        root.SetByte("Dex", 10);
        root.SetByte("Con", 20);
        root.SetByte("Int", 10);
        root.SetByte("Wis", 10);
        root.SetByte("Cha", 18);
        root.SetInt("WalkRate", 7);
        root.SetByte("NaturalAC", 6);
        root.SetShort("HitPoints", HitPoints);
        root.SetShort("CurrentHitPoints", HitPoints);
        root.SetShort("MaxHitPoints", HitPoints);
        root.SetShort("ForcePoints", 0);
        root.SetShort("CurrentForce", 0);
        root.SetShort("refbonus", 0);
        root.SetShort("willbonus", 4);
        root.SetShort("fortbonus", 4);
        root.SetByte("GoodEvil", 50);
        root.SetByte("LawfulChaotic", 0);
        root.SetFloat("ChallengeRating", 20f);
        root.SetByte("PerceptionRange", 11);

        foreach ((string field, string script) in Scripts)
        {
            root.SetResRef(field, script);
        }

        GffWriterList skills = root.AddList("SkillList");
        for (int i = 0; i < 8; i++)
        {
            skills.Add(0).SetByte("Rank", 0);
        }

        root.AddList("FeatList");
        root.AddList("TemplateList");
        root.AddList("SpecAbilityList");

        // Twenty levels of class 0 (Soldier), as g_rancor01 has.
        GffWriterStruct soldier = root.AddList("ClassList").Add(2);
        soldier.SetInt("Class", 0);
        soldier.SetShort("ClassLevel", 20);
        soldier.AddList("KnownList0");

        // One stock creature weapon in the left claw slot (struct id = the slot's bit).
        root.AddList("Equip_ItemList").Add(0x4000).SetResRef("EquippedRes", "g_w_crslash003");

        root.SetByte("PaletteID", 5);
        root.SetString("Comment", "KOTOR co-op arena boss (generated by ChungusBlueprint)");

        return writer.ToBytes();
    }
}

public enum ChungusInstallOutcome
{
    /// <summary>The row and the blueprint were already there and current.</summary>
    UpToDate,

    /// <summary>Something was written. The game reads its tables when it starts, so the boss
    /// only counts as ready the next time it does.</summary>
    Installed,

    /// <summary>Nothing could be installed; <see cref="ChungusInstallResult.Message"/> says why.</summary>
    Unavailable,
}

public sealed record ChungusInstallResult(ChungusInstallOutcome Outcome, int AppearanceRow, string Message);

/// <summary>
/// Makes the arena boss loadable in one install: adds his row to appearance.2da (the table that
/// says which model a creature wears) and writes his blueprint with that row's number.
///
/// The table is the player's own: the Override copy when a mod has put one there, otherwise the
/// game's, with one row appended. Every existing row, column and cell is carried over as it is,
/// so another mod's appearances are kept. <see cref="Uninstall"/> takes the row out again, and
/// removes the Override file altogether when nothing else was in it.
/// </summary>
public static class ChungusInstaller
{
    /// <summary>The row's label, by which it is found again.</summary>
    public const string AppearanceLabel = "coop_chungus";

    private const string TableFile = "appearance.2da";

    /// <summary>The single-model creature rows the new row is copied from, most preferred first.
    /// A whole stock row is copied so that every column the engine reads has a value it is known
    /// to accept; only the cells in <see cref="RowValues"/> are then changed.</summary>
    private static readonly string[] Donors = ["Creature_Gizka", "Creature_Tach", "Creature_Rancor_Small"];

    /// <summary>
    /// What makes the row his. walkdist and rundist are how far one walk and run cycle of the
    /// model carries him (tools/chungus/anims.py prints them), so his feet do not slide.
    /// </summary>
    public static readonly IReadOnlyList<(string Column, string Value)> RowValues =
    [
        ("label", AppearanceLabel),
        ("race", ChungusBlueprint.ResRef),
        ("racetex", ""),
        ("modeltype", "S"),
        ("walkdist", "2.0"),
        ("rundist", "6.3"),
        ("hitradius", "1.0"),
        ("perspace", "1.0"),
        ("creperspace", "1.2"),
        ("sizecategory", "4"),
        ("footsteptype", "5"),
        ("hitdist", "2.2"),
        ("prefatckdist", "2.6"),
        ("headtrack", "0"),
    ];

    /// <summary>The files that make up the boss beside the blueprint; all of them ship in the
    /// package's Override folder.</summary>
    public static readonly IReadOnlyList<string> ShippedFiles =
        ["k_coop_chungus.mdl", "k_coop_chungus.mdx", "k_coop_chungus.tga", "k_coop_chungusg.tga", "k_coop_chungusg.txi"];

    public static ChungusInstallResult Install(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameDirectory);

        try
        {
            string overrideDirectory = FindOverride(gameDirectory)
                ?? Directory.CreateDirectory(Path.Combine(gameDirectory, "Override")).FullName;

            foreach (string shipped in ShippedFiles)
            {
                if (!File.Exists(Path.Combine(overrideDirectory, shipped)))
                {
                    return new ChungusInstallResult(
                        ChungusInstallOutcome.Unavailable, -1, $"Override\\{shipped} is missing - run the setup again");
                }
            }

            TwoDa table;
            using (GameFiles files = new(gameDirectory))
            {
                if (!files.TryReadResource("appearance", "2da", out byte[] bytes))
                {
                    return new ChungusInstallResult(
                        ChungusInstallOutcome.Unavailable, -1, "the game's appearance.2da could not be read");
                }

                table = TwoDa.Parse(bytes);
            }

            if (!table.TryGetColumnIndex("label", out int labelColumn))
            {
                return new ChungusInstallResult(
                    ChungusInstallOutcome.Unavailable, -1, "appearance.2da has no label column");
            }

            bool wrote = false;
            int row = FindRow(table, labelColumn, AppearanceLabel);
            if (row < 0)
            {
                int donor = Donors.Select(label => FindRow(table, labelColumn, label)).FirstOrDefault(found => found >= 0, -1);
                if (donor < 0)
                {
                    return new ChungusInstallResult(
                        ChungusInstallOutcome.Unavailable, -1, "appearance.2da has no stock creature row to copy");
                }

                List<string> cells = [.. Enumerable.Range(0, table.Columns.Count).Select(c => table.Get(donor, c))];
                foreach ((string column, string value) in RowValues)
                {
                    if (table.TryGetColumnIndex(column, out int index))
                    {
                        cells[index] = value;
                    }
                }

                TwoDaWriter writer = new(table);
                row = writer.AddRow(table.RowCount.ToString(CultureInfo.InvariantCulture), cells);
                byte[] merged = writer.ToBytes();
                TwoDa.Parse(merged);   // never write a table that does not read back
                File.WriteAllBytes(Path.Combine(overrideDirectory, TableFile), merged);
                wrote = true;
            }
            else
            {
                // His row from an earlier version of the mod: bring the cells that are his up to
                // date (how far a walk or run cycle carries him changed with his animations).
                int found = row;
                List<(string Column, string Value)> stale = [.. RowValues.Where(cell =>
                    table.TryGetColumnIndex(cell.Column, out int index) && table.Get(found, index) != cell.Value)];
                if (stale.Count > 0)
                {
                    TwoDaWriter writer = new(table);
                    foreach ((string column, string value) in stale)
                    {
                        writer.SetCell(row, column, value);
                    }

                    byte[] merged = writer.ToBytes();
                    TwoDa.Parse(merged);
                    File.WriteAllBytes(Path.Combine(overrideDirectory, TableFile), merged);
                    wrote = true;
                }
            }

            if (row is < 0 or > ushort.MaxValue)
            {
                return new ChungusInstallResult(ChungusInstallOutcome.Unavailable, row, "the appearance row is out of range");
            }

            byte[] blueprint = ChungusBlueprint.Build((ushort)row);
            string blueprintPath = Path.Combine(overrideDirectory, ChungusBlueprint.FileName);
            if (!File.Exists(blueprintPath) || !File.ReadAllBytes(blueprintPath).AsSpan().SequenceEqual(blueprint))
            {
                File.WriteAllBytes(blueprintPath, blueprint);
                wrote = true;
            }

            return new ChungusInstallResult(
                wrote ? ChungusInstallOutcome.Installed : ChungusInstallOutcome.UpToDate, row,
                wrote ? $"appearance row {row} and the blueprint were written" : $"appearance row {row}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or FormatException)
        {
            return new ChungusInstallResult(ChungusInstallOutcome.Unavailable, -1, ex.Message);
        }
    }

    /// <summary>
    /// Takes the row back out of <c>Override\appearance.2da</c>. Only when it is the table's last
    /// row (rows after it would be renumbered, and something may name them by number); when the
    /// table that is left is the game's own, the Override file is deleted. Returns what it did,
    /// for the log. The blueprint itself is one of the <c>k_coop_*</c> files the uninstall removes.
    /// </summary>
    public static string Uninstall(string gameDirectory)
    {
        try
        {
            string? overrideDirectory = FindOverride(gameDirectory);
            string? path = overrideDirectory is null ? null : Path.Combine(overrideDirectory, TableFile);
            if (path is null || !File.Exists(path))
            {
                return "no appearance.2da in Override";
            }

            TwoDa table = TwoDa.Parse(File.ReadAllBytes(path));
            if (!table.TryGetColumnIndex("label", out int labelColumn))
            {
                return "appearance.2da left alone (no label column)";
            }

            int row = FindRow(table, labelColumn, AppearanceLabel);
            if (row < 0)
            {
                return "appearance.2da has no boss row";
            }

            if (row != table.RowCount - 1)
            {
                return $"appearance.2da left alone (the boss row {row} is not the last row; another mod added rows after it)";
            }

            TwoDaWriter writer = new([.. table.Columns]);
            for (int r = 0; r < row; r++)
            {
                writer.AddRow(table.RowLabels[r], [.. Enumerable.Range(0, table.Columns.Count).Select(c => table.Get(r, c))]);
            }

            if (SameAsTheGames(gameDirectory, writer))
            {
                File.Delete(path);
                return "removed Override\\appearance.2da (it held only the boss row)";
            }

            File.WriteAllBytes(path, writer.ToBytes());
            return "removed the boss row from Override\\appearance.2da";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or FormatException)
        {
            return $"appearance.2da left alone ({ex.Message})";
        }
    }

    /// <summary>Whether a table is cell for cell the one in the game's own archives.</summary>
    private static bool SameAsTheGames(string gameDirectory, TwoDaWriter candidate)
    {
        try
        {
            KeyIndex key = KeyIndex.Load(Path.Combine(gameDirectory, "chitin.key"));
            if (!key.TryRead("appearance", "2da", out byte[] stockBytes))
            {
                return false;
            }

            TwoDa stock = TwoDa.Parse(stockBytes);
            TwoDa ours = TwoDa.Parse(candidate.ToBytes());
            if (stock.RowCount != ours.RowCount || !stock.Columns.SequenceEqual(ours.Columns, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            for (int r = 0; r < stock.RowCount; r++)
            {
                for (int c = 0; c < stock.Columns.Count; c++)
                {
                    if (!string.Equals(stock.Get(r, c), ours.Get(r, c), StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            return false;
        }
    }

    private static int FindRow(TwoDa table, int labelColumn, string label)
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

    private static string? FindOverride(string gameDirectory) =>
        Directory.Exists(gameDirectory)
            ? Directory.EnumerateDirectories(gameDirectory)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "override", StringComparison.OrdinalIgnoreCase))
            : null;
}
