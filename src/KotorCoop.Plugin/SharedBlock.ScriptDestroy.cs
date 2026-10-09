namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- script-destroyed creatures (C460..C564, no TotalSize growth) -------------------------
    //
    // Plugin -> agent. The targets of every script DestroyObject that ScriptDestroyGuardHook let
    // through in this world. The HOST's liveness scan reports an id gone only after seeing it
    // alive, so a scene that removed an NPC before the first scan of an area never told the
    // joiner, whose copy then stood at 1 hp for good (2026-10-09, tar_m02aa: the Duros the Sith
    // kill outside the apartments). The host's agent reports these ids gone as soon as they are
    // absent from its scan, with no grace period.
    //
    //     +0   total recorded this world (the ring index is total % capacity)
    //     +4   64 object ids
    // Cleared when a world comes up.
    public const int OffsetScriptDestroyedCount = 0xC460;
    public const int OffsetScriptDestroyedIds = 0xC464;
    public const int ScriptDestroyedCapacity = 64;
}
