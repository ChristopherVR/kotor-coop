using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>A host load can abandon a follower's scripted dialogue/fade before it can save.</summary>
public static class ControlledSceneRecoveryRule
{
    public static bool MayUseLivingCheckpoint(RejoinTrigger trigger, bool controlledReturnPending,
        bool localReadable, PlayerState local, bool localScriptControlled) =>
        trigger == RejoinTrigger.HostReload && controlledReturnPending && localReadable
        && !localScriptControlled && !local.IsAway && !local.IsInSoloSegment
        && !local.IsDowned && !local.IsPartyWiped && local.Health > 0;
}
