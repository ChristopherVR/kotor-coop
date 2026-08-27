// Odyssey.PartyPool.cs - the shared party item pool (CSWPartyTable+0x118's CItemRepository).
// See docs/research/party-pool-sync.md for the full research this implements.
using System.Text;

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- party pool ---------------------------------------------------------------
    //
    // CSWPartyTable+0x118 (pt_item_repo) -> CItemRepository*, the SAME class a placeable's
    // container uses (Odyssey.Items.cs's PlaceableRepository field) — items_list at +0x0C,
    // item_count at +0x10. VERIFIED: tools/kotor1_0_3.db offset 280 = 0x118, cross-checked
    // against CSWPartyTable::GetPartyItems 0x00563340's own `mov eax,[edi+0x118]`.

    /// <summary>CServerExoAppInternal + this = the embedded CSWPartyTable. Mirrors
    /// Odyssey.Journal.cs's PartyTableOffset (private, so redeclared here rather than shared
    /// across files that were never meant to depend on each other's internals).</summary>
    private const int PartyPoolTableOffset = 0x1B770;

    private const int PartyTableItemRepo = 0x118;

    /// <summary>CSWSCreature + this = the creature's OWN backpack CItemRepository* (distinct
    /// from the equip-slot CSWInventory* at +0xA2C). VERIFIED, docs/research/item-identity.md
    /// §"creature backpack".</summary>
    private const int CreatureItemRepo = 0xA30;

    /// <summary>
    /// CSWPartyTable::GetPartyItems 0x00563340 — VERIFIED, full disasm. `__thiscall(this)`, no
    /// stack args, returns the repository pointer, LAZILY CONSTRUCTING it (via
    /// CItemRepository::Constructor 0x0055D290, parent id = CServerExoApp::GetPlayerCreatureId)
    /// the first time it is asked for on a fresh game. Reading +0x118 directly is fine once the
    /// repository already exists (the read side, KotorGameBridge.PartyPool.cs, does exactly
    /// that — it cannot make engine calls); calling the real accessor here, on the write side,
    /// guarantees a repository exists to insert into rather than risking a null deref on the
    /// very first item anyone ever deposits.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetPartyItemsFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x00563340;

    /// <summary>
    /// CItemRepository::RemoveItem(CSWSItem*) 0x00555FD0 — VERIFIED, full disasm. `__thiscall
    /// (this=repo)`, one stack arg (the item pointer, NOT an id). Reads the item's own object
    /// id off it, linear-scans <c>items_list</c> for a match, unlinks it and shrinks the array
    /// if found. Returns nonzero on a genuine removal, zero as a safe no-op when the item was
    /// never in this repository — the exact primitive <c>CSWSPlaceable::RemoveItem</c>
    /// 0x00584AC0 (Odyssey.Items.cs's <c>PlaceableRemoveItemFn</c>) calls internally after its
    /// own membership pre-check, reused here directly since the party pool has no owning
    /// <c>CSWSPlaceable</c> to route through.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int> RepositoryRemoveItemFn =
        (delegate* unmanaged[Thiscall]<nint, nint, int>)0x00555FD0;

    /// <summary>
    /// CItemRepository::InsertItem(CSWSItem*, int index) 0x0055D2C0 — VERIFIED, full disasm.
    /// `__thiscall(this=repo)`, two stack args (item pointer, index). An index outside
    /// <c>[0, item_count)</c> — this codebase always passes -1 — takes the APPEND path: reads
    /// the item's own object id off it, grows the backing array through
    /// <c>CExoArrayList::Allocate</c> 0x005E03C0 if needed, and appends. The minimal "link this
    /// item's id into that repository's array" primitive <c>CItemRepository::AddItem</c>
    /// 0x0055D330 itself eventually calls — used here directly rather than the heavier AddItem,
    /// which additionally special-cases credit-chit/Pazaak-card item types (consuming the item
    /// into gold/a card instead of linking it) and dispatches a "you received an item" GUI
    /// message whose recipient-resolution path was not traced far enough to trust unattended.
    /// See docs/research/party-pool-sync.md's design section for the full reasoning.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, void> RepositoryInsertItemFn =
        (delegate* unmanaged[Thiscall]<nint, nint, int, void>)0x0055D2C0;

    /// <summary>
    /// CSWSItem::SetPossessor(uint newPossessor, int, int, uint) 0x00553210 — VERIFIED shape
    /// (symbol DB: __thiscall, 16 bytes of stack args = 4 uint/int params) and VERIFIED call
    /// site: <c>CSWSPlaceable::RemoveItem</c> calls it as
    /// <c>(item, OBJECT_INVALID, 1, 1, OBJECT_INVALID)</c> immediately after unlinking an item
    /// from a container's repository. The middle two arguments were not individually traced
    /// (the function itself builds a possession-changed GUI message, the same local,
    /// no-network client/server message bus <c>OpenInventory</c>/<c>CloseInventory</c> already
    /// use — see docs/research/container-sync.md §1.3-1.4); this reuses the EXACT literal
    /// argument shape the engine's own removal path uses rather than guessing new ones.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int, int, uint, void> SetPossessorFn =
        (delegate* unmanaged[Thiscall]<nint, uint, int, int, uint, void>)0x00553210;

    /// <summary>Why the last party-pool call refused, in words. Same "a guard that fails safe
    /// still has to say what it saw" discipline as <see cref="LastItemResolveNote"/>.</summary>
    public static string LastPartyPoolNote = string.Empty;

    /// <summary>Resolves the party pool's repository pointer, constructing it through the
    /// engine's own lazy accessor if it does not exist yet. Returns 0 (with
    /// <see cref="LastPartyPoolNote"/> set) when there is no world loaded.</summary>
    private static nint ResolvePartyPoolRepo()
    {
        nint server = GetServer();
        if (server == 0)
        {
            LastPartyPoolNote = "no server (not in a world)";
            return 0;
        }

        nint internalServer = *(nint*)(server + AppServerOffsetInternal);
        if (internalServer == 0)
        {
            LastPartyPoolNote = "no server internal";
            return 0;
        }

        nint partyTable = internalServer + PartyPoolTableOffset;
        nint repo = GetPartyItemsFn(partyTable);
        if (repo == 0)
        {
            LastPartyPoolNote = "CSWPartyTable::GetPartyItems returned null";
            return 0;
        }

        return repo;
    }

    /// <summary>
    /// True when a live item's Tag matches <paramref name="resref"/>, case-insensitively. The
    /// tag IS the resref proxy this whole codebase already relies on for a live item (see
    /// docs/research/item-identity.md §8.2) — cut at the first null, per the project's own
    /// CExoString length trap (a reported length routinely includes the terminator).
    /// </summary>
    private static bool TagEqualsIgnoreCase(nint item, byte* resref, int resrefLength)
    {
        nint text = *(nint*)(item + ItemTag);
        uint length = *(uint*)(item + ItemTag + 4);

        if (text == 0 || length == 0)
        {
            return false;
        }

        byte* tagBytes = (byte*)text;
        int cap = (int)Math.Min(length, 64u);
        int tagLength = 0;
        while (tagLength < cap && tagBytes[tagLength] != 0)
        {
            tagLength++;
        }

        if (tagLength != resrefLength)
        {
            return false;
        }

        for (int i = 0; i < tagLength; i++)
        {
            byte a = tagBytes[i];
            byte b = resref[i];
            if (a is >= (byte)'A' and <= (byte)'Z')
            {
                a += 32;
            }

            if (b is >= (byte)'A' and <= (byte)'Z')
            {
                b += 32;
            }

            if (a != b)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The item's raw engine +0x288 bits (<see cref="ItemBitIdentified"/> etc.,
    /// bits 0/5/6), as the wire's <c>ItemIdentity*</c> flag bits (bits 1/2/3) — the same
    /// mapping <c>CheatController.ToWireFlags</c> performs, duplicated here rather than shared
    /// across classes for one small, stable conversion.</summary>
    private static uint EngineBitsToWireFlags(uint bits)
    {
        uint flags = 0;

        if ((bits & ItemBitIdentified) != 0)
        {
            flags |= SharedBlock.ItemIdentityIdentified;
        }

        if ((bits & ItemBitStolen) != 0)
        {
            flags |= SharedBlock.ItemIdentityStolen;
        }

        if ((bits & ItemBitNonEquippable) != 0)
        {
            flags |= SharedBlock.ItemIdentityNonEquippable;
        }

        return flags;
    }

    /// <summary>True when a resolved item's non-stack identity (charges/max/upgrades/flags)
    /// matches the requested stack key.</summary>
    private static bool StackIdentityMatches(
        in ItemIdentityFields fields, uint charges, uint maxCharges, uint upgrades, uint flags)
    {
        uint wireBits = EngineBitsToWireFlags(fields.BitFlags);
        uint wireBitsOnly = flags & (SharedBlock.ItemIdentityIdentified | SharedBlock.ItemIdentityStolen
            | SharedBlock.ItemIdentityNonEquippable);

        return fields.Charges == charges && fields.MaxCharges == maxCharges
            && fields.Upgrades == upgrades && wireBits == wireBitsOnly;
    }

    /// <summary>
    /// Adds <paramref name="count"/> units of a resref+identity stack to the shared party pool:
    /// spawns the item straight into the local player's own inventory (the same
    /// <c>CreateItemOnObject</c> VM routine <see cref="GiveItemToInventory"/> already wraps —
    /// there is no VM path that targets the pool directly, since it is not a scriptable
    /// object), applies its identity, then unlinks it from the player's own backpack and links
    /// it into the pool's repository. No floor placement, no duplicate object — the item never
    /// exists anywhere except (briefly) the player's own pack and then the pool.
    ///
    /// The item's possessor field is left exactly as <c>CreateItemOnObject</c> set it (the
    /// local player's own object id) rather than cleared to OBJECT_INVALID: an item sitting in
    /// the party pool's repository is, per <c>CSWPartyTable::GetPartyItems</c>'s own
    /// constructor call, "owned" by the current player creature — leaving the field alone
    /// matches that invariant rather than fighting it. NEEDS-LIVE (docs/research/
    /// party-pool-sync.md): the possessor semantics of a genuine hand-deposited stash item were
    /// not independently observed.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool AddItemToPartyPool(
        uint playerObjectId, byte* resrefAscii, int resrefLength, int count,
        uint upgrades, uint charges, uint maxCharges, uint flags,
        out uint itemId, out ItemIdentityFields after)
    {
        itemId = 0;
        after = default;
        LastPartyPoolNote = string.Empty;

        nint repo = ResolvePartyPoolRepo();
        if (repo == 0)
        {
            return false;
        }

        itemId = GiveItemToInventory(playerObjectId, resrefAscii, resrefLength, count, out int failure);
        if (itemId is 0 or ObjectInvalid)
        {
            LastPartyPoolNote = $"did not spawn (failure {failure})";
            return false;
        }

        if (!TryReadItemIdentityFields(itemId, out ItemIdentityFields spawned) || spawned.LooksUnloaded)
        {
            LastPartyPoolNote = $"0x{itemId:X} spawned EMPTY — not a blueprint here";
            return false;
        }

        if (!ApplyItemIdentity(
                itemId, upgrades, (uint)count, charges, maxCharges,
                flags | SharedBlock.ItemIdentityKnown, out after))
        {
            LastPartyPoolNote = $"identity apply failed for 0x{itemId:X}: {LastItemResolveNote}";
            return false;
        }

        nint player = GetGameObjectFn(GetServer(), playerObjectId);
        nint playerRepo = player == 0 ? 0 : *(nint*)(player + CreatureItemRepo);
        nint item = ResolveItemBase(itemId);

        if (playerRepo == 0 || item == 0)
        {
            LastPartyPoolNote = $"0x{itemId:X} spawned but could not resolve player backpack/item base";
            return false;
        }

        // Unlink from the player's own backpack, link into the pool. Deliberately NOT
        // calling SetPossessor — see this method's own remarks.
        RepositoryRemoveItemFn(playerRepo, item);
        RepositoryInsertItemFn(repo, item, -1);

        return true;
    }

    /// <summary>
    /// Removes <paramref name="countToRemove"/> units of a resref+identity stack from the
    /// shared party pool, greedily over whichever live items currently match (by Tag and
    /// identity), reducing an oversized stack in place or unlinking a whole matching item when
    /// it is entirely consumed. <see cref="CSWSItem.SetPossessor"/> ("SetPossessorFn") marks
    /// every fully-unlinked item OBJECT_INVALID afterward, matching
    /// <c>CSWSPlaceable::RemoveItem</c>'s own post-removal step — the item becomes an inert
    /// orphan on this instance, never referenced again, the same fate a container-loot removal
    /// already gives a picked-up item on a peer's copy.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool RemovePartyPoolStack(
        byte* resrefAscii, int resrefLength, int countToRemove,
        uint charges, uint maxCharges, uint upgrades, uint flags, out int removed)
    {
        removed = 0;
        LastPartyPoolNote = string.Empty;

        nint repo = ResolvePartyPoolRepo();
        if (repo == 0)
        {
            return false;
        }

        int count = *(int*)(repo + RepositoryItemCount);
        nint ids = *(nint*)(repo + RepositoryItemArray);
        if (ids == 0 || count is <= 0 or > 4096)
        {
            LastPartyPoolNote = "party pool is empty";
            return false;
        }

        // Snapshot the ids first: RepositoryRemoveItemFn mutates the array (shifts entries
        // down), so walking it live while removing from it would skip or re-visit entries.
        uint[] snapshot = new uint[Math.Min(count, 4096)];
        for (int i = 0; i < snapshot.Length; i++)
        {
            snapshot[i] = *(uint*)(ids + (nint)(i * 4));
        }

        // Built once, not per candidate — see PartyPoolStackMatcher's own remarks for why the
        // spawn ring (a point-in-time capture, immune to a just-inserted item's still-settling
        // Tag) is the primary match signal and Tag only a fallback when the ring knows nothing
        // about a given item's id.
        string targetResref = Encoding.ASCII.GetString(resrefAscii, resrefLength);

        foreach (uint id in snapshot)
        {
            if (removed >= countToRemove)
            {
                break;
            }

            nint item = ResolveItemBase(id);
            if (item == 0)
            {
                continue;
            }

            string? spawnRingResref = ItemTemplateSpawnHook.TryGetResRef(id);
            bool tagMatches = TagEqualsIgnoreCase(item, resrefAscii, resrefLength);
            if (!PartyPoolStackMatcher.Matches(spawnRingResref, targetResref, tagMatches))
            {
                continue;
            }

            ItemIdentityFields fields = ReadItemFields(item);
            if (!StackIdentityMatches(fields, charges, maxCharges, upgrades, flags))
            {
                continue;
            }

            int stackSize = Math.Max(1, (int)fields.StackSize);
            int take = Math.Min(stackSize, countToRemove - removed);

            if (take >= stackSize)
            {
                RepositoryRemoveItemFn(repo, item);
                SetPossessorFn(item, ObjectInvalid, 1, 1, ObjectInvalid);
            }
            else
            {
                *(uint*)(item + ItemStackSize) = (uint)(stackSize - take);
            }

            removed += take;
        }

        if (removed == 0)
        {
            LastPartyPoolNote = "no matching stack found in the pool";
            return false;
        }

        return true;
    }
}
