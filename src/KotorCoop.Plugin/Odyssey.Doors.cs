// Odyssey.Doors.cs - door open/close state, the door-module-transition gate
// (linked_to/linked_to_flags, IsPartyTogether), and the door/placeable action
// surface (UseObject, CollectDoors, lock/trap helpers). The party-follow logic
// that keeps companions together across a transition lives in Odyssey.DoorTransitions*.cs.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- doors -------------------------------------------------------------------

    /// <summary>CSWSDoor vtable. Shared with the sweep rather than written twice — a constant
    /// with two copies is a constant that drifts.</summary>
    private const nint DoorVtable = CreatureFinder.DoorVtable;

    /// <summary>CSWSDoor + this = committed open state: 0 closed, 1/2 open, 3 destroyed.</summary>
    private const int DoorOpenState = 0x2CC;

    /// <summary>CSWSDoor + this = pending open state the engine will commit after the swing.</summary>
    private const int DoorPendingOpenState = 0x2CD;

    /// <summary>CSWSDoor + this = locked byte. A plain write is the correct apply.</summary>
    private const int DoorLocked = 0x2C4;

    /// <summary>Resolves a door by object id, vtable-checked. 0 if absent or not a door.</summary>
    public static nint ResolveDoor(uint objectId)
    {
        nint door = ResolveServerObject(objectId);
        return door != 0 && *(nint*)door == DoorVtable ? door : 0;
    }

    /// <summary>
    /// CSWSDoor::OpenDoor 0x00589C70 — <c>__thiscall</c>, ONE argument (the OPENER's object
    /// id); <c>ret 4</c> at 0x00589D3A.
    ///
    /// The engine's own door opening, and worth every byte of it rather than a state poke. It
    /// resolves the opener, takes the dot product of (opener.position − door.position) with the
    /// door's own orientation vector at +0x9C and picks swing state **1 or 2 from which side
    /// the opener is standing** (0x00589C97–0x00589CE1); records the opener at +0x31C; calls
    /// <c>SetOpenState(state, bDeferred=1)</c>; opens the door's LINKED twin the same way
    /// (<c>GetLinkedObject 0x00589580</c>, so double doors move together); and finally runs the
    /// door's OnOpen script at +0x228 through <c>CVirtualMachine::RunScript 0x005D0FC0</c>.
    /// Not one of those five things is reproducible by writing a byte.
    ///
    /// It leaves the state PENDING, exactly as the engine does — the swing animation commits
    /// it. <see cref="OpenDoorFully"/> commits it immediately instead, because a staging verb
    /// needs the walkmesh open now rather than in a second and a half.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, void> OpenDoorFn =
        (delegate* unmanaged[Thiscall]<nint, uint, void>)0x00589C70;

    /// <summary>
    /// Opens a door through the engine's own path and commits the swing at once, so the
    /// walkmesh is open on this frame.
    ///
    /// The commit is <see cref="CommitDoorOpenState"/> (<c>MoveToNextOpenState 0x00589680</c>),
    /// which moves pending → current and then calls <c>CSWSArea::BudgeCreatures 0x00508180</c>
    /// with the door's position and its new open/closed bit — the update that makes the door
    /// stop being an obstacle. That is why the animation cannot simply be skipped by writing
    /// +0x2CC: the byte moves the collision without telling the area.
    ///
    /// Returns the open state the door actually ended up in, so the caller reports what the
    /// engine did rather than what it asked for. MAIN THREAD ONLY.
    /// </summary>
    public static byte OpenDoorFully(nint door, uint openerId)
    {
        if (door == 0)
        {
            return 0;
        }

        OpenDoorFn(door, openerId);
        MoveToNextOpenStateFn(door);

        return GetDoorOpenState(door);
    }

    /// <summary>The object's facing vector (CSWSObject::Orientation, +0x9C). For a door this
    /// is the axis <c>OpenDoor</c> itself uses to decide which side the opener is on.</summary>
    public static Vec3 GetOrientation(nint obj) => *(Vec3*)(obj + ObjOrientation);

    // ---- door module transitions: reading the link, and asking to walk through it ----
    //
    // See docs/research/cinematics.md §8 and DoorUse. A door's transition is TWO phases, and
    // the first one is the whole reason this exists: on use, CSWSDoor::EventHandler raises
    // CSWSArea::SetTransitionPending(1, id) 0x00506B30 and starts a 0.5 s fade
    // (0x0058C2A0-0x0058C315); when the fade completes it does the three move-to-module calls
    // (0x0058C185-0x0058C1BE) that Odyssey.TravelToModule also does, and clears the flag.
    // Phase 1 is what opens PluginHost's pre-store flush window, and a plugin-initiated warp
    // never opens it, which is why only a real door can test what a movie does to it.

    /// <summary>CSWSDoor + this = <c>linked_to_flags</c> (1 or 2 = a module transition).</summary>
    private const int DoorLinkedToFlags = 0x384;

    /// <summary>CSWSDoor + this = <c>linked_to</c>, the destination waypoint tag (CExoString).
    /// The engine passes exactly this to SetMoveToModuleStartWaypoint at 0x0058C1AB.</summary>
    private const int DoorLinkedTo = 0x388;

    /// <summary>CSWSDoor + this = <c>linked_to_module</c> (CExoString). The engine passes
    /// exactly this to SetMoveToModuleString at 0x0058C197.</summary>
    private const int DoorLinkedToModule = 0x390;

    /// <summary>
    /// CSWSDoor::GetIsLinked 0x005890D0 — <c>__thiscall</c>, NO arguments. The entire function
    /// is <c>mov al,[ecx+0x384]; cmp al,1; je ok; cmp al,2; je ok; xor eax,eax; ret</c> — a
    /// bare <c>ret</c>, so the argument size is 0, and it dereferences nothing beyond
    /// <c>this</c>. Called rather than reading the byte ourselves because the ENGINE owns the
    /// definition of "this door is a module transition" (transition-window rule 4: identity
    /// comes from the engine whenever an in-process call exists).
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int> DoorGetIsLinkedFn =
        (delegate* unmanaged[Thiscall]<nint, int>)0x005890D0;

    /// <summary>
    /// CSWCMessage::SendPlayerToServerInput_UseObject 0x00677D10 — <c>__thiscall</c>, ONE
    /// argument (the object id). <c>ret 4</c> at 0x00677D4E and 0x00677D6A.
    ///
    /// ⚠ **THIS IS THE PLACEABLE MESSAGE, NOT THE DOOR ONE, AND IT CANNOT START A TRANSITION.**
    /// Its ONE caller in the whole binary is `CSWCPlaceable::UsePlaceable 0x00682660`; the only
    /// thing that sends a door input is `CSWCDoor::ToggleDoorState 0x00683D90`, which sends
    /// `SendPlayerToServerInput_ChangeDoorState 0x00677D70` with 0x2725 to open. Neither one
    /// transitions: a module transition is event **0x1E** on the door, and 0x1E is built only
    /// by movement code (see `CheatController.UseDoor`). Sending this for a door queues
    /// `AddUseObjectAction`, walks the player to the door's action point and stops there —
    /// measured live: 4.1 m to 1.5 m, then nothing at all. Kept, declared honestly, because
    /// the address is right for a PLACEABLE and the comment is what stops the next person
    /// repeating the mistake.
    ///
    /// This is LITERALLY what clicking a placeable does. Its server handler is
    /// <c>CSWSMessage::HandlePlayerToServerInputMessage 0x005254C0</c>, whose jump table at
    /// 0x0052684C sends message type 0x0B to 0x00525B1A, which does
    /// <c>CSWSCreature::ActionManager(2)</c> and then
    /// <c>CSWSObject::AddUseObjectAction 0x0057C810</c> on the player. The queued action runs
    /// <c>CSWSObject::AIActionUseObject 0x0057E8C0</c>, which walks the PC to the door's
    /// action point and posts the signal event (0x0057ECC7 / 0x0057F3C9) that
    /// <c>CSWSDoor::EventHandler 0x0058B850</c> turns into the transition.
    ///
    /// ⚠ The DIRECT send, not the client-side wrapper — the same lesson
    /// <see cref="SendDialogRequest"/> paid for. <c>CSWCDoor</c>'s own menu helpers
    /// (<c>GetTargetActions 0x00684410</c>, <c>MenuAction* 0x00683E50</c>) have client-side
    /// gates that a mouse click satisfies and we do not, and driving them from here leaves the
    /// interaction half-started. The send is both the smaller and the safer half.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, void> SendUseObjectInputFn =
        (delegate* unmanaged[Thiscall]<nint, uint, void>)0x00677D10;

    /// <summary>CSWCMessage::SendPlayerToServerInput_ChangeDoorState 0x00677D70;
    /// __thiscall, two stack arguments, ret 8. CSWCDoor::ToggleDoorState passes the door
    /// object id and 0x2725 for the native open/change-state request.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, uint, void>
        SendChangeDoorStateInputFn =
        (delegate* unmanaged[Thiscall]<nint, uint, uint, void>)0x00677D70;

    /// <summary>Sends native door-state input without unlocking or writing door state.</summary>
    public static bool ChangeDoorState(uint doorId, uint operation = 0x2725)
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint message = GetSWCMessageFn(client);
        if (message == 0)
        {
            return false;
        }

        SendChangeDoorStateInputFn(message, doorId, operation);
        return true;
    }

    /// <summary>
    /// CSWParty::IsPartyTogether 0x00635350 — <c>__thiscall</c>, NO arguments (bare <c>ret</c>
    /// at 0x0063538D). Reads the player's position and compares every party member's against
    /// it.
    ///
    /// ⚠ THIS IS THE GATE THAT SILENTLY CANCELS A DOOR TRANSITION. <c>CSWSDoor::EventHandler</c>
    /// tests it at 0x0058C228 and, when it is false, jumps to 0x0058C31A — which runs the
    /// script <c>k_trg_transfail</c> (string at 0x007499C4) instead of starting the transition.
    /// Nothing about that failure is visible: the door is used, the fade never starts, and no
    /// module is stored. Reported rather than refused, deliberately — see
    /// <c>CheatController.UseDoor</c>.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int> IsPartyTogetherFn =
        (delegate* unmanaged[Thiscall]<nint, int>)0x00635350;

    /// <summary>
    /// True when the engine currently considers the party gathered — the predicate a door
    /// transition is gated on. False here predicts <c>k_trg_transfail</c> rather than a move.
    /// Returns true when there is no party object to ask, because "unknown" must not read as
    /// "broken".
    /// </summary>
    public static bool IsPartyTogether()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return true;
        }

        nint party = GetSWPartyFn(client);
        return party == 0 || IsPartyTogetherFn(party) != 0;
    }

    /// <summary>
    /// CClientExoAppInternal::ServerToClientObjectId 0x005EDA50 — <c>__thiscall</c> on the same
    /// <c>client</c> pointer <see cref="GetClient"/> resolves, one stack arg (a SERVER object
    /// id), returns the CLIENT-side id space's own id for the same object.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, uint> ServerToClientObjectIdFn =
        (delegate* unmanaged[Thiscall]<nint, uint, uint>)0x005EDA50;

    /// <summary>
    /// CSWParty::GetIndex 0x006349C0 — <c>__thiscall</c> on a CSWParty*, one stack arg (a
    /// CLIENT-side object id, see <see cref="ServerToClientObjectIdFn"/>), returns that member's
    /// index in the 0x88-strided array <see cref="GetSWPartyFn"/> exposes, or -1.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int> GetPartyIndexFn =
        (delegate* unmanaged[Thiscall]<nint, uint, int>)0x006349C0;

    /// <summary>
    /// CSWParty::RecaulateFollowPoint 0x006359C0 — <c>__thiscall</c>, NO stack arguments beyond
    /// <c>this</c> (a CSWParty*, see <see cref="GetSWPartyFn"/>).
    ///
    /// A genuinely-owned, genuinely-following companion's own per-member follow-data record
    /// (<c>CSWParty+0x24+index*0x88</c>) can hold a TARGET follow point (<c>record+0x28</c>)
    /// frozen at wherever the leader stood the moment the module loaded, unmoving no matter how
    /// far the leader afterward walks.
    ///
    /// ⚠ LIVE-DISPROVEN AS THE FIX, BY ITSELF. Calling this every stall pass changed the frozen
    /// <c>record+0x28</c> value not at all, even with <c>record+0x18</c> confirmed (via
    /// <see cref="TryPokeFollowPriorPosition"/>'s own read-back) to genuinely hold the leader's
    /// fresh position at the moment this was called — so this function does not read its "leader
    /// position" input from <c>record+0x18</c> alone. The confirmed mechanism turned out to be
    /// <c>CSWParty::Update</c> (0x00637050, see <see cref="PartyUpdateFn"/>'s own remarks), which
    /// refreshes the party-level reference position this function actually reads; calling
    /// <c>RecaulateFollowPoint</c> without calling <c>CSWParty::Update</c> first is a no-op for
    /// that reason. NOT called from <see cref="PartyFollowController"/>'s shipped stall-handling
    /// path. See docs/research/phantom-locomotion.md and party-reconciliation.md.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> RecalculateFollowPointFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x006359C0;

    /// <summary>True when this door is a module transition, by the engine's own definition.</summary>
    public static bool DoorIsLinked(nint door) => door != 0 && DoorGetIsLinkedFn(door) != 0;

    /// <summary>The raw <c>linked_to_flags</c> byte, for an answer that says what it saw
    /// rather than just "no".</summary>
    public static byte GetDoorLinkFlags(nint door) => *(byte*)(door + DoorLinkedToFlags);

    /// <summary>Copies the door's destination module resref. Returns bytes written.</summary>
    public static int ReadDoorLinkedModule(nint door, byte* destination, int capacity) =>
        ReadExoString(door + DoorLinkedToModule, destination, capacity);

    /// <summary>Copies the door's destination waypoint tag. Returns bytes written.</summary>
    public static int ReadDoorLinkedWaypoint(nint door, byte* destination, int capacity) =>
        ReadExoString(door + DoorLinkedTo, destination, capacity);

    /// <summary>
    /// Copies a CExoString at <paramref name="field"/> as ASCII, cutting at the first null.
    ///
    /// Cutting rather than trusting the length is deliberate and load-bearing: a CExoString's
    /// length field COUNTS THE TERMINATOR, and believing it has produced three separate
    /// off-by-one bugs in this codebase. Every pointer is validated before it is followed,
    /// because a door read during a transition can hold a header that is already garbage.
    /// </summary>
    private static int ReadExoString(nint field, byte* destination, int capacity)
    {
        if (destination == null || capacity <= 0)
        {
            return 0;
        }

        nint chars = Deref(field);
        int length = *(int*)(field + 4);

        if (!LooksLikeHeapPointer(chars) || length is <= 0 or > 256)
        {
            return 0;
        }

        int written = 0;
        while (written < capacity && written < length)
        {
            byte c = *(byte*)(chars + written);
            if (c == 0)
            {
                break;
            }

            destination[written++] = c;
        }

        return written;
    }

    /// <summary>
    /// Sends the local player's "I clicked that" for <paramref name="objectId"/>, through the
    /// engine's own client-to-server input pipe. Returns false when there is no client or no
    /// message object — never silently.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool UseObject(uint objectId)
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint message = GetSWCMessageFn(client);
        if (message == 0)
        {
            return false;
        }

        SendUseObjectInputFn(message, objectId);
        return true;
    }

    /// <summary>
    /// Copies every door in the loaded module into <paramref name="into"/>, returning how many
    /// were written. One full address-space sweep; a debug verb only, never per frame.
    /// </summary>
    public static int CollectDoors(nint* into, int capacity) =>
        CreatureFinder.CollectByVtable(DoorVtable, into, capacity);

    /// <summary>Finds a door by tag. 0 when this module has no door with that tag.</summary>
    public static nint FindDoorByTag(byte* tag, int length) =>
        length <= 0 ? 0 : CreatureFinder.FindDoorByTag(new ReadOnlySpan<byte>(tag, length));

    // CSWSPlaceable. Unlike a door, a placeable has no walkmesh coupling, so mirroring its
    // locked/open state is a plain field write — no engine call, no crash risk (see
    // docs/research/placeables-containers.md). Both fields are ints, saved as bytes.
    private const nint PlaceableVtable = 0x007494D0;
    private const int PlaceableLocked = 0x260;
    private const int PlaceableOpen = 0x338;
    private const int PlaceableTrap = 0x278; // "is trapped"; clearing = disarmed

    /// <summary>Resolves a placeable by object id, vtable-checked. 0 if absent or not one.</summary>
    public static nint ResolvePlaceable(uint objectId)
    {
        nint placeable = ResolveServerObject(objectId);
        return placeable != 0 && *(nint*)placeable == PlaceableVtable ? placeable : 0;
    }

    public static bool IsPlaceableLocked(nint placeable) => *(int*)(placeable + PlaceableLocked) != 0;

    public static void UnlockPlaceable(nint placeable) => *(int*)(placeable + PlaceableLocked) = 0;

    public static bool IsPlaceableOpen(nint placeable) => *(int*)(placeable + PlaceableOpen) != 0;

    public static void SetPlaceableOpen(nint placeable) => *(int*)(placeable + PlaceableOpen) = 1;

    public static bool IsPlaceableTrapped(nint placeable) => *(int*)(placeable + PlaceableTrap) != 0;

    public static void DisarmPlaceable(nint placeable) => *(int*)(placeable + PlaceableTrap) = 0;

}
