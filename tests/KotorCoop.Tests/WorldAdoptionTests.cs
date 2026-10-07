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
    private readonly TimeSpan _savedRetryDelay = WorldAdoption.FetchRetryDelay;

    public WorldAdoptionTests() => WorldAdoption.FetchRetryDelay = TimeSpan.FromMilliseconds(200);

    public void Dispose()
    {
        WorldAdoption.FetchRetryDelay = _savedRetryDelay;
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
    public void ATransitionAutosaveLoadsTheModuleBeingEnteredNotTheOneLeft()
    {
        // Live 2026-09-02: the autosave a `warp tar_m10aa` writes still nests END_M01AA's
        // module .sav (the module being LEFT) while savenfo.res's LASTMODULE already says
        // tar_m10aa. The ERF answer sent a rejoining client into END_M01AA against a host in
        // tar_m10aa, whose follow-warp autosaved and re-triggered the rejoin — forever.
        string save = BuildSave("end_m01aa", 0x127, "Yasuko Kaar", [(2u, "n_trask")]);
        GffWriter nfo = new("NFO ");
        nfo.Root.SetString("LASTMODULE", "tar_m10aa").SetString("SAVEGAMENAME", "");
        File.WriteAllBytes(Path.Combine(save, "savenfo.res"), nfo.ToBytes());

        Assert.True(PluginMenuLoader.TryReadSaveModuleName(save, out string module));
        Assert.Equal("tar_m10aa", module);

        // Without a readable savenfo.res (a placeholder byte, an empty LASTMODULE) the ERF
        // key list still answers, so nothing that only has the archive to go on regresses.
        File.WriteAllBytes(Path.Combine(save, "savenfo.res"), [9]);
        Assert.True(PluginMenuLoader.TryReadSaveModuleName(save, out module));
        Assert.Equal("end_m01aa", module);

        nfo = new("NFO ");
        nfo.Root.SetString("LASTMODULE", "");
        File.WriteAllBytes(Path.Combine(save, "savenfo.res"), nfo.ToBytes());
        Assert.True(PluginMenuLoader.TryReadSaveModuleName(save, out module));
        Assert.Equal("end_m01aa", module);
    }

    [Fact]
    public async Task TheJoinerKeepsTheirOwnCharacter()
    {
        // The whole point of carrying a pifo: the world is the host's, the body is not.
        string hostSave = BuildSave(
            "tar_m02aa", 0x341, "Bastila Shan", [(2u, "n_sith")], pazaakCount: 0x341, sideCard: 0x11);
        string joinerSave = BuildSave(
            "end_m01aa", 0x127, "Yasuko Kaar", [(2u, "n_trask")], pazaakCount: 0x127, sideCard: 0x2A);
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

        GffStruct cards = Gff.Parse(File.ReadAllBytes(Path.Combine(
            joinerGame, "saves", PluginMenuLoader.AutosaveSlot, "PARTYTABLE.res"))).Root;
        Assert.Equal(0x127, (int)cards.GetList("PT_PAZAAKCARDS")[0].Fields["PT_PAZAAKCOUNT"]);
        Assert.Equal(0x2A, (int)cards.GetList("PT_PAZSIDELIST")[0].Fields["PT_PAZSIDECARD"]);
        Assert.Equal(0x341, (int)cards.Fields["PT_GOLD"]);

        // The private overlay is for adoption only; the engine-facing character file must
        // remain an ordinary pifo even though its card data was restored into PARTYTABLE.res.
        GffStruct engineCharacter = Gff
            .Parse(File.ReadAllBytes(Path.Combine(joinerGame, "Temp", "pifo.ifo")))
            .Root.GetList("Mod_PlayerList")[0];
        Assert.False(engineCharacter.Fields.ContainsKey("KC_Pazaak"));
    }

    [Fact]
    public async Task PersonalModeGraftsTheJoinersOwnGoldAndBackpackNotTheHosts()
    {
        // The live-observed bug (docs/research/personal-inventory.md): host purse 5000,
        // joiner's own last capture 777; a plain adoption used to leave the joiner holding a
        // copy of the HOST's purse and backpack. inventoryPersonal/creditsPersonal true is
        // what the negotiated inventory_mode/credits_mode = personal resolves to at the real
        // call sites (SessionManager, AdoptCommand).
        string hostSave = BuildSave(
            "tar_m02aa", 0x341, "Bastila Shan", [(2u, "n_sith")],
            gold: 5000, items: [("g_w_ionblstr01", false), ("rakghoulserum", false)]);
        string joinerSave = BuildSave("end_m01aa", 0x127, "Yasuko Kaar", [(2u, "n_trask")]);
        string joinerGame = BuildGameDirectory();

        // The joiner's own dedicated Co-op-slot capture from a PREVIOUS session — exactly what
        // CoopSaveRedirect's periodic/disconnect capture leaves behind.
        string ownCapture = BuildSave(
            "end_m01aa", 0x1, "Yasuko Kaar", [], gold: 777, items: [("g_i_medeqpmnt01", false)]);
        string coopSlot = Path.Combine(joinerGame, "saves", "000005 - Co-op");
        Directory.Move(ownCapture, coopSlot);

        int port = await ServeAsync(WorldTransfer.ReadSaveDirectory(hostSave));

        List<string> log = [];
        AdoptedWorld? adopted = await WorldAdoption.TryAdoptAsync(
            joinerGame, joinerSave, Local(port), SessionPassword.None, expectedSaveIdentity: 0,
            log.Add, _cts.Token, inventoryPersonal: true, creditsPersonal: true);

        Assert.NotNull(adopted);

        string staged = Path.Combine(joinerGame, "saves", PluginMenuLoader.AutosaveSlot);
        GffStruct table = Gff.Parse(File.ReadAllBytes(Path.Combine(staged, "PARTYTABLE.res"))).Root;
        Assert.True(table.TryGetDword("PT_GOLD", out uint gold));
        Assert.Equal(777u, gold); // the joiner's own, never the host's 5000

        byte[] erf = File.ReadAllBytes(Path.Combine(staged, "SAVEGAME.sav"));
        Assert.True(ErfArchive.TryFindMember(erf, 0, "INVENTORY", out byte[] inventoryData));
        List<string> tags = [.. Gff.Parse(inventoryData).Root.GetList("ItemList")
            .Select(item => { item.TryGetString("Tag", out string t); return t; })];
        Assert.Contains("g_i_medeqpmnt01", tags); // the joiner's own item
        Assert.Contains("rakghoulserum", tags); // the host's quest item
        Assert.DoesNotContain("g_w_ionblstr01", tags); // the host's OWN ordinary item never appears

        // The mirror into the SAME Co-op slot (which runs AFTER the graft) reflects the
        // transformed content, not a fresh copy of the host's un-grafted world — ordering
        // proof, not just the staged AUTOSAVE copy above.
        GffStruct mirroredTable = Gff.Parse(File.ReadAllBytes(Path.Combine(coopSlot, "PARTYTABLE.res"))).Root;
        Assert.True(mirroredTable.TryGetDword("PT_GOLD", out uint mirroredGold));
        Assert.Equal(777u, mirroredGold);
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

        // A save file update need not move its containing directory timestamp. Conversely,
        // unrelated directory activity can move that timestamp without changing the save.
        // Adoption must compare the sidecar with SAVEGAME.sav itself.
        DateTime oldSave = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(Path.Combine(joinerSave, "SAVEGAME.sav"), oldSave);
        Directory.SetLastWriteTimeUtc(joinerSave, oldSave.AddDays(3));

        Assert.True(SaveDigest.TryComputeSaveIdentity(hostSave, out uint hostIdentity));

        // Give the sidecar a distinguishable name so the test can tell WHICH character rode.
        string sessionSavedPath = BuildSave(
            "end_m01aa", 0x999, "New Character", [(2u, "n_trask")], pazaakCount: 0x999, sideCard: 0x3B);
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

        GffStruct cards = Gff.Parse(File.ReadAllBytes(Path.Combine(
            joinerGame, "saves", PluginMenuLoader.AutosaveSlot, "PARTYTABLE.res"))).Root;
        Assert.Equal(0x999, (int)cards.GetList("PT_PAZAAKCARDS")[0].Fields["PT_PAZAAKCOUNT"]);
        Assert.Equal(0x3B, (int)cards.GetList("PT_PAZSIDELIST")[0].Fields["PT_PAZSIDECARD"]);
    }

    [Fact]
    public async Task ANewerOwnSaveBeatsAStaleSessionSidecarRegardlessOfDirectoryTimestamp()
    {
        string hostSave = BuildSave("tar_m02aa", 0x341, "Bastila Shan", [(2u, "n_sith")]);
        string ownSave = BuildSave(
            "end_m01aa", 0x127, "Current Character", [(2u, "n_trask")],
            pazaakCount: 0x127, sideCard: 0x2A);
        string sidecarSource = BuildSave(
            "end_m01aa", 0x999, "Stale Character", [(2u, "n_trask")],
            pazaakCount: 0x999, sideCard: 0x3B);
        string joinerGame = BuildGameDirectory();

        DateTime newerSave = DateTime.UtcNow.AddDays(2);
        File.SetLastWriteTimeUtc(Path.Combine(ownSave, "SAVEGAME.sav"), newerSave);
        Directory.SetLastWriteTimeUtc(ownSave, DateTime.UtcNow.AddDays(-2));

        Assert.True(PlayerIfo.TryExtractPlayer(sidecarSource, out GffStruct sidecarPlayer, out _));
        SidecarMetadata metadata = new(0, DateTime.UtcNow);
        Assert.True(SaveDigest.TryComputeSaveIdentity(hostSave, out uint hostIdentity));
        metadata = metadata with { HostSaveIdentity = hostIdentity };

        int port = await ServeAsync(WorldTransfer.ReadSaveDirectory(hostSave), hostIdentity);
        AdoptedWorld? adopted = await WorldAdoption.TryAdoptAsync(
            joinerGame, ownSave, (sidecarPlayer, metadata), Local(port), SessionPassword.None,
            expectedSaveIdentity: 0, log: null, _cts.Token);

        Assert.NotNull(adopted);
        Assert.Contains("own save", adopted!.Value.CharacterSource, StringComparison.OrdinalIgnoreCase);

        GffStruct carried = Gff
            .Parse(File.ReadAllBytes(Path.Combine(joinerGame, "Temp", "pifo.ifo")))
            .Root.GetList("Mod_PlayerList")[0];
        Assert.Equal("Current Character", carried.GetLocString("FirstName")!.Text());

        GffStruct cards = Gff.Parse(File.ReadAllBytes(Path.Combine(
            joinerGame, "saves", PluginMenuLoader.AutosaveSlot, "PARTYTABLE.res"))).Root;
        Assert.Equal(0x127, (int)cards.GetList("PT_PAZAAKCARDS")[0].Fields["PT_PAZAAKCOUNT"]);
        Assert.Equal(0x2A, (int)cards.GetList("PT_PAZSIDELIST")[0].Fields["PT_PAZSIDECARD"]);
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
    public async Task AnUnreadableSelectedSaveDoesNotSubstituteAnUnboundSidecar()
    {
        string selectedSave = BuildSave("end_m01aa", 0x127, "Selected Save Character", [(2u, "n_trask")]);
        string sidecarSource = BuildSave("end_m01aa", 0x999, "Different Character", [(2u, "n_trask")]);
        File.WriteAllBytes(Path.Combine(selectedSave, "SAVEGAME.sav"), BuildErf("SAV"));
        Assert.False(PlayerIfo.TryExtractPlayer(selectedSave, out _, out _));
        Assert.True(PlayerIfo.TryExtractPlayer(sidecarSource, out GffStruct unrelatedPlayer, out _));

        string joinerGame = BuildGameDirectory();
        string slot = Path.Combine(joinerGame, "saves", PluginMenuLoader.AutosaveSlot);
        Directory.CreateDirectory(slot);
        string sentinel = Path.Combine(slot, "sentinel.txt");
        File.WriteAllText(sentinel, "preserve existing autosave");

        List<string> log = [];
        Assert.Null(await WorldAdoption.TryAdoptAsync(
            joinerGame,
            selectedSave,
            (unrelatedPlayer, new SidecarMetadata(0, DateTime.UtcNow.AddYears(1))),
            new IPEndPoint(IPAddress.Loopback, FreePort()),
            SessionPassword.None,
            expectedSaveIdentity: 0,
            log.Add,
            _cts.Token));

        Assert.Contains(log, line => line.Contains("does not bind it to the selected save", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(log, line => line.Contains("join_mode = auto", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("preserve existing autosave", File.ReadAllText(sentinel));
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
        bool noWorldCallback = false;
        Assert.Null(await WorldAdoption.TryAdoptAsync(
            joinerGame, joinerSave, Local(port), SessionPassword.None,
            expectedSaveIdentity: 0xDEADBEEF, log.Add, _cts.Token,
            onNoWorldAvailable: () => noWorldCallback = true));

        Assert.Contains(log, line => line.Contains("refusing to play a world we did not receive"));
        Assert.False(noWorldCallback);
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
        bool noWorldCallback = false;
        Assert.Null(await WorldAdoption.TryAdoptAsync(
            joinerGame, joinerSave, Local(port), SessionPassword.None,
            expectedSaveIdentity: 0, log.Add, _cts.Token,
            onNoWorldAvailable: () => noWorldCallback = true));

        Assert.Contains(log, line => line.Contains("not made a save available yet"));
        Assert.DoesNotContain(log, line => line.Contains("ask them to save once"));
        Assert.DoesNotContain(log, line => line.Contains("could not fetch"));
        Assert.True(noWorldCallback);
    }

    [Fact]
    public async Task AHostWhoseWorldServiceComesUpAMomentLateIsRetriedNotReported()
    {
        // Live 2026-10-08: the joiner asked one second before the host's listener was up and
        // was told the host had no save. The fetch now waits a few seconds for it.
        string hostSave = BuildSave("tar_m02aa", 0x341, "Bastila Shan", [(2u, "n_sith")]);
        string joinerSave = BuildSave("end_m01aa", 0x127, "Yasuko Kaar", [(2u, "n_trask")]);
        string joinerGame = BuildGameDirectory();
        int port = FreePort();

        System.Collections.Concurrent.ConcurrentQueue<string> log = [];
        Task<AdoptedWorld?> adopting = WorldAdoption.TryAdoptAsync(
            joinerGame, joinerSave, Local(port), SessionPassword.None,
            expectedSaveIdentity: 0, log.Enqueue, _cts.Token);

        // The host comes up only once the joiner has already been turned away once.
        for (int i = 0; i < 200 && !log.Any(line => line.Contains("retrying")); i++)
        {
            await Task.Delay(10, _cts.Token);
        }

        _ = WorldTransfer.ServeAsync(WorldTransfer.ReadSaveDirectory(hostSave), port,
            SessionPassword.None, 0, null, _cts.Token);

        Assert.NotNull(await adopting);
        Assert.Contains(log, line => line.Contains("not ready yet - retrying"));
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
        Assert.Equal(created.FieldNames.Count(label => label != "KC_Pazaak"), staged.Count);
        Assert.False(staged.Fields.ContainsKey("KC_Pazaak"));
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
        string module,
        uint nextObjId,
        string? name,
        (uint Id, string Resref)[] creatures,
        int? pazaakCount = null,
        int? sideCard = null,
        uint? gold = null,
        (string Tag, bool Plot)[]? items = null)
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

        // INVENTORY.res is a TOP-LEVEL member of the OUTER SAVEGAME.sav ERF, a sibling of the
        // nested module .sav — measured directly off a real GOG save,
        // docs/research/personal-inventory.md's PersonalInventoryGraftTests companion. Only
        // added when a caller actually supplies items, so every pre-existing call site (no
        // INVENTORY.res member at all) is unaffected.
        List<(string, int, byte[])> outerMembers = [(module, 2057 /* module .sav */, inner)];
        if (items is not null)
        {
            GffWriter inventory = new("INV ");
            GffWriterList itemList = inventory.Root.AddList("ItemList");
            foreach ((string tag, bool plot) in items)
            {
                itemList.Add(0)
                    .SetInt("BaseItem", 1)
                    .SetString("Tag", tag)
                    .SetWord("StackSize", 1)
                    .SetByte("Plot", (byte)(plot ? 1 : 0));
            }

            outerMembers.Add(("INVENTORY", 0 /* res */, inventory.ToBytes()));
        }

        File.WriteAllBytes(
            Path.Combine(directory, "SAVEGAME.sav"),
            BuildErf("SAV ", [.. outerMembers]));

        File.WriteAllBytes(Path.Combine(directory, "GLOBALVARS.res"), [1, 2, 3, 4]);
        GffWriter party = new("PT");
        party.Root.SetInt("PT_GOLD", gold.HasValue ? unchecked((int)gold.Value) : (int)nextObjId);
        party.Root.AddList("PT_PAZAAKCARDS").Add(0).SetInt("PT_PAZAAKCOUNT", pazaakCount ?? (int)nextObjId);
        party.Root.AddList("PT_PAZSIDELIST").Add(0).SetInt("PT_PAZSIDECARD", sideCard ?? -1);
        File.WriteAllBytes(Path.Combine(directory, "PARTYTABLE.res"), party.ToBytes());
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
