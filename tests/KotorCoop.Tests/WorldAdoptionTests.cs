using System.Buffers.Binary;
using System.Net;
using System.Text;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// A joiner adopting the host's world, end to end over loopback: two installs whose saves
/// have diverged — the state that used to make a join impossible — become two installs the
/// compatibility check admits, with each player still in their own body.
///
/// The central assertion is <see cref="AdoptionMakesTheSaveIdentityGatePass"/>: before, the
/// two saves fingerprint differently and <see cref="ContentCompatibility"/> refuses; after,
/// they fingerprint identically and it admits with every id-keyed channel intact. That is
/// the whole design in one test.
/// </summary>
public sealed class WorldAdoptionTests : IDisposable
{
    private readonly List<string> _tempDirectories = [];
    private readonly CancellationTokenSource _cts = new();

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();

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

    [Fact]
    public async Task AdoptionMakesTheSaveIdentityGatePass()
    {
        // Two players who have each been playing on their own: different object-id
        // counters, different world contents. This is precisely the pair the session
        // refuses today.
        string hostSave = BuildSave("tar_m02aa", nextObjId: 0x341, "Bastila Shan", [(2u, "n_sith"), (3u, "n_duros")]);
        string joinerSave = BuildSave("end_m01aa", nextObjId: 0x127, "Yasuko Kaar", [(2u, "n_trask")]);

        Assert.True(SaveDigest.TryComputeSaveIdentity(hostSave, out uint hostIdentity));
        Assert.True(SaveDigest.TryComputeSaveIdentity(joinerSave, out uint joinerIdentity));
        Assert.NotEqual(hostIdentity, joinerIdentity);

        // Before: the world-time check refuses outright.
        ContentCompatibility.AllowDifferentSave = false;
        CompatibilityVerdict before = ContentCompatibility.ApplyWorldMismatch(
            Admitted(), globalsCatalogueMismatch: false, saveIdentityMismatch: true, moduleGitMismatch: false);
        Assert.False(before.Admitted);
        Assert.Equal(RefusalReason.SaveGame, before.Refusal);

        // Adopt.
        string joinerGame = BuildGameDirectory();
        int port = await ServeAsync(WorldTransfer.ReadSaveDirectory(hostSave));

        AdoptedWorld? adopted = await WorldAdoption.TryAdoptAsync(
            joinerGame, joinerSave, Local(port), SessionPassword.None,
            expectedSaveIdentity: hostIdentity, log: null, _cts.Token);

        Assert.NotNull(adopted);

        // After: the joiner's staged world fingerprints as the host's, so the mismatch the
        // gate tests for is simply not there any more.
        string staged = Path.Combine(joinerGame, "saves", PluginMenuLoader.AutosaveSlot);
        Assert.True(SaveDigest.TryComputeSaveIdentity(staged, out uint stagedIdentity));
        Assert.Equal(hostIdentity, stagedIdentity);
        Assert.Equal(hostIdentity, adopted!.Value.SaveIdentity);

        CompatibilityVerdict after = ContentCompatibility.ApplyWorldMismatch(
            Admitted(),
            globalsCatalogueMismatch: false,
            saveIdentityMismatch: stagedIdentity != hostIdentity,
            moduleGitMismatch: false);

        Assert.True(after.Admitted);
        Assert.Equal(SessionCapabilities.All, after.Allowed);
    }

    [Fact]
    public async Task TheJoinerKeepsTheirOwnCharacter()
    {
        // The whole point of carrying a pifo: the world is the host's, the body is not.
        string hostSave = BuildSave("tar_m02aa", 0x341, "Bastila Shan", [(2u, "n_sith")]);
        string joinerSave = BuildSave("end_m01aa", 0x127, "Yasuko Kaar", [(2u, "n_trask")]);
        string joinerGame = BuildGameDirectory();

        int port = await ServeAsync(WorldTransfer.ReadSaveDirectory(hostSave));

        Assert.NotNull(await WorldAdoption.TryAdoptAsync(
            joinerGame, joinerSave, Local(port), SessionPassword.None, 0, null, _cts.Token));

        GffStruct carried = Gff
            .Parse(File.ReadAllBytes(Path.Combine(joinerGame, "Temp", "pifo.ifo")))
            .Root.GetList("Mod_PlayerList")[0];

        Assert.Equal("Yasuko Kaar", carried.GetLocString("FirstName")!.Text());

        // ...standing in the host's module, not their own.
        Assert.True(PluginMenuLoader.TryReadSaveModuleName(
            Path.Combine(joinerGame, "saves", PluginMenuLoader.AutosaveSlot), out string module));
        Assert.Equal("tar_m02aa", module, ignoreCase: true);
    }

    [Fact]
    public async Task TheJoinersOwnSaveIsNeverTouched()
    {
        // Adoption writes the autosave slot and Temp\ only. Leaving the session and loading
        // their own game again has to cost nothing, or nobody would risk joining.
        string hostSave = BuildSave("tar_m02aa", 0x341, "Bastila Shan", [(2u, "n_sith")]);
        string joinerSave = BuildSave("end_m01aa", 0x127, "Yasuko Kaar", [(2u, "n_trask")]);
        string joinerGame = BuildGameDirectory();

        byte[] before = File.ReadAllBytes(Path.Combine(joinerSave, "SAVEGAME.sav"));
        DateTime stamp = File.GetLastWriteTimeUtc(Path.Combine(joinerSave, "SAVEGAME.sav"));

        int port = await ServeAsync(WorldTransfer.ReadSaveDirectory(hostSave));
        Assert.NotNull(await WorldAdoption.TryAdoptAsync(
            joinerGame, joinerSave, Local(port), SessionPassword.None, 0, null, _cts.Token));

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(joinerSave, "SAVEGAME.sav")));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(joinerSave, "SAVEGAME.sav")));
    }

    [Fact]
    public async Task ACharacterlessSaveFailsBeforeAnythingIsDestroyed()
    {
        // Order is load-bearing: the step most likely to fail runs before the autosave slot
        // is deleted, so a joiner whose save cannot be read still has whatever was staged.
        string hostSave = BuildSave("tar_m02aa", 0x341, "Bastila Shan", [(2u, "n_sith")]);
        string joinerSave = BuildSave("end_m01aa", 0x127, name: null, [(2u, "n_trask")]);
        string joinerGame = BuildGameDirectory();

        string slot = Path.Combine(joinerGame, "saves", PluginMenuLoader.AutosaveSlot);
        Directory.CreateDirectory(slot);
        File.WriteAllText(Path.Combine(slot, "sentinel.txt"), "still here");

        int port = await ServeAsync(WorldTransfer.ReadSaveDirectory(hostSave));

        Assert.Null(await WorldAdoption.TryAdoptAsync(
            joinerGame, joinerSave, Local(port), SessionPassword.None, 0, null, _cts.Token));

        Assert.True(File.Exists(Path.Combine(slot, "sentinel.txt")));
    }

    [Fact]
    public async Task AWorldThatDoesNotFingerprintAsTheHostsIsRefused()
    {
        // The self-check. If what we staged is not what the host is playing, every id-keyed
        // channel would be quietly wrong — better to fail the join than to play it.
        string hostSave = BuildSave("tar_m02aa", 0x341, "Bastila Shan", [(2u, "n_sith")]);
        string joinerSave = BuildSave("end_m01aa", 0x127, "Yasuko Kaar", [(2u, "n_trask")]);
        string joinerGame = BuildGameDirectory();

        int port = await ServeAsync(WorldTransfer.ReadSaveDirectory(hostSave));

        List<string> log = [];
        Assert.Null(await WorldAdoption.TryAdoptAsync(
            joinerGame, joinerSave, Local(port), SessionPassword.None,
            expectedSaveIdentity: 0xDEADBEEF, log.Add, _cts.Token));

        Assert.Contains(log, line => line.Contains("refusing to play a world we did not receive"));
    }

    [Fact]
    public async Task AnUnreachableHostIsADiagnosticNotACrash()
    {
        string joinerSave = BuildSave("end_m01aa", 0x127, "Yasuko Kaar", [(2u, "n_trask")]);
        string joinerGame = BuildGameDirectory();

        List<string> log = [];
        Assert.Null(await WorldAdoption.TryAdoptAsync(
            joinerGame, joinerSave, new IPEndPoint(IPAddress.Loopback, FreePort()),
            SessionPassword.None, 0, log.Add, _cts.Token));

        Assert.Contains(log, line => line.Contains("could not fetch"));
    }

    // ---- helpers ---------------------------------------------------------------

    private static CompatibilityVerdict Admitted() =>
        new(RefusalReason.None, SessionCapabilities.All, []);

    private static IPEndPoint Local(int port) => new(IPAddress.Loopback, port);

    private async Task<int> ServeAsync(IReadOnlyList<WorldFile> world)
    {
        int port = FreePort();
        _ = WorldTransfer.ServeAsync(world, port, SessionPassword.None, null, _cts.Token);

        for (int attempt = 0; attempt < 200; attempt++)
        {
            try
            {
                using System.Net.Sockets.TcpClient probe = new();
                await probe.ConnectAsync(Local(port), _cts.Token);
                return port;
            }
            catch (System.Net.Sockets.SocketException)
            {
                await Task.Delay(10, _cts.Token);
            }
        }

        throw new TimeoutException($"nothing came up on :{port}");
    }

    private static int FreePort()
    {
        using System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private string BuildGameDirectory()
    {
        string directory = TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "saves"));
        return directory;
    }

    /// <summary>
    /// A save directory holding the nested containers the engine writes, with everything
    /// <see cref="SaveDigest"/> fingerprints: the module name, the NextObjId counters, the
    /// stored GIT's explicit object ids — and a player, unless <paramref name="name"/> is
    /// null.
    /// </summary>
    private string BuildSave(
        string module, uint nextObjId, string? name, (uint Id, string Resref)[] creatures)
    {
        string directory = TempDirectory();

        GffWriter ifo = new("IFO ");
        ifo.Root.SetDword("Mod_NextObjId0", nextObjId).SetDword("Mod_NextObjId1", 0);

        GffWriterList players = ifo.Root.AddList("Mod_PlayerList");
        if (name is not null)
        {
            players.Add(48813)
                .SetDword("ObjectId", 0x7FFFFFFF)
                .SetInt("Mod_IsPrimaryPlr", 1)
                .SetInt("IsPC", 1)
                .SetLocString("FirstName", -1, (0, name))
                .SetShort("CurrentHitPoints", 12)
                .SetFloat("XPosition", 21.0f)
                .AddStruct("FollowInfo", 3)
                .SetVector("LastLeaderPos", 1f, 2f, 3f);
        }

        GffWriter git = new("GIT ");
        GffWriterList list = git.Root.AddList("Creature List");
        foreach ((uint id, string resref) in creatures)
        {
            list.Add(4).SetResRef("TemplateResRef", resref).SetDword("ObjectId", id);
        }

        byte[] inner = BuildErf("SAV ",
            ("Module", 2014 /* ifo */, ifo.ToBytes()),
            (module, 2023 /* git */, git.ToBytes()));

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
        string directory = Path.Combine(
            Path.GetTempPath(), "kotorcoop-adopt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);
        return directory;
    }

    /// <summary>Lays out an ERF exactly as <see cref="ErfArchive"/> decodes it.</summary>
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
