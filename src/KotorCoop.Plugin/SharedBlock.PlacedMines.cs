namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- party-placed floor-mine replication (version 78, region 0x5A00..0x5BBC) -----------
    //
    // docs/research/mines.md §14, coop.ini placed_mine_sync (default OFF, NEEDS-LIVE). A mine a
    // party member PLACES gets a per-process dynamic object id, so it cannot ride the GIT-mine
    // removal flags (mines.md §7). The host owns the canonical set and names each mine with a
    // session serial; every instance keeps its own serial -> local id map
    // (PartyPlacedMineIdentityMap). Four parts, all serial-edged:
    //
    //   plugin -> agent  capture ring   "a party member here just placed this mine"
    //   plugin -> agent  gone ring      "the mine with serial S is gone here"
    //   agent  -> plugin roster level   "these serials exist in your world" (absent = remove)
    //   agent  -> plugin spawn mailbox  "build / register serial S" + the plugin's ack
    //
    // The region was reserved for this stream by the 2026-09-24 parallel wave (0x5A00..0x5C00);
    // TotalSize grows to 0x5C00 with it.

    // -- capture ring (plugin -> agent) --

    /// <summary>Plugin -&gt; agent. Number of captures ever published; bumped LAST, after the
    /// entry. Entry for capture n lives at slot (n - 1) % <see cref="PlacedMineCaptureCapacity"/>.</summary>
    public const int OffsetPlacedMineCaptureHead = 0x5A00;

    public const int OffsetPlacedMineCaptureRing = 0x5A08;
    public const int PlacedMineCaptureCapacity = 4;
    public const int PlacedMineCaptureStride = 0x28;

    // Entry fields. CaptureSerial is written LAST inside the entry (a reader re-checks it).
    public const int PlacedMineCaptureSerialOffset = 0x00;
    public const int PlacedMineCaptureWorldSerialOffset = 0x04;

    /// <summary>Diagnostic only: this instance's dynamic id. The agent never sends it.</summary>
    public const int PlacedMineCaptureLocalIdOffset = 0x08;
    public const int PlacedMineCaptureXOffset = 0x0C;
    public const int PlacedMineCaptureYOffset = 0x10;
    public const int PlacedMineCaptureZOffset = 0x14;
    public const int PlacedMineCaptureOrientXOffset = 0x18;
    public const int PlacedMineCaptureOrientYOffset = 0x1C;
    public const int PlacedMineCaptureDetectDcOffset = 0x20;
    public const int PlacedMineCaptureDisarmDcOffset = 0x22;
    public const int PlacedMineCaptureTrapTypeOffset = 0x24;
    public const int PlacedMineCaptureFactionOffset = 0x26;

    // -- gone ring (plugin -> agent), 0x5AA8..0x5B30 --

    /// <summary>Plugin -&gt; agent. Number of gone events ever published; bumped LAST.</summary>
    public const int OffsetPlacedMineGoneHead = 0x5AA8;

    public const int OffsetPlacedMineGoneRing = 0x5AB0;
    public const int PlacedMineGoneCapacity = 8;
    public const int PlacedMineGoneStride = 0x10;
    public const int PlacedMineGoneSequenceOffset = 0x00;
    public const int PlacedMineGoneWorldSerialOffset = 0x04;

    /// <summary>The host serial of the vanished mine, or 0 for a capture that vanished before
    /// the host's serial for it arrived (then <see cref="PlacedMineGoneCaptureOffset"/> names it).</summary>
    public const int PlacedMineGoneMineSerialOffset = 0x08;
    public const int PlacedMineGoneCaptureOffset = 0x0C;

    // -- roster level (agent -> plugin), 0x5B30..0x5B7C --

    /// <summary>Agent -&gt; plugin seqlock: odd while the agent is writing, even when stable.
    /// 0 = no roster yet (the plugin removes nothing).</summary>
    public const int OffsetPlacedMineRosterGeneration = 0x5B30;

    /// <summary>The <see cref="OffsetPlacedMineWorld"/> token the roster was built for; a roster
    /// for any other world is ignored.</summary>
    public const int OffsetPlacedMineRosterWorldSerial = 0x5B34;

    /// <summary>A mapped serial at or below this fence that is absent from the list is removed.</summary>
    public const int OffsetPlacedMineRosterHighest = 0x5B38;
    public const int OffsetPlacedMineRosterCount = 0x5B3C;
    public const int OffsetPlacedMineRosterSerials = 0x5B40;
    public const int PlacedMineRosterCapacity = 15;

    // -- spawn mailbox (agent -> plugin) and its ack, 0x5B80..0x5BB8 --

    /// <summary>Agent -&gt; plugin. A request is pending while this is ahead of
    /// <see cref="OffsetPlacedMineSpawnApplied"/>. Written LAST.</summary>
    public const int OffsetPlacedMineSpawnRequest = 0x5B80;
    public const int OffsetPlacedMineSpawnWorldSerial = 0x5B84;
    public const int OffsetPlacedMineSpawnSerial = 0x5B88;

    /// <summary>Non-zero: this is THIS instance's own capture coming back under the host's
    /// serial — register the mine the engine already built, do not build another.</summary>
    public const int OffsetPlacedMineSpawnCapture = 0x5B8C;
    public const int OffsetPlacedMineSpawnX = 0x5B90;
    public const int OffsetPlacedMineSpawnY = 0x5B94;
    public const int OffsetPlacedMineSpawnZ = 0x5B98;
    public const int OffsetPlacedMineSpawnDetectDc = 0x5B9C;
    public const int OffsetPlacedMineSpawnDisarmDc = 0x5B9E;
    public const int OffsetPlacedMineSpawnTrapType = 0x5BA0;
    public const int OffsetPlacedMineSpawnFaction = 0x5BA2;

    /// <summary>Plugin -&gt; agent. Echoes the request serial; written AFTER the status.</summary>
    public const int OffsetPlacedMineSpawnApplied = 0x5BA4;

    /// <summary>Plugin -&gt; agent. <see cref="PlacedMineSpawnStatus"/> of the last request.</summary>
    public const int OffsetPlacedMineSpawnStatus = 0x5BA8;

    /// <summary>Plugin -&gt; agent, diagnostic: the local id the last request produced.</summary>
    public const int OffsetPlacedMineSpawnLocalId = 0x5BAC;

    /// <summary>Plugin -&gt; agent, diagnostic: how many times the fire gate refused a
    /// party-placed mine on this (client) instance.</summary>
    public const int OffsetPlacedMineFireSuppressed = 0x5BB0;

    /// <summary>Plugin -&gt; agent, diagnostic: mapped serials in the current scope.</summary>
    public const int OffsetPlacedMineMappedCount = 0x5BB4;

    /// <summary>
    /// Plugin -&gt; agent. The placed-mine WORLD TOKEN: bumped every time the controller begins a
    /// new scope (a new world epoch — every module transition, load or same-module reload — or
    /// a new session). Every other field in this region that names a world names this token,
    /// not <see cref="OffsetWorldLoadSerial"/>, which deliberately does NOT move on an ordinary
    /// door transition. 0 = no scope yet. Region ends at 0x5BBC; 0x5BBC..0x5C00 is free.
    /// </summary>
    public const int OffsetPlacedMineWorld = 0x5BB8;
}

/// <summary>Outcome of one spawn-mailbox request (<see cref="SharedBlock.OffsetPlacedMineSpawnStatus"/>).
/// The agent mirrors these numbers in <c>KotorCoop.Memory.PlacedMineSpawnStatusCodes</c>.</summary>
internal enum PlacedMineSpawnStatus : uint
{
    None = 0,

    /// <summary>A replica was built through the engine's own construction sequence.</summary>
    Spawned = 1,

    /// <summary>This instance's own captured mine is now registered under the host's serial.</summary>
    Registered = 2,

    /// <summary>The request named another world load; nothing done.</summary>
    StaleWorld = 3,

    /// <summary>Not ready (no area, no player, route unverified this frame); retry later.</summary>
    Retry = 4,

    /// <summary>Refused for good (bad record, identity map refused, engine returned no id).</summary>
    Refused = 5,

    /// <summary>The own capture this names already vanished here; a gone event was queued.</summary>
    OwnCaptureGone = 6,

    /// <summary>placed_mine_sync is off in this plugin.</summary>
    Disabled = 7,
}
