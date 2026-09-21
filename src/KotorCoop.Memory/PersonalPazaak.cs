using KotorCoop.Memory.GameFiles;

namespace KotorCoop.Memory;

/// <summary>Personal card collection carried beside character data, never into the engine IFO.</summary>
public static class PersonalPazaak
{
    internal const string Metadata = "KC_Pazaak";
    private static readonly string[] Lists = ["PT_PAZAAKCARDS", "PT_PAZSIDELIST"];

    public static GffStruct Capture(GffStruct player, string saveDirectory)
    {
        string path = Path.Combine(saveDirectory, "PARTYTABLE.res");
        if (!File.Exists(path)) return player; // Older/synthetic saves remain readable.
        GffStruct table = Gff.Parse(File.ReadAllBytes(path)).Root;
        Validate(table);
        GffStruct result = WithoutMetadata(player);
        GffStruct cards = new(0);
        foreach (string label in Lists)
            cards.Add(label, GffFieldType.List, table.GetList(label));
        result.Add(Metadata, GffFieldType.Struct, cards);
        return result;
    }

    internal static GffStruct WithoutMetadata(GffStruct player)
    {
        GffStruct result = new(player.StructId);
        foreach (string label in player.FieldNames)
            if (label != Metadata && player.TryGetFieldType(label, out GffFieldType type))
                result.Add(label, type, player.Fields[label]);
        return result;
    }

    /// <summary>Changes only personal card lists in the staged host save. Legacy characters have no overlay.</summary>
    public static void Restore(GffStruct player, string saveDirectory)
    {
        if (!player.Fields.ContainsKey(Metadata)) return;
        GffStruct cards = player.GetStruct(Metadata)
            ?? throw new InvalidDataException("Personal Pazaak metadata is not a struct.");
        Validate(cards);
        string path = Path.Combine(saveDirectory, "PARTYTABLE.res");
        GffStruct table = Gff.Parse(File.ReadAllBytes(path)).Root;
        Validate(table);
        GffWriter writer = new("PT");
        GffCopy.CopyInto(table, writer.Root);
        foreach (string label in Lists)
        {
            GffWriterList list = writer.Root.AddList(label);
            foreach (GffStruct card in cards.GetList(label))
                GffCopy.CopyInto(card, list.Add(card.StructId));
        }
        string temporary = path + ".kcoop.tmp";
        try
        {
            File.WriteAllBytes(temporary, writer.ToBytes());
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Validate(GffStruct cards)
    {
        for (int i = 0; i < Lists.Length; i++)
        {
            string label = Lists[i];
            string value = i == 0 ? "PT_PAZAAKCOUNT" : "PT_PAZSIDECARD";
            if (!cards.TryGetFieldType(label, out GffFieldType type) || type != GffFieldType.List)
                throw new InvalidDataException($"Missing Pazaak list '{label}'.");
            foreach (GffStruct entry in cards.GetList(label))
                if (!entry.TryGetFieldType(value, out type) || type is not (GffFieldType.Int or GffFieldType.Byte))
                    throw new InvalidDataException($"Invalid Pazaak value '{value}'.");
        }
    }
}
