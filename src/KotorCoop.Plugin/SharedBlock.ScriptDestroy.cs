namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- refused script destroys (C460..C4A4, no TotalSize growth) ----------------------------
    //
    // Plugin -> agent. Ids whose DestroyObject a joiner's own script asked for and
    // ScriptDestroyGuardHook refused because the host had them alive at world-up. When the host
    // never had them after all (2026-10-09, tar_m02aa: a scene killed and removed a Duros on the
    // host before the host's first liveness scan), the joiner's copy stood at 1 hp forever. The
    // agent treats these ids as local-only candidates: one the host's full liveness view lacks
    // for LocalOnlyHostileTracker.GraceMs is removed through the ordinary Gone path.
    //
    //     +0   total refusals recorded this world (the ring index is total % capacity)
    //     +4   16 object ids
    // Cleared when a world comes up.
    public const int OffsetScriptDestroyRefusedCount = 0xC460;
    public const int OffsetScriptDestroyRefusedIds = 0xC464;
    public const int ScriptDestroyRefusedCapacity = 16;
}
