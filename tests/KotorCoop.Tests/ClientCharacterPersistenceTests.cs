using System.Buffers.Binary;
using System.Text;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Roadmap item 14 phase 1, "resume tomorrow" (docs/research/session-save.md §2). Two
/// halves: the PURE preference rule (<see cref="ClientCharacterPersistence.ShouldPreferSidecar"/>)
/// — no filesystem at all — and the sidecar's own atomic write/tolerant read, exercised
/// against a real temp directory.
/// </summary>
public sealed class ClientCharacterPersistenceTests : IDisposable
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

    // ---- the pure preference rule ------------------------------------------------

    [Fact]
    public void ANewerValidSidecarBeatsTheOwnSave()
    {
        DateTime ownSave = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime sidecar = ownSave + TimeSpan.FromDays(1);

        bool prefer = ClientCharacterPersistence.ShouldPreferSidecar(
            sidecarValid: true, sidecar, ownSave,
            sidecarHostIdentity: 0, currentHostIdentity: 0, out string reason);

        Assert.True(prefer);
        Assert.Contains("newer", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnInvalidSidecarFallsBack()
    {
        // Stands in for BOTH "missing" and "corrupt": TryReadSidecar collapses both to
        // sidecarValid=false before this rule ever runs, so from here they are one case.
        bool prefer = ClientCharacterPersistence.ShouldPreferSidecar(
            sidecarValid: false,
            sidecarCapturedAtUtc: DateTime.UtcNow,
            ownSaveLastWriteUtc: DateTime.UtcNow - TimeSpan.FromDays(1),
            sidecarHostIdentity: 0, currentHostIdentity: 0, out string reason);

        Assert.False(prefer);
        Assert.Contains("no valid", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASidecarNoNewerThanTheOwnSaveFallsBack()
    {
        DateTime same = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        bool preferEqual = ClientCharacterPersistence.ShouldPreferSidecar(
            sidecarValid: true, same, same, 0, 0, out string reasonEqual);
        Assert.False(preferEqual);
        Assert.Contains("own save", reasonEqual, StringComparison.OrdinalIgnoreCase);

        bool preferOlder = ClientCharacterPersistence.ShouldPreferSidecar(
            sidecarValid: true, same - TimeSpan.FromHours(1), same, 0, 0, out string reasonOlder);
        Assert.False(preferOlder);
    }

    [Fact]
    public void AKnownHostIdentityMismatchFallsBackEvenWhenNewer()
    {
        DateTime ownSave = DateTime.UtcNow - TimeSpan.FromDays(1);
        DateTime sidecar = DateTime.UtcNow;

        bool prefer = ClientCharacterPersistence.ShouldPreferSidecar(
            sidecarValid: true, sidecar, ownSave,
            sidecarHostIdentity: 0x1111_1111, currentHostIdentity: 0x2222_2222, out string reason);

        Assert.False(prefer);
        Assert.Contains("different host save", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnknownHostIdentityOnEitherSideNeverBlocks()
    {
        DateTime ownSave = DateTime.UtcNow - TimeSpan.FromDays(1);
        DateTime sidecar = DateTime.UtcNow;

        // Sidecar recorded no host identity (0 = never captured against a known host, or a
        // pre-item-14 build) — never blocks, per the house "unknown is not a mismatch" rule.
        Assert.True(ClientCharacterPersistence.ShouldPreferSidecar(
            true, sidecar, ownSave, sidecarHostIdentity: 0, currentHostIdentity: 0xABCD, out _));

        // Current host identity not yet known (e.g. decided before a fetch resolves it) —
        // also never blocks.
        Assert.True(ClientCharacterPersistence.ShouldPreferSidecar(
            true, sidecar, ownSave, sidecarHostIdentity: 0xABCD, currentHostIdentity: 0, out _));
    }

    [Fact]
    public void AKnownMatchingHostIdentityIsFine()
    {
        DateTime ownSave = DateTime.UtcNow - TimeSpan.FromDays(1);
        DateTime sidecar = DateTime.UtcNow;

        Assert.True(ClientCharacterPersistence.ShouldPreferSidecar(
            true, sidecar, ownSave, sidecarHostIdentity: 0x5555, currentHostIdentity: 0x5555, out _));
    }

    // ---- the sidecar's own I/O: atomic write, tolerant read -----------------------

    [Fact]
    public void ASidecarRoundTripsTheCapturedCharacter()
    {
        string saves = TempDirectory();
        GffStruct player = BuildPlayer("Yasuko Kaar");

        Assert.True(ClientCharacterPersistence.TryWriteSidecar(
            saves, player, hostSaveIdentity: 0x1234_5678, DateTime.UtcNow, out string writeDiag));
        Assert.True(File.Exists(Path.Combine(saves, ClientCharacterPersistence.PifoFileName)));
        Assert.True(File.Exists(Path.Combine(saves, ClientCharacterPersistence.MetaFileName)));

        Assert.True(ClientCharacterPersistence.TryReadSidecar(
            saves, out GffStruct read, out SidecarMetadata meta, out string readDiag));

        Assert.Equal("Yasuko Kaar", read.GetLocString("FirstName")!.Text());
        Assert.Equal(0x1234_5678u, meta.HostSaveIdentity);
        Assert.True(writeDiag.Length > 0);
        Assert.True(readDiag.Length > 0);
    }

    [Fact]
    public void ReadingAMissingSidecarFailsCleanly()
    {
        string saves = TempDirectory();

        Assert.False(ClientCharacterPersistence.TryReadSidecar(
            saves, out _, out SidecarMetadata meta, out string diagnostic));
        Assert.Equal(0u, meta.HostSaveIdentity);
        Assert.Contains("no session-saved character", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadingAHalfWrittenPifoFailsCleanlyRatherThanThrowing()
    {
        string saves = TempDirectory();
        GffStruct player = BuildPlayer("Bastila Shan");

        Assert.True(ClientCharacterPersistence.TryWriteSidecar(
            saves, player, 0, DateTime.UtcNow, out _));

        // Simulate a process killed mid-write: the pifo is truncated garbage, the json is
        // untouched (still valid) — TryReadSidecar must report false, never throw.
        File.WriteAllBytes(Path.Combine(saves, ClientCharacterPersistence.PifoFileName), [1, 2, 3]);

        Assert.False(ClientCharacterPersistence.TryReadSidecar(saves, out _, out _, out string diagnostic));
        Assert.True(diagnostic.Length > 0);
    }

    [Fact]
    public void ReadingCorruptMetadataFailsCleanlyRatherThanThrowing()
    {
        string saves = TempDirectory();
        GffStruct player = BuildPlayer("Bastila Shan");

        Assert.True(ClientCharacterPersistence.TryWriteSidecar(
            saves, player, 0, DateTime.UtcNow, out _));

        File.WriteAllText(Path.Combine(saves, ClientCharacterPersistence.MetaFileName), "{ not json");

        Assert.False(ClientCharacterPersistence.TryReadSidecar(saves, out _, out _, out string diagnostic));
        Assert.True(diagnostic.Length > 0);
    }

    [Fact]
    public void CaptureAndWriteExtractsFromARealSaveDirectory()
    {
        string save = BuildSave("end_m01aa", nextObjId: 0x99, "Trask Ulgo");
        string saves = TempDirectory();

        Assert.True(ClientCharacterPersistence.TryCaptureAndWrite(
            saves, save, hostSaveIdentity: 0x42, out string diagnostic));
        Assert.True(diagnostic.Length > 0);

        Assert.True(ClientCharacterPersistence.TryReadSidecar(saves, out GffStruct read, out SidecarMetadata meta, out _));
        Assert.Equal("Trask Ulgo", read.GetLocString("FirstName")!.Text());
        Assert.Equal(0x42u, meta.HostSaveIdentity);
    }

    [Fact]
    public void ASecondCaptureOverwritesTheFirstAtomically()
    {
        string saves = TempDirectory();

        Assert.True(ClientCharacterPersistence.TryWriteSidecar(
            saves, BuildPlayer("First"), 1, DateTime.UtcNow, out _));
        Assert.True(ClientCharacterPersistence.TryWriteSidecar(
            saves, BuildPlayer("Second"), 2, DateTime.UtcNow, out _));

        Assert.True(ClientCharacterPersistence.TryReadSidecar(saves, out GffStruct read, out SidecarMetadata meta, out _));
        Assert.Equal("Second", read.GetLocString("FirstName")!.Text());
        Assert.Equal(2u, meta.HostSaveIdentity);

        // No stray .tmp files left behind.
        Assert.Empty(Directory.GetFiles(saves, "*.tmp"));
    }

    // ---- helpers --------------------------------------------------------------

    private static GffStruct BuildPlayer(string name)
    {
        string save = "";
        try
        {
            save = BuildSaveStatic("end_m01aa", 0x1, name);
            Assert.True(PlayerIfo.TryExtractPlayer(save, out GffStruct player, out _));
            return player;
        }
        finally
        {
            if (save.Length > 0)
            {
                try
                {
                    Directory.Delete(save, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private string BuildSave(string module, uint nextObjId, string name)
    {
        string directory = BuildSaveStatic(module, nextObjId, name);
        _tempDirectories.Add(directory);
        return directory;
    }

    /// <summary>A save directory holding one player, in the exact shape
    /// <see cref="PlayerIfo.TryExtractPlayer"/> reads — trimmed from
    /// <c>WorldAdoptionTests.BuildSave</c> to just what a character-only test needs.</summary>
    private static string BuildSaveStatic(string module, uint nextObjId, string name)
    {
        string directory = Path.Combine(Path.GetTempPath(), "kotorcoop-charpersist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        GffWriter ifo = new("IFO ");
        ifo.Root.SetDword("Mod_NextObjId0", nextObjId).SetDword("Mod_NextObjId1", 0);

        GffWriterList players = ifo.Root.AddList("Mod_PlayerList");
        players.Add(48813)
            .SetDword("ObjectId", 0x7FFFFFFF)
            .SetInt("Mod_IsPrimaryPlr", 1)
            .SetInt("IsPC", 1)
            .SetLocString("FirstName", -1, (0, name))
            .SetShort("CurrentHitPoints", 12)
            .SetFloat("XPosition", 21.0f)
            .AddStruct("FollowInfo", 3)
            .SetVector("LastLeaderPos", 1f, 2f, 3f);

        byte[] inner = BuildErf("SAV ", ("Module", 2014 /* ifo */, ifo.ToBytes()));

        File.WriteAllBytes(
            Path.Combine(directory, "SAVEGAME.sav"),
            BuildErf("SAV ", (module, 2057 /* module .sav */, inner)));

        File.WriteAllBytes(Path.Combine(directory, "GLOBALVARS.res"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(directory, "PARTYTABLE.res"), [5, 6, 7, 8]);
        File.WriteAllBytes(Path.Combine(directory, "savenfo.res"), [9]);

        return directory;
    }

    private string TempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kotorcoop-charpersist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);
        return directory;
    }

    /// <summary>Lays out an ERF exactly as <see cref="ErfArchive"/> decodes it — the same
    /// layout <c>WorldAdoptionTests</c> builds, duplicated here so this file has no
    /// cross-file test dependency.</summary>
    private static byte[] BuildErf(string fileType, params (string Resref, int Type, byte[] Data)[] members)
    {
        int count = members.Length;
        int keysOffset = 160;
        int valuesOffset = keysOffset + (24 * count);
        int dataOffset = valuesOffset + (8 * count);

        byte[] file = new byte[dataOffset + members.Sum(m => m.Data.Length)];
        Encoding.ASCII.GetBytes(fileType.PadRight(4)).CopyTo(file, 0);
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
