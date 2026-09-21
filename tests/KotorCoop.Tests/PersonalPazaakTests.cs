using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PersonalPazaakTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kcoop-pazaak-" + Guid.NewGuid());

    public PersonalPazaakTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void SidecarCarriesCardsButEngineIfoDoesNotReceiveMetadata()
    {
        WriteTable(7, 4, 100);
        GffStruct captured = PersonalPazaak.Capture(Player(), _directory);
        Assert.True(PlayerIfo.TryExtractPlayerFromPifoBytes(PlayerIfo.BuildPifo(captured), out GffStruct restored, out _));
        WriteTable(1, -1, 900);
        PersonalPazaak.Restore(restored, _directory);
        GffStruct table = Gff.Parse(File.ReadAllBytes(TablePath)).Root;
        Assert.Equal(900, (int)table.Fields["PT_GOLD"]);
        Assert.Equal(7, (int)table.GetList("PT_PAZAAKCARDS")[0].Fields["PT_PAZAAKCOUNT"]);
        Assert.Equal(4, (int)table.GetList("PT_PAZSIDELIST")[0].Fields["PT_PAZSIDECARD"]);
        Assert.Equal(17u, table.GetList("PT_PAZAAKCARDS")[0].StructId);
        Assert.True(table.GetList("PT_PAZAAKCARDS")[1].TryGetFieldType("PT_PAZAAKCOUNT", out GffFieldType type));
        Assert.Equal(GffFieldType.Byte, type);
        Assert.True(PlayerIfo.TryWritePifo(_directory, restored, out _));
        GffStruct engine = Gff.Parse(File.ReadAllBytes(Path.Combine(_directory, "Temp", "pifo.ifo"))).Root.GetList("Mod_PlayerList")[0];
        Assert.False(engine.Fields.ContainsKey("KC_Pazaak"));
        Assert.Equal(12, (int)engine.Fields["Experience"]);
    }

    [Fact]
    public void LegacyCharacterDoesNotReplaceHostCards()
    {
        WriteTable(3, -1, 42);
        byte[] original = File.ReadAllBytes(TablePath);
        PersonalPazaak.Restore(Player(), _directory);
        Assert.Equal(original, File.ReadAllBytes(TablePath));
    }

    [Fact]
    public void MalformedPersonalCardsCannotOverwriteStagedSave()
    {
        WriteTable(3, -1, 42);
        byte[] original = File.ReadAllBytes(TablePath);
        GffWriter player = new("IFO");
        player.Root.AddStruct("KC_Pazaak", 0).SetString("PT_PAZAAKCARDS", "invalid");
        Assert.Throws<InvalidDataException>(() => PersonalPazaak.Restore(Gff.Parse(player.ToBytes()).Root, _directory));
        Assert.Equal(original, File.ReadAllBytes(TablePath));
    }

    private string TablePath => Path.Combine(_directory, "PARTYTABLE.res");

    private void WriteTable(int count, int sideCard, int gold)
    {
        GffWriter table = new("PT");
        table.Root.SetInt("PT_GOLD", gold);
        GffWriterList cards = table.Root.AddList("PT_PAZAAKCARDS");
        cards.Add(17).SetInt("PT_PAZAAKCOUNT", count);
        cards.Add(0).SetByte("PT_PAZAAKCOUNT", 2);
        table.Root.AddList("PT_PAZSIDELIST").Add(0).SetInt("PT_PAZSIDECARD", sideCard);
        File.WriteAllBytes(TablePath, table.ToBytes());
    }

    private static GffStruct Player()
    {
        GffWriter player = new("IFO");
        player.Root.SetInt("Experience", 12);
        return Gff.Parse(player.ToBytes()).Root;
    }
}

