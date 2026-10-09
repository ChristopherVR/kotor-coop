using System.Buffers.Binary;
using System.Text;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>Host spawns only lets a joiner build friendly story actors itself (live 2026-10-10:
/// sta_carth in sta_m45aa was suppressed and the scene had no Carth). The decision is pure
/// (<see cref="FriendlyTemplateRule"/>); the agent classifies a module's .utc blueprints by
/// FactionID (<see cref="FriendlyCreatureResolver"/>).</summary>
public sealed class FriendlyTemplateTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "kcoop-utc-" + Guid.NewGuid().ToString("N"));

    public FriendlyTemplateTests()
    {
        Directory.CreateDirectory(Path.Combine(_game, "modules"));
        Directory.CreateDirectory(Path.Combine(_game, "override"));
        File.WriteAllBytes(Path.Combine(_game, "modules", "sta_m45aa.rim"),
            BuildRim(
                ("sta_carth", 2027, Utc(2)),
                ("sta_darkjedi", 2027, Utc(1)),
                ("sta_neutral", 2027, Utc(5)),
                ("sta_friend2", 2027, Utc(4)),
                ("sta_insane", 2027, Utc(6)),
                ("sta_override", 2027, Utc(1)),
                ("sta_door", 2042, Utc(2))));
        File.WriteAllBytes(Path.Combine(_game, "override", "sta_override.utc"), Utc(2));
    }

    public void Dispose()
    {
        try { Directory.Delete(_game, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TheModulesNonHostileFactionsAreFriendlyAndOverrideWins()
    {
        IReadOnlyList<string> friendly = new FriendlyCreatureResolver(_game).FriendlyTemplates("sta_m45aa");
        Assert.Equal(["sta_carth", "sta_friend2", "sta_neutral", "sta_override"], friendly);
    }

    [Fact]
    public void AnotherModuleOrAMissingInstallIsEmpty()
    {
        Assert.Empty(new FriendlyCreatureResolver(_game).FriendlyTemplates("tar_m02aa"));
        Assert.Empty(new FriendlyCreatureResolver(Path.Combine(_game, "nope")).FriendlyTemplates("sta_m45aa"));
    }

    [Fact]
    public void ABlueprintWithoutAFactionIsNotFriendly()
    {
        Assert.Equal(-1, FriendlyCreatureResolver.FactionOf(new GffWriter("UTC ").ToBytes()));
        Assert.Equal(-1, FriendlyCreatureResolver.FactionOf([1, 2, 3]));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(-1, false)]
    public void OnlyFriendly1IsFriendly(int faction, bool expected) =>
        Assert.Equal(expected, FriendlyTemplateRule.IsFriendlyFaction(faction));

    [Fact]
    public void TheHashIsLowercaseFnv1aOfTheResref()
    {
        Assert.Equal(0x811C9DC5u, FriendlyTemplateRule.Hash(string.Empty));
        Assert.Equal(0xE40C292Cu, FriendlyTemplateRule.Hash("a"));
        Assert.Equal(FriendlyTemplateRule.Hash("sta_carth"), FriendlyTemplateRule.Hash("STA_Carth"));
        Assert.Equal(FriendlyTemplateRule.Hash("sta_carth"), FriendlyTemplateRule.Hash("sta_carth\0"));
        Assert.NotEqual(FriendlyTemplateRule.Hash("sta_carth"), FriendlyTemplateRule.Hash("sta_carth2"));
    }

    [Fact]
    public void MembershipFollowsThePublishedSet()
    {
        uint[] published = FriendlyTemplateRule.Publishable(["sta_carth", "sta_neutral"]);
        Assert.True(FriendlyTemplateRule.IsFriendlyTemplate("sta_carth", published));
        Assert.True(FriendlyTemplateRule.IsFriendlyTemplate("STA_NEUTRAL", published));
        Assert.False(FriendlyTemplateRule.IsFriendlyTemplate("sta_darkjedi", published));
    }

    [Fact]
    public void UnknownStaysSuppressed()
    {
        Assert.False(FriendlyTemplateRule.IsFriendlyTemplate("sta_carth", []));
        Assert.False(FriendlyTemplateRule.IsFriendlyTemplate(string.Empty, FriendlyTemplateRule.Publishable(["sta_carth"])));
    }

    [Fact]
    public void ThePublishedSetIsDistinctAndTruncatedAtEighty()
    {
        string[] many = [.. Enumerable.Range(0, 120).Select(i => "creature" + i), "creature3", ""];
        uint[] published = FriendlyTemplateRule.Publishable(many);
        Assert.Equal(FriendlyTemplateRule.Capacity, published.Length);
        Assert.Equal(80, published.Distinct().Count());
        Assert.True(FriendlyTemplateRule.IsFriendlyTemplate("creature79", published));
        Assert.False(FriendlyTemplateRule.IsFriendlyTemplate("creature80", published));
        Assert.Single(FriendlyTemplateRule.Publishable(["a", "A", ""]));
    }

    private static byte[] Utc(int faction)
    {
        GffWriter writer = new("UTC ");
        writer.Root.SetWord("FactionID", (ushort)faction);
        return writer.ToBytes();
    }

    private static byte[] BuildRim(params (string Resref, int Type, byte[] Data)[] members)
    {
        int tableOffset = 0x78;
        int dataOffset = tableOffset + (32 * members.Length);
        byte[] file = new byte[dataOffset + members.Sum(m => m.Data.Length)];
        Encoding.ASCII.GetBytes("RIM V1.0").CopyTo(file, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x0C), (uint)members.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x10), (uint)tableOffset);
        int offset = dataOffset;
        for (int i = 0; i < members.Length; i++)
        {
            int entry = tableOffset + (32 * i);
            Encoding.ASCII.GetBytes(members[i].Resref).CopyTo(file, entry);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 16), (uint)members[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 20), (uint)i);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 24), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 28), (uint)members[i].Data.Length);
            members[i].Data.CopyTo(file, offset);
            offset += members[i].Data.Length;
        }

        return file;
    }
}
