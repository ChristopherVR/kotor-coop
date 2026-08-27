// Odyssey.Items.cs - item floor/possession state, container loot handling, and
// item-identity resolution (LastItemPointer/LastItemResolveNote diagnostics).

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- item floor state ------------------------------------------------------

    private const nint ItemVtable = 0x00748DA0;

    /// <summary>CSWSItem + this = possessor object id. OBJECT_INVALID means "on the floor".</summary>
    private const int ItemPossessor = 0x268;

    internal enum ItemFloorState
    {
        /// <summary>Destroyed, or merged into a stack the picker already carried.</summary>
        Gone,

        OnFloor,

        /// <summary>In somebody's inventory. <c>possessor</c> names whose.</summary>
        Held,
    }

    /// <summary>
    /// Asks the engine's own question about an item: is it lying on the ground?
    ///
    /// <c>CSWSCreature::AIActionPickUpItem</c> gates the whole pickup on
    /// <c>item+0x268 == OBJECT_INVALID</c>, and <c>AcquireItem</c> re-validates it, so this
    /// is the invariant rather than a proxy for it. Picking an item up does not destroy it -
    /// it is moved, keeping its object id - EXCEPT when it merges into a stack the picker
    /// already carries, in which case the object really is freed. Both are pickups.
    /// </summary>
    /// <summary>Diagnostics from the last <see cref="GetItemFloorState"/> call.</summary>
    public static nint LastItemPointer;

    public static uint LastItemIdAtOffset14;

    public static ItemFloorState GetItemFloorState(uint objectId, out uint possessor)
    {
        possessor = ObjectInvalid;
        LastItemPointer = 0;
        LastItemIdAtOffset14 = 0;

        nint appManager = Deref(AppManagerPtr);
        if (appManager == 0)
        {
            return ItemFloorState.Gone;
        }

        nint server = Deref(appManager + AppServerOffset);
        if (server == 0)
        {
            return ItemFloorState.Gone;
        }

        // The raw pointer, not the +0x10-adjusted one: the possessor field is indexed from
        // the CSWSItem base, which is not where CSWSObject starts for an item.
        //
        // Identify the layout by where the object id lands rather than by the vtable. The
        // id test is the one already proven against this build; a vtable equality check
        // looked stricter but simply rejected every item, which showed up as an item being
        // reported picked up 36ms after it was spawned - and that removal then propagated
        // back and deleted the item in every instance.
        nint pointer = GetGameObjectFn(server, objectId);
        LastItemPointer = pointer;

        if (pointer == 0)
        {
            return ItemFloorState.Gone;
        }

        LastItemIdAtOffset14 = *(uint*)(pointer + 0x14);

        // Accept either shape rather than assuming one. An item's CSWSObject is embedded at
        // CSWSItem+0x10, so depending on which of the two the lookup hands back, the object
        // id sits at +0x14 (CSWSItem base) or +0x04 (the CSWSObject inside it). Assuming
        // the base is what made this report every freshly spawned item as already gone —
        // and because a local vanish is broadcast, that false positive deleted the item in
        // every instance.
        nint itemBase;

        if (LastItemIdAtOffset14 == objectId)
        {
            itemBase = pointer;
        }
        else if (*(uint*)(pointer + 0x04) == objectId)
        {
            itemBase = pointer - 0x10;
        }
        else
        {
            return ItemFloorState.Gone;
        }

        // Sanity-check the layout before trusting a field 0x268 bytes in.
        if (*(nint*)itemBase != ItemVtable)
        {
            return ItemFloorState.Gone;
        }

        possessor = *(uint*)(itemBase + ItemPossessor);
        return possessor == ObjectInvalid ? ItemFloorState.OnFloor : ItemFloorState.Held;
    }

    // ---- container loot ----------------------------------------------------------
    //
    // A container's contents live in one CItemRepository* at CSWSPlaceable+0x36C (also
    // corpses/body-bags). Its item list at repo+0x0C is a flat array of OBJECT IDS (DWORDs,
    // not pointers); the count is at repo+0x10. RemoveItem deletes one. See
    // docs/research/placeables-containers.md §2 (all VERIFIED there).

    /// <summary>CSWSPlaceable + this = CItemRepository* (0 when it has no inventory).</summary>
    private const int PlaceableRepository = 0x36C;

    /// <summary>CItemRepository + this = flat array of item object ids (DWORDs).</summary>
    private const int RepositoryItemArray = 0x0C;

    /// <summary>CItemRepository + this = item count.</summary>
    private const int RepositoryItemCount = 0x10;

    // CSWSPlaceable::RemoveItem(CSWSItem*) 0x00584AC0 — the container "take" primitive
    // (VERIFIED, clean thiscall). It confirms the item is in this placeable's repository
    // (GetItemInRepository), unlinks the id, then detaches the item, so calling it for an
    // item that is not in this container is itself a safe no-op. MAIN THREAD ONLY.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, void> PlaceableRemoveItemFn =
        (delegate* unmanaged[Thiscall]<nint, nint, void>)0x00584AC0;

    /// <summary>
    /// Why the last <see cref="ResolveItemBase"/> refused, in words, or empty when it
    /// succeeded.
    ///
    /// This exists because the failure it reports is invisible otherwise. `ApplyItemIdentity`
    /// deliberately does nothing when the id is not a live item — a wrong write into a
    /// stranger's object is far worse than a stock item — but "did nothing" and "is broken"
    /// look identical from outside, and that ambiguity is exactly what left the whole apply
    /// path unverified through a live pass (docs/live-verification.md). A guard
    /// that fails safe still has to say what it saw.
    /// </summary>
    public static string LastItemResolveNote = string.Empty;

    /// <summary>
    /// Resolves an item object id to its CSWSItem base, vtable-checked. Mirrors the two-shape
    /// id logic the ground-item reader uses: GetGameObject may hand back the CSWSItem base
    /// (id at +0x14) or the embedded CSWSObject (id at +0x04). Returns 0 if not a live item,
    /// leaving <see cref="LastItemResolveNote"/> saying which step refused and what it read.
    ///
    /// The vtable constant is not a guess: <c>CSWSItem::Constructor</c> 0x005530A0 writes
    /// <c>0x748DA0</c> to <c>[this]</c> and <c>0x748CC0</c> to <c>[this+0x10]</c> (the
    /// embedded CGameObject's own vptr) at 0x005530EE/0x005530F4. So the base carries
    /// 0x748DA0 and the subobject carries something else, which is why a vtable test applied
    /// to the RAW pointer rejects every item and one applied to the resolved base does not.
    /// </summary>
    private static nint ResolveItemBase(uint itemId)
    {
        nint server = GetServer();
        if (server == 0)
        {
            LastItemResolveNote = "no server (not in a world)";
            return 0;
        }

        nint pointer = GetGameObjectFn(server, itemId);
        if (pointer == 0)
        {
            LastItemResolveNote = $"no object 0x{itemId:X} in this module";
            return 0;
        }

        nint itemBase;
        if (*(uint*)(pointer + 0x14) == itemId)
        {
            itemBase = pointer;
        }
        else if (*(uint*)(pointer + 0x04) == itemId)
        {
            itemBase = pointer - 0x10;
        }
        else
        {
            LastItemResolveNote =
                $"0x{itemId:X} resolves to 0x{pointer:X}, but its id is neither at +0x14 "
                + $"(0x{*(uint*)(pointer + 0x14):X}) nor at +0x04 (0x{*(uint*)(pointer + 0x04):X})";
            return 0;
        }

        nint vtable = *(nint*)itemBase;
        if (vtable != ItemVtable)
        {
            LastItemResolveNote =
                $"0x{itemId:X} is at 0x{itemBase:X} but its vtable is 0x{vtable:X}, "
                + $"not CSWSItem's 0x{ItemVtable:X} — it is some other kind of object";
            return 0;
        }

        LastItemResolveNote = string.Empty;
        return itemBase;
    }

    /// <summary>
    /// A live <c>CSWSItem</c>'s per-instance state, exactly as the five fields hold it. Raw
    /// engine values rather than the wire shape: this is what a read-back has to report, and
    /// re-encoding it would hide the very difference a read-back exists to reveal.
    /// </summary>
    internal struct ItemIdentityFields
    {
        public uint StackSize;
        public uint Charges;
        public uint MaxCharges;
        public uint Upgrades;

        /// <summary>The whole +0x288 dword, uncarried bits included — a read-back that
        /// silently dropped bits 3/4/7/8 could not show that the write left them alone.</summary>
        public uint BitFlags;

        /// <summary>The possessor id at +0x268; <see cref="ObjectInvalid"/> means the item is
        /// lying on the floor. Not part of identity, but it is the one fact that tells a
        /// listing whose pack an item is in.</summary>
        public uint Possessor;

        /// <summary>
        /// False when the item's Tag is empty — which, for a body we just asked the engine to
        /// create from a named blueprint, means the blueprint DOES NOT EXIST. See
        /// <see cref="LooksUnloaded"/>.
        /// </summary>
        public bool HasTag;

        /// <summary>
        /// This body was never filled in from a template.
        ///
        /// ⚠ `CreateItemOnFloor` 0x00547F60 calls `LoadFromTemplate` 0x005608B0 at
        /// 0x0054806B and **never tests its result** — it goes straight on to add the item to
        /// the area and returns the object id at 0x005480AF. So a resref that names no
        /// blueprint yields a live, addressable, perfectly valid `CSWSItem` carrying nothing
        /// but its constructor's defaults, and every "did it spawn?" guard reports success.
        ///
        /// The tell is the pair below, both from `CSWSItem::Constructor` 0x005530A0: it
        /// leaves the Tag empty (it never writes +0x28) and sets Charges to exactly 50
        /// (0x00553189). All 557 item blueprints in the base game declare a Tag, so an empty
        /// one is not something a real load produces.
        ///
        /// Measured live: a drop of `g_w_blstrpstl01` — a typo, the real ones are
        /// `g_w_blstrpstl001`..`010` — produced object 0x131 with no tag, 50 charges, an
        /// uninitialised MaxCharges of 1768092, stack 1 and no upgrades. Exactly this.
        /// </summary>
        public bool LooksUnloaded => !HasTag;
    }

    /// <summary>CSWSItem + this = the object's Tag, a CExoString (char* then length). From
    /// `SaveItem` 0x0055CCEE, which writes <c>[esi+0x28]</c> under the GFF label "Tag".</summary>
    private const int ItemTag = 0x28;

    /// <summary>
    /// Reads one live item's identity fields through the engine's own object lookup.
    ///
    /// The counterpart to <see cref="ApplyItemIdentity"/> and the read-back that proves it:
    /// the plugin writes, then reads the same five addresses back and reports what is really
    /// there, so "applied" means the values are in the item rather than that a function
    /// returned true. False leaves <see cref="LastItemResolveNote"/> saying why.
    ///
    /// MAIN THREAD ONLY (it calls GetGameObject).
    /// </summary>
    internal static bool TryReadItemIdentityFields(uint itemId, out ItemIdentityFields fields)
    {
        fields = default;

        nint item = ResolveItemBase(itemId);
        if (item == 0)
        {
            return false;
        }

        fields = ReadItemFields(item);
        return true;
    }

    /// <summary>The five identity fields plus the possessor, straight off a resolved item.
    /// One reader shared by the plain read and by the apply's read-back, so the two can never
    /// disagree about which addresses they are talking about.</summary>
    private static ItemIdentityFields ReadItemFields(nint item) => new()
    {
        StackSize = *(uint*)(item + ItemStackSize),
        Charges = *(uint*)(item + ItemCharges),
        MaxCharges = *(uint*)(item + ItemMaxCharges),
        Upgrades = *(uint*)(item + ItemUpgrades),
        BitFlags = *(uint*)(item + ItemBitFlags),
        Possessor = *(uint*)(item + ItemPossessor),
        HasTag = HasNonEmptyTag(item),
    };

    /// <summary>
    /// True when the item carries a Tag. A CExoString is a char* followed by its length, and
    /// the length routinely INCLUDES the terminating null (three bugs in this project so
    /// far), so a one-byte "length" over an empty buffer still means empty — hence the first
    /// byte is checked too rather than the length alone.
    /// </summary>
    private static bool HasNonEmptyTag(nint item)
    {
        nint text = *(nint*)(item + ItemTag);
        uint length = *(uint*)(item + ItemTag + 4);

        return text != 0 && length != 0 && *(byte*)text != 0;
    }

    /// <summary>
    /// Deletes item <paramref name="itemId"/> from placeable <paramref name="placeable"/>'s
    /// container, if it is there. Confirms membership by scanning the repository's own id list
    /// first, so a miss is a clean no-op that never calls into the engine, and every deref is
    /// guarded. Returns true only when the item was found and RemoveItem was called.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool RemoveContainerItem(nint placeable, uint itemId)
    {
        if (placeable == 0)
        {
            return false;
        }

        nint repo = *(nint*)(placeable + PlaceableRepository);
        if (repo == 0)
        {
            return false;
        }

        int count = *(int*)(repo + RepositoryItemCount);
        nint ids = *(nint*)(repo + RepositoryItemArray);
        if (ids == 0 || count is <= 0 or > 4096)
        {
            return false;
        }

        bool present = false;
        for (int i = 0; i < count; i++)
        {
            if (*(uint*)(ids + (nint)(i * 4)) == itemId)
            {
                present = true;
                break;
            }
        }

        if (!present)
        {
            return false;
        }

        nint item = ResolveItemBase(itemId);
        if (item == 0)
        {
            return false;
        }

        PlaceableRemoveItemFn(placeable, item);
        return true;
    }

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/dialogue-consequences.md's script-item-sync round,
    /// Controls 2/3 — <c>CheatKindLootTake</c>). Finds the FIRST item id in
    /// <paramref name="placeable"/>'s repository, reads its Tag (the same resref proxy
    /// <c>TryGetItemResref</c> already relies on agent-side, cut at the first null per the
    /// project's own CExoString length trap) into <paramref name="tagDest"/> BEFORE removing —
    /// <see cref="RemoveContainerItem"/>'s underlying <c>RemoveItem</c> call deletes the object,
    /// so the tag has to be captured while it still exists — then removes it via the same real
    /// <c>RemoveItem</c> primitive <see cref="RemoveContainerItem"/> already uses. This is what
    /// makes the container's next <c>CollectContainerLootRemovals</c> scan see a genuine
    /// removal, exactly as a real take would produce; the caller
    /// (<c>CheatController.World.LootTake</c>) separately gives the same tag to the player,
    /// reproducing the other half. Returns false with nothing touched when the container is
    /// empty or its first item has no readable tag (nothing safe to give back).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool TryTakeFirstContainerItem(
        nint placeable, byte* tagDest, int tagCap, out uint itemId, out int tagLength)
    {
        itemId = 0;
        tagLength = 0;

        if (placeable == 0)
        {
            return false;
        }

        nint repo = *(nint*)(placeable + PlaceableRepository);
        if (repo == 0)
        {
            return false;
        }

        int count = *(int*)(repo + RepositoryItemCount);
        nint ids = *(nint*)(repo + RepositoryItemArray);
        if (ids == 0 || count is <= 0 or > 4096)
        {
            return false;
        }

        uint firstId = *(uint*)ids;
        if (firstId is 0 or ObjectInvalid)
        {
            return false;
        }

        nint item = ResolveItemBase(firstId);
        if (item == 0)
        {
            return false;
        }

        nint text = *(nint*)(item + ItemTag);
        uint length = *(uint*)(item + ItemTag + 4);
        if (text != 0 && length != 0)
        {
            byte* tagBytes = (byte*)text;
            int cap = Math.Min((int)length, tagCap);
            while (tagLength < cap && tagBytes[tagLength] != 0)
            {
                tagDest[tagLength] = tagBytes[tagLength];
                tagLength++;
            }
        }

        if (tagLength == 0)
        {
            LastItemResolveNote = $"0x{firstId:X} in this container has no readable tag";
            return false;
        }

        itemId = firstId;
        PlaceableRemoveItemFn(placeable, item);
        return true;
    }

    // ---- item identity ---------------------------------------------------------------
    //
    // Everything that is true of ONE item rather than of its blueprint. CreateItemOnFloor
    // takes a template name and nothing else, so an item spawned for a peer arrives stock:
    // one of it, full charges, unidentified, no upgrades. These four fields are what the
    // engine itself carries across a rebuild-from-template.
    //
    // Provenance (docs/research/item-identity.md), all read out of this binary:
    //   CSWSItem::SaveItem       0x0055CCD0 — writes StackSize from the word at +0x28C,
    //                                        Charges/MaxCharges from +0x258/+0x25C,
    //                                        Upgrades from the dword at +0x294, and the
    //                                        Identified/Stolen/NonEquippable GFF fields
    //                                        from bits 0/5/6 of +0x288.
    //   CSWSItem::LoadDataFromGff 0x0055FCD0 — the matching reader, same offsets.
    //   CSWSItem::SetIdentified  0x00554170 — eight instructions that toggle bit 0 of +0x288
    //                                        and nothing else, which is why the bit poke
    //                                        below needs no engine call.
    //   CSWSItem::HasUpgrade     0x004EADA0 — `[this+0x294] & (1 << n)`, so +0x294 is a
    //                                        MASK of installed upgrade slots.
    //   CSWGuiUpgrade            0x006C5620 — the workbench itself: LoadFromTemplate, then
    //                                        SetIdentified(1), then a raw copy of the old
    //                                        item's +0x294 and Stolen bit onto the new body,
    //                                        with no rebuild call afterwards. This IS the
    //                                        sequence below, performed by the engine.
    //
    // Nothing derived caches any of it: ComputeArmorClass (0x00553CC0) reads the BASE ITEM
    // row and GetCost (0x00554000) recomputes from the base item every call, so there is no
    // stale sibling to refresh after the poke — unlike an appearance change, which there is.

    /// <summary>CSWSItem + this = charges (int, byte-ranged).</summary>
    private const int ItemCharges = 0x258;

    /// <summary>CSWSItem + this = max charges (int, byte-ranged).</summary>
    private const int ItemMaxCharges = 0x25C;

    /// <summary>CSWSItem + this = the flag bitfield. Bit 0 identified, 5 stolen, 6 non-equippable.</summary>
    private const int ItemBitFlags = 0x288;

    /// <summary>CSWSItem + this = stack size. A dword whose high half the engine keeps zero.</summary>
    private const int ItemStackSize = 0x28C;

    /// <summary>CSWSItem + this = the installed-upgrade bitmask.</summary>
    private const int ItemUpgrades = 0x294;

    private const uint ItemBitIdentified = 1u << 0;
    private const uint ItemBitStolen = 1u << 5;
    private const uint ItemBitNonEquippable = 1u << 6;

    /// <summary>
    /// Writes one item's per-instance state onto a freshly created body, so a peer's
    /// half-spent stack of twenty arrives as twenty and their upgraded lightsaber arrives
    /// upgraded.
    ///
    /// Field writes only — no engine call, so there is no argument size to get wrong and no
    /// stack to skew. An id that does not resolve to a live CSWSItem (wrong module, item
    /// already destroyed, the pointer reused) fails the vtable check inside
    /// <see cref="ResolveItemBase"/> and this returns false having touched nothing: the item
    /// stays exactly as the engine made it, which is the old behaviour rather than a crash.
    ///
    /// Only ever reached with an identity the agent actually READ, so the values are written
    /// as they came rather than second-guessed — a stock item reads as stock and writing its
    /// own numbers back changes nothing. The single exception is a stack size of 0, which the
    /// engine never produces and which would make the item unpickable; the decoder already
    /// clamps it, and this refuses it again because it is the one field that ends up inside
    /// somebody else's world.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool ApplyItemIdentity(
        uint itemId, uint upgrades, uint stackSize, uint charges, uint maxCharges, uint flags,
        out ItemIdentityFields after)
    {
        after = default;

        nint item = ResolveItemBase(itemId);
        if (item == 0)
        {
            return false;
        }

        *(uint*)(item + ItemUpgrades) = upgrades;

        if (stackSize > 0)
        {
            *(uint*)(item + ItemStackSize) = Math.Min(stackSize, ushort.MaxValue);
        }

        *(uint*)(item + ItemMaxCharges) = maxCharges;
        *(uint*)(item + ItemCharges) = charges;

        uint bits = *(uint*)(item + ItemBitFlags);
        bits = Set(bits, ItemBitIdentified, (flags & SharedBlock.ItemIdentityIdentified) != 0);
        bits = Set(bits, ItemBitStolen, (flags & SharedBlock.ItemIdentityStolen) != 0);
        bits = Set(bits, ItemBitNonEquippable, (flags & SharedBlock.ItemIdentityNonEquippable) != 0);
        *(uint*)(item + ItemBitFlags) = bits;

        // Read back from the SAME addresses that were just written, rather than reporting the
        // values we intended. The distinction is the whole point of the out parameter: an
        // apply that logs its inputs proves only that it was asked; an apply that logs what
        // is now in the item proves the write landed where the offsets say it does — which is
        // the one claim in docs/research/item-identity.md nothing had ever measured.
        after = ReadItemFields(item);

        return true;

        static uint Set(uint bits, uint bit, bool on) => on ? bits | bit : bits & ~bit;
    }

    public static byte GetDoorOpenState(nint door) => *(byte*)(door + DoorOpenState);

    public static byte GetDoorPendingOpenState(nint door) => *(byte*)(door + DoorPendingOpenState);

    public static bool IsDoorLocked(nint door) => *(byte*)(door + DoorLocked) != 0;

    public static void UnlockDoor(nint door) => *(byte*)(door + DoorLocked) = 0;

    /// <summary>
    /// Asks the door to swing to a state through the engine's own deferred path: the
    /// animation plays and the engine commits state + walkmesh when the swing lands.
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void SetDoorOpenState(nint door, int state) => SetOpenStateFn(door, state, 1);

    /// <summary>
    /// Forces the pending open state to commit (state + walkmesh). Fallback for a swing
    /// whose commit never arrived. MAIN THREAD ONLY.
    /// </summary>
    public static void CommitDoorOpenState(nint door) => MoveToNextOpenStateFn(door);

}
