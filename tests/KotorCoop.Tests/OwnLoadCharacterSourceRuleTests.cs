using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class OwnLoadCharacterSourceRuleTests
{
    [Fact]
    public void CandidateRequiresFreshDirectAutosaveRequestBeforeLoadSerialMoves()
    {
        string saves = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(saves, "000001 - AUTOSAVE"));
        File.WriteAllBytes(Path.Combine(saves, "000001 - AUTOSAVE", "SAVEGAME.sav"), [1, 2, 3]);
        try
        {
            LoadProvenance valid = Request(8, 17, LoadProvenanceSource.MainMenuDirectDirectory,
                LoadProvenanceStatus.CapturedDirectory, "000001 - AUTOSAVE");
            Assert.True(OwnLoadCharacterSourceRule.TryCreate(valid, 6, saves, 17, 100, out OwnLoadCharacterSource? source));
            Assert.NotNull(source);
            Assert.False(OwnLoadCharacterSourceRule.TryCreate(valid, 8, saves, 17, 100, out _));

            Assert.False(OwnLoadCharacterSourceRule.TryCreate(
                Request(10, 17, LoadProvenanceSource.MainMenuDirectDirectory,
                    LoadProvenanceStatus.CapturedDirectory, "000002 - OTHER"),
                8, saves, 17, 100, out _));
            Assert.False(OwnLoadCharacterSourceRule.TryCreate(
                Request(10, 17, LoadProvenanceSource.MainMenuUnsupportedEntry,
                    LoadProvenanceStatus.UnsupportedEntry, ""),
                8, saves, 17, 100, out _));
            Assert.False(OwnLoadCharacterSourceRule.TryCreate(valid, 6, saves, 18, 100, out _));
        }
        finally
        {
            Directory.Delete(saves, recursive: true);
        }
    }

    [Fact]
    public void CompletionRequiresSameRequestOneSerialEdgeAndBoundedAge()
    {
        OwnLoadCharacterSource source = new(8, 17, "source", "hash", 10, 100);
        LoadProvenance matching = Request(8, 17, LoadProvenanceSource.MainMenuDirectDirectory,
            LoadProvenanceStatus.CapturedDirectory, "000001 - AUTOSAVE");

        Assert.True(OwnLoadCharacterSourceRule.IsCompletedFor(source, matching, true, 18, 101));
        Assert.False(OwnLoadCharacterSourceRule.IsCompletedFor(source, matching, false, 18, 101));
        Assert.False(OwnLoadCharacterSourceRule.IsCompletedFor(source, matching, true, 19, 101));
        Assert.False(OwnLoadCharacterSourceRule.IsCompletedFor(source, matching with { Generation = 10 }, true, 18, 101));
        Assert.False(OwnLoadCharacterSourceRule.IsCompletedFor(source, matching, true, 18,
            100 + OwnLoadCharacterSourceRule.MaxRequestAgeMs + 1));
    }

    [Fact]
    public void ConsoleLoadRequiresItsExactRequestAcknowledgementAndStartingSerial()
    {
        OwnLoadCharacterSource source = new(0, 17, "source", "hash", 10, 100,
            CommandRequestId: 42, CommandStartWorldLoadSerial: 17);
        Assert.True(OwnLoadCharacterSourceRule.IsCommandCompletedFor(source, 18, 42, 0, 101));
        Assert.False(OwnLoadCharacterSourceRule.IsCommandCompletedFor(source, 18, 41, 0, 101));
        Assert.False(OwnLoadCharacterSourceRule.IsCommandCompletedFor(source, 18, 42, 3, 101));
        Assert.False(OwnLoadCharacterSourceRule.IsCommandCompletedFor(source, 19, 42, 0, 101));
        Assert.False(OwnLoadCharacterSourceRule.IsCommandCompletedFor(source, 18, 42, 0,
            100 + OwnLoadCharacterSourceRule.MaxRequestAgeMs + 1));
    }

    [Fact]
    public void ConsoleLoadRetainsExactStagedSourceAcrossWrapperTeardownAndDirectoryCallback()
    {
        string saves = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string selected = Path.Combine(saves, "000026 - selected");
        string autosave = Path.Combine(saves, "000001 - AUTOSAVE");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(autosave);
        byte[] selectedBytes = [9, 8, 7, 6];
        File.WriteAllBytes(Path.Combine(selected, "SAVEGAME.sav"), selectedBytes);
        File.WriteAllBytes(Path.Combine(autosave, "SAVEGAME.sav"), selectedBytes);
        try
        {
            Assert.True(ClientCharacterPersistence.TryHashSave(selected, out string hash, out long length));
            OwnLoadCharacterSource command = new(0, 2, selected, hash, length, 100,
                CommandRequestId: 4, CommandStartWorldLoadSerial: 2);
            LoadProvenance wrapperDirectoryLoad = Request(8, 3,
                LoadProvenanceSource.MainMenuDirectDirectory,
                LoadProvenanceStatus.CapturedDirectory, "000001 - AUTOSAVE");

            // Plugin teardown increments the serial before MainMenuDirectDirectory runs.
            Assert.True(OwnLoadCharacterSourceRule.IsCommandProvenanceFor(command, wrapperDirectoryLoad, saves));
            Assert.True(OwnLoadCharacterSourceRule.IsCommandCompletedFor(command, 3, 4, 0, 101));

            File.WriteAllBytes(Path.Combine(autosave, "SAVEGAME.sav"), [1, 1, 1]);
            Assert.False(OwnLoadCharacterSourceRule.IsCommandProvenanceFor(command, wrapperDirectoryLoad, saves));
            Assert.False(OwnLoadCharacterSourceRule.IsCommandProvenanceFor(command,
                wrapperDirectoryLoad with { WorldLoadSerialAtRequest = 4 }, saves));
            Assert.False(OwnLoadCharacterSourceRule.IsCommandProvenanceFor(command,
                Request(9, 3, LoadProvenanceSource.MainMenuDirectDirectory,
                    LoadProvenanceStatus.CapturedDirectory, "000002 - OTHER"), saves));
        }
        finally
        {
            Directory.Delete(saves, recursive: true);
        }
    }

    [Fact]
    public void CaptureSafetyBlocksDelayedNewestSaveAndNonOwnAdoptionsUntilFreshSave()
    {
        Assert.False(OwnLoadCaptureSafetyRule.MayCaptureFromNewestSave(
            verdictPending: false, requiresConfirmedSave: true, verifiedAwaitingRejoin: false));
        Assert.False(OwnLoadCaptureSafetyRule.MayAutomaticallyAdopt(
            requiresConfirmedSave: true, verifiedAwaitingRejoin: false, ownLoadTrigger: false));
        Assert.False(OwnLoadCaptureSafetyRule.MayAutomaticallyAdopt(
            requiresConfirmedSave: false, verifiedAwaitingRejoin: true, ownLoadTrigger: false));
        Assert.True(OwnLoadCaptureSafetyRule.MayAutomaticallyAdopt(
            requiresConfirmedSave: false, verifiedAwaitingRejoin: true, ownLoadTrigger: true));

        uint afterLoad = OwnLoadCaptureSafetyRule.NextEpoch(4);
        Assert.False(OwnLoadCaptureSafetyRule.MayReleaseWithPeriodicSave(
            requestEpoch: 4, currentEpoch: afterLoad, saveLanded: true, captureWritten: true));
        Assert.False(OwnLoadCaptureSafetyRule.MayReleaseWithPeriodicSave(
            requestEpoch: afterLoad, currentEpoch: afterLoad, saveLanded: true, captureWritten: false));
        Assert.True(OwnLoadCaptureSafetyRule.MayReleaseWithPeriodicSave(
            requestEpoch: afterLoad, currentEpoch: afterLoad, saveLanded: true, captureWritten: true));
    }

    [Fact]
    public void LoadVerdictCanWaitForLateExactLoadAcknowledgementThenRejoin()
    {
        bool loadVerdictSeen = false; // same-save reload may leave no digest verdict at all
        bool rawOwnLoadEpisodePending = true;
        Assert.False(OwnLoadCaptureSafetyRule.MayRejoinAfterCapture(rawOwnLoadEpisodePending, selectedCharacterCaptured: false));
        Assert.False(OwnLoadCaptureSafetyRule.MustRefuseAfterDeadline(
            loadVerdictSeen, selectedCharacterCaptured: false, requestWindowPending: true));

        // A proven exact source plus request acknowledgement can rejoin even when the
        // save digest is unchanged or its classifier sample arrives late.
        Assert.True(OwnLoadCaptureSafetyRule.MayRejoinAfterCapture(rawOwnLoadEpisodePending, selectedCharacterCaptured: true));
        Assert.False(OwnLoadCaptureSafetyRule.MayRejoinAfterCapture(
            rawOwnLoadEpisodePending: false, selectedCharacterCaptured: true));
        Assert.False(OwnLoadCaptureSafetyRule.MustRefuseAfterDeadline(
            loadVerdictSeen, selectedCharacterCaptured: true, requestWindowPending: false));
    }

    private static LoadProvenance Request(
        uint generation,
        uint serial,
        LoadProvenanceSource source,
        LoadProvenanceStatus status,
        string directory) => new(
            generation,
            LoadProvenance.NoSaveSlot,
            source,
            status,
            serial,
            System.Text.Encoding.Latin1.GetBytes(directory));
}
