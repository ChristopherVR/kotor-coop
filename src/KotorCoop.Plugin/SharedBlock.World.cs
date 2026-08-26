namespace KotorCoop.Plugin;

// Door/equipment replication, module travel, placeable state, transition cinematics and
// announce, mid-module movie relay, and the generic trigger relay (both directions). See
// SharedBlock.cs for the struct header.
internal partial struct SharedBlock
{
    // ---- door replication (version 4) -------------------------------------------
    //
    // The agent writes the DESIRED state of doors in the local module: open state and
    // whether the door has been unlocked, both of which only ever advance (a door sync
    // value is monotonic so the reliable flag channel's max-merge applies cleanly). The
    // plugin applies them through the engine's own CSWSDoor::SetOpenState so the model
    // animates and the walkmesh follows — a raw field write moves the walkmesh but NOT
    // the model, giving a door you can walk through that still looks shut.

    /// <summary>Agent -&gt; plugin. How many door entries are valid.</summary>
    public const int OffsetDoorCount = 0x690;

    /// <summary>Agent -&gt; plugin. objectId u32 @0, openState u8 @4, unlocked u8 @5.</summary>
    public const int OffsetDoorSlots = 0x698;

    public const int DoorSlotStride = 0x08;

    public const int DoorCapacity = 32;


    // ---- equipment replication (version 5) --------------------------------------
    //
    // Per remote peer, the blueprint resrefs of the visibly equipped gear: body armor,
    // right weapon, left weapon. The plugin gives the phantom the real items through
    // CreateItemOnObject + ActionEquipItem, so armor changes the body model and weapons
    // appear in hand exactly as the engine normally renders them.

    /// <summary>Agent -&gt; plugin. 8 peers x 3 resrefs x 16 ascii bytes.</summary>
    public const int OffsetEquipSlots = 0x7A0;

    public const int EquipSlotStride = 0x30;

    /// <summary>Resref order within a peer's equip slot.</summary>
    public const int EquipBody = 0;
    public const int EquipRightWeapon = 1;
    public const int EquipLeftWeapon = 2;
    public const int EquipCount = 3;


    // ---- module travel (version 6) -----------------------------------------------
    //
    // Seamless travel: when the session decides this instance should follow a peer into
    // another module, the agent writes the destination and bumps the serial. The plugin
    // executes the move through the engine's own warp path (SetMoveToModuleString +
    // SetMoveToModulePending, the machinery the shipped `warp` cheat uses) — after
    // flushing everything it has spawned, because StoreCurrentModule serialises the live
    // area on the way out and a leftover phantom would become a permanent NPC there.

    /// <summary>Agent -&gt; plugin. Bumped once per travel request; written last.</summary>
    public const int OffsetTravelSerial = 0x920;

    /// <summary>Agent -&gt; plugin. Destination module name, null-terminated ascii.</summary>
    public const int OffsetTravelModule = 0x924;

    /// <summary>Agent -&gt; plugin. Optional start waypoint tag, null-terminated ascii.</summary>
    public const int OffsetTravelWaypoint = 0x944;

    /// <summary>Plugin -&gt; agent. Serial of the last request acted on.</summary>
    public const int OffsetTravelApplied = 0x964;

    /// <summary>Plugin -&gt; agent. 0 = ok, otherwise the failure code of the last request.</summary>
    public const int OffsetTravelStatus = 0x968;

    public const int TravelNameLength = 32;


    // ---- placeable state (version 11) --------------------------------------------
    //
    // Chests, corpses, switches and containers. Mirrors the door channel one-for-one: the
    // agent writes the desired locked/open state per placeable id for the local module, and
    // the plugin applies it. Unlike doors, placeables have no walkmesh coupling, so the
    // apply is a plain field write with no engine call.

    /// <summary>Agent -&gt; plugin. How many placeable entries are valid.</summary>
    public const int OffsetPlaceableCount = 0xB58;

    /// <summary>Agent -&gt; plugin. objectId u32 @0, open u8 @4, unlocked u8 @5.</summary>
    public const int OffsetPlaceableSlots = 0xB60;

    public const int PlaceableSlotStride = 0x08;

    public const int PlaceableCapacity = 32; // occupies 0xB60..0xC60


    // ---- transition cinematics (version 42, region 0x3800..0x3A00) ----------------
    //
    // Both players should see the same cinematic. Measured on 2026-08-15: the owner's
    // transition stalled its main thread for 7,407 ms playing `01c` while the follower logged
    // no `cinematic:` line at all and simply loaded in. The follower is not GATED — its
    // move_to_module_movies queue is empty, and the engine plays what is in the queue.
    //
    // Two directions, because the two halves of the fix are different facts:
    //
    //   plugin -> agent   "the transition I am about to make plays these movies". The one
    //                     frame of warning the engine gives: NWScript StartNewModule fills
    //                     CClientExoAppInternal+0x2D8 and the module load drains it, and
    //                     CinematicWatch is reading that list on the frame the script writes
    //                     it. Published as a level plus a serial, not an event — a follower
    //                     that misses a tick must not miss the cinematic.
    //
    //   agent -> plugin   "queue these before the pending travel". Consumed by
    //                     TravelController immediately before Odyssey.TravelToModule, which
    //                     is the same path run A proved plays a movie for real (15,531 ms).
    //
    // ⚠ THE NAMES ARE STRIPPED TO RESREFS, and that is load-bearing rather than tidy. The
    // engine does not store what the script passed: AddMovieToExoArrayList resolves the name
    // to a path and stores THAT, so the queue reads `.\movies\01c.bik`. Feeding that back to
    // AddMoveToModuleMovie fails — the string contains no ':', so the no-colon branch builds
    // `MOVIES:.\movies\01c.bik`, cannot open it, and refuses the name. Forwarding the raw
    // stored string would degrade the whole feature to "the engine refused every movie",
    // silently, on every transition. See MovieStaging.StripToResRef.

    /// <summary>Plugin -&gt; agent. Bumped when this instance observes its own transition queue
    /// fill. Written LAST, after the names, so a half-written list is never visible.</summary>
    public const int OffsetCinematicSerial = 0x3800;

    /// <summary>Plugin -&gt; agent. How many of <see cref="OffsetCinematicNames"/> are valid.</summary>
    public const int OffsetCinematicCount = 0x3804;

    /// <summary>Plugin -&gt; agent. <see cref="CinematicCapacity"/> movie resrefs, each
    /// <see cref="CinematicNameLength"/> bytes of null-padded ASCII. Occupies
    /// 0x3808..0x3868.</summary>
    public const int OffsetCinematicNames = 0x3808;

    /// <summary>Agent -&gt; plugin. How many movies the pending travel request should queue.
    /// Written BEFORE <see cref="OffsetTravelSerial"/>, like every other travel field.</summary>
    public const int OffsetTravelMovieCount = 0x3880;

    /// <summary>Agent -&gt; plugin. The movie resrefs for the pending travel, same layout as
    /// <see cref="OffsetCinematicNames"/>. Occupies 0x3884..0x38E4.</summary>
    public const int OffsetTravelMovieNames = 0x3884;

    /// <summary>Engine resrefs are 16 characters; the names here are already stripped to
    /// resrefs, so this is the exact width rather than a guess.</summary>
    public const int CinematicNameLength = 16;

    /// <summary>The engine's own ceiling: NWScript <c>StartNewModule</c> takes exactly six
    /// movie arguments, so six is the longest queue the game can ever build.</summary>
    public const int CinematicCapacity = 6;


    // ---- transition announce (version 47, region 0x38E4..0x3928) ------------------
    //
    // The faster, earlier sibling of the cinematic channel above: not the film, but where a
    // follower should go, resolved the instant this instance's own engine has COMMITTED to a
    // transition — the rising edge of Odyssey.IsAreaTransitionPending() for a real door, or
    // the frame immediately before TravelController calls Odyssey.TravelToModule for a
    // plugin-initiated warp. Both are frames on which the destination module and entry
    // waypoint are already known, and the movie queue is not always filled (a plain door with
    // no cinematic queues nothing at all), so this cannot ride the cinematic channel's own
    // serial. See docs/research/transition-smoothness.md.
    //
    // Placed in the gap TravelMovieNames leaves before the peer-effect region (0x38E4..0x3A00,
    // 0x11C bytes; the version-44 comment above already establishes it as free and too small
    // for an 8-entry ring in each direction — this uses 0x44 of it, leaving 0xB0 still free).
    //
    // OUT (plugin -> agent): the destination this instance's own transition is heading to.
    // IN (agent -> plugin): whether the client-door-refusal watch is armed right now.

    /// <summary>Plugin -&gt; agent. Bumped when this instance's engine commits to a
    /// transition's destination. Written LAST, after the module and waypoint, so a
    /// half-written destination is never visible.</summary>
    public const int OffsetTransitionAnnounceSerial = 0x38E4;

    /// <summary>Plugin -&gt; agent. Null-terminated destination module name, 32 bytes.
    /// Occupies 0x38E8..0x3908.</summary>
    public const int OffsetTransitionAnnounceModule = 0x38E8;

    /// <summary>Plugin -&gt; agent. Null-terminated entry waypoint tag, 32 bytes (empty when
    /// the source resolved none). Occupies 0x3908..0x3928.</summary>
    public const int OffsetTransitionAnnounceWaypoint = 0x3908;

    /// <summary>Agent -&gt; plugin. Nonzero arms the refusal of a CLIENT-initiated real door
    /// transition (coop.ini <c>client_door_refusal</c>). A LEVEL, re-sent every agent tick —
    /// the plugin has no notion of "host" or "client" on its own, so it trusts whatever this
    /// says right now.</summary>
    public const int OffsetClientDoorRefusalArmed = 0x3928;


    // ---- mid-module movie relay (version 48, region 0x392C..0x396C) ---------------
    //
    // A script's own PlayMovie call with NO transition involved (the Leviathan capture, the
    // Star Forge arrival, the Taris bombardment, the Dantooine vision) — the DETECTION half
    // docs/research/trigger-relay.md §6 shipped MovieRelayQueue (the pure decision) without:
    // "nothing yet captures WHICH movie a mid-module PlayMovie call played". MoviePlayHook is
    // that capture, on CClientExoAppInternal::PlayMovie 0x00602CF0 — a DIFFERENT engine
    // function from the transition queue's own CClientExoAppInternal+0x2D8 the cinematic
    // region above already reads, so this needs its own record rather than reusing that one.
    // See docs/research/cinematics.md's 2026-08-22 section.
    //
    // Placed in the free gap OffsetClientDoorRefusalArmed leaves before the peer-effect region
    // (0x392C..0x3A00, 0xD4 bytes — the version-47 comment already establishes this gap is
    // real). This region uses 0x40 of it (0x392C..0x396C); version 49 below takes 4 more
    // (0x396C..0x3970), leaving 0x90 bytes still free. No TotalSize growth, no relayout of
    // anything that already exists.
    //
    // OUT (plugin -> agent, 0x392C..0x394C, 0x20 bytes): the resref this instance's own
    // MoviePlayHook most recently captured, already excluded against the engine's own
    // transition queue.
    //
    // IN (agent -> plugin, 0x394C..0x396C): a peer's mid-module movie, relayed by the session,
    // for THIS instance to act on — play now, or defer to this instance's own next transition
    // — per MovieRelayQueue.Decide.

    /// <summary>Plugin -&gt; agent. Bumped when <c>MoviePlayHook</c> captures a new mid-module
    /// PlayMovie call. Written LAST, after the resref, so a half-written name is never
    /// visible.</summary>
    public const int OffsetMidModuleMovieSerial = 0x392C;

    /// <summary>Plugin -&gt; agent. Null-terminated resref, 16 bytes. Occupies
    /// 0x3930..0x3940. 12 bytes (0x3940..0x394C) reserved.</summary>
    public const int OffsetMidModuleMovieResRef = 0x3930;

    public const int MidModuleMovieNameLength = 16;

    /// <summary>Agent -&gt; plugin. Bumped when a peer's mid-module movie is ready to act on.
    /// A request is pending while this is ahead of <see cref="OffsetMidModulePlayApplied"/>.
    /// </summary>
    public const int OffsetMidModulePlayRequestSerial = 0x394C;

    /// <summary>Agent -&gt; plugin. Null-terminated resref, 16 bytes. Occupies
    /// 0x3950..0x3960.</summary>
    public const int OffsetMidModulePlayRequestResRef = 0x3950;

    /// <summary>Plugin -&gt; agent. Echoes <see cref="OffsetMidModulePlayRequestSerial"/> once
    /// this instance has decided what to do with the request (whatever it decided — this is
    /// "handled", not "played").</summary>
    public const int OffsetMidModulePlayApplied = 0x3960;

    /// <summary>Plugin -&gt; agent. The verdict for the log — see
    /// <c>MidModulePlayStatusCode</c>. 0 = nothing handled yet. Region ends here, at 0x3968;
    /// 0x3968..0x396C reserved.</summary>
    public const int OffsetMidModulePlayStatus = 0x3964;


    // ---- generic trigger relay (0x3B88..0x3BD8, no TotalSize growth) --------------
    //
    // A GENERIC trigger's OnEnter (a CSWSTrigger whose script starts a cutscene, dialogue or
    // scripted event — not a trap, not an area transition) already fires correctly on
    // whichever engine the real player is standing in; the gap is that firing there never
    // touches THIS instance's object-id counter, so a script that CreateObject/DestroyObject
    // (a plot actor, most commonly) drifts the two instances' ids apart for the rest of the
    // module. This region is the same shape as the encounter region above for the same
    // reason: a client REPORTS where it stands, and the host's engine re-runs the OnEnter
    // script against that peer's phantom. See docs/research/trigger-relay.md.
    //
    // Placed in the 0x78 bytes of genuine padding OffsetPeerEffectInSlots leaves before
    // TotalSize (0x3AC8 + 0x18*8 = 0x3B88, TotalSize = 0x3C00) — the tail-slack the version-44
    // comment above already measures and does not use. This region ends at 0x3BD8, so 40
    // bytes of that slack are still free after it. No TotalSize growth, no relayout of
    // anything that already existed: existing fields keep their offsets untouched.

    /// <summary>Agent -&gt; plugin. 1 on a peer that is not enemy-authority (mirrors
    /// <see cref="OffsetEncounterWatch"/>'s reasoning exactly): that instance's watch reports
    /// which generic triggers the local player stands in, for the host to fire on its
    /// phantom's behalf. Dead weight on the host, which fires its own.</summary>
    public const int OffsetTriggerWatch = 0x3B88;

    /// <summary>Plugin -&gt; agent. How many entries of <see cref="OffsetTriggerInside"/> are
    /// valid.</summary>
    public const int OffsetTriggerInsideCount = 0x3B8C;

    /// <summary>Plugin -&gt; agent. Bumped whenever the set changes.</summary>
    public const int OffsetTriggerInsideSerial = 0x3B90;

    /// <summary>Plugin -&gt; agent. Object ids of the generic triggers whose polygon contains
    /// the local player AND whose OnEnter script matched the coop.ini allowlist pattern (the
    /// plugin's watch excludes everything else before it ever reaches this array). Occupies
    /// 0x3B94..0x3BA4.</summary>
    public const int OffsetTriggerInside = 0x3B94;

    public const int TriggerInsideCapacity = 4;

    /// <summary>Agent -&gt; plugin. How many request slots are valid.</summary>
    public const int OffsetTriggerRequestCount = 0x3BA4;

    /// <summary>Agent -&gt; plugin. Bumped after the slots are written; the plugin only acts
    /// when this moves.</summary>
    public const int OffsetTriggerRequestSerial = 0x3BA8;

    /// <summary>Agent -&gt; plugin. trigger object id u32 @0, requesting peer id u32 @4.
    /// Occupies 0x3BAC..0x3BCC. Capacity 4, not the encounter region's 8: unlike an
    /// encounter fire, a trigger fire is NOT free to repeat (no engine one-shot flag), so
    /// TriggerRelayStore hands out only genuinely-new requests and 4 is already the wire
    /// packet's own per-tick cap (<see cref="ProtocolConstants.MaxTriggerRelayEntries"/> in
    /// KotorCoop.Protocol).</summary>
    public const int OffsetTriggerRequests = 0x3BAC;

    public const int TriggerRequestStride = 0x08;

    public const int TriggerRequestCapacity = 4;

    /// <summary>Plugin -&gt; agent. How many triggers this instance has actually fired on a
    /// peer's behalf, and how many requests it turned down, for the log.</summary>
    public const int OffsetTriggerFired = 0x3BCC;

    public const int OffsetTriggerRefused = 0x3BD0;

    /// <summary>Plugin -&gt; agent. Why the last refusal happened
    /// (<c>OdysseyTriggers.TriggerFireResult</c>'s status codes). 0 = nothing refused yet.
    /// Region ends here, at 0x3BD8.</summary>
    public const int OffsetTriggerStatus = 0x3BD4;


    // ---- generic trigger relay: native fires (protocol 36, region 0x3BD8..0x3BF0) ----
    //
    // The REVERSE direction docs/research/trigger-relay.md's symmetry pass closes: the region
    // above only ever carried a client's own WATCH report (currently standing in). This one
    // carries the SAME instance's own engine having NATIVELY fired a generic trigger for its
    // own real character — observed by TriggerFireHook's detour on
    // CSWSTrigger::EventHandler's generic-script RunScript call site, not by a per-frame
    // polygon test — so it fires exactly once per module per genuinely-new trigger id, already
    // allowlist- and eligibility-filtered by TriggerRelayController the same way the watch is.
    //
    // Placed in the 40 bytes of tail-slack OffsetTriggerStatus's own comment already measured
    // and did not use (0x3BD8..0x3C00). This region ends at 0x3BF0, so 16 bytes of that slack
    // are still free after it. No TotalSize growth.

    /// <summary>Plugin -&gt; agent. How many entries of <see cref="OffsetTriggerNativeFired"/>
    /// are valid.</summary>
    public const int OffsetTriggerNativeFiredCount = 0x3BD8;

    /// <summary>Plugin -&gt; agent. Bumped whenever the set changes.</summary>
    public const int OffsetTriggerNativeFiredSerial = 0x3BDC;

    /// <summary>Plugin -&gt; agent. Object ids of the generic triggers THIS instance's own
    /// engine has natively fired for its own real character, allowlist- and
    /// eligibility-filtered, capacity <see cref="TriggerNativeFiredCapacity"/>. Occupies
    /// 0x3BE0..0x3BF0. Region ends here, at 0x3BF0.</summary>
    public const int OffsetTriggerNativeFired = 0x3BE0;

    public const int TriggerNativeFiredCapacity = 4;

}
