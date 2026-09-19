namespace KotorCoop.Plugin;

/// <summary>What <see cref="MineController"/> should do about one "this mine was removed on a
/// peer" entry. Pure so every refusal is unit-tested.</summary>
internal enum MineRemovalDecision
{
    /// <summary>Post the engine's own DestroyObject at the trigger.</summary>
    Remove = 0,

    /// <summary>coop.ini <c>mine_sync</c> is turned off.</summary>
    Disabled = 1,

    /// <summary>The id no longer resolves to a trigger here — the usual case, the mine is
    /// already gone (or this instance never had it). Silent.</summary>
    NotResolved = 2,

    /// <summary>The id names a trigger but its trap gate (+0x2BC) is 0: an ordinary story or
    /// transition trigger. An id that drifted between instances must never delete one.</summary>
    NotAMine = 3,

    /// <summary>The engine's own DestroyObject handler skips an object whose is_destroyable
    /// (+0xEC) is 0, so posting it would be a no-op (CSWSTrigger::EventHandler event 11).</summary>
    NotDestroyable = 4,

    /// <summary>A destroy was already posted for this id and the trigger has not been reaped
    /// yet; do not post a second one.</summary>
    AlreadyRequested = 5,
}

internal static class MineRemovalRule
{
    public static MineRemovalDecision Decide(
        bool enabled,
        bool alreadyRequested,
        bool resolvedAsTrigger,
        bool trapGateSet,
        bool destroyable)
    {
        if (!enabled)
        {
            return MineRemovalDecision.Disabled;
        }

        if (alreadyRequested)
        {
            return MineRemovalDecision.AlreadyRequested;
        }

        if (!resolvedAsTrigger)
        {
            return MineRemovalDecision.NotResolved;
        }

        if (!trapGateSet)
        {
            return MineRemovalDecision.NotAMine;
        }

        return destroyable ? MineRemovalDecision.Remove : MineRemovalDecision.NotDestroyable;
    }
}
