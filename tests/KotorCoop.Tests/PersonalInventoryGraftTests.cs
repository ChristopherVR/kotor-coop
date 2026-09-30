using System.Buffers.Binary;
using System.Text;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// docs/research/personal-inventory.md's rejoin-duplication fix. Live-observed bug: adopting
/// the host's world under <c>inventory_mode</c>/<c>credits_mode = personal</c> handed the
/// joiner a full copy of the HOST's purse and backpack (5000 gold host, joiner should have kept
/// its own 777) because <c>PARTYTABLE.res -&gt; PT_GOLD</c> and
/// <c>SAVEGAME.sav -&gt; INVENTORY.res -&gt; ItemList</c> are world-level resources the pifo
/// carry never reads. These tests build a staged (host) save and a joiner's own dedicated
/// Co-op-slot capture exactly as the real files are shaped (docs/research/key-items-and-solo.md
/// §1 — Tag/Plot/StackSize fields read straight off a real save on disk) and assert the graft.
/// </summary>
public sealed class PersonalInventoryGraftTests : IDisposable
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
            }
        }
    }

    [Fact]
    public void GoldBecomesTheJoinersOwnNotTheHosts()
    {
        string game = BuildGameDirectory();
        string staged = BuildSaveDirectory(gold: 5000, items: []);
        BuildCoopSlot(game, gold: 777, items: []);

        PersonalInventoryGraft.Apply(game, staged, inventoryPersonal: false, creditsPersonal: true, log: null);

        Assert.Equal(777u, ReadGold(staged));
    }

    [Fact]
    public void WithNoOwnCaptureGoldIsZeroNeverTheHosts()
    {
        string game = BuildGameDirectory(); // no Co-op slot created at all
        string staged = BuildSaveDirectory(gold: 5000, items: []);

        PersonalInventoryGraft.Apply(game, staged, inventoryPersonal: false, creditsPersonal: true, log: null);

        Assert.Equal(0u, ReadGold(staged));
    }

    [Fact]
    public void BackpackIsTheJoinersOwnNonQuestItemsPlusTheHostsQuestItems()
    {
        string game = BuildGameDirectory();

        // Host's staged world: one ordinary item (must NOT carry over) and one quest item
        // (must carry over) — the buyable gate tag from the census.
        string staged = BuildSaveDirectory(
            gold: 5000,
            items:
            [
                Item("g_w_ionblstr01", plot: false),
                Item("rakghoulserum", plot: false), // census TAG, not a Plot bit
            ]);

        // Joiner's own last capture: one ordinary item (must carry over) and a STALE quest item
        // (must be dropped in favour of the host's own current copy, never duplicated alongside it).
        BuildCoopSlot(
            game,
            gold: 777,
            items:
            [
                Item("g_i_medeqpmnt01", plot: false),
                Item("dan13_bluecryst", plot: true), // Plot flag this time
            ]);

        PersonalInventoryGraft.Apply(game, staged, inventoryPersonal: true, creditsPersonal: false, log: null);

        List<string> tags = [.. ReadInventoryTags(staged)];
        Assert.Equal(2, tags.Count); // the joiner's own non-quest item + the host's quest item, nothing else
        Assert.Contains("g_i_medeqpmnt01", tags); // the joiner's own non-quest item
        Assert.Contains("rakghoulserum", tags); // the host's quest item, by census tag
        Assert.DoesNotContain("g_w_ionblstr01", tags); // the host's OWN non-quest item never appears
        Assert.DoesNotContain("dan13_bluecryst", tags); // the joiner's stale quest copy is not duplicated in
    }

    [Fact]
    public void WithNoOwnCaptureBackpackIsTheHostsQuestItemsOnly()
    {
        string game = BuildGameDirectory();
        string staged = BuildSaveDirectory(
            gold: 5000,
            items: [Item("g_w_ionblstr01", plot: false), Item("rakghoulserum", plot: false)]);

        PersonalInventoryGraft.Apply(game, staged, inventoryPersonal: true, creditsPersonal: false, log: null);

        List<string> tags = [.. ReadInventoryTags(staged)];
        Assert.Equal(["rakghoulserum"], tags);
    }

    [Fact]
    public void SharedModeIsAByteForByteNoOpInBothDirections()
    {
        string game = BuildGameDirectory();
        string staged = BuildSaveDirectory(gold: 5000, items: [Item("g_w_ionblstr01", plot: false)]);
        BuildCoopSlot(game, gold: 777, items: [Item("g_i_medeqpmnt01", plot: false)]);

        byte[] tableBefore = File.ReadAllBytes(Path.Combine(staged, "PARTYTABLE.res"));
        byte[] savegameBefore = File.ReadAllBytes(Path.Combine(staged, "SAVEGAME.sav"));

        PersonalInventoryGraft.Apply(game, staged, inventoryPersonal: false, creditsPersonal: false, log: null);

        Assert.Equal(tableBefore, File.ReadAllBytes(Path.Combine(staged, "PARTYTABLE.res")));
        Assert.Equal(savegameBefore, File.ReadAllBytes(Path.Combine(staged, "SAVEGAME.sav")));
    }

    [Fact]
    public void CreditsOffLeavesGoldAtTheHostsWhileInventoryOffLeavesTheBackpackAtTheHosts()
    {
        string game = BuildGameDirectory();
        string staged = BuildSaveDirectory(gold: 5000, items: [Item("g_w_ionblstr01", plot: false)]);
        BuildCoopSlot(game, gold: 777, items: [Item("g_i_medeqpmnt01", plot: false)]);

        PersonalInventoryGraft.Apply(game, staged, inventoryPersonal: false, creditsPersonal: false, log: null);
        Assert.Equal(5000u, ReadGold(staged));
        Assert.Equal(["g_w_ionblstr01"], [.. ReadInventoryTags(staged)]);
    }

    // -- fixtures --------------------------------------------------------------------

    [Fact]
    public void PrivateLockerKeepsOwnUpgradedStacksAfterHostHasLooted()
    {
        string game = BuildGameDirectory();
        string staged = BuildSaveDirectory(0, [Item("man28_envirosuit", true)]);
        string own = BuildCoopSlot(game, 0, []);
        AddLocker(staged, [], includeOther: true);
        AddLocker(own, [Item("own_rifle", false), Item("man28_envirosuit", true)]);

        PersonalInventoryGraft.Apply(game, staged, true, false, null);

        IReadOnlyList<GffStruct> items = ReadLocker(staged);
        GffStruct item = Assert.Single(items);
        Assert.True(item.TryGetString("Tag", out string tag));
        Assert.Equal("own_rifle", tag);
        Assert.True(item.TryGetWord("StackSize", out ushort stack));
        Assert.Equal((ushort)3, stack);
        Assert.True(item.TryGetDword("Upgrades", out uint upgrades));
        Assert.Equal(4100u, upgrades);
        Assert.Single(item.GetList("PropertiesList"));
        Assert.Equal(["man28_envirosuit"], ReadInventoryTags(staged));
        Assert.Single(ReadLocker(staged, "ordinary_chest"));
        Assert.Equal(2, ReadLocker(own).Count); // original capture is untouched
    }

    [Fact]
    public void EmptyOwnLockerDoesNotRefillFromHostsGear()
    {
        string game = BuildGameDirectory();
        string staged = BuildSaveDirectory(0, []);
        string own = BuildCoopSlot(game, 0, []);
        AddLocker(staged, [Item("host_rifle", false)]);
        AddLocker(own, []);
        PersonalInventoryGraft.Apply(game, staged, true, false, null);
        Assert.Empty(ReadLocker(staged));
    }

    [Fact]
    public void FirstJoinNeverCopiesHostPrivateGearButRetainsUnclaimedQuestItems()
    {
        string game = BuildGameDirectory();
        string staged = BuildSaveDirectory(0, []);
        AddLocker(staged, [Item("host_rifle", false), Item("man28_envirosuit", true)]);
        PersonalInventoryGraft.Apply(game, staged, true, false, null);
        GffStruct item = Assert.Single(ReadLocker(staged));
        Assert.True(item.TryGetString("Tag", out string tag));
        Assert.Equal("man28_envirosuit", tag);
    }

    [Fact]
    public void SharedInventoryDoesNotRewritePrivateLocker()
    {
        string game = BuildGameDirectory();
        string staged = BuildSaveDirectory(0, []);
        AddLocker(staged, [Item("host_rifle", false)]);
        byte[] before = File.ReadAllBytes(Path.Combine(staged, "SAVEGAME.sav"));
        PersonalInventoryGraft.Apply(game, staged, false, true, null);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(staged, "SAVEGAME.sav")));
    }

    private static void AddLocker(string directory, (string Tag, bool Plot)[] items, bool includeOther = false)
    {
        GffWriter git = new("GIT ");
        GffWriterList placeables = git.Root.AddList("Placeable List");
        GffWriterStruct locker = placeables.Add(9).SetString("Tag", "lev40_ptylocker");
        GffWriterList contents = locker.AddList("ItemList");
        foreach ((string tag, bool plot) in items)
        {
            GffWriterStruct item = contents.Add(0).SetString("Tag", tag)
                .SetByte("Plot", (byte)(plot ? 1 : 0)).SetWord("StackSize", 3).SetDword("Upgrades", 4100);
            item.AddList("PropertiesList").Add(0).SetWord("PropertyName", 1);
        }
        if (includeOther)
            placeables.Add(9).SetString("Tag", "ordinary_chest").AddList("ItemList")
                .Add(0).SetString("Tag", "other_item");
        byte[] original = File.ReadAllBytes(Path.Combine(directory, "SAVEGAME.sav"));
        Assert.True(ErfArchive.TryFindMember(original, 0, "INVENTORY", out byte[] inventory));
        byte[] inner = BuildErf(("lev_m40aa", 2023, git.ToBytes()));
        File.WriteAllBytes(Path.Combine(directory, "SAVEGAME.sav"),
            BuildErf(("INVENTORY", 0, inventory), ("lev_m40aa", 2057, inner)));
    }

    private static IReadOnlyList<GffStruct> ReadLocker(string directory, string tag = "lev40_ptylocker")
    {
        byte[] outer = File.ReadAllBytes(Path.Combine(directory, "SAVEGAME.sav"));
        Assert.True(ErfArchive.TryFindMember(outer, 2057, "lev_m40aa", out byte[] inner));
        Assert.True(ErfArchive.TryFindMember(inner, 2023, "lev_m40aa", out byte[] git));
        return Gff.Parse(git).Root.GetList("Placeable List")
            .Single(p => p.TryGetString("Tag", out string value) && value == tag).GetList("ItemList");
    }

    private string BuildGameDirectory()
    {
        string directory = TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "saves"));
        return directory;
    }

    private static (string Tag, bool Plot) Item(string tag, bool plot) => (tag, plot);

    private string BuildSaveDirectory(uint gold, (string Tag, bool Plot)[] items)
    {
        string directory = TempDirectory();
        WritePartyTable(directory, gold);
        WriteSaveGame(directory, items);
        return directory;
    }

    private string BuildCoopSlot(string gameDirectory, uint gold, (string Tag, bool Plot)[] items)
    {
        string directory = Path.Combine(gameDirectory, "saves", "000005 - Co-op");
        Directory.CreateDirectory(directory);
        WritePartyTable(directory, gold);
        WriteSaveGame(directory, items);
        return directory;
    }

    private static void WritePartyTable(string directory, uint gold)
    {
        GffWriter table = new("PT");
        table.Root.SetDword("PT_GOLD", gold);
        File.WriteAllBytes(Path.Combine(directory, "PARTYTABLE.res"), table.ToBytes());
    }

    private static void WriteSaveGame(string directory, (string Tag, bool Plot)[] items)
    {
        GffWriter inventory = new("INV ");
        GffWriterList list = inventory.Root.AddList("ItemList");
        foreach ((string tag, bool plot) in items)
        {
            list.Add(0)
                .SetInt("BaseItem", 1)
                .SetString("Tag", tag)
                .SetWord("StackSize", 1)
                .SetByte("Plot", (byte)(plot ? 1 : 0));
        }

        byte[] erf = BuildErf(("INVENTORY", 0, inventory.ToBytes()));
        File.WriteAllBytes(Path.Combine(directory, "SAVEGAME.sav"), erf);
    }

    private static uint ReadGold(string directory)
    {
        GffStruct table = Gff.Parse(File.ReadAllBytes(Path.Combine(directory, "PARTYTABLE.res"))).Root;
        Assert.True(table.TryGetDword("PT_GOLD", out uint gold));
        return gold;
    }

    private static IEnumerable<string> ReadInventoryTags(string directory)
    {
        byte[] erf = File.ReadAllBytes(Path.Combine(directory, "SAVEGAME.sav"));
        Assert.True(ErfArchive.TryFindMember(erf, 0, "INVENTORY", out byte[] data));
        GffStruct root = Gff.Parse(data).Root;
        foreach (GffStruct item in root.GetList("ItemList"))
        {
            Assert.True(item.TryGetString("Tag", out string tag));
            yield return tag;
        }
    }

    private string TempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kcoop-persinv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);
        return directory;
    }

    /// <summary>Lays out an ERF exactly as <see cref="ErfArchive"/> decodes it.</summary>
    private static byte[] BuildErf(params (string Resref, int Type, byte[] Data)[] members)
    {
        int count = members.Length;
        int keysOffset = 160;
        int valuesOffset = keysOffset + (24 * count);
        int dataOffset = valuesOffset + (8 * count);

        byte[] file = new byte[dataOffset + members.Sum(m => m.Data.Length)];
        Encoding.ASCII.GetBytes("SAV ").CopyTo(file, 0);
        Encoding.ASCII.GetBytes("V1.0").CopyTo(file, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x10, 4), (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x18, 4), (uint)keysOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x1C, 4), (uint)valuesOffset);

        int offset = dataOffset;
        for (int i = 0; i < count; i++)
        {
            (string resref, int type, byte[] data) = members[i];
            Encoding.ASCII.GetBytes(resref).CopyTo(file, keysOffset + (24 * i));
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(keysOffset + (24 * i) + 16, 4), (uint)i);
            file[keysOffset + (24 * i) + 20] = (byte)type;
            file[keysOffset + (24 * i) + 21] = (byte)(type >> 8);

            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(valuesOffset + (8 * i), 4), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(valuesOffset + (8 * i) + 4, 4), (uint)data.Length);
            data.CopyTo(file, offset);
            offset += data.Length;
        }

        return file;
    }
}
