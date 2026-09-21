// Item identity and the tag/address cache it is built on: resolving an inventory slot's
// engine object id down to a blueprint resref, reading a live item's per-instance identity
// (stack size, charges, upgrades), and listing every item body the sweep can see.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    /// <summary>Point-of-use liveness gate for a scan row.  A vtable hit can be a freed heap
    /// body whose old id/tag bytes have not been overwritten yet; the server object array is
    /// the engine's authoritative id-to-body map.</summary>
    internal static bool IsLiveServerItem(bool serverArrayResolved, nint scannedAddress, nint serverAddress) =>
        serverArrayResolved && scannedAddress != 0 && scannedAddress == serverAddress;

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
    /// Ids are resolved through <see cref="TryGetItemResref"/> — the SAME (spawn-cache-first,
    /// then Tag-cache, then a rate-limited rescan) precedence every other resref consumer in
    /// this bridge uses, because the external agent has no GetGameObject.
    ///
    /// ⚠ ROOT CAUSE, 2026-08-28: this method used to duplicate only HALF of that precedence —
    /// it went straight to <c>_itemTags</c> (the vtable/Tag scan) and never consulted
    /// <c>_spawnedItemResrefs</c> (fed by <see cref="RememberSpawnedItemResref"/>, including
    /// the <c>ItemTemplateSpawnHook</c> ring — docs/research/item-identity.md §13 — the instant
    /// a companion's re-equip creates a brand-new item via <c>CreateItemOnObject</c>). A
    /// mid-session re-equip's item id is, by construction, one the vtable scan has never seen;
    /// this method's own 15-second rebuild gate then denied it a chance to resolve until the
    /// NEXT whole-memory rescan — live-observed (docs/research/companion-state-sync.md,
    /// 2026-08-28 round) as an 8-plus-minute propagation gap for a host-side companion
    /// re-equip. Delegating to <see cref="TryGetItemResref"/> gives this the same
    /// spawn-cache-first answer <c>TryGetItemResref</c>'s own callers (ScriptItemGrantPump,
    /// DialoguePump's reward diff) already get, so a freshly-created item resolves as soon as
    /// <see cref="DrainItemTemplateSpawns"/> has drained the ring — the same tick it was
    /// created on, once <c>CoopAgent.PumpOnce</c> drains the ring before reading party members
    /// — instead of waiting on a rate-limited full sweep.
    /// </summary>
    internal string ReadEquippedResref(nint creature, int slotOffset)
    {
        if (!_memory.TryReadPointer(creature + OffInventory, out nint inventory) || inventory == 0
            || !_memory.TryReadUInt32(inventory + slotOffset, out uint itemId)
            || itemId is 0 or >= 0x7F000000)
        {
            return string.Empty;
        }

        return TryGetItemResref(itemId, out string resref) ? resref : string.Empty;
    }

    /// <summary>Minimum gap between item-tag scans, however many lookups miss.</summary>
    private const int ItemTagRebuildMs = 15_000;

    /// <summary>Minimum gap between item-tag scans while <see cref="IsItemViewSettled"/> is
    /// false — much shorter than <see cref="ItemTagRebuildMs"/> because the whole point of
    /// settling is to catch the engine finishing what it was still creating at the world edge
    /// (docs/research/shared-inventory-v2.md's 2026-09-02 evening round: the clothes and short
    /// sword a game-over reload creates were not on the heap yet when the "settled" world's
    /// first sweep ran, and nothing looked again for fifteen seconds) as soon as it can rather
    /// than waiting out the ordinary interval a stable world does not need.</summary>
    private const int ItemSettleRebuildMs = 2_000;

    /// <summary>Which module the address caches were built for.</summary>
    private string _cachedForModule = string.Empty;

    /// <summary>The <see cref="WorldLoadSerial"/> the address caches were built under — a
    /// reload of the SAME module replaces every object too. See the reset in
    /// <c>TryReadLocalState</c> (KotorGameBridge.Player.cs).</summary>
    private uint _cachedForWorldSerial;

    /// <inheritdoc/>
    public int WorldEpoch { get; private set; }

    /// <summary>The <see cref="WorldEpoch"/> the last background item sweep was asked for
    /// under; hits that come back under a different one are discarded, not applied.</summary>
    private int _itemScanRequestedEpoch;
    private long _itemTagsBuiltAtMs;

    /// <summary>Whether the sweep the caches hold was taken between lives — see the rule's
    /// own doc for the rejoin that published the client's own sword as a script grant.</summary>
    private readonly ItemSweepProvenanceRule _itemSweepProvenance = new();

    public bool IsItemResolverReady => EngineGlobals.TryGetServerObjectArray(_memory, out _);

    public bool LastPossessedItemReadWasComplete { get; private set; } = true;

    /// <summary>What the last sweep found, as <see cref="ItemSweepProvenanceRule.Fingerprint"/>s
    /// (id + possessor + whether the tag read) — the set two settle sweeps must agree on. Rebuilt
    /// by <see cref="ApplyItemScanHits"/>; the possessor is read there, once per hit, because the
    /// possessed view <see cref="ReadPossessedItems"/> serves is filtered on that field and a
    /// reload assigns the save's equipment to the PC AFTER minting it (live 2026-09-03 00:00).</summary>
    private readonly List<ulong> _lastSweepFingerprints = [];

    /// <inheritdoc/>
    public bool IsItemViewSettled => !_itemSweepProvenance.Settling;

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

    /// <summary>The background sweeper — see <see cref="ItemScanWorker"/> for why the scan does
    /// not run on the caller's thread. Created lazily because <c>_memory</c> is not available
    /// until the bridge has attached.</summary>
    private ItemScanWorker? _itemScanWorker;

    private ItemScanWorker ItemScanWorker => _itemScanWorker ??= new ItemScanWorker(_memory);

    /// <summary>
    /// Brings the item tag/address caches up to date WITHOUT ever blocking the caller, and
    /// returns whether they are usable at all yet.
    ///
    /// Three states. The caches have never been built (attach, or a module change cleared
    /// them): sweep inline, once, and accept the stall — nothing is being rendered from this
    /// data yet and every caller would otherwise have to cope with an empty inventory, which
    /// <c>ScriptItemGrantPump</c> in particular must never see (an empty baseline followed by a
    /// full one reads as "a script just granted the player everything they own"). A finished
    /// background sweep is waiting: apply it, which is a few thousand small reads and costs
    /// tens of milliseconds rather than hundreds. Otherwise: ask for a sweep if the view has
    /// gone stale, and carry on with what we have.
    ///
    /// A fourth state sits across the first: the inline sweep ran while the world was still
    /// between lives (the new world's player resolves seconds before the plugin acks it on
    /// screen, and any lookup in that window builds the caches from a heap still being
    /// filled). That sweep is served — an empty inventory would be worse — but it is
    /// provisional, and the first lookup in the settled world sweeps inline again rather than
    /// trusting it for a rebuild interval. <see cref="ItemSweepProvenanceRule"/> carries the
    /// live round: a client's own short sword, missed by the load-time sweep and found by the
    /// next, published as a script grant.
    ///
    /// ⚠ A fifth state rides alongside all four of the above: even a sweep taken AFTER the world
    /// settled can be incomplete, because "the plugin says the world is up" and "the engine has
    /// finished creating everything the save describes" are different moments — the equipped
    /// items a reload creates are minted after the player object resolves. <see
    /// cref="ItemSweepProvenanceRule.Settling"/> covers that window on its own clock: while it is
    /// true, this method asks for a background sweep on <see cref="ItemSettleRebuildMs"/>'s
    /// short cadence instead of waiting out <see cref="ItemTagRebuildMs"/>, so the rule gets a
    /// real chance to see two sweeps agree before <see cref="IWorldStateBridge.IsItemViewSettled"/>
    /// callers stop being told to hold back. See <see cref="IsItemViewSettled"/> and
    /// <c>ScriptItemGrantPump</c>'s use of it for the 2026-09-02 evening round this closes.
    /// </summary>
    private bool ServiceItemTagCache()
    {
        bool betweenLives = IsWorldBetweenLives;

        if (_itemTagsBuiltAtMs == 0 || _itemSweepProvenance.MustRebuildNow(betweenLives))
        {
            _itemTagsBuiltAtMs = Environment.TickCount64;
            RebuildItemTagCacheNow();
            return true;
        }

        if (ItemScanWorker.TryTakeHits(ItemVtable, out List<nint> hits))
        {
            // A sweep asked for in one world and finished in the next saw a heap mid-teardown:
            // freed item bodies still wearing the vtable, ids and tags half overwritten by the
            // objects that replaced them (live 2026-09-02, an inventory "item" tagged
            // end_door05). Those hits describe no world that exists; the reset that bumped the
            // epoch already cleared the caches, so ask again rather than fill them with this.
            if (_itemScanRequestedEpoch != WorldEpoch)
            {
                _itemScanRequestedEpoch = WorldEpoch;
                ItemScanWorker.Request(ItemVtable, ItemScanLimit);
                return _itemAddresses.Count > 0;
            }

            ApplyItemScanHits(hits);
            _itemSweepProvenance.Swept(betweenLives, _lastSweepFingerprints, Environment.TickCount64);
            _itemTagsBuiltAtMs = Environment.TickCount64;
            return true;
        }

        long rebuildIntervalMs = _itemSweepProvenance.Settling ? ItemSettleRebuildMs : ItemTagRebuildMs;
        if (Environment.TickCount64 - _itemTagsBuiltAtMs >= rebuildIntervalMs)
        {
            _itemScanRequestedEpoch = WorldEpoch;
            ItemScanWorker.Request(ItemVtable, ItemScanLimit);
        }

        return _itemAddresses.Count > 0;
    }

    /// <summary>The blocking sweep. Only two callers may reach it: the first build after attach
    /// (see <see cref="ServiceItemTagCache"/>) and an interactive console verb with a human
    /// waiting. Everything on the pump thread goes through the background worker.</summary>
    private void RebuildItemTagCacheNow()
    {
        BytePattern pattern = BytePattern.FromBytes(BitConverter.GetBytes(ItemVtable));
        PatternScanner scanner = new(_memory);

        ApplyItemScanHits(scanner.ScanAll(pattern, ScanScope.PrivateCommitted, ItemScanLimit));
        _itemSweepProvenance.Swept(IsWorldBetweenLives, _lastSweepFingerprints, Environment.TickCount64);
    }

    /// <summary>
    /// Turns one sweep's raw hit addresses into the tag/address caches. This half stays on the
    /// caller's thread on purpose — it is the only part that mutates bridge state, and it is
    /// cheap (a handful of small reads per hit) next to the sweep that produced the list.
    /// </summary>
    private void ApplyItemScanHits(List<nint> hits)
    {
        _itemTags.Clear();
        _itemAddresses.Clear();
        _lastSweepFingerprints.Clear();

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

            uint possessor = _memory.TryReadUInt32(address + ItemPossessor, out uint holder) ? holder : ObjectInvalid;
            _lastSweepFingerprints.Add(ItemSweepProvenanceRule.Fingerprint(id, possessor, tag.Length > 0));
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

        if (force)
        {
            _itemTagsBuiltAtMs = Environment.TickCount64;
            RebuildItemTagCacheNow();
            return _itemAddresses.ContainsKey(itemObjectId);
        }

        // A miss on the pump thread asks the background worker for a refresh and answers "not
        // known" for now, instead of stopping the position stream for a third of a second to
        // go and look. The rate limit still applies — inside ServiceItemTagCache — for exactly
        // the reason it was added: a sweep per miss per tick once dropped the pump to about
        // 4 Hz and measured as peers lagging by seconds.
        ServiceItemTagCache();
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
    /// 2026-08-28: this now answers with the REAL resref first when this session happens to
    /// know it (an item we ourselves told the plugin to spawn — see
    /// KotorGameBridge.GroundItems.cs's <c>_spawnedItemResrefs</c>), falling back to the item's
    /// TAG, cut and lowercased, only when we do not. The tag is still only a proxy and it is
    /// still worth naming as one: a live CSWSItem does not retain its template name at all
    /// (CSWSItem::SaveItem 0x0055CCD0 writes BaseItem and the property list, never a
    /// TemplateResRef — an item is rebuilt from its own fields, not from its blueprint; confirmed
    /// again this pass — no CSWSItem/CGameObject/CSWSObject field or accessor in
    /// tools/kotor1_0_3.db names a template resref, and CSWSItem::LoadFromTemplate 0x005608B0
    /// only ever uses the incoming name to open the GFF resource, never to write it into the
    /// object). For base-game items the tag IS the resref in upper case ("G_W_BLSTRPSTL001"),
    /// which is what makes the proxy work most of the time; a stock KOTOR 1 quest item's tag can
    /// still genuinely diverge from its resref (docs/research/playthrough-endar-spire.md's
    /// live-confirmed `k_computer_spike` / `g_i_progspike01`), which is exactly the case the new
    /// authoritative lookup does NOT cover — it only ever knows a resref for a body THIS SESSION
    /// spawned, never one a vanilla script created on its own. See
    /// docs/research/item-identity.md §12 for the 2026-08-28 write-up.
    /// </summary>
    /// <summary>The <see cref="IItemBridge"/> member: same lookup, cache-only (no forced
    /// sweep) — a pump resolving a container/corpse-loot removal's resref runs every tick
    /// this scan is throttled to, so it should never itself trigger the sweep
    /// <paramref name="force"/> would.</summary>
    bool IItemBridge.TryGetItemResref(uint itemObjectId, out string resref) =>
        TryGetItemResref(itemObjectId, out resref);

    public bool TryGetItemResref(uint itemObjectId, out string resref, bool force = false)
    {
        // The REAL blueprint resref, when we happen to know it: an item this session itself
        // told the plugin to spawn onto the floor (KotorGameBridge.GroundItems.cs's
        // _spawnedItemResrefs, learned in CollectVanishedItems the moment the slot first
        // reported the body alive). Checked BEFORE the tag cache on purpose — Tag is only ever
        // an approximation of the resref (base-game blueprints set Tag = resref uppercased, but
        // a stock KOTOR 1 quest item's Tag can genuinely diverge; see
        // docs/research/item-identity.md §8.2 and docs/research/playthrough-endar-spire.md's
        // live-confirmed `k_computer_spike` / `g_i_progspike01`), while this fact came from a
        // write WE made, never from an inference. "Keep Tag as a fallback only when the resref
        // is empty" is exactly this ordering.
        if (_spawnedItemResrefs.TryGetValue(itemObjectId, out string? spawned))
        {
            resref = spawned;
            return true;
        }

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
    public IReadOnlyList<ItemListing> ListItems() => ListItems(force: true);

    /// <summary>
    /// <paramref name="force"/> decides whether this listing is allowed to trigger a fresh
    /// <see cref="RebuildItemTagCacheNow"/> sweep, or must make do with whatever the last one
    /// found.
    ///
    /// ⚠ THIS PARAMETER IS A PERFORMANCE CONTRACT, and it exists because the unconditional
    /// sweep this method used to do was measured (2026-08-29, rig, <c>perf_trace</c>) costing
    /// <b>640 ms of every second</b> on the agent's pump thread and <b>180 MiB/s</b> of
    /// <c>ReadProcessMemory</c> against the live game. <see cref="ReadPossessedItems"/> is
    /// called once a second by <c>ScriptItemGrantPump</c> on that thread, so a full sweep of
    /// the game's committed memory was landing between two position writes: the pump ran at
    /// 12-17 Hz instead of 30 with individual ticks of 500-670 ms, and both players saw the
    /// other's avatar freeze and then dash. That is the same failure
    /// <see cref="EnsureItemKnown"/>'s own rate limit was added for ("a sweep of the game's
    /// committed memory per miss per tick once dropped the pump to about 4 Hz") — this method
    /// simply was not honouring it.
    ///
    /// So: an INTERACTIVE caller (the console's <c>items</c> verb, where a human is waiting and
    /// wants the truth as of now) forces. A PUMP caller does not, and gets a view at most
    /// <see cref="ItemTagRebuildMs"/> old — the same freshness every other pump-thread item
    /// lookup in this class has always had.
    /// </summary>
    public IReadOnlyList<ItemListing> ListItems(bool force)
    {
        if (force)
        {
            _itemTagsBuiltAtMs = Environment.TickCount64;
            RebuildItemTagCacheNow();
        }
        else
        {
            ServiceItemTagCache();
        }

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
            LastPossessedItemReadWasComplete = true;
            return [];
        }

        LastPossessedItemReadWasComplete = false;

        List<(uint, string, ItemIdentity)> result = [];

        // The shared party pool IS the PC's inventory (CSWSCreature::GetItemRepository(1) on
        // a party member is the pool), but its items' possessor field names whoever LEADS —
        // and the engine re-points it on every leadership change, including the two-second
        // hand-off to a companion while the PC is down. Membership in the pool is the stable
        // answer to "does the PC hold this"; the possessor field alone made a downed-and-back
        // host re-grant its whole pool to the peer (see TryReadPartyPoolItemIds' remarks).
        HashSet<uint>? poolIds = possessorIds.Contains(CheatKinds.LocalPlayerTarget) ? TryReadPartyPoolItemIds() : null;

        // Keep the expensive scan cache for diagnostics (`items` must continue to show ghosts),
        // but only publish rows that the native server object array still owns.  Resolve the
        // array once per read and do one bounded bucket lookup per candidate; this avoids a
        // second full-memory scan and closes the gap between a cached vtable hit and its use.
        bool serverArrayResolved = EngineGlobals.TryGetServerObjectArray(_memory, out nint objectArray);

        // force: false — this runs on the agent's pump thread, between two position writes.
        // See ListItems(bool)'s own remarks for the measured cost of the sweep this declines
        // to trigger.
        foreach (ItemListing item in ListItems(force: false))
        {
            // Apply the cheap ownership filter before the native liveness lookup.  Most of the
            // scan is NPC/party inventory and cannot enter this result anyway.
            if (!possessorIds.Contains(item.Possessor)
                && (poolIds is null || !poolIds.Contains(item.ObjectId)))
            {
                continue;
            }

            if (!serverArrayResolved
                || !_itemAddresses.TryGetValue(item.ObjectId, out nint scannedAddress)
                || !EngineGlobals.TryResolveServerObject(_memory, objectArray, item.ObjectId, out nint serverAddress)
                || !IsLiveServerItem(serverArrayResolved, scannedAddress, serverAddress))
            {
                continue;
            }

            if (possessorIds.Contains(item.Possessor))
            {
                result.Add((item.Possessor, item.Resref, item.Identity));
            }
            else if (poolIds is not null && poolIds.Contains(item.ObjectId))
            {
                result.Add((CheatKinds.LocalPlayerTarget, item.Resref, item.Identity));
            }
        }

        // The array may have been torn down after the initial pointer read.  Treat that sample
        // as incomplete so callers retain their baseline and retry after the world is ready.
        LastPossessedItemReadWasComplete = serverArrayResolved
            && EngineGlobals.TryGetServerObjectArray(_memory, out nint finalArray)
            && finalArray == objectArray;

        return result;
    }

}
