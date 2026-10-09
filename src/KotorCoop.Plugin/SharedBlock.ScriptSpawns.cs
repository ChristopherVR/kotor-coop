namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- host spawns only (v98: C568 and C600..CA64, TotalSize 0xC600 -> 0xCC00) -------
    //
    // ScriptCreateObjectHook. The host is the only game that creates creatures from scripts; a
    // joiner builds the host's from the blueprint the host names.

    // 0xC564 and 0xC56C: reserved (the joiner/host decision reads the session role instead,
    // SharedBlock.OffsetLocalSessionRole).

    /// <summary>Plugin -> agent: creature requests turned away since start (diagnostic).</summary>
    public const int OffsetSuppressedScriptCreatures = 0xC568;

    /// <summary>Plugin -> agent, host: creatures the host's scripts created, total recorded (the
    /// ring index is total % capacity). Never cleared: each record carries the liveness native
    /// epoch it was made in, which moves at "world unloading", before the next module's load
    /// scripts run, so a load-time spawn is stamped with the world it belongs to.</summary>
    public const int OffsetScriptSpawnCount = 0xC600;

    /// <summary>Records of { u32 object id, u32 native epoch, 16 bytes template resref }; the
    /// id is written last and 0 means "being written".</summary>
    public const int OffsetScriptSpawnRecords = 0xC604;

    public const int ScriptSpawnCapacity = 24;
    public const int ScriptSpawnStride = 24;
    public const int ScriptSpawnResrefLength = 16;

    /// <summary>Agent -> plugin, joiner: how many of the host's announced spawns for the
    /// current module follow (the newest <see cref="SpawnTemplateCapacity"/>).</summary>
    public const int OffsetSpawnTemplateCount = 0xC880;

    /// <summary>Records of { u32 host object id, 16 bytes template resref } (stride
    /// <see cref="SpawnTemplateStride"/>). The id is written last.</summary>
    public const int OffsetSpawnTemplateRecords = 0xC884;

    public const int SpawnTemplateCapacity = 24;
    public const int SpawnTemplateStride = 20;
}
