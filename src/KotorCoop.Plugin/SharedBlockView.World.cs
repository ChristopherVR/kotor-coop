namespace KotorCoop.Plugin;

// The generic trigger relay, equipment/door/placeable replication, module travel, and the
// transition-cinematics/announce/mid-module-movie-relay channels. See SharedBlockView.cs
// for the class declaration and shared helpers.
internal sealed unsafe partial class SharedBlockView
{
    // ---- generic trigger relay --------------------------------------------------------

    /// <summary>Agent -&gt; plugin: run the watch (only on a peer that is not enemy-authority).
    /// Mirrors <see cref="EncounterWatchEnabled"/>.</summary>
    public bool TriggerWatchEnabled => *U32(SharedBlock.OffsetTriggerWatch) != 0;

    /// <summary>
    /// Publishes the generic triggers the local player is standing inside AND whose OnEnter
    /// script matched the coop.ini allowlist pattern. Count written last, mirroring
    /// <see cref="SetEncountersInside"/>'s payload-then-key ordering.
    /// </summary>
    public void SetTriggersInside(ReadOnlySpan<uint> ids, uint serial)
    {
        int count = Math.Min(ids.Length, SharedBlock.TriggerInsideCapacity);
        for (int i = 0; i < count; i++)
        {
            *U32(SharedBlock.OffsetTriggerInside + (i * 4)) = ids[i];
        }

        *U32(SharedBlock.OffsetTriggerInsideSerial) = serial;
        *U32(SharedBlock.OffsetTriggerInsideCount) = (uint)count;
    }

    public uint TriggerRequestSerial => *U32(SharedBlock.OffsetTriggerRequestSerial);

    public uint TriggerRequestCount =>
        Math.Min(*U32(SharedBlock.OffsetTriggerRequestCount), (uint)SharedBlock.TriggerRequestCapacity);

    public (uint TriggerId, uint PeerId) GetTriggerRequest(int index)
    {
        byte* slot = baseAddress + SharedBlock.OffsetTriggerRequests
            + (index * SharedBlock.TriggerRequestStride);

        return (*(uint*)(slot + 0x00), *(uint*)(slot + 0x04));
    }

    public uint TriggersFired
    {
        get => *U32(SharedBlock.OffsetTriggerFired);
        set => *U32(SharedBlock.OffsetTriggerFired) = value;
    }

    public uint TriggersRefused
    {
        get => *U32(SharedBlock.OffsetTriggerRefused);
        set => *U32(SharedBlock.OffsetTriggerRefused) = value;
    }

    public uint TriggerStatus
    {
        set => *U32(SharedBlock.OffsetTriggerStatus) = value;
    }

    /// <summary>Plugin -&gt; agent, protocol 36: publishes the generic triggers THIS instance's
    /// own engine has natively fired for its own real character. Same payload-then-key
    /// ordering as <see cref="SetTriggersInside"/>.</summary>
    public void SetTriggerNativeFired(ReadOnlySpan<uint> ids, uint serial)
    {
        int count = Math.Min(ids.Length, SharedBlock.TriggerNativeFiredCapacity);
        for (int i = 0; i < count; i++)
        {
            *U32(SharedBlock.OffsetTriggerNativeFired + (i * 4)) = ids[i];
        }

        *U32(SharedBlock.OffsetTriggerNativeFiredSerial) = serial;
        *U32(SharedBlock.OffsetTriggerNativeFiredCount) = (uint)count;
    }

    /// <summary>Cumulative damage this player owes for hits their phantom took elsewhere.</summary>
    public uint IncomingDamage => *U32(SharedBlock.OffsetIncomingDamage);

    public uint IncomingDamageAttacker => *U32(SharedBlock.OffsetIncomingDamageAttacker);

    public uint IncomingDamageApplied
    {
        get => *U32(SharedBlock.OffsetIncomingDamageApplied);
        set => *U32(SharedBlock.OffsetIncomingDamageApplied) = value;
    }

    /// <summary>Which engine floor, if any, is keeping this player on their feet.</summary>
    public uint DownBlockReason
    {
        get => *U32(SharedBlock.OffsetDownBlockReason);
        set => *U32(SharedBlock.OffsetDownBlockReason) = value;
    }

    /// <summary>Bumped when <see cref="DownBlockReason"/> changes.</summary>
    public uint DownBlockSerial
    {
        get => *U32(SharedBlock.OffsetDownBlockSerial);
        set => *U32(SharedBlock.OffsetDownBlockSerial) = value;
    }

    /// <summary>Publishes one captured creature one-shot for the agent to read. The id is
    /// written last so the agent never pairs an id with another creature's payload.</summary>
    public void SetEnemyOneShot(int index, uint objectId, uint animation, uint serial)
    {
        byte* slot = baseAddress + SharedBlock.OffsetEnemyOneShotTable
            + (Clamp(index, SharedBlock.EnemyOneShotCapacity) * SharedBlock.EnemyOneShotStride);

        *(uint*)(slot + 0x04) = animation;
        *(uint*)(slot + 0x08) = serial;
        *(uint*)(slot + 0x00) = objectId;
    }


    // ---- equipment --------------------------------------------------------------

    /// <summary>Pointer to a peer's null-terminated equip resref (16 bytes).</summary>
    public byte* GetEquipResref(int peerIndex, int equipIndex) =>
        baseAddress + SharedBlock.OffsetEquipSlots
        + (Clamp(peerIndex, SharedBlock.SlotCapacity) * SharedBlock.EquipSlotStride)
        + (Clamp(equipIndex, SharedBlock.EquipCount) * 16);


    // ---- doors ------------------------------------------------------------------

    /// <summary>
    /// Agent-written, but the plugin zeroes it on a module transition: after a transition
    /// the ids name doors in a different module until the agent republishes.
    /// </summary>
    public uint DoorCount
    {
        get => *U32(SharedBlock.OffsetDoorCount);
        set => *U32(SharedBlock.OffsetDoorCount) = value;
    }

    public DoorView GetDoor(int index)
    {
        byte* slot = baseAddress + SharedBlock.OffsetDoorSlots
            + (Clamp(index, SharedBlock.DoorCapacity) * SharedBlock.DoorSlotStride);

        return new DoorView(
            *(uint*)(slot + 0x00),
            *(slot + 0x04),
            *(slot + 0x05) != 0);
    }


    // ---- placeables -------------------------------------------------------------

    /// <summary>Agent-written; the plugin zeroes it on a module change (ids name strangers).</summary>
    public uint PlaceableCount
    {
        get => *U32(SharedBlock.OffsetPlaceableCount);
        set => *U32(SharedBlock.OffsetPlaceableCount) = value;
    }

    /// <summary>One placeable's desired state: objectId, opened, unlocked, disarmed.</summary>
    public PlaceableView GetPlaceable(int index)
    {
        byte* slot = baseAddress + SharedBlock.OffsetPlaceableSlots
            + (Clamp(index, SharedBlock.PlaceableCapacity) * SharedBlock.PlaceableSlotStride);

        return new PlaceableView(
            *(uint*)(slot + 0x00),
            *(slot + 0x04) != 0,
            *(slot + 0x05) != 0,
            *(slot + 0x06) != 0);
    }


    // ---- module travel ----------------------------------------------------------

    /// <summary>Agent-written request serial; a request is pending while it is ahead of
    /// <see cref="TravelApplied"/>.</summary>
    public uint TravelSerial => *U32(SharedBlock.OffsetTravelSerial);

    /// <summary>Pointer to the null-terminated destination module name (32 bytes).</summary>
    public byte* TravelModule => baseAddress + SharedBlock.OffsetTravelModule;

    /// <summary>Pointer to the null-terminated start waypoint tag (32 bytes).</summary>
    public byte* TravelWaypoint => baseAddress + SharedBlock.OffsetTravelWaypoint;


    // ---- transition cinematics ---------------------------------------------------

    /// <summary>
    /// Plugin -&gt; agent. Publishes the movie resrefs this instance's own transition is about
    /// to play, and bumps the serial LAST so the agent can never read a half-written list.
    ///
    /// Called from the frame on which the engine's queue rises — the only frame that exists
    /// for this, because the module load drains the queue and a movie then takes the main
    /// thread away entirely.
    /// </summary>
    public void PublishCinematic(string[] names)
    {
        int count = Math.Min(names.Length, SharedBlock.CinematicCapacity);

        for (int i = 0; i < SharedBlock.CinematicCapacity; i++)
        {
            byte* slot = baseAddress + SharedBlock.OffsetCinematicNames
                + (i * SharedBlock.CinematicNameLength);

            for (int b = 0; b < SharedBlock.CinematicNameLength; b++)
            {
                slot[b] = 0;
            }

            if (i >= count)
            {
                continue;
            }

            string name = names[i];
            int length = Math.Min(name.Length, SharedBlock.CinematicNameLength - 1);
            for (int b = 0; b < length; b++)
            {
                slot[b] = (byte)name[b];
            }
        }

        *I32(SharedBlock.OffsetCinematicCount) = count;
        *U32(SharedBlock.OffsetCinematicSerial) = *U32(SharedBlock.OffsetCinematicSerial) + 1;
    }


    // ---- transition announce ------------------------------------------------------

    /// <summary>
    /// Plugin -&gt; agent. Publishes the destination this instance's own transition has just
    /// committed to, and bumps the serial LAST so the agent can never read a half-written
    /// destination. Called once, on the frame the destination is resolved — the rising edge
    /// of a real door's <c>trans_pending</c>, or the frame immediately before a
    /// plugin-initiated <c>Odyssey.TravelToModule</c>.
    /// </summary>
    public void PublishTransitionAnnounce(string module, string waypoint)
    {
        WriteFixedAscii(SharedBlock.OffsetTransitionAnnounceModule, module, SharedBlock.TravelNameLength);
        WriteFixedAscii(SharedBlock.OffsetTransitionAnnounceWaypoint, waypoint, SharedBlock.TravelNameLength);
        *U32(SharedBlock.OffsetTransitionAnnounceSerial) = *U32(SharedBlock.OffsetTransitionAnnounceSerial) + 1;
    }

    private void WriteFixedAscii(int offset, string value, int capacity)
    {
        byte* slot = baseAddress + offset;
        int length = Math.Min(value.Length, capacity - 1);

        for (int i = 0; i < length; i++)
        {
            slot[i] = (byte)value[i];
        }

        for (int i = length; i < capacity; i++)
        {
            slot[i] = 0;
        }
    }

    /// <summary>
    /// Agent -&gt; plugin. Nonzero while the client-door-refusal watch should refuse a
    /// CLIENT-initiated real door transition — the value is WHY (mirrors
    /// <c>KotorCoop.Protocol.ClientDoorRefusalReason</c> by value: the plugin has no assembly
    /// reference to that project, so the two sides agree on the number, not the type. 1 =
    /// "the host must lead the way" (<c>follow_lead=host</c>), 2 = "the host cannot follow
    /// right now" (<c>follow_lead=any</c>, host busy). 0 = not armed.
    /// </summary>
    public uint ClientDoorRefusalReason => *U32(SharedBlock.OffsetClientDoorRefusalArmed);


    // ---- mid-module movie relay ----------------------------------------------------

    /// <summary>
    /// Plugin -&gt; agent. Publishes the resref <c>MoviePlayHook</c> most recently captured
    /// (already excluded against the engine's own transition queue by the caller), bumping the
    /// serial LAST so the agent never reads a half-written name.
    /// </summary>
    public void PublishMidModuleMovie(string resRef)
    {
        WriteFixedAscii(SharedBlock.OffsetMidModuleMovieResRef, resRef, SharedBlock.MidModuleMovieNameLength);
        *U32(SharedBlock.OffsetMidModuleMovieSerial) = *U32(SharedBlock.OffsetMidModuleMovieSerial) + 1;
    }

}
