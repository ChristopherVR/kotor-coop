// Item identity and the tag/address cache it is built on: resolving an inventory slot's
// engine object id down to a blueprint resref, reading a live item's per-instance identity
// (stack size, charges, upgrades), and listing every item body the sweep can see.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    /// <summary>CSWSItem vtable; every 4-byte occurrence in private memory is an item.</summary>
    private const uint ItemVtable = ItemIdentityLayout.ItemVtable;

    /// <summary>objectId -&gt; lowercased tag, rebuilt when an id is not found.</summary>
    private readonly Dictionary<uint, string> _itemTags = [];

    /// <summary>objectId -&gt; the CSWSItem BASE address the same scan found it at, so an
    /// item's per-instance state can be read without a second sweep. Kept beside the tag
    /// map rather than folded into it because the two have different lifetimes in practice:
    /// a tag is cached for the whole module, an address is only worth believing while the
    /// vtable at it still says "item".</summary>
    private readonly Dictionary<uint, nint> _itemAddresses = [];

    /// <summary>
    /// Reads the tag of the item equipped in an inventory slot, lowercased so it can be
    /// used as a blueprint resref on the far side. Empty when the slot is empty or the
    /// item cannot be resolved.
    ///
    /// ⚠ A CSWInventory slot holds an OBJECT ID, not a pointer. Measured live: the PC's
    /// body and right-weapon slots read 0xC9 and 0xCA. Dereferencing them as pointers
    /// silently produced empty strings for every slot, which looked exactly like "the
    /// player has nothing equipped" — the first live run of equipment sync published
    /// three empty resrefs and nothing was ever equipped on a phantom.
    ///
    /// Ids are resolved through a tag cache built by scanning for the CSWSItem vtable,
    /// because the external agent has no GetGameObject. Both item layouts are accepted
    /// (id at +0x14 = CSWSItem base, at +0x04 = the CSWSObject inside it).
    /// </summary>
    private string ReadEquippedResref(nint creature, int slotOffset)
    {
        if (!_memory.TryReadPointer(creature + OffInventory, out nint inventory) || inventory == 0
            || !_memory.TryReadUInt32(inventory + slotOffset, out uint itemId)
            || itemId is 0 or >= 0x7F000000)
        {
            return string.Empty;
        }

        if (_itemTags.TryGetValue(itemId, out string? cached))
        {
            return cached;
        }

        // ⚠ A MISS MUST NOT COST A SCAN. RebuildItemTagCache walks the game's committed
        // memory looking for the item vtable; this method runs three times per tick (body,
        // right hand, left hand), so a single id that never resolves — an empty slot holding
        // a stale id, or an item whose tag cannot be read — meant three full memory scans
        // EVERY TICK. That pinned a CPU core, dropped the pump from 60 Hz to about 4, and
        // measured end to end as a peer taking 3-7 seconds to move. It was the single
        // biggest cause of the "everything lags by seconds" report.
        //
        // So: rate-limit rebuilds regardless of how many lookups miss (below), rather than
        // remembering which specific ids missed.
        //
        // ⚠ THIS USED TO ALSO PERMANENTLY BLACKLIST A MISSED id (a HashSet<uint>, checked and
        // added to right here, short-circuiting every future lookup for that id before ever
        // reaching the rebuild below). That is wrong for a GENUINELY NEW item: `companion_
        // gear_sync`'s live-verification round (docs/research/companion-state-sync.md)
        // observed a freshly created item's id miss on the very first read after
        // `EquipItemFromTemplate` created it — before the periodic scan below had a chance to
        // include it — and then never resolve again for the rest of the session, because the
        // blacklist check returned early on every subsequent tick and no code path ever
        // retried it. The time-gate immediately below already does the ONLY thing the
        // blacklist was for (stopping a miss from costing a scan on every tick); removing the
        // blacklist costs nothing extra and lets a new item resolve on the next scheduled scan
        // instead of never.
        long nowMs = Environment.TickCount64;
        if (nowMs - _itemTagsBuiltAtMs < ItemTagRebuildMs)
        {
            return string.Empty;
        }

        _itemTagsBuiltAtMs = nowMs;
        RebuildItemTagCache();

        return _itemTags.TryGetValue(itemId, out string? found) ? found : string.Empty;
    }

    /// <summary>Minimum gap between item-tag scans, however many lookups miss.</summary>
    private const int ItemTagRebuildMs = 15_000;

    /// <summary>Which module the address caches were built for.</summary>
    private string _cachedForModule = string.Empty;
    private long _itemTagsBuiltAtMs;

    /// <summary>
    /// Trims a tag down to a usable blueprint resref: lowercase, and cut at the first
    /// character a resref cannot contain. A CExoString's length can include the
    /// terminating null (and occasionally trailing junk), which produced resrefs like
    /// "g_a_clothes01\0" — the engine's CreateItemOnObject then found no blueprint and
    /// the equip was refused with failure 7 while the name LOOKED right in the log.
    /// </summary>
    private static string SanitiseResref(string tag)
    {
        int end = 0;
        while (end < tag.Length && end < 16)
        {
            char c = char.ToLowerInvariant(tag[end]);
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                break;
            }

            end++;
        }

        return tag[..end].ToLowerInvariant();
    }

    /// <summary>
    /// How many item bodies one sweep will record.
    ///
    /// ⚠ This is a TRUNCATION, not a budget, and it used to be 512 with nothing saying so.
    /// The scanner stops the moment it has that many hits — mid-region, in ascending address
    /// order — so every item allocated above the cut is invisible for the life of the cache,
    /// and the caller's only symptom is "that id is not an item here". A module with a
    /// populated party pool and a few looted containers gets close to 512 on its own, and the
    /// item a test cares about is usually the one allocated LAST.
    ///
    /// 4096 is chosen to be past anything a KOTOR module holds rather than to be a limit that
    /// bites. The cost of raising it is bounded: the sweep already runs to completion whenever
    /// the game holds fewer items than the cap, which is the normal case, so this changes the
    /// work done only in the situation where the old number was giving a wrong answer.
    /// <see cref="LastItemScanNote"/> reports when the cap is reached anyway, because a silent
    /// truncation is the failure this constant exists to make visible.
    /// </summary>
    private const int ItemScanLimit = 4096;

    /// <summary>
    /// What the last item sweep actually saw: how many CSWSItem bodies, how many carried a
    /// readable tag, whether the cap truncated it. Read by the console's `items` verb and by
    /// the drop refusal, so an unattended run can tell "your id is wrong" from "the sweep
    /// never got that far".
    /// </summary>
    public string LastItemScanNote { get; private set; } = "no item sweep yet";

    private void RebuildItemTagCache()
    {
        _itemTags.Clear();
        _itemAddresses.Clear();

        BytePattern pattern = BytePattern.FromBytes(BitConverter.GetBytes(ItemVtable));
        PatternScanner scanner = new(_memory);

        List<nint> hits = scanner.ScanAll(pattern, ScanScope.PrivateCommitted, ItemScanLimit);
        int duplicates = 0;

        foreach (nint address in hits)
        {
            uint id;
            nint tagAddress;

            if (_memory.TryReadUInt32(address + 0x14, out id) && id is > 0 and < 0x7F000000)
            {
                tagAddress = address + 0x28;
            }
            else if (_memory.TryReadUInt32(address + 0x04, out id) && id is > 0 and < 0x7F000000)
            {
                tagAddress = address + 0x18;
            }
            else
            {
                continue;
            }

            // A second body claiming an id we already have is worth counting rather than
            // silently overwriting: a picked-up item really is freed and its memory reused
            // (Odyssey.GetItemFloorState documents the same hazard), so a duplicate is the
            // signature of a stale copy sitting in freed heap. The read re-checks the vtable
            // and the id at the moment of reading, which is what keeps a stale winner from
            // being believed — but if this number is ever non-zero live, the read path's
            // "last writer wins" is worth revisiting.
            if (_itemAddresses.ContainsKey(id))
            {
                duplicates++;
            }

            // The scan hit the vtable POINTER, which is the CSWSItem base by construction —
            // the vtable is at +0x00. That is the address the per-instance state fields are
            // measured from, so record it whether or not the tag reads.
            _itemAddresses[id] = address;

            string tag = SanitiseResref(ReadExoString(tagAddress));
            if (tag.Length > 0)
            {
                _itemTags[id] = tag;
            }
        }

        LastItemScanNote =
            $"{_itemAddresses.Count} item bodies, {_itemTags.Count} with a readable tag"
            + (duplicates > 0 ? $", {duplicates} duplicate id(s)" : string.Empty)
            + (hits.Count >= ItemScanLimit
                ? $" — ⚠ the sweep STOPPED at its {ItemScanLimit} cap, so items above that "
                  + "address are missing"
                : string.Empty);
    }

    /// <summary>
    /// Makes sure the item caches have had a chance to see <paramref name="itemObjectId"/>,
    /// rebuilding them once if it is not there yet.
    ///
    /// ⚠ THIS IS THE FIX FOR THE BUG THAT BLOCKED ITEM IDENTITY (docs/live-verification.md,
    /// 2026-08-15). Every lookup used to reach the caches directly, and only ONE of them —
    /// <see cref="TryReadItemIdentity"/> — could trigger a rebuild. The drop path asks for
    /// the resref first and the identity second, joined by <c>||</c>, so a miss on the resref
    /// short-circuited and the rebuild never ran. The caches were last built when the agent
    /// attached; an item spawned a minute later was therefore permanently unknown, which is
    /// exactly what `drop #131 -> object 0x131 is not a readable item here` was reporting for
    /// a ground body the plugin log had just announced creating.
    ///
    /// The rate limit stays, and stays for the reason it was added: a sweep of the game's
    /// committed memory per miss per tick once dropped the pump to about 4 Hz and measured
    /// as peers lagging by seconds. A caller that must not wait (an interactive console verb)
    /// passes <paramref name="force"/>.
    /// </summary>
    private bool EnsureItemKnown(uint itemObjectId, bool force = false)
    {
        if (_itemAddresses.ContainsKey(itemObjectId))
        {
            return true;
        }

        long nowMs = Environment.TickCount64;
        if (!force && nowMs - _itemTagsBuiltAtMs < ItemTagRebuildMs)
        {
            return false;
        }

        _itemTagsBuiltAtMs = nowMs;
        RebuildItemTagCache();
        return _itemAddresses.ContainsKey(itemObjectId);
    }

    /// <summary>
    /// Reads one live item's per-instance state — stack size, charges, upgrade mask and the
    /// identified/stolen bits — so a drop can carry THIS item rather than its blueprint.
    ///
    /// Pure reads: no engine call, nothing written, so a wrong address costs a false answer
    /// and never a crash in the game. The vtable is re-checked at the moment of reading
    /// rather than trusted from the scan, because an item picked up and merged into a stack
    /// really is freed and its memory reused (see <c>Odyssey.GetItemFloorState</c>'s note on
    /// the same hazard). A miss returns <see cref="ItemIdentity.Unknown"/>, which every
    /// consumer treats as "leave the far side's fresh item alone".
    /// </summary>
    public bool TryReadItemIdentity(uint itemObjectId, out ItemIdentity identity) =>
        TryReadItemIdentity(itemObjectId, out identity, out _, force: false);

    /// <summary>
    /// As <see cref="TryReadItemIdentity(uint, out ItemIdentity)"/>, but says why it refused
    /// and can insist on a fresh sweep.
    ///
    /// The <paramref name="why"/> sentence is the difference between a diagnosable refusal
    /// and "nothing happened": it names which of the three gates fired — the id is outside
    /// the engine's range, no body with that id was swept up (and what the sweep did see), or
    /// the address we remembered no longer looks like that item.
    /// </summary>
    public bool TryReadItemIdentity(
        uint itemObjectId, out ItemIdentity identity, out string why, bool force)
    {
        identity = ItemIdentity.Unknown;

        if (itemObjectId is 0 or >= 0x7F000000)
        {
            why = $"0x{itemObjectId:X} is not an engine object id";
            return false;
        }

        if (!EnsureItemKnown(itemObjectId, force) || !_itemAddresses.TryGetValue(itemObjectId, out nint address))
        {
            why = $"no item body with id 0x{itemObjectId:X} in this process ({LastItemScanNote})";
            return false;
        }

        // Re-checked HERE rather than trusted from the sweep: an item picked up and merged
        // into a stack the picker already carried really is freed, and its memory reused (the
        // same hazard Odyssey.GetItemFloorState documents). A stale address costs a false
        // "unknown", never a bad write.
        bool vtableRead = _memory.TryReadUInt32(address, out uint vtable);
        bool idRead = _memory.TryReadUInt32(address + 0x14, out uint id);

        if (!vtableRead || vtable != ItemVtable || !idRead || id != itemObjectId)
        {
            _itemAddresses.Remove(itemObjectId);
            _itemTags.Remove(itemObjectId);
            why = $"0x{itemObjectId:X} was at 0x{address:X}, which now reads vtable 0x{vtable:X} "
                + $"id 0x{id:X} — the item was freed and its memory reused";
            return false;
        }

        byte[] window = new byte[ItemIdentityLayout.WindowLength];
        if (!_memory.TryRead(address + ItemIdentityLayout.WindowStart, window))
        {
            why = $"could not read 0x{address + ItemIdentityLayout.WindowStart:X}"
                + $"..+0x{ItemIdentityLayout.WindowLength:X}";
            return false;
        }

        identity = ItemIdentityLayout.Decode(window);
        why = string.Empty;
        return identity.IsKnown;
    }

    /// <summary>The blueprint resref of a live item, if the tag scan has seen it. Empty when
    /// it has not — the caller must not invent one.</summary>
    public string ItemResref(uint itemObjectId) =>
        _itemTags.TryGetValue(itemObjectId, out string? tag) ? tag : string.Empty;

    /// <summary>
    /// The blueprint resref of a live item, sweeping for it if the caches have not seen it.
    ///
    /// ⚠ Use this and not <see cref="ItemResref"/> whenever the id came from OUTSIDE the
    /// caches — a console line, a plugin log, a peer. <see cref="ItemResref"/> is a pure
    /// cache read that cannot populate itself, and a caller that reached for it first is
    /// how an id the sweep had simply never seen came back as "not an item here" for ever.
    ///
    /// The resref is really the item's TAG, cut and lowercased. That is a proxy and it is
    /// worth naming as one: a live CSWSItem does not retain its template name at all
    /// (CSWSItem::SaveItem 0x0055CCD0 writes BaseItem and the property list, never a
    /// TemplateResRef — an item is rebuilt from its own fields, not from its blueprint). For
    /// base-game items the tag IS the resref in upper case ("G_W_BLSTRPSTL001"), which is
    /// what makes the proxy work; a mod that tags items differently breaks it, in the same
    /// way and for the same reason every other blueprint-keyed channel here breaks.
    /// </summary>
    /// <summary>The <see cref="IItemBridge"/> member: same lookup, cache-only (no forced
    /// sweep) — a pump resolving a container/corpse-loot removal's resref runs every tick
    /// this scan is throttled to, so it should never itself trigger the sweep
    /// <paramref name="force"/> would.</summary>
    bool IItemBridge.TryGetItemResref(uint itemObjectId, out string resref) =>
        TryGetItemResref(itemObjectId, out resref);

    public bool TryGetItemResref(uint itemObjectId, out string resref, bool force = false)
    {
        if (_itemTags.TryGetValue(itemObjectId, out string? cached))
        {
            resref = cached;
            return true;
        }

        EnsureItemKnown(itemObjectId, force);

        if (_itemTags.TryGetValue(itemObjectId, out string? tag))
        {
            resref = tag;
            return true;
        }

        // Last resort, and the only one that works for a body WE asked the plugin to make.
        return TryGetSpawnedItemResref(itemObjectId, out resref);
    }

    /// <summary>
    /// The resref the plugin was told to spawn for the body now living at
    /// <paramref name="objectId"/>, recovered from the item slot we wrote it into.
    ///
    /// ⚠ THIS IS WHAT MAKES A REPLICATED ITEM RE-SENDABLE. The wire carries a blueprint name,
    /// the only name a live <c>CSWSItem</c> retains is its Tag, and a body the engine created
    /// from a template it could not find has no Tag at all — so the one item an unattended run
    /// can reliably manufacture was the one item that could not travel (measured on the rig,
    /// 2026-08-15: `dropitem 0x131` refused with "its tag is unreadable").
    ///
    /// There is no need to guess. The agent WROTE the resref into <c>slot + 0x14</c> and the
    /// plugin wrote the object id it produced into <c>status + 0x04</c>, so the mapping from
    /// body back to blueprint is a fact we already hold — better evidence than the tag proxy,
    /// which is only ever an approximation of the resref (docs/research/item-identity.md §8.2).
    /// </summary>
    private bool TryGetSpawnedItemResref(uint objectId, out string resref)
    {
        resref = string.Empty;

        if (_pluginBlock == 0 || objectId is 0 or ObjectInvalid)
        {
            return false;
        }

        for (int i = 0; i < BlockItemCapacity; i++)
        {
            nint status = _pluginBlock + BlockItemStatus + (i * BlockItemStatusStride);

            if (!_memory.TryReadUInt32(status + 0x08, out uint state) || state != ItemStateAlive
                || !_memory.TryReadUInt32(status + 0x04, out uint body) || body != objectId)
            {
                continue;
            }

            nint slot = _pluginBlock + BlockItemSlots + (i * BlockItemSlotStride);
            if (_memory.TryReadAsciiString(slot + 0x14, BlockItemResrefLength, out resref)
                && resref.Length > 0)
            {
                return true;
            }
        }

        resref = string.Empty;
        return false;
    }

    /// <summary>
    /// One live item as a console listing wants it: what it is, whose it is, and what makes
    /// it different from its blueprint.
    /// </summary>
    /// <param name="ObjectId">The engine object id — the handle every other verb takes.</param>
    /// <param name="Resref">The item's tag, cut and lowercased; empty when unreadable.</param>
    /// <param name="Possessor">Who is holding it, or <c>OBJECT_INVALID</c> when it is lying
    /// on the floor. <c>CSWSCreature::AIActionPickUpItem</c> gates a pickup on exactly this
    /// field being <c>OBJECT_INVALID</c>, so it is the engine's own answer rather than a
    /// proxy for one.</param>
    public readonly record struct ItemListing(
        uint ObjectId, string Resref, uint Possessor, ItemIdentity Identity)
    {
        /// <summary>True when this item differs from a freshly created blueprint in a way the
        /// identity channel carries — which is what makes it worth picking for a test.</summary>
        public bool IsInteresting =>
            Identity.IsKnown
            && (Identity.StackSize > 1 || Identity.Charges > 0 || Identity.Upgrades != 0);

        /// <summary>Nobody is holding it. A zero possessor counts too: the field is only ever
        /// <c>OBJECT_INVALID</c> or a real id, so zero means the read did not land and "on the
        /// floor" is the honest, harmless reading of that.</summary>
        public bool OnFloor => Possessor is 0 or ObjectInvalid;
    }

    /// <summary>CSWSItem + this = possessor object id; OBJECT_INVALID means "on the floor".
    /// Same field the plugin's floor probe reads, from the constructor's own default write at
    /// 0x0055315F.</summary>
    private const int ItemPossessor = 0x268;

    /// <summary>The engine's OBJECT_INVALID. Public because <see cref="ItemListing"/> exposes
    /// a possessor id and a caller reading one has to be able to recognise "nobody".</summary>
    public const uint ObjectInvalid = 0x7F000000;

    /// <summary>
    /// Every item body the sweep can see, with its identity, newest sweep first.
    ///
    /// This exists so an unattended run can PICK a target instead of guessing one. The apply
    /// path can only be exercised by sending an item that genuinely differs from its
    /// blueprint, and until this verb there was no way to find out which of the hundreds of
    /// items in a loaded module those are — the harness dropped a resref, which by
    /// construction has no identity to read.
    ///
    /// Forces a sweep, because a person typing `items` is asking about NOW and the fifteen
    /// second rate limit exists to protect the 60 Hz pump, not this. The sweep walks the
    /// game's committed memory and is not free — call it from a command surface, never from
    /// the pump.
    /// </summary>
    public IReadOnlyList<ItemListing> ListItems()
    {
        _itemTagsBuiltAtMs = Environment.TickCount64;
        RebuildItemTagCache();

        List<ItemListing> listings = new(_itemAddresses.Count);

        // ⚠ Iterate a SNAPSHOT. TryReadItemIdentity evicts an address whose vtable or id no
        // longer matches — which is the right thing for it to do, and would throw here if the
        // loop were walking the dictionary itself.
        foreach ((uint objectId, nint address) in _itemAddresses.ToArray())
        {
            if (!TryReadItemIdentity(objectId, out ItemIdentity identity, out _, force: false))
            {
                continue;
            }

            uint possessor = _memory.TryReadUInt32(address + ItemPossessor, out uint holder)
                ? holder
                : ObjectInvalid;

            // The spawn-record fallback too, so a body the plugin made for us shows the
            // blueprint it was made from rather than "<no tag>" — that name is the one thing
            // a re-drop needs, and the listing is where the operator picks a target.
            TryGetItemResref(objectId, out string resref);

            listings.Add(new ItemListing(objectId, resref, possessor, identity));
        }

        // The interesting ones first — an item with a stack, charges or an upgrade mask is
        // the whole reason anyone runs this — then by id so the order is stable between runs.
        listings.Sort((a, b) => a.IsInteresting != b.IsInteresting
            ? (a.IsInteresting ? -1 : 1)
            : a.ObjectId.CompareTo(b.ObjectId));

        return listings;
    }

    /// <summary>
    /// One sweep of <see cref="ListItems"/>, filtered to whichever of <paramref
    /// name="possessorIds"/> actually hold something. See the interface doc on
    /// <see cref="IGameBridge.ReadPossessedItems"/> for why this takes a SET rather than being
    /// called once per creature.
    /// </summary>
    public IReadOnlyList<(uint Possessor, string Resref, ItemIdentity Identity)> ReadPossessedItems(
        IReadOnlySet<uint> possessorIds)
    {
        if (possessorIds.Count == 0)
        {
            return [];
        }

        List<(uint, string, ItemIdentity)> result = [];
        foreach (ItemListing item in ListItems())
        {
            if (possessorIds.Contains(item.Possessor))
            {
                result.Add((item.Possessor, item.Resref, item.Identity));
            }
        }

        return result;
    }

}
