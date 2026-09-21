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
    public void SaveIdentityUsesTheActiveModuleInsteadOfTheFirstVisitedModule()
    {
        byte[] oldSave = BuildSavegameErf("end_m01aa", 295, [(2u, "old")], 2057);
        byte[] currentSave = BuildSavegameErf("tar_m10aa", 400, [(5u, "current")], 2057);
        Assert.True(ErfArchive.TryFindMember(oldSave, 2057, "end_m01aa", out byte[] oldModule));
        Assert.True(ErfArchive.TryFindMember(currentSave, 2057, "tar_m10aa", out byte[] currentModule));
        string multiple = TempDirectory();
        File.WriteAllBytes(Path.Combine(multiple, "SAVEGAME.sav"), BuildErf("SAV ",
            ("end_m01aa", 2057, oldModule), ("tar_m10aa", 2057, currentModule)));
        GffWriter nfo = new("NFO ");
        nfo.Root.SetString("LASTMODULE", "TAR_M10AA");
        File.WriteAllBytes(Path.Combine(multiple, "savenfo.res"), nfo.ToBytes());

        string activeOnly = BuildSaveDirectory("tar_m10aa", 400, [(5u, "current")]);
        Assert.True(SaveDigest.TryComputeSaveIdentity(activeOnly, out uint expected));
        Assert.True(SaveDigest.TryComputeSaveIdentity(multiple, out uint actual));
        Assert.Equal(expected, actual);

        // Without an active-module marker, visited-module order cannot identify the world.
        File.Delete(Path.Combine(multiple, "savenfo.res"));
        Assert.False(SaveDigest.TryComputeSaveIdentity(multiple, out _));
        // A transition save can name a destination it has not stored yet. Unknown is safer
        // than presenting the old module as the new world's identity.
        nfo.Root.SetString("LASTMODULE", "tar_m10ab");
        File.WriteAllBytes(Path.Combine(multiple, "savenfo.res"), nfo.ToBytes());
        Assert.False(SaveDigest.TryComputeSaveIdentity(multiple, out _));
    }

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
    public void SavedGitReorderingByObjectIdIsEquivalent()
    {
        string gameDir = BuildGameDirectory("tar_m02aa", PristineGit([("n_a", 0)]));
        string first = BuildSaveDirectory("tar_m02aa", 295, [(7u, "n_b"), (2u, "n_a")]);
        string reordered = BuildSaveDirectory("tar_m02aa", 295, [(2u, "n_a"), (7u, "n_b")]);

        Assert.True(SaveDigest.TryComputeModuleGit(gameDir, first, "tar_m02aa", out uint a));
        Assert.True(SaveDigest.TryComputeModuleGit(gameDir, reordered, "tar_m02aa", out uint b));
        Assert.Equal(a, b);

        Assert.True(SaveDigest.TryComputeSaveIdentity(first, out uint saveA));
        Assert.True(SaveDigest.TryComputeSaveIdentity(reordered, out uint saveB));
        Assert.Equal(saveA, saveB);
    }

    [Fact]
    public void SavedGitAddedRemovedOrChangedObjectIdsPerturbTheDigest()
    {
        string gameDir = BuildGameDirectory("tar_m02aa", PristineGit([("n_a", 0)]));
        string baseline = BuildSaveDirectory("tar_m02aa", 295, [(2u, "n_a"), (7u, "n_b")]);
        string added = BuildSaveDirectory("tar_m02aa", 295, [(2u, "n_a"), (7u, "n_b"), (9u, "n_c")]);
        string removed = BuildSaveDirectory("tar_m02aa", 295, [(2u, "n_a")]);
        string changed = BuildSaveDirectory("tar_m02aa", 295, [(2u, "n_a"), (8u, "n_b")]);
        string remapped = BuildSaveDirectory("tar_m02aa", 295, [(2u, "n_b"), (7u, "n_a")]);

        Assert.True(SaveDigest.TryComputeModuleGit(gameDir, baseline, "tar_m02aa", out uint expected));
        Assert.True(SaveDigest.TryComputeModuleGit(gameDir, added, "tar_m02aa", out uint addedDigest));
        Assert.True(SaveDigest.TryComputeModuleGit(gameDir, removed, "tar_m02aa", out uint removedDigest));
        Assert.True(SaveDigest.TryComputeModuleGit(gameDir, changed, "tar_m02aa", out uint changedDigest));
        Assert.True(SaveDigest.TryComputeModuleGit(gameDir, remapped, "tar_m02aa", out uint remappedDigest));

        Assert.NotEqual(expected, addedDigest);
        Assert.NotEqual(expected, removedDigest);
        Assert.NotEqual(expected, changedDigest);
        Assert.NotEqual(expected, remappedDigest);
    }

    [Fact]
    public void SavedGitMissingOrDuplicateObjectIdIsUnknown()
    {
        string gameDir = BuildGameDirectory("tar_m02aa", PristineGit([("n_a", 0)]));
        string missing = TempDirectory();
        File.WriteAllBytes(
            Path.Combine(missing, "SAVEGAME.sav"),
            BuildSavegameErf(
                "tar_m02aa", 295,
                SavedGit((null, "n_a")),
                moduleSavType: 2057));
        string duplicate = TempDirectory();
        File.WriteAllBytes(
            Path.Combine(duplicate, "SAVEGAME.sav"),
            BuildSavegameErf(
                "tar_m02aa", 295,
                SavedGit((2u, "n_a"), (2u, "n_b")),
                moduleSavType: 2057));
        string crossListDuplicate = TempDirectory();
        File.WriteAllBytes(
            Path.Combine(crossListDuplicate, "SAVEGAME.sav"),
            BuildSavegameErf(
                "tar_m02aa", 295,
                SavedGitWithCrossListDuplicate(),
                moduleSavType: 2057));

        Assert.False(SaveDigest.TryComputeModuleGit(gameDir, missing, "tar_m02aa", out _));
        Assert.False(SaveDigest.TryComputeModuleGit(gameDir, duplicate, "tar_m02aa", out _));
        Assert.False(SaveDigest.TryComputeModuleGit(gameDir, crossListDuplicate, "tar_m02aa", out _));
        Assert.False(SaveDigest.TryComputeSaveIdentity(missing, out _));
        Assert.False(SaveDigest.TryComputeSaveIdentity(duplicate, out _));
        Assert.False(SaveDigest.TryComputeSaveIdentity(crossListDuplicate, out _));
    }

    [Fact]
    public void AVisitedModuleWithMissingOrUnreadableGitDoesNotFallBackToPristine()
    {
        string gameDir = BuildGameDirectory("tar_m02aa", PristineGit([("n_a", 0)]));

        string missingGit = TempDirectory();
        File.WriteAllBytes(
            Path.Combine(missingGit, "SAVEGAME.sav"),
            BuildSavegameErfWithoutGit("tar_m02aa", 295));

        string unreadableInner = TempDirectory();
        File.WriteAllBytes(
            Path.Combine(unreadableInner, "SAVEGAME.sav"),
            BuildErf("SAV ",
                ("tar_m02aa", 2057, [1, 2, 3, 4])));

        string malformedOuter = TempDirectory();
        File.WriteAllBytes(Path.Combine(malformedOuter, "SAVEGAME.sav"), [1, 2, 3, 4]);

        Assert.False(SaveDigest.TryComputeModuleGit(gameDir, missingGit, "tar_m02aa", out _));
        Assert.False(SaveDigest.TryComputeModuleGit(gameDir, unreadableInner, "tar_m02aa", out _));
        Assert.False(SaveDigest.TryComputeModuleGit(gameDir, malformedOuter, "tar_m02aa", out _));

        // No SAVEGAME.sav still means the module is unvisited, so the pristine archive is
        // the correct source for a newly-created save directory.
        string noSave = TempDirectory();
        Assert.True(SaveDigest.TryComputeModuleGit(gameDir, noSave, "tar_m02aa", out _));
    }

    [Fact]
    public void DuplicateSavedModuleMembersAreUnknownRatherThanOrderDependent()
    {
        string gameDir = BuildGameDirectory("tar_m02aa", PristineGit([("n_a", 0)]));
        byte[] one = BuildSavegameErf("tar_m02aa", 295, [(2u, "n_a")], 2057);
        Assert.True(ErfArchive.TryFindMember(one, 2057, "tar_m02aa", out byte[] module));

        string duplicate = TempDirectory();
        File.WriteAllBytes(
            Path.Combine(duplicate, "SAVEGAME.sav"),
            BuildErf("SAV ",
                ("tar_m02aa", 2057, module),
                ("tar_m02aa", 2057, module)));

        Assert.False(SaveDigest.TryComputeModuleGit(gameDir, duplicate, "tar_m02aa", out _));
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
    public void TheCacheServesTheStagedAutosaveWithoutBlockingItsCaller()
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

        // ⚠ The contract that protects the transition: the FIRST ask never does the disk
        // work on the caller's thread. It answers 0 = unknown (which can only ever warn)
        // and computes in the background. The background job is a real thread started by
        // the getter, so on a fast machine with this tiny fake save it can finish before the
        // getter reads the field back — the first answer is then already the final digest.
        // Either is correct; what is NOT allowed is a first answer that is neither 0 nor the
        // value the background job eventually publishes (a partial or stale read).
        uint firstIdentity = cache.SaveIdentity;
        uint firstGit = cache.ModuleGit("end_m01aa");

        Assert.True(cache.WaitForPending(TimeSpan.FromSeconds(10)));

        uint identity = cache.SaveIdentity;
        uint git = cache.ModuleGit("end_m01aa");
        Assert.NotEqual(0u, identity);
        Assert.NotEqual(0u, git);
        Assert.True(firstIdentity == 0 || firstIdentity == identity, $"first identity answer {firstIdentity} is neither unknown nor the final {identity}");
        Assert.True(firstGit == 0 || firstGit == git, $"first GIT answer {firstGit} is neither unknown nor the final {git}");
        Assert.True(cache.WaitForPending(TimeSpan.FromSeconds(10)));
        Assert.Equal(cache.ModuleGit("end_m01aa"), cache.ModuleGit("END_M01AA")); // module names are case-insensitive

        // No saves at all: everything unknown, nothing thrown.
        SaveDigestCache empty = new(Path.Combine(Path.GetTempPath(), "kotorcoop-nowhere"));
        Assert.Equal(0u, empty.SaveIdentity);
        Assert.True(empty.WaitForPending(TimeSpan.FromSeconds(10)));
        Assert.Equal(0u, empty.SaveIdentity);
        Assert.Equal(0u, empty.ModuleGit("end_m01aa"));
        Assert.Equal(0u, empty.ModuleGit(""));
    }

    [Fact]
    public void AdoptingAWorldReplacesTheCachedIdentityInsteadOfLatchingTheOldOne()
    {
        // ⚠ THE BUG THIS PINS. World adoption stages the host's world over the autosave
        // slot, which can happen AFTER an agent has already fingerprinted whatever was
        // there. A cache that latched forever would keep announcing the identity of the
        // save the joiner had before — and the session would tell them their save does not
        // match the host's at the exact moment it literally IS the host's.
        string gameDir = BuildGameDirectory("end_m01aa", PristineGit([("n_duros", 0)]));
        string staged = Path.Combine(gameDir, "saves", "000001 - AUTOSAVE");
        Directory.CreateDirectory(staged);
        string savegame = Path.Combine(staged, "SAVEGAME.sav");
        File.WriteAllBytes(savegame, BuildSavegameErf("end_m01aa", 295, [(2u, "n_duros")], 2057));

        SaveDigestCache cache = new(gameDir);
        _ = cache.SaveIdentity;
        Assert.True(cache.WaitForPending(TimeSpan.FromSeconds(10)));
        uint before = cache.SaveIdentity;
        Assert.NotEqual(0u, before);

        // The adoption: a different world lands in the same slot.
        File.WriteAllBytes(savegame, BuildSavegameErf("tar_m02aa", 833, [(2u, "n_sith")], 2057));
        File.SetLastWriteTimeUtc(savegame, DateTime.UtcNow.AddSeconds(5));

        // The re-stat is rate-limited, so nudge the clock the only way the API allows:
        // wait out the interval. Two seconds is the whole cost of noticing an adoption.
        Assert.True(WaitFor(() => cache.SaveIdentity != before && cache.SaveIdentity != 0,
            TimeSpan.FromSeconds(15)));

        Assert.True(SaveDigest.TryComputeSaveIdentity(staged, out uint expected));
        Assert.Equal(expected, cache.SaveIdentity);
    }

    [Fact]
    public void LiveResolverAndCacheChooseTheSameFreshQuicksaveWhenFolderMtimeStaysPut()
    {
        string gameDir = BuildGameDirectory("end_m01aa", PristineGit([("n_duros", 0)]));
        string staged = Path.Combine(gameDir, "saves", "000001 - AUTOSAVE");
        string quick = Path.Combine(gameDir, "saves", "000000 - QUICKSAVE");
        Directory.CreateDirectory(staged);
        Directory.CreateDirectory(quick);
        File.WriteAllBytes(Path.Combine(staged, "SAVEGAME.sav"),
            BuildSavegameErf("end_m01aa", 295, [(2u, "n_duros")], 2057));
        File.WriteAllBytes(Path.Combine(quick, "SAVEGAME.sav"),
            BuildSavegameErf("tar_m02aa", 833, [(2u, "n_sith")], 2057));
        DateTime stagedTime = DateTime.UtcNow.AddMinutes(-2);
        DateTime quickTime = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(Path.Combine(staged, "SAVEGAME.sav"), stagedTime);
        File.SetLastWriteTimeUtc(Path.Combine(quick, "SAVEGAME.sav"), quickTime);
        DateTime folderTime = Directory.GetLastWriteTimeUtc(quick);
        File.WriteAllBytes(Path.Combine(quick, "SAVEGAME.sav"),
            BuildSavegameErf("tar_m02aa", 833, [(2u, "n_sith")], 2057));
        File.SetLastWriteTimeUtc(Path.Combine(quick, "SAVEGAME.sav"), quickTime.AddMinutes(1));
        Directory.SetLastWriteTimeUtc(quick, folderTime);

        string? selected = SaveDigest.FindLiveSessionSave(gameDir);
        Assert.Equal(quick, selected);
        Assert.True(SaveDigest.TryComputeSaveIdentity(quick, out uint expected));

        SaveDigestCache cache = new(gameDir);
        _ = cache.SaveIdentity;
        Assert.True(cache.WaitForPending(TimeSpan.FromSeconds(10)));
        Assert.Equal(expected, cache.SaveIdentity);
    }

    [Fact]
    public void OlderHistoricalQuicksaveDoesNotDisplaceFreshStagedBaseline()
    {
        string gameDir = BuildGameDirectory("end_m01aa", PristineGit([("n_duros", 0)]));
        string staged = Path.Combine(gameDir, "saves", "000001 - AUTOSAVE");
        string quick = Path.Combine(gameDir, "saves", "000000 - QUICKSAVE");
        Directory.CreateDirectory(staged);
        Directory.CreateDirectory(quick);
        File.WriteAllBytes(Path.Combine(staged, "SAVEGAME.sav"),
            BuildSavegameErf("end_m01aa", 295, [(2u, "n_duros")], 2057));
        File.WriteAllBytes(Path.Combine(quick, "SAVEGAME.sav"),
            BuildSavegameErf("tar_m02aa", 833, [(2u, "n_sith")], 2057));
        File.SetLastWriteTimeUtc(Path.Combine(staged, "SAVEGAME.sav"), DateTime.UtcNow);
        File.SetLastWriteTimeUtc(Path.Combine(quick, "SAVEGAME.sav"), DateTime.UtcNow.AddDays(-1));

        Assert.Equal(staged, SaveDigest.FindLiveSessionSave(gameDir));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptFreshQuicksaveRemainsUnknownInsteadOfFallingBackToStagedBytes(bool empty)
    {
        string gameDir = BuildGameDirectory("end_m01aa", PristineGit([("n_duros", 0)]));
        string staged = Path.Combine(gameDir, "saves", "000001 - AUTOSAVE");
        string quick = Path.Combine(gameDir, "saves", "000000 - QUICKSAVE");
        Directory.CreateDirectory(staged);
        Directory.CreateDirectory(quick);
        File.WriteAllBytes(Path.Combine(staged, "SAVEGAME.sav"),
            BuildSavegameErf("end_m01aa", 295, [(2u, "n_duros")], 2057));
        File.WriteAllBytes(Path.Combine(quick, "SAVEGAME.sav"), empty ? [] : [1, 2, 3]);
        File.SetLastWriteTimeUtc(Path.Combine(staged, "SAVEGAME.sav"), DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(Path.Combine(quick, "SAVEGAME.sav"), DateTime.UtcNow);

        Assert.Equal(quick, SaveDigest.FindLiveSessionSave(gameDir));
        Assert.False(SaveDigest.TryComputeSaveIdentity(quick, out _));
        SaveDigestCache cache = new(gameDir);
        _ = cache.SaveIdentity;
        Assert.True(cache.WaitForPending(TimeSpan.FromSeconds(10)));
        Assert.Equal(0u, cache.SaveIdentity);
    }

    [Fact]
    public void StagingAnOlderSaveMakesItNewerThanTheHistoricalQuicksave()
    {
        string gameDir = TempDirectory();
        string saves = Path.Combine(gameDir, "saves");
        string older = Path.Combine(saves, "000002 - Older");
        string quick = Path.Combine(saves, "000000 - QUICKSAVE");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(quick);
        byte[] olderBytes = BuildSavegameErf("end_m01aa", 295, [(2u, "n_duros")], 2057);
        File.WriteAllBytes(Path.Combine(older, "SAVEGAME.sav"), olderBytes);
        File.WriteAllBytes(Path.Combine(quick, "SAVEGAME.sav"),
            BuildSavegameErf("tar_m02aa", 833, [(2u, "n_sith")], 2057));
        DateTime now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(Path.Combine(older, "SAVEGAME.sav"), now.AddDays(-2));
        File.SetLastWriteTimeUtc(Path.Combine(quick, "SAVEGAME.sav"), now.AddDays(-1));
        Directory.SetLastWriteTimeUtc(older, now.AddDays(-2));
        Directory.SetLastWriteTimeUtc(quick, now.AddDays(-1));

        Assert.True(PluginMenuLoader.TryStageSaveAsAutosave(gameDir, 1, out string diagnostic), diagnostic);
        string staged = Path.Combine(saves, PluginMenuLoader.AutosaveSlot);
        Assert.Equal(olderBytes, File.ReadAllBytes(Path.Combine(staged, "SAVEGAME.sav")));
        Assert.Equal(staged, SaveDigest.FindLiveSessionSave(gameDir));
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return condition();
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

    private static byte[] BuildSavegameErf(
        string module, uint nextObjId, byte[] gitBytes, int moduleSavType)
    {
        GffWriter ifo = new("IFO ");
        ifo.Root.SetDword("Mod_NextObjId0", nextObjId);
        ifo.Root.SetDword("Mod_NextObjId1", 0);

        byte[] inner = BuildErf("SAV ",
            ("Module", 2014 /* ifo */, ifo.ToBytes()),
            (module, 2023 /* git */, gitBytes));

        return BuildErf("SAV ",
            ("GLOBALVARS", 0 /* res */, [0xAA, 0xBB]),
            (module, moduleSavType, inner));
    }

    private static byte[] BuildSavegameErfWithoutGit(string module, uint nextObjId)
    {
        GffWriter ifo = new("IFO ");
        ifo.Root.SetDword("Mod_NextObjId0", nextObjId);
        ifo.Root.SetDword("Mod_NextObjId1", 0);

        byte[] inner = BuildErf("SAV ", ("Module", 2014 /* ifo */, ifo.ToBytes()));
        return BuildErf("SAV ",
            ("GLOBALVARS", 0 /* res */, [0xAA, 0xBB]),
            (module, 2057, inner));
    }

    private static byte[] SavedGit(params (uint? Id, string Resref)[] creatures)
    {
        GffWriter git = new("GIT ");
        GffWriterList list = git.Root.AddList("Creature List");
        foreach ((uint? id, string resref) in creatures)
        {
            var entry = list.Add(4).SetResRef("TemplateResRef", resref);
            if (id.HasValue)
            {
                entry.SetDword("ObjectId", id.Value);
            }
        }

        return git.ToBytes();
    }

    private static byte[] SavedGitWithCrossListDuplicate()
    {
        GffWriter git = new("GIT ");
        git.Root.AddList("Creature List")
            .Add(4)
            .SetResRef("TemplateResRef", "n_a")
            .SetDword("ObjectId", 2);
        git.Root.AddList("Door List")
            .Add(4)
            .SetResRef("TemplateResRef", "door_a")
            .SetDword("ObjectId", 2);
        return git.ToBytes();
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
