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
        bool loadVerdictSeen = OwnLoadCaptureSafetyRule.LatchLoadVerdict(false, true);
        Assert.False(OwnLoadCaptureSafetyRule.MayRejoinAfterCapture(loadVerdictSeen, selectedCharacterCaptured: false));
        Assert.False(OwnLoadCaptureSafetyRule.MustRefuseAfterDeadline(
            loadVerdictSeen, selectedCharacterCaptured: false, requestWindowPending: true));

        // The request acknowledgement can arrive after the digest classifier reports Load.
        Assert.True(OwnLoadCaptureSafetyRule.MayRejoinAfterCapture(loadVerdictSeen, selectedCharacterCaptured: true));
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
