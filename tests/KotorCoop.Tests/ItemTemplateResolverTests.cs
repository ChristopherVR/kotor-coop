using System.Buffers.Binary;
using System.Text;
using KotorCoop.Memory.GameFiles;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>A live item keeps only its tag; <see cref="ItemTemplateResolver"/> maps a tag that is
/// not itself a blueprint name to the module blueprint carrying it (2026-10-09: the Endar Spire's
/// g_w_vbroshort002.uti is tagged "OneDamageBlade", and corpse loot named "onedamageblade" could
/// not be built on the joiner).</summary>
public sealed class ItemTemplateResolverTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "kcoop-uti-" + Guid.NewGuid().ToString("N"));

    public ItemTemplateResolverTests()
    {
        Directory.CreateDirectory(Path.Combine(_game, "modules"));
        File.WriteAllBytes(Path.Combine(_game, "modules", "end_m01aa_s.rim"),
            BuildRim(("g_w_vbroshort002", 2025, Uti("OneDamageBlade")), ("end_locker01", 2025, Uti("end_locker01"))));
    }

    public void Dispose()
    {
        try { Directory.Delete(_game, recursive: true); } catch (IOException) { }
    }

    private static byte[] Uti(string tag)
    {
        GffWriter writer = new("UTI ");
        writer.Root.SetString("Tag", tag);
        return writer.ToBytes();
    }

    [Fact]
    public void ATagThatIsNotABlueprintResolvesToTheModuleBlueprintCarryingIt()
    {
        Assert.Equal("g_w_vbroshort002", new ItemTemplateResolver(_game).Resolve("onedamageblade", "end_m01aa"));
    }

    [Fact]
    public void ATagThatIsItselfAModuleBlueprintIsKept()
    {
        Assert.Equal("end_locker01", new ItemTemplateResolver(_game).Resolve("end_locker01", "end_m01aa"));
    }

    [Fact]
    public void AnUnknownTagOrAnotherModuleKeepsTheTag()
    {
        ItemTemplateResolver resolver = new(_game);
        Assert.Equal("g_w_blstrpstl001", resolver.Resolve("g_w_blstrpstl001", "end_m01aa"));
        Assert.Equal("onedamageblade", resolver.Resolve("onedamageblade", "tar_m02aa"));
    }

    [Fact]
    public void AMissingInstallKeepsTheTag()
    {
        Assert.Equal("onedamageblade", new ItemTemplateResolver(Path.Combine(_game, "nope")).Resolve("onedamageblade", "end_m01aa"));
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
