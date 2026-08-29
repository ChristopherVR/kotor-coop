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

    /// <summary>CItemRepository + this = the one-byte flags field whose bit 0 gates
    /// <c>InsertItem</c>'s bail-out (docs/research/party-pool-sync.md §16) and is set TRUE only
    /// for the party pool's own repository (<c>CSWPartyTable::GetPartyItems</c>'s own construction
    /// call — every other repository in this codebase, creature backpack/placeable/store, is
    /// constructed with it clear). VERIFIED, fresh disasm, §16/§18.</summary>
    private const int RepositoryFlagsOffset = 0x08;

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
    /// `__thiscall(this=repo)`, two stack args (item pointer, index). Its VERY FIRST instruction
    /// is `test byte ptr [repo+8],1 / jne &lt;ret&gt;` — an unconditional bail-out, before touching
    /// anything, whenever bit 0 of the repository's own <c>+0x08</c> byte is set. **The party
    /// pool's own repository is constructed with that bit SET** (see
    /// <see cref="RepositoryAddItemFn"/>'s own doc comment for why that bit exists and who else
    /// reads it) — so a bare call against the live party-pool repo is a guaranteed, silent,
    /// total no-op: not "sometimes stale," genuinely does nothing, confirmed live 6/6
    /// (docs/research/party-pool-sync.md §15's own before/after <c>item_count</c> diagnostic).
    ///
    /// <see cref="AddItemToPartyPool"/> (docs/research/party-pool-sync.md §18) calls this
    /// primitive anyway, by transiently CLEARING that one bit for the duration of the single call
    /// (<see cref="WithRepoBit0Cleared"/>) — its append path (grow the backing array via
    /// <c>CExoArrayList::Allocate</c> 0x005E03C0 if <c>count==capacity</c>, write
    /// <c>items_list[count]=id</c>, <c>item_count++</c>) is fully verified, side-effect-free
    /// beyond that array link, and — unlike <see cref="RepositoryAddItemFn"/> — never touches the
    /// item's own NewItem bit (<c>item+0x288</c> bit 7) or the repository's own "new item"
    /// counter (<c>repo+0x04</c>), which is what actually arms the crash documented on
    /// <see cref="RepositoryAddItemFn"/>'s own doc comment. This is why <c>InsertItem</c>, not
    /// <c>AddItem</c>, is the party pool's own add primitive as of §18.
    ///
    /// Also still used, unmodified, by <c>Odyssey.Store.cs</c>'s own <c>InsertItemIntoStore</c>: a
    /// store's repository is constructed with the SAME bit clear (VERIFIED, fresh disasm of
    /// <c>CSWSStore::Constructor</c> 0x005C6AB0's own single call to
    /// <c>CItemRepository::Constructor</c>, RE-CONFIRMED this pass — its second (flag) stack
    /// argument is <c>edi</c>, proven zero by an <c>xor edi,edi</c> at function entry never
    /// touched again before the call), so no bit-clear dance is needed there — a bare call already
    /// works.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, void> RepositoryInsertItemFn =
        (delegate* unmanaged[Thiscall]<nint, nint, int, void>)0x0055D2C0;

    /// <summary>
    /// CItemRepository::AddItem(CSWSItem** ppItem, int wantMerge, int arg3, int markNewItem)
    /// 0x0055D330 — VERIFIED, full disasm (docs/research/party-pool-sync.md §16-18). The FULL
    /// "give item to this repository" engine primitive.
    ///
    /// docs/research/party-pool-sync.md §17 found this **crashes the host 3/3 times, live**, when
    /// used against the party pool. §18's own fresh full disasm (this pass) narrows why, past
    /// §17's own "not narrowed further" verdict, and settles that the task brief's own leading
    /// hypothesis (an argument tweak, or routing through <c>CSWSCreature::AcquireItem</c>
    /// 0x005158E0 instead) does NOT reliably avoid it:
    ///
    /// <list type="number">
    /// <item>A merge-search loop (<c>wantMerge==1</c> only, gated by
    /// <c>cmp [ebp+0xc],1 / jne 0x55d655</c> at the very top) walks existing entries and, on a
    /// candidate whose <c>+0xc</c>/<c>+0x264</c> fields match the incoming item, calls
    /// <c>CSWSItem::MergeItem</c> 0x00553F90; a return of 1 jumps into a notify block
    /// (0x0055D5B6-0x0055D610) that resolves a per-connection message object
    /// (<c>CServerExoApp::GetSWSMessage</c> 0x004AEDB0) and calls
    /// <c>CSWSMessage::SendServerPlayerItemUpdate_DestroyItem</c> 0x00569FC0 on it — telling a
    /// (nonexistent, in retail single-player) network peer the OLD stack was destroyed by the
    /// merge. This trigger is NOT gated by the repository's own <c>+0x08</c> bit 0 — it fires for
    /// ANY repository (store, creature backpack, party pool) whenever a genuine same-template
    /// merge happens, gated only by <c>wantMerge</c> and <c>MergeItem</c>'s own return value.</item>
    /// <item>SEPARATELY, and more importantly for what §17's own 3/3 live crashes actually showed
    /// (every one logged <c>itemLocal unchanged (fresh entry)</c> — i.e. NO merge happened on the
    /// call that was logged): every successful insert with <c>markNewItem!=0</c> (both this
    /// codebase's own prior call AND <c>CSWSCreature::AcquireItem</c>'s own call, traced this
    /// pass, pass <c>markNewItem=1</c>) unconditionally sets the just-inserted item's NewItem bit
    /// (<c>item+0x288</c> bit 7, 0x0055D786-0x0055D7A7) and increments the repository's own "new
    /// item" counter (<c>repo+0x04</c>). <c>tools/xrefs.py 0x56e360</c> shows
    /// <c>CSWSMessage::WriteRepositoryUpdate</c> 0x0056E360 (the function that actually crashes,
    /// at 0x0056E549) is called from exactly two sites, BOTH inside
    /// <c>CSWSMessage::WriteGameObjUpdate_MajorGUIPanels</c> 0x00577670, itself called from
    /// <c>CSWSMessage::SendServerToPlayerGameObjUpdate</c> 0x00578200 — the per-player
    /// server-&gt;client full game-object network sync sweep, NOT anything <c>AddItem</c> calls
    /// directly. This sweep, on a LATER pass (matching §17's own observed ~150ms-to-immediate
    /// gap), walks whichever item the repository's own "new item" bookkeeping names as pending
    /// sync (via <c>ItemListGetItemObjectID</c> 0x005560E0 + <c>GetGameObjectByID</c> 0x004AE760)
    /// and, at 0x0056E549 (<c>mov dl, byte ptr [edi+0x18]</c>), dereferences the resolved object
    /// with **no null/validity check at all** on the immediately preceding branches — a genuine,
    /// unconditional retail defect in dead multiplayer bookkeeping, armed by <c>markNewItem=1</c>
    /// alone, independent of whether a merge ever happened.</item>
    /// </list>
    ///
    /// **Because <c>CSWSCreature::AcquireItem</c>'s own self-deposit call site passes the
    /// IDENTICAL argument shape this codebase's prior call did
    /// (<c>wantMerge=1, arg3=0, markNewItem=1</c>, traced this pass), it is exposed to the same
    /// two triggers — it is not a provably safer primitive, only an untested one; its "known to
    /// work" reputation most likely reflects that live testing never happened to deposit a
    /// duplicate-template stack nor watch a full ~150ms+ window afterward, not that it sidesteps
    /// either defect.** For that reason <see cref="AddItemToPartyPool"/> (§18) does not call this
    /// primitive, or <c>AcquireItem</c>, at all — it uses <see cref="RepositoryInsertItemFn"/>
    /// instead (see that field's own doc comment), whose append path never sets the NewItem bit
    /// or the counter and never runs a merge search, so neither trigger can arm.
    ///
    /// Signature (unchanged from §16): first stack argument is a <c>CSWSItem**</c> (in/out) — on a
    /// MERGE, the callee writes the EXISTING stack's pointer back into <c>*ppItem</c>; on a
    /// credit-chit/Pazaak-card conversion, it clears <c>*ppItem</c> to 0 after deleting the
    /// incoming item. A fresh insert additionally calls <c>CSWSItem::SetPossessor</c> 0x00553210
    /// on the item (bit-0-set branch only, 0x0055D6FE-0x0055D728, with the caller's own
    /// <c>arg3</c> reused for the middle two arguments) — <see cref="AddItemToPartyPool"/> now
    /// reproduces that possessor write by hand via <see cref="SetPossessorFn"/> instead, since it
    /// no longer calls this function to get it for free.
    ///
    /// NOT CALLED against the party pool. Declared and left in place only as a documented,
    /// disassembled reference for whoever next tries to fix the underlying retail defect (see
    /// docs/research/party-pool-sync.md §18's own "future work" note) rather than route around it.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, int, int, void> RepositoryAddItemFn =
        (delegate* unmanaged[Thiscall]<nint, nint, int, int, int, void>)0x0055D330;

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

    /// <summary>
    /// The identity flags a shared-inventory apply may actually write onto a freshly-spawned
    /// item: the requested Stolen bit (the one bit that IS part of a stack's identity — see
    /// <see cref="PartyPoolIdentityMatcher.StackIdentityFlags"/>) plus, taken verbatim from the
    /// body the engine has just built from the blueprint, its own Identified and NonEquippable
    /// bits.
    ///
    /// ⚠ This is the half of the §13.2 keying fix that lives on the WRITE side, and it is not
    /// optional. <see cref="ApplyItemIdentity"/> does not "leave a bit alone" — it SETS or CLEARS
    /// all three unconditionally from what it is handed. A stack key can no longer carry
    /// Identified (<c>ItemIdentity.StackIdentityFlagMask</c> narrows it away), so handing the key's
    /// flags straight through would make every wire-driven apply, and every bare console
    /// <c>pooladd</c>, actively CLEAR the identified bit on an item whose blueprint set it —
    /// manufacturing exactly the unidentified twin of an identified save stack that
    /// docs/research/shared-inventory-v2.md §13.2 watched split `g_i_medeqpmnt01` across `/f1` and
    /// `/f3`. Preserving the template's own value instead means a created item matches what the
    /// same blueprint produced for the save, on both instances, with no convention to agree on:
    /// both loaded the same <c>.uti</c>.
    /// </summary>
    private static uint IdentityFlagsForCreatedItem(uint requestedWireFlags, in ItemIdentityFields spawned)
    {
        uint requested = requestedWireFlags == SharedBlock.CheatFieldUnchanged ? 0 : requestedWireFlags;
        uint fromTemplate = EngineBitsToWireFlags(spawned.BitFlags)
            & (SharedBlock.ItemIdentityIdentified | SharedBlock.ItemIdentityNonEquippable);

        return (requested & SharedBlock.ItemIdentityStolen)
            | fromTemplate
            | SharedBlock.ItemIdentityKnown;
    }

    /// <summary>True when a resolved item's non-stack identity (charges/max/upgrades/flags)
    /// matches the requested stack key. Thin engine-facing wrapper over
    /// <see cref="PartyPoolIdentityMatcher"/> (the pure, independently-tested rule — see its own
    /// remarks for the charge clamp and the <see cref="SharedBlock.CheatFieldUnchanged"/>
    /// wildcard docs/research/party-pool-sync.md §20 added): converts this item's raw engine
    /// bits to wire shape and hands both off, since the pure class has no
    /// <see cref="SharedBlock"/>/engine-offset dependency of its own.
    ///
    /// ⚠ The Identified and NonEquippable bits are NOT part of this match — see
    /// <see cref="PartyPoolIdentityMatcher.StackIdentityFlags"/> for the engine disassembly that
    /// settles it and docs/research/shared-inventory-v2.md §13.2/§14 for the live divergence
    /// that comparing them caused.</summary>
    private static bool StackIdentityMatches(
        in ItemIdentityFields fields, uint charges, uint maxCharges, uint upgrades, uint flags)
    {
        (uint safeCharges, uint safeMaxCharges) =
            PartyPoolIdentityMatcher.SafeChargeFields(fields.Charges, fields.MaxCharges);

        // Both sides go in as raw wire-shape flags; PartyPoolIdentityMatcher.Matches narrows
        // them to PartyPoolIdentityMatcher.StackIdentityFlags itself (Stolen alone — the ONLY
        // bit the engine's own merge predicate at 0x00553CF0 compares) and passes a Wildcard
        // request through untouched. Doing the narrowing there rather than here keeps the rule
        // in the one class that is unit-tested for it.
        uint wireBits = EngineBitsToWireFlags(fields.BitFlags);

        return PartyPoolIdentityMatcher.Matches(
            safeCharges, safeMaxCharges, fields.Upgrades, wireBits,
            charges, maxCharges, upgrades, flags);
    }

    /// <summary>
    /// Runs <paramref name="action"/> with the party pool repository's own <c>+0x08</c> flags
    /// byte's bit 0 (see <see cref="RepositoryFlagsOffset"/>) temporarily CLEARED, restoring the
    /// original byte (not just re-setting bit 0 — preserves any other bit already there)
    /// immediately afterward. Synchronous, MAIN THREAD ONLY, no engine call in between the clear
    /// and the restore — this is a two-instruction bracket around one native call, not a
    /// standing state change. See <see cref="RepositoryInsertItemFn"/>'s own doc comment for why
    /// this exists: it lets the party pool use <c>CItemRepository::InsertItem</c>'s own fully
    /// verified, side-effect-free append path instead of the crash-prone
    /// <see cref="RepositoryAddItemFn"/>.
    /// </summary>
    private static void WithRepoBit0Cleared(nint repo, Action action)
    {
        byte* flags = (byte*)(repo + RepositoryFlagsOffset);
        byte original = *flags;
        *flags = (byte)(original & ~1);
        try
        {
            action();
        }
        finally
        {
            *flags = original;
        }
    }

    /// <summary>
    /// Every id currently linked into <paramref name="repo"/> paired with that item's own stack
    /// size. Taken BEFORE a deposit so the engine's own merge can be identified by membership
    /// rather than inferred from stack arithmetic — see <see cref="PartyPoolDepositRule"/>.
    /// Null when the repository's count/array fields do not read sanely, which every caller
    /// treats as "there is nothing here I may reason about".
    /// </summary>
    private static (uint Id, uint Stack)[]? SnapshotRepositoryStacks(nint repo)
    {
        if (repo == 0)
        {
            return null;
        }

        int count = *(int*)(repo + RepositoryItemCount);
        nint ids = *(nint*)(repo + RepositoryItemArray);
        if (count is < 0 or > 4096 || (count > 0 && ids == 0))
        {
            return null;
        }

        (uint Id, uint Stack)[] snapshot = new (uint, uint)[count];
        for (int i = 0; i < count; i++)
        {
            uint id = *(uint*)(ids + (nint)(i * 4));
            nint item = id == 0 ? 0 : ResolveItemBase(id);
            snapshot[i] = (id, item == 0 ? 0u : *(uint*)(item + ItemStackSize));
        }

        return snapshot;
    }

    /// <summary>True when <paramref name="snapshot"/> (from
    /// <see cref="SnapshotRepositoryStacks"/>) already held <paramref name="itemId"/>, handing
    /// back the stack size it carried then. A null snapshot answers false — an unreadable
    /// repository is never evidence that an item is new.</summary>
    private static bool TryFindSnapshotStack((uint Id, uint Stack)[]? snapshot, uint itemId, out uint stack)
    {
        stack = 0;
        if (snapshot is null)
        {
            return false;
        }

        foreach ((uint id, uint size) in snapshot)
        {
            if (id == itemId)
            {
                stack = size;
                return true;
            }
        }

        return false;
    }

    /// <summary>How many array slots of <paramref name="repo"/> currently hold
    /// <paramref name="itemId"/>. Legitimately 0 or 1; anything higher is a pre-fix build's own
    /// double link, and every aggregate scan counts that item's stack once per slot.</summary>
    private static int CountRepositoryLinks(nint repo, uint itemId)
    {
        if (repo == 0)
        {
            return 0;
        }

        int count = *(int*)(repo + RepositoryItemCount);
        nint ids = *(nint*)(repo + RepositoryItemArray);
        if (ids == 0 || count is <= 0 or > 4096)
        {
            return 0;
        }

        int links = 0;
        for (int i = 0; i < count; i++)
        {
            if (*(uint*)(ids + (nint)(i * 4)) == itemId)
            {
                links++;
            }
        }

        return links;
    }

    /// <summary>
    /// Temporarily sets the Stolen bit on every item in <paramref name="repo"/> whose Tag is
    /// <paramref name="resrefAscii"/> and which does not already carry it, so that a
    /// <c>CreateItemOnObject</c> issued while they are flipped CANNOT merge into any of them and
    /// is guaranteed to produce a fresh body. Stolen (engine bit 5) is the ONE flag bit the
    /// engine's merge predicate 0x00553CF0 compares — three instructions, fully disassembled in
    /// docs/research/item-identity.md §14 — so flipping it is the narrowest possible way to
    /// refuse a merge, and it is restored synchronously by <see cref="RestoreStolenBits"/> in the
    /// caller's own <c>finally</c>.
    ///
    /// Only <see cref="Odyssey.InsertItemIntoStore"/> needs this: a store deposit has to end up
    /// as a body it can MOVE, and a merged stack cannot be moved without dragging the player's
    /// own standing units into the shop. The party pool wants the opposite — a merge there is
    /// the correct outcome and is simply accounted for (<see cref="PartyPoolDepositRule"/>).
    ///
    /// Returns how many entries of <paramref name="flipped"/> were filled in. MAIN THREAD ONLY.
    /// </summary>
    private static int BlockMergesForTag(nint repo, byte* resrefAscii, int resrefLength, nint[] flipped)
    {
        if (repo == 0)
        {
            return 0;
        }

        int count = *(int*)(repo + RepositoryItemCount);
        nint ids = *(nint*)(repo + RepositoryItemArray);
        if (ids == 0 || count is <= 0 or > 4096)
        {
            return 0;
        }

        int n = 0;
        for (int i = 0; i < count && n < flipped.Length; i++)
        {
            uint id = *(uint*)(ids + (nint)(i * 4));
            nint item = id == 0 ? 0 : ResolveItemBase(id);
            if (item == 0 || !TagEqualsIgnoreCase(item, resrefAscii, resrefLength))
            {
                continue;
            }

            uint bits = *(uint*)(item + ItemBitFlags);
            if ((bits & ItemBitStolen) != 0)
            {
                // Already unmergeable with a blueprint-fresh body, which carries the template's
                // own (clear) Stolen bit — leave it exactly as it is.
                continue;
            }

            *(uint*)(item + ItemBitFlags) = bits | ItemBitStolen;
            flipped[n++] = item;
        }

        return n;
    }

    /// <summary>Undoes <see cref="BlockMergesForTag"/> — clears the Stolen bit on exactly the
    /// items it set it on, and only those (it never records one that already carried it).</summary>
    private static void RestoreStolenBits(nint[] flipped, int count)
    {
        for (int i = 0; i < count; i++)
        {
            nint item = flipped[i];
            if (item != 0)
            {
                *(uint*)(item + ItemBitFlags) &= ~ItemBitStolen;
            }
        }
    }

    /// <summary>
    /// The repository a creature's own <c>AcquireItem</c> deposits into:
    /// <c>CSWSCreature::GetItemRepository(this, 1)</c> — the SHARED PARTY POOL for a live party
    /// member, that creature's own individual backpack otherwise. Falls back to the raw
    /// <see cref="CreatureItemRepo"/> field only if the engine call answers null.
    ///
    /// ⚠ Reading <c>+0xA30</c> directly (what this code did until
    /// docs/research/shared-inventory-v2.md §15) is wrong for exactly the case the shared
    /// inventory always hits: a spawn onto a party member lands in the party pool, so the
    /// "unlink it from the player's backpack" step was a guaranteed no-op — which is half of
    /// why every deposit ended up linked into the pool twice. Odyssey.Store.cs's own section
    /// header flagged this hazard for both callers; this is it being taken.
    /// </summary>
    private static nint ResolveCreatureItemRepo(nint creature)
    {
        if (creature == 0)
        {
            return 0;
        }

        nint shared = GetItemRepositoryFn(creature, 1);
        return shared != 0 ? shared : *(nint*)(creature + CreatureItemRepo);
    }

    /// <summary>
    /// Adds <paramref name="count"/> units of a resref+identity stack to the shared party pool:
    /// spawns the item straight into the local player's own inventory (the same
    /// <c>CreateItemOnObject</c> VM routine <see cref="GiveItemToInventory"/> already wraps —
    /// there is no VM path that targets the pool directly, since it is not a scriptable
    /// object), applies its identity, then unlinks it from the player's own backpack and links
    /// it into the pool's repository via <see cref="RepositoryInsertItemFn"/>. No floor
    /// placement, no duplicate object — the item never exists anywhere except (briefly) the
    /// player's own pack and then the pool.
    ///
    /// BUG FIX HISTORY: §16 switched this from <c>InsertItem</c> (a guaranteed no-op against the
    /// party pool's own repository — see <see cref="RepositoryInsertItemFn"/>'s own doc comment)
    /// to <c>CItemRepository::AddItem</c>. §17's own live rig then found THAT crashes the host
    /// 3/3. §18 (docs/research/party-pool-sync.md) found, by fresh disassembly, that the crash's
    /// two triggers (a native-merge notify call, and — the one that actually matches §17's own
    /// observed symptom — the NewItem-bit/counter bookkeeping <c>AddItem</c> unconditionally sets
    /// on every insert, later consumed by a crashing, unguarded null-deref inside the retail
    /// engine's own per-tick network sync sweep) are BOTH armed by <c>AddItem</c> alone, not by
    /// anything <c>InsertItem</c>'s own append path touches, and are NOT avoided by routing
    /// through <c>CSWSCreature::AcquireItem</c> either (same argument shape, same exposure — see
    /// <see cref="RepositoryAddItemFn"/>'s own doc comment for the full trace and reasoning).
    ///
    /// This method therefore reverts to <c>InsertItem</c> for the actual array link — restoring
    /// §16's "guaranteed no-op" defect in the process, so it now BRACKETS the call with
    /// <see cref="WithRepoBit0Cleared"/>, transiently clearing the one flag bit that makes
    /// <c>InsertItem</c> bail out against this specific repository, so its own fully-verified,
    /// side-effect-free append path (grow-if-needed, <c>items_list[count]=id</c>,
    /// <c>item_count++</c>, nothing else) runs instead. It then reproduces, by hand, the one
    /// thing <c>AddItem</c>'s own bit-0-set branch used to do for free: a possessor write
    /// (<c>CSWSItem::SetPossessor(item, playerObjectId, 0, 0, OBJECT_INVALID)</c> — the exact
    /// literal argument shape §16.4's own disassembly of that branch found, reusing the caller's
    /// own player id rather than re-resolving it) — see <see cref="SetPossessorFn"/>'s own doc
    /// comment. Net effect: the SAME array link and the SAME possessor write <c>AddItem</c> used
    /// to perform for a fresh (non-merging) deposit, with neither of its two crash triggers ever
    /// armed.
    ///
    /// ⚠ §15 (docs/research/shared-inventory-v2.md) then found that on the ordinary path — a
    /// spawn onto a live party member — NONE of that relink is reached correctly, and both of
    /// its halves were actively destructive. <c>CreateItemOnObject</c> routes through
    /// <c>CSWSCreature::AcquireItem</c>, which files the item into
    /// <c>GetItemRepository(creature, 1)</c> — the party pool ITSELF — and merges it into any
    /// stack already there, handing back that pre-existing stack's object id. So the unlink
    /// targeted a repository the item was never in (a no-op), the insert linked an
    /// already-linked id a SECOND time, and the identity apply overwrote the merged stack's size
    /// with the requested count, destroying the units it landed on. See
    /// <see cref="PartyPoolDepositRule"/> for the arithmetic that produces, and §14.3's live
    /// trace it reproduces exactly. This method now decides through that rule: it relinks ONLY
    /// when the engine did not file the item itself, adds to a merged stack instead of
    /// overwriting it, and drops any duplicate link a pre-fix build left behind.
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

        // Snapshot the pool BEFORE the spawn. The engine's own CreateItemOnObject ->
        // CSWSCreature::AcquireItem -> CItemRepository::AddItem(wantMerge=1) may hand back a
        // PRE-EXISTING pool stack rather than a new body (it writes the merge target's pointer
        // back through ppItem), and "was this id already in the repository?" is the only
        // observation that identifies that without guessing from stack arithmetic. See
        // PartyPoolDepositRule for the full mechanism and the live trace it reproduces.
        int countBefore = *(int*)(repo + RepositoryItemCount);
        (uint Id, uint Stack)[]? before = SnapshotRepositoryStacks(repo);

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

        bool merged = TryFindSnapshotStack(before, itemId, out uint stackBefore);
        int linksNow = CountRepositoryLinks(repo, itemId);
        PartyPoolDepositRule.Deposit deposit =
            PartyPoolDepositRule.ForCreatedItem(merged, stackBefore, linksNow, count);

        // A merge landed our units on somebody else's stack: its charges/upgrades/flags belong
        // to the units that were already there and are rewritten from the engine's own current
        // values, not from the request — which the merge predicate (0x00553CF0) already proved
        // equal for every field it compares. Only the stack size moves, and it moves to
        // standing + requested rather than to the request alone. That last word is the whole
        // §14.3 defect: overwriting it destroyed the standing units and made the next scan
        // report a REMOVE for a deposit.
        uint applyUpgrades = deposit.KeepEngineIdentity ? spawned.Upgrades : upgrades;
        uint applyCharges = deposit.KeepEngineIdentity ? spawned.Charges : charges;
        uint applyMaxCharges = deposit.KeepEngineIdentity ? spawned.MaxCharges : maxCharges;
        uint applyFlags = deposit.KeepEngineIdentity
            ? EngineBitsToWireFlags(spawned.BitFlags) | SharedBlock.ItemIdentityKnown
            : IdentityFlagsForCreatedItem(flags, spawned);

        if (!ApplyItemIdentity(
                itemId, applyUpgrades, deposit.TargetStackSize, applyCharges, applyMaxCharges,
                applyFlags, out after))
        {
            LastPartyPoolNote = $"identity apply failed for 0x{itemId:X}: {LastItemResolveNote}";
            return false;
        }

        nint item = ResolveItemBase(itemId);
        if (item == 0)
        {
            LastPartyPoolNote = $"0x{itemId:X} spawned but could not resolve the item base";
            return false;
        }

        if (deposit.NeedsLink)
        {
            // The engine did NOT file it into the pool itself (the spawn target was not a live
            // party member, so AcquireItem used its own individual backpack). Unlink it from
            // there and link it into the pool via InsertItem's own (fully-verified,
            // side-effect-free) append path — see WithRepoBit0Cleared's and
            // RepositoryInsertItemFn's own doc comments for why the bit needs clearing and why
            // this avoids both of AddItem's crash triggers (party-pool-sync.md §18).
            nint player = GetGameObjectFn(GetServer(), playerObjectId);
            nint playerRepo = ResolveCreatureItemRepo(player);
            if (playerRepo == 0)
            {
                LastPartyPoolNote = $"0x{itemId:X} spawned but could not resolve the player's repository";
                return false;
            }

            RepositoryRemoveItemFn(playerRepo, item);
            WithRepoBit0Cleared(repo, () => RepositoryInsertItemFn(repo, item, -1));
            SetPossessorFn(item, playerObjectId, 0, 0, ObjectInvalid);
        }

        // Heal a pool a PREVIOUS build double-linked: an id can only legitimately occupy one
        // slot, and every extra one makes every aggregate scan report that stack's size again.
        for (int i = 0; i < deposit.DuplicateLinksToDrop; i++)
        {
            RepositoryRemoveItemFn(repo, item);
            PluginHost.Log(
                $"party pool: dropped a DUPLICATE array link of 0x{itemId:X} (a pre-fix build "
                + "linked an already-pooled item a second time; every scan counted it twice)");
        }

        // DIAGNOSTIC (docs/research/party-pool-sync.md §13.3's own "two lookups, one frame" ask,
        // kept through every round since), now also reporting the merge decision — which is what
        // five rounds of divergence turned out to hinge on.
        int countAfter = *(int*)(repo + RepositoryItemCount);
        PluginHost.Log(
            $"party pool: add diag — repo 0x{repo:X}, item_count {countBefore}->{countAfter}, "
            + $"0x{itemId:X} links {linksNow}->{CountRepositoryLinks(repo, itemId)}, "
            + (merged
                ? $"MERGED into a standing stack of {stackBefore} (stack now {after.StackSize})"
                : $"fresh body ({(deposit.NeedsLink ? "linked by us" : "linked by the engine")}, "
                    + $"stack {after.StackSize})"));

        return true;
    }

    /// <summary>
    /// Removes <paramref name="countToRemove"/> units of a resref+identity stack from the
    /// shared party pool, greedily over whichever live items currently match (by Tag/spawn-ring
    /// resref and identity), reducing an oversized stack in place or unlinking a whole matching
    /// item when it is entirely consumed. <paramref name="charges"/>/<paramref name="maxCharges"/>/
    /// <paramref name="upgrades"/>/<paramref name="flags"/> each accept
    /// <see cref="SharedBlock.CheatFieldUnchanged"/> as a WILDCARD (see
    /// <see cref="StackIdentityMatches"/>'s own remarks) — the wire-driven apply
    /// (<c>CoopCommandDispatcher.DrainAutomaticPartyPool</c>) always passes the exact
    /// <c>PartyPoolStackKey</c> fields a scan reported, but a human's own bare
    /// <c>poolremove &lt;resref&gt;</c> has no identity to give, and coercing that to a literal
    /// 0 used to demand an exact match against whatever an ambient script's own deposit
    /// actually carries. <see cref="CSWSItem.SetPossessor"/> ("SetPossessorFn") marks
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

        // An id can occupy more than one slot if a pre-fix build double-linked it (see
        // AddItemToPartyPool's own remarks and PartyPoolDepositRule). Visiting it twice would
        // credit the same physical units twice — an "asked for 2, removed 2" that took only 1.
        int visitedCount = 0;
        uint[] visited = new uint[snapshot.Length];

        foreach (uint id in snapshot)
        {
            if (removed >= countToRemove)
            {
                break;
            }

            bool seen = false;
            for (int i = 0; i < visitedCount; i++)
            {
                if (visited[i] == id)
                {
                    seen = true;
                    break;
                }
            }

            if (seen)
            {
                continue;
            }

            visited[visitedCount++] = id;

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
