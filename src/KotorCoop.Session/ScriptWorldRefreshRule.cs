using KotorCoop.Protocol;

namespace KotorCoop.Session;

public readonly record struct JftsClassRefreshState(bool Baselined, uint Serial, bool Refreshed, uint RefreshedLoadSerial);

/// <summary>A real local host class edge, never a replayed network class grant.</summary>
public static class ScriptWorldRefreshRule
{
    /// <summary>Wait for an enabled class-choice relay to finish. A player who deliberately
    /// disabled that relay keeps their own non-Jedi class when adopting the scripted world.</summary>
    public static bool CanCaptureCharacter(bool classGrantSyncEnabled, IReadOnlyList<int> localClassIds) =>
        !classGrantSyncEnabled || localClassIds.Any(c => c is >= 3 and <= 5);

    public static (bool Refresh, JftsClassRefreshState Next) ObserveHostClass(
        JftsClassRefreshState current, uint serial, int classId, uint loadSerial,
        string module, bool jftsDetected)
    {
        if (!current.Baselined) return (false, new(true, serial, false, 0));
        if (serial == current.Serial) return (false, current);
        bool refresh = jftsDetected && module.Equals("end_m01aa", StringComparison.OrdinalIgnoreCase)
            && classId is >= 3 and <= 5
            && (!current.Refreshed || current.RefreshedLoadSerial != loadSerial);
        return (refresh, current with { Serial = serial, Refreshed = current.Refreshed || refresh,
            RefreshedLoadSerial = refresh ? loadSerial : current.RefreshedLoadSerial });
    }

    public static long Token(uint hostSessionId, WorldDigest digest) =>
        hostSessionId == 0 || digest.ScriptRefreshRevision == 0 || digest.ScriptRefreshSaveIdentity == 0
            ? 0 : ((long)hostSessionId << 32) | digest.ScriptRefreshRevision;

    public static bool NeedsRefresh(uint hostSessionId, WorldDigest digest, long completedToken) =>
        Token(hostSessionId, digest) is var token && token != 0 && token != completedToken;

    /// <summary>Disk extraction can outlast a host transition or new prompt. Recheck the
    /// authority being adopted at the point of use, not just before reading the archive.</summary>
    public static bool MatchesCapturedHost(uint expectedLoadSerial, long expectedToken,
        uint hostSessionId, WorldDigest digest, PlayerState? hostState) =>
        expectedToken != 0 && digest.LoadSerial == expectedLoadSerial
        && Token(hostSessionId, digest) == expectedToken
        && hostState is { } state && ManualWorldRefreshRule.IsSafe(state)
        && HostWorldReplacedRule.IsHostReady(state, digest.Module);
}
