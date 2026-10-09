namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- duels (version 100, CC80..CC8C, no TotalSize growth) -----------------------------------
    //
    // docs/research/pvp-plan.md. Agent -> plugin LEVELS, written every pump from the host's duel
    // round (KotorCoop.Session.PvpDuelRound, carried to every player as PvpStatePacket) and zeroed
    // on disconnect (KotorGameBridge.SessionScopedBlockLevels): a lost host, or a session that
    // ends mid-duel, therefore drops the pin and the hostile body by the same path a normal end
    // does. Placed in the free tail after the mirror's local body ids (CA80..CC80); the block was
    // already grown to 0xCE00 for them, so this takes 12 of the remaining 0x180 bytes.

    /// <summary>Agent -&gt; plugin. 0 = no duel, <see cref="PvpPhaseArmed"/> = countdown (this
    /// player is pinned at 1 hp minimum and their hit points are snapshotted; nobody is hostile),
    /// <see cref="PvpPhaseLive"/> = the fight (the opponent's stand-in is hostile to this
    /// player's side).</summary>
    public const int OffsetPvpPhase = 0xCC80;

    /// <summary>Agent -&gt; plugin. The opponent's peer id (the id the remote slot table carries
    /// in <see cref="RemoteSlot.PeerId"/>), valid while <see cref="OffsetPvpPhase"/> is nonzero.</summary>
    public const int OffsetPvpOpponentPeer = 0xCC84;

    /// <summary>Agent -&gt; plugin. The host's round counter, for the log only: two rounds in a
    /// row read the same phase, and only this tells a log reader they were different fights.</summary>
    public const int OffsetPvpRound = 0xCC88;

    public const uint PvpPhaseNone = 0;
    public const uint PvpPhaseArmed = 1;
    public const uint PvpPhaseLive = 2;
}
