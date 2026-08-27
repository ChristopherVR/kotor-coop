namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- solo-segment spectate (version 56, region 0x40C8..0x40CC) -----------------
    //
    // docs/research/star-forge-finale.md §3.3/§3.5, product decision "the OWNER's fight; the
    // other human SPECTATES". Same shape as OffsetLocalDowned: a plain plugin -> agent LEVEL,
    // mirrored into PlayerState.FlagSoloSegment every tick by KotorGameBridge.Player.cs's
    // ReadLocalFlags, exactly the way FlagDowned already rides OffsetLocalDowned.
    //
    // Rides in the free tail after the companion-equipment region (OffsetCompanionEquipSlots
    // 0x40C8 — after the behaviour-override dwords at 0x40C0..0x40C8, still inside the 0x40 free bytes before TotalSize 0x4100; this
    // region uses 4 of them). No TotalSize growth needed.

    /// <summary>Plugin -&gt; agent. Nonzero while THIS instance's own local player is the
    /// active OWNER of a story-forced solo segment (<c>SoloSegmentController</c>, driving
    /// <c>KotorCoop.Protocol.SoloSegmentMachine</c>). A LEVEL, not an edge — republished every
    /// frame, same convention <see cref="OffsetLocalDowned"/> already uses.</summary>
    public const int OffsetLocalInSoloSegment = 0x40C8;
}
