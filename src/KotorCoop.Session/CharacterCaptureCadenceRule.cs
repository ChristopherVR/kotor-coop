using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// When a CLIENT asks its own engine for a quicksave so the co-op character can be captured
/// into the sidecar (docs/research/session-save.md, "Periodic and pre-disconnect capture",
/// 2026-09-18). Until then the character was captured only when a door autosave or a clean
/// disconnect happened to produce a save: a crash after the last door lost every level since
/// (docs/review-seamless-2026-09-18.md §2).
///
/// Pure. <c>CoopCommandDispatcher.MaintainSessionSave</c> owns the clock, the cheat request and
/// the capture; this only answers "is one due", "did the save land" and "stop waiting".
/// </summary>
public static class CharacterCaptureCadenceRule
{
    /// <summary>The longest a capture waits for the engine to write the requested save before
    /// it captures whatever the newest save is. <c>CSWSPlayer::GetIsAllowedToSave</c> refuses
    /// silently (docs/research/world-adoption.md), so "no directory changed" is the only signal
    /// there is, and the pre-2026-09-18 behaviour — capture the newest existing save — is the
    /// fallback either way.</summary>
    public const int SaveWaitMs = 5_000;

    /// <summary>
    /// A periodic capture is due: the feature is on, nothing is already in flight, the interval
    /// has elapsed since the last capture (or since the connection, for the first one), and the
    /// local player is somewhere a save is both allowed and sensible — not fighting, not in a
    /// conversation, not in a minigame, not down.
    /// </summary>
    public static bool IsDue(
        int intervalMinutes, long nowMs, long lastCaptureMs, bool requestPending, PlayerState? local)
    {
        if (intervalMinutes <= 0 || requestPending || local is not { } state)
        {
            return false;
        }

        if (state.IsInCombat || state.IsInDialogue || state.IsInMinigame
            || state.IsDowned || state.IsPartyWiped)
        {
            return false;
        }

        return nowMs - lastCaptureMs >= intervalMinutes * 60_000L;
    }

    /// <summary>The requested archive's <c>SAVEGAME.sav</c> became newer than the baseline
    /// captured before the request. A directory change alone is not evidence: if a transient
    /// quicksave directory disappears, the resolver can briefly return an older named save.</summary>
    public static bool SaveLanded(
        string? sourceBefore, DateTime stampBefore, string? sourceNow, DateTime stampNow) =>
        sourceNow is not null
        && stampNow > stampBefore;

    public static bool WaitExpired(long requestedMs, long nowMs) => nowMs - requestedMs >= SaveWaitMs;
}
