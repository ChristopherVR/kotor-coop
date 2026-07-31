using System.Buffers.Binary;
using System.Text;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The save-identity and per-module GIT digests, against synthetic saves and modules built
/// with the real <see cref="GffWriter"/> and hand-laid ERF/RIM containers (the same
/// layouts <see cref="ErfArchive"/>/<see cref="RimArchive"/> decode). What matters: the
/// digest is deterministic, every identity-bearing part perturbs it, unreadable inputs
/// are "unknown" rather than errors, and the pristine prediction reproduces the
/// engine's id-dealing rule (each GIT entry takes the next id, then its blueprint's
/// owned items take the ids after it).
/// </summary>
public sealed class SaveDigestTests : IDisposable
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

    // ---- save identity ---------------------------------------------------------

    [Fact]
    public void SaveIdentityIsDeterministicAcrossIdenticalSaves()
    {
        string saveA = BuildSaveDirectory("end_m01aa", 295, [(2u, "n_duros"), (3u, "n_republic")]);
        string saveB = BuildSaveDirectory("end_m01aa", 295, [(2u, "n_duros"), (3u, "n_republic")]);

        Assert.True(SaveDigest.TryComputeSaveIdentity(saveA, out uint a));
        Assert.True(SaveDigest.TryComputeSaveIdentity(saveB, out uint b));

        Assert.NotEqual(0u, a);   // 0 is the wire's "unknown" and must never be produced
        Assert.Equal(a, b);       // two staged copies of the same save fingerprint alike
    }

    [Fact]
    public void EveryIdentityBearingPartPerturbsTheSaveDigest()
    {
        string baseline = BuildSaveDirectory("end_m01aa", 295, [(2u, "n_duros"), (3u, "n_republic")]);
        Assert.True(SaveDigest.TryComputeSaveIdentity(baseline, out uint reference));

        // A different NextObjId counter: runtime-created objects would take different ids.
        string bumpedCounter = BuildSaveDirectory("end_m01aa", 296, [(2u, "n_duros"), (3u, "n_republic")]);
        Assert.True(SaveDigest.TryComputeSaveIdentity(bumpedCounter, out uint counter));
        Assert.NotEqual(reference, counter);

        // A shifted GIT object id: the exact corruption class the digest exists to catch.
        string shiftedId = BuildSaveDirectory("end_m01aa", 295, [(2u, "n_duros"), (4u, "n_republic")]);
        Assert.True(SaveDigest.TryComputeSaveIdentity(shiftedId, out uint shifted));
        Assert.NotEqual(reference, shifted);

        // A different module: the save stands somewhere else entirely.
        string otherModule = BuildSaveDirectory("tar_m02aa", 295, [(2u, "n_duros"), (3u, "n_republic")]);
        Assert.True(SaveDigest.TryComputeSaveIdentity(otherModule, out uint module));
        Assert.NotEqual(reference, module);
    }

    [Fact]
    public void TheClassicModuleSavRestypeIsAcceptedToo()
    {
        // This build writes 2057 for the nested module .sav; older tables say 2055. Both
        // must digest, or a save written by a different build reads as unknown for no
        // reason (see PluginMenuLoader, which accepts both for the same reason).
        string save = BuildSaveDirectory(
            "end_m01aa", 295, [(2u, "n_duros")], moduleSavType: 2055);

        Assert.True(SaveDigest.TryComputeSaveIdentity(save, out uint digest));
        Assert.NotEqual(0u, digest);
    }

    [Fact]
    public void AnUnreadableSaveIsUnknownNotAnError()
    {
        Assert.False(SaveDigest.TryComputeSaveIdentity(
            Path.Combine(Path.GetTempPath(), "definitely-not-a-save"), out _));

        // A directory without SAVEGAME.sav, and one with garbage bytes in it.
        string empty = TempDirectory();
        Assert.False(SaveDigest.TryComputeSaveIdentity(empty, out _));

        string garbage = TempDirectory();
        File.WriteAllBytes(Path.Combine(garbage, "SAVEGAME.sav"), [1, 2, 3, 4, 5]);
        Assert.False(SaveDigest.TryComputeSaveIdentity(garbage, out _));
    }

    // ---- per-module GIT --------------------------------------------------------

    [Fact]
    public void ModuleGitPrefersTheSavesStoredStateWhenVisited()
    {
        // The save has visited end_m01aa (a stored module .sav exists), so its explicit
        // ObjectId list wins over the pristine prediction — a spawned/killed creature in
        // the save is part of the identity the peers must share.
        string gameDir = BuildGameDirectory(
            "end_m01aa", PristineGit([("n_duros", 0), ("n_republic", 0)]));
        string save = BuildSaveDirectory("end_m01aa", 295, [(2u, "n_duros"), (5u, "n_spawned")]);

        Assert.True(SaveDigest.TryComputeModuleGit(gameDir, save, "end_m01aa", out uint fromSave));
        Assert.True(SaveDigest.TryComputeModuleGit(gameDir, saveDirectory: null, "end_m01aa", out uint pristine));

        Assert.NotEqual(0u, fromSave);
        Assert.NotEqual(0u, pristine);
        Assert.NotEqual(fromSave, pristine);

        // A module the save has NOT visited falls through to the pristine file, so both
        // peers on the same save still choose the same source.
        string otherModuleGame = BuildGameDirectory("tar_m02aa", PristineGit([("n_sith", 0)]));
        Assert.True(SaveDigest.TryComputeModuleGit(otherModuleGame, save, "tar_m02aa", out uint unvisited));
        Assert.True(SaveDigest.TryComputePristineModuleGit(otherModuleGame, "tar_m02aa", out uint direct));
        Assert.Equal(direct, unvisited);
    }

    [Fact]
    public void PristinePredictionCountsBlueprintOwnedItemsIntoTheIds()
    {
        // Two creatures; the first's blueprint owns 3 items (2 equipped + 1 carried), so
        // the second creature's id lands 3 further along. A mod that changes the
        // blueprint's inventory shifts every id after it — the digest must move.
        string withItems = BuildGameDirectory(
            "end_m01aa",
            PristineGit([("n_trooper", 0), ("n_duros", 0)]),
            ("n_trooper", "utc", CreatureBlueprint(equipped: 2, carried: 1)));

        string withoutItems = BuildGameDirectory(
            "end_m01aa",
            PristineGit([("n_trooper", 0), ("n_duros", 0)]),
            ("n_trooper", "utc", CreatureBlueprint(equipped: 0, carried: 0)));

        Assert.True(SaveDigest.TryComputePristineModuleGit(withItems, "end_m01aa", out uint items));
        Assert.True(SaveDigest.TryComputePristineModuleGit(withoutItems, "end_m01aa", out uint bare));
        Assert.NotEqual(items, bare);

        // And the prediction is deterministic for the same content.
        Assert.True(SaveDigest.TryComputePristineModuleGit(withItems, "end_m01aa", out uint again));
        Assert.Equal(items, again);
    }

    [Fact]
    public void ReorderingTheGitChangesThePristineDigest()
    {
        // Same entries, different file order: the engine would deal the ids differently,
        // which is exactly the row-8 corruption class (mod edits a module's GIT).
        string forward = BuildGameDirectory("end_m01aa", PristineGit([("n_a", 0), ("n_b", 0)]));
        string reversed = BuildGameDirectory("end_m01aa", PristineGit([("n_b", 0), ("n_a", 0)]));

        Assert.True(SaveDigest.TryComputePristineModuleGit(forward, "end_m01aa", out uint a));
        Assert.True(SaveDigest.TryComputePristineModuleGit(reversed, "end_m01aa", out uint b));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void AMissingModuleIsUnknownNotAnError()
    {
        string gameDir = BuildGameDirectory("end_m01aa", PristineGit([("n_a", 0)]));

        Assert.False(SaveDigest.TryComputeModuleGit(gameDir, null, "not_a_module", out _));
        Assert.False(SaveDigest.TryComputeModuleGit(gameDir, null, "", out _));
    }

    // ---- the session-lived cache ------------------------------------------------

    [Fact]
    public void TheCacheServesTheStagedAutosaveAndLatchesUnknowns()
    {
        // A game directory whose staged co-op autosave exists: the cache must find and
        // digest it without being told which save the session loaded.
        string gameDir = BuildGameDirectory("end_m01aa", PristineGit([("n_duros", 0)]));
        string staged = Path.Combine(gameDir, "saves", "000001 - AUTOSAVE");
        Directory.CreateDirectory(staged);
        File.WriteAllBytes(
            Path.Combine(staged, "SAVEGAME.sav"),
            BuildSavegameErf("end_m01aa", 295, [(2u, "n_duros")], 2057));

        SaveDigestCache cache = new(gameDir);
        Assert.NotEqual(0u, cache.SaveIdentity);
        Assert.NotEqual(0u, cache.ModuleGit("end_m01aa"));
        Assert.Equal(cache.ModuleGit("end_m01aa"), cache.ModuleGit("END_M01AA")); // module names are case-insensitive

        // No saves at all: everything unknown, nothing thrown, and the answer is latched
        // rather than re-hitting the disk every heavy pump.
        SaveDigestCache empty = new(Path.Combine(Path.GetTempPath(), "kotorcoop-nowhere"));
        Assert.Equal(0u, empty.SaveIdentity);
        Assert.Equal(0u, empty.ModuleGit("end_m01aa"));
        Assert.Equal(0u, empty.ModuleGit(""));
    }

    // ---- real install (skipped when absent) ------------------------------------

    [GogInstallFact]
    public void RealInstallPredictsTheEndarSpireGitDeterministically()
    {
        // The full pristine path against genuine data: RIM parse, GIT walk, blueprint
        // owned-item resolution through the module archives and chitin.key.
        Assert.True(SaveDigest.TryComputePristineModuleGit(GameFilesTests.GogDirectory, "end_m01aa", out uint a));
        Assert.True(SaveDigest.TryComputePristineModuleGit(GameFilesTests.GogDirectory, "end_m01aa", out uint b));

        Assert.NotEqual(0u, a);
        Assert.Equal(a, b);

        // A different module deals different ids, so it must digest differently.
        Assert.True(SaveDigest.TryComputePristineModuleGit(GameFilesTests.GogDirectory, "tar_m02aa", out uint other));
        Assert.NotEqual(a, other);
    }

    [GogInstallFact]
    public void ARealSaveDigestsWhenOneExists()
    {
        // Environment-dependent by nature: only provable on a machine that has saved the
        // game. When a save is there, the whole identity path must work against it —
        // this is what exercises the genuine 2057-restype nested module .sav.
        string? save = SaveDigest.FindSessionSave(GameFilesTests.GogDirectory);
        if (save is null)
        {
            return; // no saves on this machine; the synthetic fixtures cover the logic
        }

        Assert.True(SaveDigest.TryComputeSaveIdentity(save, out uint digest), $"could not digest {save}");
        Assert.NotEqual(0u, digest);
    }

    // ---- fixture builders ------------------------------------------------------

    private string TempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"kotorcoop-savedigest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);
        return directory;
    }

    /// <summary>A save directory holding a SAVEGAME.sav whose nested module .sav carries a
    /// Module.ifo (the NextObjId counters) and a stored GIT with explicit ObjectIds.</summary>
    private string BuildSaveDirectory(
        string module, uint nextObjId, (uint Id, string Resref)[] creatures, int moduleSavType = 2057)
    {
        string directory = TempDirectory();
        File.WriteAllBytes(
            Path.Combine(directory, "SAVEGAME.sav"),
            BuildSavegameErf(module, nextObjId, creatures, moduleSavType));
        return directory;
    }

    private static byte[] BuildSavegameErf(
        string module, uint nextObjId, (uint Id, string Resref)[] creatures, int moduleSavType)
    {
        GffWriter ifo = new("IFO ");
        ifo.Root.SetDword("Mod_NextObjId0", nextObjId);
        ifo.Root.SetDword("Mod_NextObjId1", 0);

        GffWriter git = new("GIT ");
        GffWriterList list = git.Root.AddList("Creature List");
        foreach ((uint id, string resref) in creatures)
        {
            list.Add(4)
                .SetResRef("TemplateResRef", resref)
                .SetDword("ObjectId", id);
        }

        byte[] inner = BuildErf("SAV ",
            ("Module", 2014 /* ifo */, ifo.ToBytes()),
            (module, 2023 /* git */, git.ToBytes()));

        return BuildErf("SAV ",
            ("GLOBALVARS", 0 /* res */, [0xAA, 0xBB]),
            (module, moduleSavType, inner));
    }

    /// <summary>A pristine (never-saved) GIT: entries without ObjectId fields, the way the
    /// module files on disk ship. Ids for it are predicted, not read.</summary>
    private static byte[] PristineGit((string Resref, int Unused)[] creatures)
    {
        GffWriter git = new("GIT ");
        GffWriterList list = git.Root.AddList("Creature List");
        foreach ((string resref, _) in creatures)
        {
            list.Add(4)
                .SetResRef("TemplateResRef", resref)
                .SetFloat("XPosition", 1.0f);
        }

        return git.ToBytes();
    }

    /// <summary>A creature blueprint whose Equip_ItemList/ItemList lengths are what the id
    /// prediction consumes.</summary>
    private static byte[] CreatureBlueprint(int equipped, int carried)
    {
        GffWriter utc = new("UTC ");

        GffWriterList equip = utc.Root.AddList("Equip_ItemList");
        for (int i = 0; i < equipped; i++)
        {
            equip.Add((uint)i).SetResRef("EquippedRes", $"g_w_item{i}");
        }

        GffWriterList inventory = utc.Root.AddList("ItemList");
        for (int i = 0; i < carried; i++)
        {
            inventory.Add((uint)i).SetResRef("InventoryRes", $"g_i_item{i}");
        }

        return utc.ToBytes();
    }

    /// <summary>A game directory whose modules\ holds one RIM with the given GIT and any
    /// extra members (blueprints the prediction resolves module-archives-first).</summary>
    private string BuildGameDirectory(
        string module, byte[] git, params (string Resref, string Extension, byte[] Data)[] extras)
    {
        string directory = TempDirectory();
        string modules = Path.Combine(directory, "modules");
        Directory.CreateDirectory(modules);

        List<(string Resref, int Type, byte[] Data)> members = [(module, 2023 /* git */, git)];
        foreach ((string resref, string extension, byte[] data) in extras)
        {
            Assert.True(ResourceTypes.TryGetType(extension, out int type));
            members.Add((resref, type, data));
        }

        File.WriteAllBytes(Path.Combine(modules, module + ".rim"), BuildRim([.. members]));
        return directory;
    }

    /// <summary>Lays out an ERF exactly as <see cref="ErfArchive"/> decodes it: 160-byte
    /// header, 24-byte key entries, 8-byte resource entries, then the member bytes.</summary>
    private static byte[] BuildErf(string fileType, params (string Resref, int Type, byte[] Data)[] members)
    {
        int count = members.Length;
        int keysOffset = 160;
        int valuesOffset = keysOffset + (24 * count);
        int dataOffset = valuesOffset + (8 * count);

        byte[] file = new byte[dataOffset + members.Sum(m => m.Data.Length)];
        Encoding.ASCII.GetBytes(fileType.PadRight(4)).CopyTo(file, 0);
        Encoding.ASCII.GetBytes("V1.0").CopyTo(file, 4);
        WriteU32(file, 0x10, (uint)count);
        WriteU32(file, 0x18, (uint)keysOffset);
        WriteU32(file, 0x1C, (uint)valuesOffset);

        int offset = dataOffset;
        for (int i = 0; i < count; i++)
        {
            (string resref, int type, byte[] data) = members[i];
            Encoding.ASCII.GetBytes(resref).CopyTo(file, keysOffset + (24 * i));
            WriteU32(file, keysOffset + (24 * i) + 16, (uint)i);          // resId
            file[keysOffset + (24 * i) + 20] = (byte)type;                // resType (u16)
            file[keysOffset + (24 * i) + 21] = (byte)(type >> 8);

            WriteU32(file, valuesOffset + (8 * i), (uint)offset);
            WriteU32(file, valuesOffset + (8 * i) + 4, (uint)data.Length);
            data.CopyTo(file, offset);
            offset += data.Length;
        }

        return file;
    }

    /// <summary>Lays out a RIM exactly as <see cref="RimArchive"/> decodes it: 0x78-byte
    /// header, 32-byte entries, then the member bytes.</summary>
    private static byte[] BuildRim(params (string Resref, int Type, byte[] Data)[] members)
    {
        int count = members.Length;
        int tableOffset = 0x78;
        int dataOffset = tableOffset + (32 * count);

        byte[] file = new byte[dataOffset + members.Sum(m => m.Data.Length)];
        Encoding.ASCII.GetBytes("RIM V1.0").CopyTo(file, 0);
        WriteU32(file, 0x0C, (uint)count);
        WriteU32(file, 0x10, (uint)tableOffset);

        int offset = dataOffset;
        for (int i = 0; i < count; i++)
        {
            (string resref, int type, byte[] data) = members[i];
            int entry = tableOffset + (32 * i);
            Encoding.ASCII.GetBytes(resref).CopyTo(file, entry);
            WriteU32(file, entry + 16, (uint)type);
            WriteU32(file, entry + 20, (uint)i);                          // index
            WriteU32(file, entry + 24, (uint)offset);
            WriteU32(file, entry + 28, (uint)data.Length);
            data.CopyTo(file, offset);
            offset += data.Length;
        }

        return file;
    }

    private static void WriteU32(byte[] destination, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset, 4), value);
}
