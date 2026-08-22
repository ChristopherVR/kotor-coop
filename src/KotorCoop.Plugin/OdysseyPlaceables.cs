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
    /// <see cref="PlaceableController"/>'s existing one-way mirror (host's "ever opened" ->
    /// write 1 on a peer's copy, via <c>Odyssey.SetPlaceableOpen</c>, never cleared) is UNAFFECTED
    /// by this correction and keeps its own job: a late joiner sees an already-looted chest with
    /// its lid open. This method is only ever called by the NEW transient tracking in
    /// <see cref="InteractionLockController"/>, which is a separate concern from that durable
    /// cosmetic mirror — see docs/research/container-sync.md.
    /// </summary>
    public static void ClearPlaceableOpenFlag(nint placeable) => *(int*)(placeable + OpenOffset) = 0;

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
