namespace KotorCoop.Session;

/// <summary>Pure fences shared by own-load character capture, retries and automatic adoption.</summary>
public static class OwnLoadCaptureSafetyRule
{
    public static uint NextEpoch(uint current) => unchecked(current + 1);

    public static bool MayCaptureFromNewestSave(bool verdictPending, bool requiresConfirmedSave, bool verifiedAwaitingRejoin) =>
        !verdictPending && !requiresConfirmedSave && !verifiedAwaitingRejoin;

    public static bool MayAutomaticallyAdopt(bool requiresConfirmedSave, bool verifiedAwaitingRejoin, bool ownLoadTrigger) =>
        !requiresConfirmedSave && (!verifiedAwaitingRejoin || ownLoadTrigger);

    /// <summary>A periodic request can release a load safety hold only if it began in the
    /// current post-load epoch and a complete save actually landed.</summary>
    public static bool MayReleaseWithPeriodicSave(uint requestEpoch, uint currentEpoch, bool saveLanded, bool captureWritten) =>
        requestEpoch == currentEpoch && saveLanded && captureWritten;

    public static bool LatchLoadVerdict(bool previouslySeen, bool loadVerdictSeen) =>
        previouslySeen || loadVerdictSeen;

    public static bool MayRejoinAfterCapture(bool loadVerdictSeen, bool selectedCharacterCaptured) =>
        loadVerdictSeen && selectedCharacterCaptured;

    public static bool MustRefuseAfterDeadline(bool loadVerdictSeen, bool selectedCharacterCaptured, bool requestWindowPending) =>
        loadVerdictSeen && !selectedCharacterCaptured && !requestWindowPending;
}
