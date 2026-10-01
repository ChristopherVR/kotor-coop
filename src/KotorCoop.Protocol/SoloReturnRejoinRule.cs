namespace KotorCoop.Protocol;

/// <summary>Lifecycle state for a host excursion into a known single-seat module. The load
/// serials are owned by independent processes, so they are stored as separate baselines and
/// are only compared with their own later readings.</summary>
public readonly record struct SoloReturnRejoinState(
    string OriginModule,
    bool InSoloLeg,
    bool Attempted,
    long MismatchSinceMs,
    long ReturnCandidateSinceMs,
    uint BaselineLocalLoadSerial,
    uint BaselineHostLoadSerial,
    uint BaselineHostSaveIdentity,
    bool LocalLoadSerialKnown,
    bool HostLoadSerialKnown,
    bool LocalLeg = false)
{
    public static SoloReturnRejoinState Initial => new(string.Empty, false, false, 0, 0, 0, 0, 0, false, false);
}

public static class SoloReturnRejoinRule
{
    public const int SettleMs = 2000;

    public static (SoloReturnRejoinState Next, bool ShouldRejoin) Observe(
        SoloReturnRejoinState current,
        string localModule,
        uint localGit,
        string hostModule,
        uint hostGit,
        uint? localLoadSerial,
        uint? hostLoadSerial,
        uint hostSaveIdentity,
        long nowMs,
        bool gateEnabled)
    {
        localModule ??= string.Empty;
        hostModule ??= string.Empty;
        if (localModule.Length == 0 || hostModule.Length == 0)
        {
            return (current, false);
        }

        if (!current.InSoloLeg)
        {
            bool together = string.Equals(localModule, hostModule, StringComparison.OrdinalIgnoreCase);
            bool equalGit = localGit != 0 && localGit == hostGit;
            if (together && equalGit)
            {
                // Keep the baseline at the latest together tick. A save made before the
                // excursion must not be mistaken for the fresh return save after a later
                // session save identity change.
                uint baselineIdentity = hostSaveIdentity != 0
                    ? hostSaveIdentity : current.BaselineHostSaveIdentity;
                return (new SoloReturnRejoinState(localModule, false, false, 0, 0,
                    localLoadSerial ?? 0, hostLoadSerial ?? 0,
                    baselineIdentity,
                    localLoadSerial.HasValue, hostLoadSerial.HasValue), false);
            }

            if (current.OriginModule.Length > 0
                && MinigameModuleRule.IsMinigameModule(hostModule)
                && !MinigameModuleRule.IsMinigameModule(localModule)
                && string.Equals(localModule, current.OriginModule, StringComparison.OrdinalIgnoreCase))
            {
                return (current with { InSoloLeg = true, MismatchSinceMs = 0 }, false);
            }

            // The LOCAL player is the one on the track (a joiner-led race) while the host stays in
            // the origin: the same "return, then a mismatch that persists" decision applies to the
            // local player's freshly reloaded world.
            if (current.OriginModule.Length > 0
                && MinigameModuleRule.IsMinigameModule(localModule)
                && string.Equals(hostModule, current.OriginModule, StringComparison.OrdinalIgnoreCase))
            {
                return (current with { InSoloLeg = true, LocalLeg = true, MismatchSinceMs = 0 }, false);
            }

            return (SoloReturnRejoinState.Initial, false);
        }

        bool serialChanged = (!current.LocalLeg && current.LocalLoadSerialKnown && localLoadSerial.HasValue
                && current.BaselineLocalLoadSerial != localLoadSerial.Value)
            || (current.HostLoadSerialKnown && hostLoadSerial.HasValue
                && current.BaselineHostLoadSerial != hostLoadSerial.Value);
        if (serialChanged)
        {
            return (SoloReturnRejoinState.Initial, false);
        }

        bool returned = string.Equals(localModule, current.OriginModule, StringComparison.OrdinalIgnoreCase)
            && string.Equals(hostModule, current.OriginModule, StringComparison.OrdinalIgnoreCase);
        if (!returned && current.LocalLeg)
        {
            // Still on the track while the host holds the origin: keep waiting. Anything else
            // (the host moved on, or the local player left for somewhere else) ends the excursion.
            return MinigameModuleRule.IsMinigameModule(localModule)
                && string.Equals(hostModule, current.OriginModule, StringComparison.OrdinalIgnoreCase)
                ? (current with { MismatchSinceMs = 0, ReturnCandidateSinceMs = 0 }, false)
                : (SoloReturnRejoinState.Initial, false);
        }

        if (!returned)
        {
            // A normal module change ends the observed excursion. Do not retain a stale
            // origin forever if the host never actually comes back to it.
            if (!string.Equals(localModule, current.OriginModule, StringComparison.OrdinalIgnoreCase)
                || !MinigameModuleRule.IsMinigameModule(hostModule))
            {
                return (SoloReturnRejoinState.Initial, false);
            }
            return (current with { MismatchSinceMs = 0, ReturnCandidateSinceMs = 0 }, false);
        }

        // A local excursion never changes the HOST's save identity, so it cannot be required.
        bool refreshedSave = current.LocalLeg
            || (hostSaveIdentity != 0
                && current.BaselineHostSaveIdentity != 0
                && hostSaveIdentity != current.BaselineHostSaveIdentity);
        if (!refreshedSave)
        {
            // Module/GIT values may still be served from the pre-excursion cache. Wait for
            // the host's fresh return save identity before deciding that the return is real.
            return (current with { MismatchSinceMs = 0 }, false);
        }

        if (localGit == 0 || hostGit == 0)
        {
            // Git may be unresolved for a few frames while the fresh archive is being
            // indexed. Keep the excursion armed until that evidence becomes available.
            return (current with { MismatchSinceMs = 0 }, false);
        }

        if (localGit == hostGit)
        {
            return (SoloReturnRejoinState.Initial, false);
        }

        if (!current.LocalLoadSerialKnown || !current.HostLoadSerialKnown
            || !localLoadSerial.HasValue || !hostLoadSerial.HasValue)
        {
            return (current with { MismatchSinceMs = 0 }, false);
        }

        long since = current.MismatchSinceMs == 0 ? nowMs : current.MismatchSinceMs;
        bool fire = gateEnabled && !current.Attempted && nowMs - since >= SettleMs;
        return (current with { MismatchSinceMs = since, ReturnCandidateSinceMs = 0,
            Attempted = current.Attempted || fire }, fire);
    }
}
