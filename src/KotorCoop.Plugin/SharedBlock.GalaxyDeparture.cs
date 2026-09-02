namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- galaxy-map departure relay (version 67, region 0x4C00..0x4C20, TotalSize grown to
    // 0x4D00) --------------------------------------------------------------------------
    //
    // docs/research/galaxy-map-and-party-select.md's 2026-09-03 live addendum: a galaxy-map
    // accept runs the WHOLE interplanetary departure (k_sup_galaxymap -> take-off movie ->
    // StartNewModule STUNT_00 -> the launch cutscene -> a fresh ebo_m12aa) inside a 17-45 s
    // main-thread freeze, so the frame hook never sees the `move_to_module_pending` edge and
    // no transition announce is ever published. The follower was left, silently, in a stale
    // Ebon Hawk (run 2b, 2026-09-03 07:11: no notice, no rejoin, nothing).
    //
    // The one moment that IS observable is the accept click itself: GalaxyMapConfirmHook runs
    // BEFORE the confirm script, on the main thread, and the agent reads this block from its
    // own process, so a publish made there is visible to the session even while the game
    // thread is frozen for the next half minute. The agent relays it (GalaxyDeparturePacket)
    // and the follower REPLAYS the same departure on its own engine — unlock + select + open
    // + accept in one frame (Odyssey.DepartToPlanet, GalaxyDepartureController) — so both
    // peers run the identical scripted leg natively and arrive in the same fresh ebo_m12aa.
    //
    // Two directions, each serial-edged and acked, the same shape the mid-module movie relay
    // at 0x392C..0x3968 uses:
    //   plugin -> agent  OffsetGalaxyDepartureSerial/Planet   "this instance's own accept ran"
    //   agent  -> plugin OffsetGalaxyDepartRequestSerial/Planet "replay a peer's departure"
    //   plugin -> agent  OffsetGalaxyDepartApplied/Status       the ack for the request
    //
    // Starts cleanly at the old TotalSize (0x4C00): the threat-retarget diagnostics region
    // (version 66) consumed the materialised-owner-id table's tail exactly, leaving no free
    // bytes anywhere before 0x4C00 — the same "new 0x100 boundary" choice every earlier
    // growth in SharedBlock.cs made.

    /// <summary>Plugin -&gt; agent. Bumped LAST by <see cref="GalaxyMapConfirmHook"/>'s ALLOW
    /// path, after <see cref="OffsetGalaxyDeparturePlanet"/> is written, once per accept click
    /// this instance's OWN player made (a replayed accept — one
    /// <see cref="GalaxyDepartureController"/> issued on a peer's behalf — never bumps it, or
    /// the two peers would ping-pong the same leg forever). 0 = never departed.</summary>
    public const int OffsetGalaxyDepartureSerial = 0x4C00;

    /// <summary>Plugin -&gt; agent. The <c>planetary.2da</c> row the accept confirmed —
    /// <c>CSWPartyTable+0xE0</c> as read at the click. -1 when the table was unreadable.</summary>
    public const int OffsetGalaxyDeparturePlanet = 0x4C04;

    /// <summary>Agent -&gt; plugin. A replay request is pending while this is ahead of
    /// <see cref="OffsetGalaxyDepartApplied"/>. Written LAST, after the planet.</summary>
    public const int OffsetGalaxyDepartRequestSerial = 0x4C08;

    /// <summary>Agent -&gt; plugin. The planet index to replay (0-15).</summary>
    public const int OffsetGalaxyDepartRequestPlanet = 0x4C0C;

    /// <summary>Plugin -&gt; agent. Echoes <see cref="OffsetGalaxyDepartRequestSerial"/> once
    /// the request has been acted on (or refused). Written AFTER the status.</summary>
    public const int OffsetGalaxyDepartApplied = 0x4C10;

    /// <summary>Plugin -&gt; agent. <see cref="GalaxyDepartStatusCode"/> for the last handled
    /// request. Region ends at 0x4C18; 0x4C18..0x4D00 is free.</summary>
    public const int OffsetGalaxyDepartStatus = 0x4C14;
}
