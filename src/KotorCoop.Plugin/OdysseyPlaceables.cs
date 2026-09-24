namespace KotorCoop.Plugin;

/// <summary>
/// Placeable/container wrappers that sit alongside <c>Odyssey.cs</c> rather than inside it (a
/// rig agent owns that file for the duration of this pass) — the container-lock/animation half
/// of docs/research/container-sync.md.
///
/// Every address below is VERIFIED against <c>tools/kotor1_0_3.db</c> and fresh
/// <c>tools/disasm.py</c> output taken this pass (2026-08-22), the same sha256-matched GOG
/// build every other file in this plugin assumes (no ASLR, disk offset == memory offset for a
/// fixed address).
/// </summary>
internal static unsafe class OdysseyPlaceables
{
    /// <summary>CSWSPlaceable + this = the object's own id (DWORD), same embedded-CSWSObject
    /// layout every other object class in this plugin already relies on (creatures, doors).
    /// docs/research/placeables-containers.md §1.</summary>
    private const int ObjectIdOffset = 0x04;

    /// <summary>CSWSPlaceable + this = has_inventory (int, saved): nonzero means this
    /// placeable is a real container (its <c>+0x36C</c> repository is meaningful), not a
    /// switch/lever with no loot. docs/research/placeables-containers.md §1.</summary>
    private const int HasInventoryOffset = 0x334;

    /// <summary>CSWSPlaceable + this = "open" (int, saved as byte): the container GUI's
    /// currently-shown state, NOT a permanent "ever searched" flag — see the correction below.
    /// docs/research/placeables-containers.md §1 (VERIFIED closed/open bit), and
    /// docs/research/container-sync.md (VERIFIED live-cleared-on-close, this pass).</summary>
    private const int OpenOffset = 0x338;

    /// <summary>
    /// Clears the "open" field back to closed (0). The engine's own
    /// <c>CSWSPlaceable::CloseInventory 0x00587560</c> does exactly this — an unconditional
    /// <c>mov dword ptr [esi+0x338], 0</c> right after it plays the close animation (VERIFIED
    /// disasm this pass: <c>tools/disasm.py 0x587560 550</c>) — so a mirror that also clears
    /// this after playing the close animation (see <see cref="PlayContainerCloseAnimation"/>)
    /// leaves its OWN copy in the same state the real engine call would have.
    ///
    /// ⚠ CORRECTS docs/research/placeables-containers.md's own reading of this field
    /// ("0 = closed/unsearched, nonzero = opened", implying permanent). Fresh disassembly of
    /// BOTH <c>OpenInventory</c> (0x00587420) and <c>CloseInventory</c> (0x00587560) this pass
    /// shows <c>CloseInventory</c> unconditionally resets it to 0 — so it tracks "is the GUI
    /// showing right now", a TRANSIENT per-instance flag, not a durable "ever searched" one.
    /// It still appears in <c>SavePlaceable</c> because every field in the struct is
    /// serialised, and it is always 0 at real save time (the game does not allow saving with a
    /// container panel open) — the two readings are not actually in tension.
    ///
    /// <see cref="PlaceableController"/>'s own durable channel (host's open/locked state, both
    /// directions since 2026-09-24 — docs/research/door-relock.md §6) reads this SAME
    /// <c>+0x338</c> field independently on every instance
    /// (<c>KotorGameBridge.CollectChangedPlaceableState</c>); at the time this method was added
    /// (2026-08-22) that channel was one-way and monotonic, so it fought a real close it could
    /// not itself express — <see cref="ContainerCloseHook.Register"/> below was that pass's LIVE
    /// fix. The durable channel now carries an honest "closed" value too, so the two no longer
    /// fight (see <c>PlaceableController.Apply</c>'s own remarks); <see cref="ContainerCloseHook"/>
    /// is left installed and this call to <see cref="ContainerCloseHook.Register"/> is kept —
    /// see that class's own updated remarks — every caller of THIS method is still a mirror-close
    /// (the two call sites in <c>InteractionLockController</c>), the entirely separate live
    /// "someone else is looting this right now" claim mirror, unrelated to the durable channel.
    /// </summary>
    public static void ClearPlaceableOpenFlag(nint placeable)
    {
        *(int*)(placeable + OpenOffset) = 0;
        ContainerCloseHook.Register(GetPlaceableObjectId(placeable));
    }

    /// <summary>Reads the placeable's own object id — same embedded layout every object class
    /// in this plugin already uses.</summary>
    public static uint GetPlaceableObjectId(nint placeable) => *(uint*)(placeable + ObjectIdOffset);

    /// <summary>Whether this placeable is a real container (has a meaningful item repository at
    /// <c>+0x36C</c>) rather than a switch/lever.</summary>
    public static bool HasInventory(nint placeable) => *(int*)(placeable + HasInventoryOffset) != 0;

    // ---- animation: CSWSObject::SetAnimation, VERIFIED (2026-08-22) --------------------
    //
    // docs/research/placeables-containers.md called the placeable's animation call
    // "INFERRED / NEEDS-LIVE — a guessed-ABI vtable call". It no longer is. Both
    // OpenInventory (0x00587420) and CloseInventory (0x00587560) reach it through the SAME
    // vtable slot: `mov edx,[esi]; push <animId>; mov ecx,esi; call [edx+0x7c]`. Reading the
    // CSWSPlaceable vtable's own +0x7c slot out of the (sha256-matched, no-ASLR) exe on disk
    // resolves to a FIXED address, 0x004CCF60, which the symbol db names
    // CSWSObject::SetAnimation — disassembling it confirms a two-field setter with no branch,
    // no allocation, no re-entrancy:
    //
    //   0x004CCF60  or dword ptr [ecx+0x1fc], 1   ; a "dirty/pending" bit
    //   0x004CCF67  mov eax, dword ptr [esp+4]    ; the one stack arg: the requested anim id
    //   0x004CCF6B  mov dword ptr [ecx+0xd4], eax ; stored for the engine's own update to pick up
    //   0x004CCF71  ret 4
    //
    // __thiscall void SetAnimation(int animId) — this=ecx, animId pushed before ecx is loaded,
    // exactly the shape `delegate* unmanaged[Thiscall]<nint, int, void>` already models
    // elsewhere in this codebase (e.g. Odyssey.ClearAllActionsFn). Calling the fixed address
    // directly, rather than indirecting through the vtable slot, is equivalent and simpler —
    // the same choice this plugin already makes for every other VERIFIED fixed-address call.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetAnimationFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x004CCF60;

    /// <summary>
    /// The OPEN pose, animation id 0x275B — VERIFIED as the id
    /// <c>CSWSPlaceable::OpenInventory</c>'s own success path plays
    /// (<c>push 0x275b; call [edx+0x7c]</c> at 0x00587530, immediately before it sets
    /// <c>+0x338 = 1</c>), gated there on the caller's own "animate" argument.
    /// </summary>
    private const int OpenAnimationId = 0x275B;

    /// <summary>
    /// The CLOSE pose, animation id 0x275C — VERIFIED as the id
    /// <c>CSWSPlaceable::CloseInventory</c> plays unconditionally
    /// (<c>push 0x275c; call [edx+0x7c]</c> at 0x00587688) right before it clears
    /// <c>+0x338</c> back to 0. Also the id <c>OpenInventory</c>'s own degenerate
    /// no-message-context branch plays (0x00587475) — consistent with "nobody is actually
    /// looking at this, leave it in the closed pose".
    /// </summary>
    private const int CloseAnimationId = 0x275C;

    /// <summary>Plays the container open pose on this instance's own copy of
    /// <paramref name="placeable"/> — the mirror half of a peer's Container-kind interaction
    /// claim appearing (docs/research/container-sync.md). Does NOT call the real
    /// <c>OpenInventory</c>: that function also runs GUI/message-context logic meant for a real
    /// local player, which would be wrong to invoke on a mirror.</summary>
    public static void PlayContainerOpenAnimation(nint placeable) =>
        SetAnimationFn(placeable, OpenAnimationId);

    /// <summary>Plays the container close pose on this instance's own copy of
    /// <paramref name="placeable"/> — the mirror half of a peer's Container-kind interaction
    /// claim ending. Pairs with <see cref="ClearPlaceableOpenFlag"/>, the same two actions the
    /// real <c>CloseInventory</c> performs.</summary>
    public static void PlayContainerCloseAnimation(nint placeable) =>
        SetAnimationFn(placeable, CloseAnimationId);

    // ---- the REAL CloseInventory call, for the closeplaceable staging verb ---------------
    //
    // `useplaceable` only replicates the network HALF of a real click
    // (Odyssey.UseObject -> CSWCMessage::SendPlayerToServerInput_UseObject): the client-side
    // menu wrapper that actually SHOWS the loot panel (CSWCPlaceable::UsePlaceable, see
    // Odyssey.cs's remarks on SendUseObjectInputFn) is deliberately not replicated, so the
    // ENGINE's own +0x338 flips to 1 (confirmed live) but no panel appears for an Escape
    // keystroke — or a real player — to close. This is the fix: call CSWSPlaceable::
    // CloseInventory 0x00587560 directly, the same "call the fixed address, no vtable
    // indirection" choice this file already makes for SetAnimation.
    //
    // ⚠ LIVE-CORRECTED: arg1 (`[esp+0x1c]` on entry, matching OpenInventory's own arg1) is NOT
    // safe as 0 — passing 0 live-disproved silently (the call returns but `+0x338` never
    // clears), because arg1 is pushed RAW into a second message dispatch
    // (`push ebx /* = raw arg1 */; ...; call 0x4aed80; call 0x4b08d0`) that a real close always
    // crosses too, before the close animation and the `+0x338 = 0` clear. Fresh disasm of the
    // real call site, `AIActionUseObject`'s open call at 0x0057EEA3
    // (`tools/disasm.py 0x57EE80 0x50`), shows what a real click passes for this argument:
    // `mov eax,[edi+4]; push eax` — the ACTING CREATURE'S OWN OBJECT ID (the standard
    // `object + 4` convention this whole plugin already uses for every CSWSObject subclass).
    // <see cref="CloseInventoryReal"/> passes that instead, and it is live-confirmed to clear
    // `+0x338` reliably. Full investigation in docs/research/container-sync.md §1.2.
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int, void> CloseInventoryFn =
        (delegate* unmanaged[Thiscall]<nint, uint, int, void>)0x00587560;

    /// <summary>CSWSObject + this = the object's own id — same embedded layout as
    /// <see cref="ObjectIdOffset"/>, applied to a creature here rather than a placeable.</summary>
    private const int CreatureObjectIdOffset = 0x04;

    /// <summary>Calls the real engine <c>CloseInventory</c> on <paramref name="placeable"/> with
    /// arg1 = <paramref name="closingPlayer"/>'s own object id (the exact value a real click's
    /// <c>AIActionUseObject</c> passes for this argument — see the class remarks above for why
    /// 0 is NOT safe here) and arg2 = 0 (skip the item-transfer bookkeeping; there is nothing to
    /// give the player through this programmatic close). Plays the close animation and clears
    /// <c>+0x338</c>, exactly what closing the loot panel through the normal UI would do. Used
    /// by the <c>closeplaceable</c> staging verb only — the container-sync mirror
    /// (<see cref="PlayContainerCloseAnimation"/> + <see cref="ClearPlaceableOpenFlag"/>) never
    /// calls the real function, for the same "not a real local player" reason
    /// <c>OpenInventory</c> is never mirrored either.</summary>
    public static void CloseInventoryReal(nint placeable, nint closingPlayer)
    {
        uint playerId = closingPlayer != 0 ? *(uint*)(closingPlayer + CreatureObjectIdOffset) : 0;
        CloseInventoryFn(placeable, playerId, 0);
    }

    // ---- sweeps, for the useplaceable staging verb --------------------------------------

    /// <summary>Copies every placeable in the loaded module into <paramref name="into"/>,
    /// returning how many were written. One full address-space sweep; a debug verb only.</summary>
    public static int CollectPlaceables(nint* into, int capacity) =>
        CreatureFinder.CollectByVtable(CreatureFinder.PlaceableVtable, into, capacity);

    /// <summary>Finds a placeable by tag. 0 when this module has none with that tag (or several
    /// share it — see <see cref="CreatureFinder.FindPlaceableByTag"/>).</summary>
    public static nint FindPlaceableByTag(byte* tag, int length) =>
        length <= 0 ? 0 : CreatureFinder.FindPlaceableByTag(new ReadOnlySpan<byte>(tag, length));

    /// <summary>Placeables one module can hold. Generous headroom, matching
    /// <c>CheatController.DoorSweepCapacity</c>'s own reasoning — a ceiling, not an
    /// expectation.</summary>
    public const int SweepCapacity = 256;

    /// <summary>
    /// The nearest CONTAINER (a real repository, not a switch/lever) to <paramref name="player"/>,
    /// for the <c>useplaceable</c> verb's bare form. <paramref name="seen"/> is every placeable
    /// the sweep found, container or not, so a refusal can say whether the module has placeables
    /// at all or simply none close enough / none that hold loot.
    /// </summary>
    public static nint FindNearestContainer(nint player, out int seen)
    {
        nint* placeables = stackalloc nint[SweepCapacity];
        seen = CollectPlaceables(placeables, SweepCapacity);

        Vec3 here = Odyssey.GetPosition(player);
        nint best = 0;
        float bestDistanceSquared = float.MaxValue;

        for (int i = 0; i < seen; i++)
        {
            if (!HasInventory(placeables[i]))
            {
                continue;
            }

            Vec3 there = Odyssey.GetPosition(placeables[i]);
            float dx = here.X - there.X;
            float dy = here.Y - there.Y;
            float dz = here.Z - there.Z;
            float distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);

            if (distanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            best = placeables[i];
            bestDistanceSquared = distanceSquared;
        }

        return best;
    }

    /// <summary>
    /// The nearest OPEN placeable to <paramref name="player"/> — <c>closeplaceable</c>'s bare
    /// form. Filters on <c>Odyssey.IsPlaceableOpen</c> rather than <see cref="HasInventory"/>
    /// deliberately: <c>has_inventory</c> (<c>+0x334</c>) under-reports real containers,
    /// live-confirmed on a placeable that opened and held items despite reading 0 there (see
    /// docs/research/container-sync.md). "Is it open right now" has no such ambiguity: it is
    /// the exact field <c>OpenInventory</c>/<c>CloseInventory</c> themselves set and clear
    /// (§1.2, container-sync.md), so this is unaffected by whatever is wrong with
    /// <c>has_inventory</c>.
    /// </summary>
    public static nint FindNearestOpenContainer(nint player, out int seen)
    {
        nint* placeables = stackalloc nint[SweepCapacity];
        seen = CollectPlaceables(placeables, SweepCapacity);

        Vec3 here = Odyssey.GetPosition(player);
        nint best = 0;
        float bestDistanceSquared = float.MaxValue;

        for (int i = 0; i < seen; i++)
        {
            if (!Odyssey.IsPlaceableOpen(placeables[i]))
            {
                continue;
            }

            Vec3 there = Odyssey.GetPosition(placeables[i]);
            float dx = here.X - there.X;
            float dy = here.Y - there.Y;
            float dz = here.Z - there.Z;
            float distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);

            if (distanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            best = placeables[i];
            bestDistanceSquared = distanceSquared;
        }

        return best;
    }

    /// <summary>
    /// Logs every placeable in the module — id, container/switch, distance, locked/open/trap —
    /// so the container-sync live-test script can discover ids with the SAME <c>useplaceable</c>
    /// verb it goes on to use, rather than a second listing command. Called from
    /// <c>CheatController.UsePlaceable</c>'s bare form (no tag or id given), a debug verb only —
    /// one full sweep, never on the per-frame path.
    /// </summary>
    public static void LogPlaceableSweep(nint player)
    {
        nint* placeables = stackalloc nint[SweepCapacity];
        int seen = CollectPlaceables(placeables, SweepCapacity);

        Vec3 here = Odyssey.GetPosition(player);
        PluginHost.Log($"placeables: {seen} in this module");

        for (int i = 0; i < seen; i++)
        {
            nint p = placeables[i];
            uint id = GetPlaceableObjectId(p);
            Vec3 there = Odyssey.GetPosition(p);
            float dx = here.X - there.X;
            float dy = here.Y - there.Y;
            float dz = here.Z - there.Z;
            float distance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));

            PluginHost.Log(
                $"  0x{id:X}{(HasInventory(p) ? " [container]" : string.Empty)} at {distance:F1}m "
                + $"locked={Odyssey.IsPlaceableLocked(p)} open={Odyssey.IsPlaceableOpen(p)} "
                + $"trapped={Odyssey.IsPlaceableTrapped(p)}");
        }
    }
}
