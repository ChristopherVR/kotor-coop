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

        // Before: the world-time check costs every object-id-keyed channel (and, for a host
        // that asked for the strict pre-adoption rule, the whole session).
        ContentCompatibility.RequireIdenticalSave = false;
        CompatibilityVerdict before = ContentCompatibility.ApplyWorldMismatch(
            Admitted(), globalsCatalogueMismatch: false, saveIdentityMismatch: true, moduleGitMismatch: false);
        Assert.True(before.Admitted);
        Assert.Equal(SessionCapabilities.None, before.Allowed & ContentCompatibility.ObjectIdKeyed);

        try
        {
            ContentCompatibility.RequireIdenticalSave = true;
            CompatibilityVerdict strict = ContentCompatibility.ApplyWorldMismatch(
                Admitted(), globalsCatalogueMismatch: false, saveIdentityMismatch: true, moduleGitMismatch: false);
            Assert.Equal(RefusalReason.SaveGame, strict.Refusal);
        }
        finally
        {
            ContentCompatibility.RequireIdenticalSave = false;
        }

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
    public async Task ANewerSessionSavedCharacterBeatsTheOwnSaveAtAdoption()
    {
        // Roadmap item 14 phase 1: a character captured at the end of a PREVIOUS session
        // (ClientCharacterPersistence) is what actually gets carried into the world when it
        // is present, valid and newer than the joiner's own save.
        string hostSave = BuildSave("tar_m02aa", 0x341, "Bastila Shan", [(2u, "n_sith")]);
        string joinerSave = BuildSave("end_m01aa", 0x127, "Old Character", [(2u, "n_trask")]);
        string joinerGame = BuildGameDirectory();

        Assert.True(SaveDigest.TryComputeSaveIdentity(hostSave, out uint hostIdentity));

        // Give the sidecar a distinguishable name so the test can tell WHICH character rode.
        string sessionSavedPath = BuildSave("end_m01aa", 0x999, "New Character", [(2u, "n_trask")]);
        Assert.True(PlayerIfo.TryExtractPlayer(sessionSavedPath, out GffStruct newerPlayer, out _));

        SidecarMetadata metadata = new(hostIdentity, DateTime.UtcNow);

        int port = await ServeAsync(WorldTransfer.ReadSaveDirectory(hostSave), hostIdentity);

        List<string> log = [];
        AdoptedWorld? adopted = await WorldAdoption.TryAdoptAsync(
            joinerGame, joinerSave, (newerPlayer, metadata), Local(port), SessionPassword.None,
            expectedSaveIdentity: 0, log.Add, _cts.Token);

        Assert.NotNull(adopted);
        Assert.Contains("session-saved character", adopted!.Value.CharacterSource, StringComparison.OrdinalIgnoreCase);

        GffStruct carried = Gff
            .Parse(File.ReadAllBytes(Path.Combine(joinerGame, "Temp", "pifo.ifo")))
            .Root.GetList("Mod_PlayerList")[0];
        Assert.Equal("New Character", carried.GetLocString("FirstName")!.Text());
    }

    [Fact]
    public async Task AHostIdentityMismatchedSidecarFallsBackToTheOwnSaveAtAdoption()
    {
        // The sidecar was captured against a DIFFERENT host save than the one being adopted
        // now — the preference rule must not carry it in, even though it is newer.
        string hostSave = BuildSave("tar_m02aa", 0x341, "Bastila Shan", [(2u, "n_sith")]);
        string joinerSave = BuildSave("end_m01aa", 0x127, "Own Character", [(2u, "n_trask")]);
        string joinerGame = BuildGameDirectory();

        string otherHostSave = BuildSave("tat_m17aa", 0x555, "Different World", [(2u, "n_gamorrean")]);
        Assert.True(SaveDigest.TryComputeSaveIdentity(otherHostSave, out uint otherHostIdentity));
        Assert.True(PlayerIfo.TryExtractPlayer(joinerSave, out GffStruct sidecarPlayer, out _));

        SidecarMetadata metadata = new(otherHostIdentity, DateTime.UtcNow);

        int port = await ServeAsync(WorldTransfer.ReadSaveDirectory(hostSave));

        AdoptedWorld? adopted = await WorldAdoption.TryAdoptAsync(
            joinerGame, joinerSave, (sidecarPlayer, metadata), Local(port), SessionPassword.None,
            expectedSaveIdentity: 0, log: null, _cts.Token);

        Assert.NotNull(adopted);
        Assert.Contains("own save", adopted!.Value.CharacterSource, StringComparison.OrdinalIgnoreCase);

        GffStruct carried = Gff
            .Parse(File.ReadAllBytes(Path.Combine(joinerGame, "Temp", "pifo.ifo")))
            .Root.GetList("Mod_PlayerList")[0];
        Assert.Equal("Own Character", carried.GetLocString("FirstName")!.Text());
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
    public async Task TheHostsOwnStatedIdentityIsEnoughToSelfCheckAnAdoption()
    {
        // The gap this closes: `agent adopt` had no way to learn the host's identity, so it
        // passed 0 and the self-check never ran on the path that actually ships. The host
        // now states it in the transfer header, and a wrong one is caught with no caller
        // help at all.
        string hostSave = BuildSave("tar_m02aa", 0x341, "Bastila Shan", [(2u, "n_sith")]);
        string joinerSave = BuildSave("end_m01aa", 0x127, "Yasuko Kaar", [(2u, "n_trask")]);

        Assert.True(SaveDigest.TryComputeSaveIdentity(hostSave, out uint hostIdentity));

        // A host telling the truth: the adoption verifies and succeeds, caller passing 0.
        int honest = await ServeAsync(WorldTransfer.ReadSaveDirectory(hostSave), hostIdentity);
        AdoptedWorld? adopted = await WorldAdoption.TryAdoptAsync(
            BuildGameDirectory(), joinerSave, Local(honest), SessionPassword.None,
            expectedSaveIdentity: 0, log: null, _cts.Token);

        Assert.NotNull(adopted);
        Assert.Equal(hostIdentity, adopted!.Value.SaveIdentity);

        // A host whose stated identity does not match what it actually sent: refused.
        int lying = await ServeAsync(WorldTransfer.ReadSaveDirectory(hostSave), hostIdentity ^ 0xFFFFu);
        List<string> log = [];
        Assert.Null(await WorldAdoption.TryAdoptAsync(
            BuildGameDirectory(), joinerSave, Local(lying), SessionPassword.None,
            expectedSaveIdentity: 0, log.Add, _cts.Token));

        Assert.Contains(log, line => line.Contains("refusing to play a world we did not receive"));
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
    public async Task AHostWithNoSaveIsReportedClearlyNotAsAGenericFetchFailure()
    {
        // The first-time-host UX gap: pressing Host before ever saving must not read like a
        // network bug or a version mismatch to the joiner - see WorldTransferRefusedException.
        string joinerSave = BuildSave("end_m01aa", 0x127, "Yasuko Kaar", [(2u, "n_trask")]);
        string joinerGame = BuildGameDirectory();

        int port = await ServeAsync([]);

        List<string> log = [];
        Assert.Null(await WorldAdoption.TryAdoptAsync(
            joinerGame, joinerSave, Local(port), SessionPassword.None,
            expectedSaveIdentity: 0, log.Add, _cts.Token));

        Assert.Contains(log, line => line.Contains("no save to share yet"));
        Assert.DoesNotContain(log, line => line.Contains("could not fetch"));
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

    /// <summary>
    /// The <c>join_mode = newcharacter</c> adoption: a joiner with NO SAVE AT ALL, carrying the
    /// character the game's own chargen just wrote.
    ///
    /// Two things are pinned here, and the first is the whole point of the feature. The
    /// ordinary overload refuses this joiner — there is no <c>SAVEGAME.sav</c> to lift a
    /// character out of — while the carried overload lands the host's world with the handed-in
    /// character staged as <c>Temp\pifo.ifo</c> AND as the <c>kcoop_carried.ifo</c> stash the
    /// plugin restores from after <c>LoadPCAutoSave</c> truncates the former
    /// (world-adoption.md §7). The second is that the staged world still fingerprints as the
    /// host's — the provenance of the character changes nothing about the world half.
    /// </summary>
    [Fact]
    public async Task AJoinerWithNoSaveCanAdoptCarryingACharacterHandedIn()
    {
        string hostSave = BuildSave("tar_m02aa", nextObjId: 0x341, "Bastila Shan", [(2u, "n_sith")]);
        Assert.True(SaveDigest.TryComputeSaveIdentity(hostSave, out uint hostIdentity));

        int port = await ServeAsync(WorldTransfer.ReadSaveDirectory(hostSave), hostIdentity);

        // A first-time joiner: the install exists, the saves directory is empty.
        string joinerGame = BuildGameDirectory();

        // Exactly what the plugin's capture hands back: a chargen pifo, read as one entry.
        string chargenSave = BuildSave("end_m01aa", nextObjId: 0x127, "Fresh Recruit", [(2u, "n_trask")]);
        Assert.True(PlayerIfo.TryExtractPlayer(chargenSave, out GffStruct created, out _));
        Assert.True(PlayerIfo.TryExtractPlayerFromPifoBytes(
            PlayerIfo.BuildPifo(created), out GffStruct roundTripped, out _));

        // The ordinary path cannot serve this player at all — that is the bug being fixed.
        List<string> refusal = [];
        Assert.Null(await WorldAdoption.TryAdoptAsync(
            joinerGame, Path.Combine(joinerGame, "saves", "000002 - Game1"), Local(port),
            SessionPassword.None, hostIdentity, refusal.Add, _cts.Token));
        Assert.Contains(refusal, line => line.Contains("no SAVEGAME.sav"));

        List<string> log = [];
        AdoptedWorld? adopted = await WorldAdoption.TryAdoptWithCharacterAsync(
            joinerGame, roundTripped, "the character you just created", Local(port),
            SessionPassword.None, hostIdentity, log.Add, _cts.Token);

        Assert.NotNull(adopted);
        Assert.Equal(hostIdentity, adopted!.Value.SaveIdentity);
        Assert.Equal("the character you just created", adopted.Value.CharacterSource);

        // Both files, because only the stash survives the loader's own FileCopy — and the
        // stash is in the GAME ROOT, because a mid-session adoption empties Temp\ (measured
        // live 2026-08-29; join_mode = newcharacter is mid-session by construction, so this
        // caller is exactly the one that would have lost it).
        string pifo = Path.Combine(joinerGame, "Temp", "pifo.ifo");
        string stash = Path.Combine(joinerGame, PlayerIfo.CarriedCharacterFile);
        Assert.True(File.Exists(pifo));
        Assert.True(File.Exists(stash));
        Assert.Equal(File.ReadAllBytes(pifo), File.ReadAllBytes(stash));

        // And the character in them is the one that was handed in, not a blank stranger.
        Assert.True(PlayerIfo.TryExtractPlayerFromPifoBytes(
            File.ReadAllBytes(stash), out GffStruct staged, out _));
        Assert.Equal(created.Count, staged.Count);
    }

    // ---- helpers ---------------------------------------------------------------

    private static CompatibilityVerdict Admitted() =>
        new(RefusalReason.None, SessionCapabilities.All, []);

    private static IPEndPoint Local(int port) => new(IPAddress.Loopback, port);

    private async Task<int> ServeAsync(IReadOnlyList<WorldFile> world, uint saveIdentity = 0)
    {
        int port = FreePort();
        _ = WorldTransfer.ServeAsync(world, port, SessionPassword.None, saveIdentity, null, _cts.Token);

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
