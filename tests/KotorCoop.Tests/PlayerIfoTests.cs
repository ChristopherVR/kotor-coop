using System.Buffers.Binary;
using System.Collections;
using System.Text;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Carrying a character into somebody else's world: the GFF deep copy that re-emits a saved
/// PC, the standalone pifo built from it, and the pifo-neutralisation that stops an adopted
/// autosave putting the joiner in the host's body.
///
/// The oracle throughout is <see cref="Gff"/> — a copy is correct when parsing the written
/// bytes yields a structure indistinguishable from the source, field for field and type for
/// type, all the way down. Saves are built with the real writer and hand-laid ERF
/// containers, the same way <see cref="SaveDigestTests"/> does, so none of this needs a game
/// installed.
/// </summary>
public sealed class PlayerIfoTests : IDisposable
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

    // ---- the deep copy ---------------------------------------------------------

    [Fact]
    public void CopyReproducesEveryFieldTypeIncludingNesting()
    {
        // Every type the reader can produce, including the two the writer could not emit
        // before world adoption needed them: a real player entry carries a VECTOR in
        // FollowInfo (FollowLocation, LastLeaderPos, LastFollowerPos).
        GffWriter source = new("IFO ");
        source.Root
            .SetByte("Byte", 200)
            .SetChar("Char", -5)
            .SetWord("Word", 40_000)
            .SetShort("Short", -1234)
            .SetDword("ObjectId", 0x7FFFFFFF)
            .SetInt("Int", -7)
            .SetDword64("Id", 0xDEADBEEFCAFEBABE)
            .SetInt64("Int64", -9_000_000_000)
            .SetFloat("ChallengeRating", 1.5f)
            .SetDouble("Double", -2.25)
            .SetString("Mod_CommntyName", "Yasuko Kaar")
            .SetResRef("ScriptDeath", "k_def_death01")
            .SetLocString("FirstName", -1, (0, "Yasuko Kaar"))
            .SetVoid("Blob", [1, 2, 3, 4])
            .SetStrRef("StrRef", 5595)
            .SetVector("LastLeaderPos", 21.0f, 22.5f, -1.27f)
            .SetOrientation("Orientation", 0f, 0f, 0.7071f, 0.7071f);

        source.Root.AddStruct("FollowInfo", 3).SetVector("FollowLocation", 1f, 2f, 3f);

        GffWriterList classes = source.Root.AddList("ClassList");
        classes.Add(2).SetInt("Class", 0).SetShort("ClassLevel", 1);
        classes.Add(2).SetInt("Class", 1).SetShort("ClassLevel", 2);
        source.Root.AddList("EffectList");

        GffStruct original = Gff.Parse(source.ToBytes()).Root;

        GffStruct copied = Gff.Parse(GffCopy.ToFile(original, "IFO ")).Root;

        AssertStructsMatch(original, copied, "root");
    }

    [Fact]
    public void CopyPreservesNestedStructIds()
    {
        // List-member struct ids are data: the game's own blueprints number them
        // meaningfully, and a copy that renumbered them would hand a switching reader the
        // wrong shape. A real save's player entry is id 48813.
        GffWriter source = new("IFO ");
        GffWriterList list = source.Root.AddList("Mod_PlayerList");
        list.Add(48813).SetInt("IsPC", 1);
        source.Root.AddStruct("CombatInfo", 17).SetByte("Flags", 1);

        GffStruct original = Gff.Parse(source.ToBytes()).Root;
        GffStruct copied = Gff.Parse(GffCopy.ToFile(original, "IFO ")).Root;

        Assert.Equal(48813u, copied.GetList("Mod_PlayerList")[0].StructId);
        Assert.Equal(17u, copied.GetStruct("CombatInfo")!.StructId);
        AssertStructsMatch(original, copied, "root");
    }

    [Fact]
    public void CopyKeepsExoStringAndResRefApart()
    {
        // Both decode to string, so a copy that chose by CLR type instead of recorded GFF
        // type would silently rewrite one as the other.
        GffWriter source = new("UTC ");
        source.Root.SetString("Text", "value").SetResRef("Ref", "value");

        GffStruct copied = Gff.Parse(GffCopy.ToFile(Gff.Parse(source.ToBytes()).Root, "UTC ")).Root;

        Assert.True(copied.TryGetFieldType("Text", out GffFieldType text));
        Assert.True(copied.TryGetFieldType("Ref", out GffFieldType resref));
        Assert.Equal(GffFieldType.ExoString, text);
        Assert.Equal(GffFieldType.ResRef, resref);
    }

    // ---- extracting the character ----------------------------------------------

    [Fact]
    public void ExtractsThePlayerFromASave()
    {
        string save = BuildSaveDirectory("end_m01aa", ("Yasuko Kaar", true));

        Assert.True(PlayerIfo.TryExtractPlayer(save, out GffStruct player, out string diagnostic));

        Assert.True(player.TryGetInt("IsPC", out int isPc));
        Assert.Equal(1, isPc);
        Assert.Equal("Yasuko Kaar", player.GetLocString("FirstName")!.Text());
        Assert.Contains("end_m01aa", diagnostic);
    }

    [Fact]
    public void PicksThePrimaryPlayerNotTheFirstEntry()
    {
        // GetPrimaryPlayerIndex (0x4C5D90) walks for the is-PC mark rather than taking
        // index 0, so a save that ever holds more than one entry must not be read by
        // position.
        string save = BuildSaveDirectory(
            "end_m01aa",
            ("Somebody Else", false),
            ("Yasuko Kaar", true));

        Assert.True(PlayerIfo.TryExtractPlayer(save, out GffStruct player, out _));
        Assert.Equal("Yasuko Kaar", player.GetLocString("FirstName")!.Text());
    }

    [Fact]
    public void AnEmptyPlayerListIsRefusedRatherThanCarried()
    {
        // The measured failure this guards: the engine's 0xF login branch ignores
        // LoadCharacterFromIFO's return value, so an empty list produces a blank level-0 PC
        // at the module start point instead of an error. Catch it before staging.
        string save = BuildSaveDirectory("end_m01aa");

        Assert.False(PlayerIfo.TryExtractPlayer(save, out _, out string diagnostic));
        Assert.Contains("Mod_PlayerList", diagnostic);
    }

    [Fact]
    public void AMissingSaveIsADiagnosticNotAnException()
    {
        Assert.False(PlayerIfo.TryExtractPlayer(TempDirectory(), out _, out string diagnostic));
        Assert.Contains("SAVEGAME.sav", diagnostic);
    }

    // ---- the pifo --------------------------------------------------------------

    [Fact]
    public void PifoIsAnIfoHoldingExactlyOnePlayerAtIndexZero()
    {
        // LoadCharacterFromIFO(-1) indexes Mod_PlayerList by [player+0x80], which is 0 from
        // the constructor on this path. Emitting one entry makes that index right whichever
        // entry the source save marked.
        string save = BuildSaveDirectory(
            "end_m01aa",
            ("Somebody Else", false),
            ("Yasuko Kaar", true));

        Assert.True(PlayerIfo.TryExtractPlayer(save, out GffStruct player, out _));

        Gff pifo = Gff.Parse(PlayerIfo.BuildPifo(player));

        Assert.Equal("IFO ", pifo.FileType);
        IReadOnlyList<GffStruct> carried = pifo.Root.GetList("Mod_PlayerList");
        Assert.Single(carried);
        Assert.Equal("Yasuko Kaar", carried[0].GetLocString("FirstName")!.Text());

        // Nothing but the player travels: a foreign module.ifo's own state could only ever
        // disagree with the world actually loading.
        Assert.Single(pifo.Root.FieldNames);
    }

    [Fact]
    public void PifoCarriesTheCharacterLosslessly()
    {
        string save = BuildSaveDirectory("end_m01aa", ("Yasuko Kaar", true));
        Assert.True(PlayerIfo.TryExtractPlayer(save, out GffStruct player, out _));

        GffStruct carried = Gff.Parse(PlayerIfo.BuildPifo(player)).Root.GetList("Mod_PlayerList")[0];

        AssertStructsMatch(player, carried, "player");
    }

    [Fact]
    public void TheSavedObjectIdIsCarriedUntouched()
    {
        // Measured on two real saves: a saved PC's ObjectId is 0x7FFFFFFF, the invalid
        // sentinel, so the engine mints a fresh id on load. That is what makes carrying a
        // character into a foreign world safe — there is no id to collide with anything
        // already there — and it only holds while the copy leaves the field alone.
        string save = BuildSaveDirectory("end_m01aa", ("Yasuko Kaar", true));
        Assert.True(PlayerIfo.TryExtractPlayer(save, out GffStruct player, out _));

        GffStruct carried = Gff.Parse(PlayerIfo.BuildPifo(player)).Root.GetList("Mod_PlayerList")[0];

        Assert.True(carried.TryGetDword("ObjectId", out uint objectId));
        Assert.Equal(0x7FFFFFFFu, objectId);
    }

    [Fact]
    public void StagingWritesTempPifoIfo()
    {
        string game = TempDirectory();
        string save = BuildSaveDirectory("end_m01aa", ("Yasuko Kaar", true));

        Assert.True(PlayerIfo.TryStagePlayer(game, save, out string diagnostic));

        string path = Path.Combine(game, "Temp", "pifo.ifo");
        Assert.True(File.Exists(path), diagnostic);

        GffStruct carried = Gff.Parse(File.ReadAllBytes(path)).Root.GetList("Mod_PlayerList")[0];
        Assert.Equal("Yasuko Kaar", carried.GetLocString("FirstName")!.Text());
    }

    [Fact]
    public void StagingIsIdempotent()
    {
        // It is called both before and after LoadPCAutoSave, because whether that function
        // can clear an existing Temp\pifo.ifo is not yet measured on the live game.
        string game = TempDirectory();
        string save = BuildSaveDirectory("end_m01aa", ("Yasuko Kaar", true));

        Assert.True(PlayerIfo.TryStagePlayer(game, save, out _));
        byte[] first = File.ReadAllBytes(Path.Combine(game, "Temp", "pifo.ifo"));

        Assert.True(PlayerIfo.TryStagePlayer(game, save, out _));
        byte[] second = File.ReadAllBytes(Path.Combine(game, "Temp", "pifo.ifo"));

        Assert.Equal(first, second);
    }

    // ---- the autosave trap -----------------------------------------------------

    [Fact]
    public void NeutralisingAPifoLeavesTheArchiveReadable()
    {
        // LoadPCAutoSave copies <export>\pifo over TEMP:pifo. Adopting a host's AUTOSAVE
        // verbatim would therefore overwrite the joiner's staged character with the host's,
        // and both players would come up as the same person. The rename is in place: every
        // other member must still read back.
        byte[] savegame = BuildErf("SAV ",
            ("Module", 2014, MinimalIfo()),
            ("pifo", 2014, MinimalIfo()),
            ("REPUTE", 2038, [1, 2, 3]));

        Assert.True(PlayerIfo.TryNeutralisePifo(savegame));

        Assert.False(ErfArchive.TryFindMember(savegame, 2014, "pifo", out _));
        Assert.True(ErfArchive.TryFindMember(savegame, 2014, "Module", out byte[] module));
        Assert.True(ErfArchive.TryFindMember(savegame, 2038, "REPUTE", out byte[] repute));
        Assert.Equal(MinimalIfo(), module);
        Assert.Equal<byte[]>([1, 2, 3], repute);
    }

    [Fact]
    public void NeutralisingASaveWithNoPifoChangesNothing()
    {
        // The common case — a manual save has no pifo, which is why the existing load path
        // was never affected by this.
        byte[] savegame = BuildErf("SAV ", ("Module", 2014, MinimalIfo()));
        byte[] before = [.. savegame];

        Assert.False(PlayerIfo.TryNeutralisePifo(savegame));
        Assert.Equal(before, savegame);
    }

    [Fact]
    public void NeutralisingRejectsATruncatedArchive()
    {
        Assert.False(PlayerIfo.TryNeutralisePifo([1, 2, 3]));
    }

    // ---- helpers ---------------------------------------------------------------

    /// <summary>
    /// Recursively asserts two parsed structs are indistinguishable: same field order, same
    /// recorded types, same values, same nested struct ids. This is the whole correctness
    /// claim of the copy — byte-identity with BioWare's writer is not claimed and not
    /// needed.
    /// </summary>
    private static void AssertStructsMatch(GffStruct expected, GffStruct actual, string path)
    {
        Assert.Equal(expected.StructId, actual.StructId);
        Assert.Equal(expected.FieldNames, actual.FieldNames);

        foreach (string label in expected.FieldNames)
        {
            string here = $"{path}/{label}";

            Assert.True(expected.TryGetFieldType(label, out GffFieldType expectedType));
            Assert.True(actual.TryGetFieldType(label, out GffFieldType actualType));
            Assert.True(expectedType == actualType, $"{here}: {expectedType} became {actualType}");

            object left = expected.Fields[label];
            object right = actual.Fields[label];

            switch (expectedType)
            {
                case GffFieldType.Struct:
                    AssertStructsMatch((GffStruct)left, (GffStruct)right, here);
                    break;

                case GffFieldType.List:
                {
                    IReadOnlyList<GffStruct> a = (IReadOnlyList<GffStruct>)left;
                    IReadOnlyList<GffStruct> b = (IReadOnlyList<GffStruct>)right;
                    Assert.True(a.Count == b.Count, $"{here}: {a.Count} members became {b.Count}");
                    for (int i = 0; i < a.Count; i++)
                    {
                        AssertStructsMatch(a[i], b[i], $"{here}[{i}]");
                    }

                    break;
                }

                case GffFieldType.ExoLocString:
                {
                    GffLocString a = (GffLocString)left;
                    GffLocString b = (GffLocString)right;
                    Assert.True(a.StrRef == b.StrRef, $"{here}: strref {a.StrRef} became {b.StrRef}");
                    Assert.Equal(a.Substrings, b.Substrings);
                    break;
                }

                case GffFieldType.Void:
                case GffFieldType.Vector:
                case GffFieldType.Orientation:
                    // Decoded as arrays, so reference equality would never hold.
                    Assert.True(
                        StructuralComparisons.StructuralEqualityComparer.Equals(left, right),
                        $"{here}: array value differs");
                    break;

                default:
                    Assert.True(left.Equals(right), $"{here}: {left} became {right}");
                    break;
            }
        }
    }

    /// <summary>Fills a list member so it looks like a real player entry: the sentinel
    /// ObjectId, the two PC marks, a nested struct with a VECTOR and a couple of lists.</summary>
    private static void PopulatePlayer(GffWriterStruct player, string name, bool isPrimary)
    {
        player
            .SetDword("ObjectId", 0x7FFFFFFF)
            .SetInt("Mod_IsPrimaryPlr", isPrimary ? 1 : 0)
            .SetInt("IsPC", isPrimary ? 1 : 0)
            .SetString("Mod_CommntyName", name)
            .SetLocString("FirstName", -1, (0, name))
            .SetDword("AreaId", 1)
            .SetDword("Experience", 150)
            .SetShort("CurrentHitPoints", 12)
            .SetFloat("XPosition", 21.013109f)
            .SetFloat("YPosition", 21.150425f)
            .SetFloat("ZPosition", -1.275f)
            .SetResRef("ScriptDeath", "k_def_death01");

        player.AddStruct("FollowInfo", 3)
            .SetVector("FollowLocation", 0f, 0f, 0f)
            .SetVector("LastLeaderPos", 21.0f, 21.1f, -1.27f);

        GffWriterList classes = player.AddList("ClassList");
        classes.Add(2).SetInt("Class", 0).SetShort("ClassLevel", 1);

        player.AddList("EffectList");
    }

    /// <summary>A save directory holding the nested containers the engine writes:
    /// SAVEGAME.sav → &lt;module&gt;.sav → module.ifo, with the PC in Mod_PlayerList.</summary>
    private string BuildSaveDirectory(string module, params (string Name, bool IsPrimary)[] players)
    {
        string directory = TempDirectory();

        GffWriter ifo = new("IFO ");
        ifo.Root.SetDword("Mod_NextObjId0", 0x127).SetDword("Mod_NextObjId1", 0);

        // 48813 is the struct id every real save measured uses for a player entry.
        GffWriterList list = ifo.Root.AddList("Mod_PlayerList");
        foreach ((string name, bool isPrimary) in players)
        {
            PopulatePlayer(list.Add(48813), name, isPrimary);
        }

        byte[] inner = BuildErf("SAV ",
            ("Module", 2014 /* ifo */, ifo.ToBytes()),
            (module, 2023 /* git */, new GffWriter("GIT ").ToBytes()));

        File.WriteAllBytes(
            Path.Combine(directory, "SAVEGAME.sav"),
            BuildErf("SAV ", (module, 2057 /* module .sav */, inner)));

        return directory;
    }

    private static byte[] MinimalIfo() => new GffWriter("IFO ").ToBytes();

    private string TempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kotorcoop-pifo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);
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

    private static void WriteU32(byte[] destination, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset, 4), value);
}
