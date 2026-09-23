namespace KotorCoop.Plugin;

// Vanilla-script item-template-spawn ring (version 58, region 0x4100..0x4604, TotalSize
// grown to 0x4700 to hold it). Version 59 (docs/research/party-pool-sync.md §20) widened each
// entry from 0x14 to 0x18 for a per-entry "created by our own cheat, not a vanilla script" flag
// (ItemTemplateSpawnIsCheatOffset) — the ring now spans 0x4100..0x4700 and the cursor moves to
// 0x4700..0x4704, so TotalSize grew again to 0x4800.
//
// docs/research/item-identity.md §12 and docs/research/dialogue-consequences.md's
// 2026-08-28 round both re-confirmed the same gap and left it open: a live CSWSItem never
// retains its own template resref (no TemplateResRef field or accessor exists anywhere in
// the binary), so an item a VANILLA KOTOR script places directly into a creature's
// inventory — CreateItemOnObject("g_i_progspike01", GetFirstPC()), the exact stock-content
// case docs/research/playthrough-endar-spire.md step 5 live-confirmed with
// g_i_progspike01/k_computer_spike — can only ever be described by its Tag afterward, which
// is only a proxy for the resref and sometimes genuinely differs from it. The 2026-08-28
// round's own _spawnedItemResrefs cache (KotorGameBridge.GroundItems.cs) already closes this
// for a body THIS SESSION told the plugin to spawn onto the floor, but nothing recorded what
// a vanilla script's own CreateItemOnObject call actually asked for — there was no spawn
// record on either side for that case.
//
// ItemTemplateSpawnHook closes it at the source: a detour on
// CSWVirtualMachineCommands::ExecuteCommandCreateItemOnObject (0x0052F570) — see that file's
// own remarks for exactly which instruction inside it is hooked and why — captures (object
// id, resref) for every item that function's OWN engine call actually creates, whether a
// script asked for it or a player's own giveloot/scriptgive cheat did (both funnel through
// the identical VM command). This ring is how the plugin hands that to the external agent:
// a small bounded buffer with a monotonically increasing write cursor, so the agent can tell
// "nothing new" from "N new entries" from "more happened than I had room to read" without a
// per-slot serial for every one of 64 slots. See docs/research/item-identity.md §13.
internal partial struct SharedBlock
{
    /// <summary>
    /// Plugin -&gt; agent. A bounded ring of (object id, 16-byte resref, cheat flag) triples, one
    /// per <c>CreateItemOnObject</c> call this instance's own engine actually executed and
    /// produced a real item for (a failed create, or one whose id never resolved past
    /// <c>OBJECT_INVALID</c>, is never entered — see <c>ItemTemplateSpawnHook</c>).
    ///
    /// Entry stride <see cref="ItemTemplateSpawnEntryStride"/> (0x18): object id (u32) at
    /// <see cref="ItemTemplateSpawnIdOffset"/>, then 16 raw bytes at
    /// <see cref="ItemTemplateSpawnResrefOffset"/> — a CResRef is <c>char[16]</c>, not a C
    /// string and not necessarily NUL-terminated, the same convention
    /// <see cref="BarkResRefOffset"/> already uses for the identical reason — then a dword flags
    /// field (version 59, only bit 0 defined) at <see cref="ItemTemplateSpawnIsCheatOffset"/>.
    /// Occupies <see cref="ItemTemplateSpawnRingCapacity"/> (64) entries, 0x4100..0x4700.
    /// </summary>
    public const int OffsetItemTemplateSpawnRing = 0x4100;

    /// <summary>How many (id, resref, flags) triples the ring remembers before an unread one is
    /// overwritten. Generous past anything one script/cheat burst produces in the time
    /// between two agent drains — the same "past the failure, not a limit that bites"
    /// reasoning <see cref="OffsetItemTemplateSpawnCursor"/>'s own overflow note relies on.</summary>
    public const int ItemTemplateSpawnRingCapacity = 64;

    /// <summary>4 (id) + 16 (resref) + 4 (flags) bytes per entry. Grew from 0x14 (version 58) to
    /// 0x18 in version 59 for <see cref="ItemTemplateSpawnIsCheatOffset"/> — see
    /// docs/research/party-pool-sync.md §20.</summary>
    public const int ItemTemplateSpawnEntryStride = 0x18;

    public const int ItemTemplateSpawnIdOffset = 0x00;

    public const int ItemTemplateSpawnResrefOffset = 0x04;

    /// <summary>A CResRef is a fixed <c>char[16]</c>.</summary>
    public const int ItemTemplateSpawnResrefLength = 16;

    /// <summary>
    /// Plugin -&gt; agent, version 59. Dword flags for this ring entry; bit 0
    /// (<see cref="ItemTemplateSpawnIsCheatBit"/>) is set when
    /// <c>ItemTemplateSpawnHook.NextSpawnIsCheat</c> was true at the moment this entry's own
    /// <c>CreateItemOnObject</c> call ran — i.e. this instance's own code (a human's console
    /// cheat, an automatic wire-apply, <c>Odyssey.AddItemToPartyPool</c>'s own deposit) made the
    /// call, NOT a vanilla script's heartbeat/plot logic. See
    /// <c>ItemTemplateSpawnHook.NextSpawnIsCheat</c>'s own doc comment for why nothing else can
    /// derive this distinction, and docs/research/party-pool-sync.md §20 for the party-pool
    /// ambient-noise suppression it exists to feed.
    /// </summary>
    public const int ItemTemplateSpawnIsCheatOffset = 0x14;

    public const uint ItemTemplateSpawnIsCheatBit = 1u << 0;

    /// <summary>
    /// Plugin -&gt; agent, version 64 (2026-09-02). Bits 8..23 of the same flags dword carry the
    /// stack count the <c>CreateItemOnObject</c> call asked for (its <c>nStackSize</c>, 1 when
    /// omitted, clamped by the engine to the template's maximum) — 0 when the hook could not
    /// read it. The id in the entry is the SURVIVING stack, not a temporary (see
    /// <c>ItemTemplateSpawnHook.StackCount</c>'s own remarks for the merge order that makes it
    /// so), so this is the only figure that says how much of that stack the call actually
    /// contributed; the agent's ambient-noise suppression subtracts exactly this, never the
    /// whole stack. docs/research/shared-inventory-v2.md §17.
    /// </summary>
    public const int ItemTemplateSpawnStackCountShift = 8;

    public const uint ItemTemplateSpawnStackCountMask = 0xFFFFu;

    /// <summary>Version 75. The plugin's item/world generation at each ring slot, captured
    /// by the engine hook when the item is created. Entries from before an <c>InvalidateAll</c>
    /// edge must never name an object id reused by the next module.</summary>
    public const int OffsetItemTemplateSpawnGenerations = 0x4F00;

    /// <summary>Version 75. Current generation for item-template-spawn provenance. Incremented
    /// whenever PluginHost invalidates world-owned state (including ordinary announced module
    /// changes, which do not necessarily bump WorldLoadSerial).</summary>
    public const int OffsetItemTemplateSpawnGeneration = 0x5000;

    /// <summary>Version 75. Each row's full cursor value, published after its payload and
    /// before the global cursor. Readers can reject a row overwritten during a drain even
    /// when the replacement belongs to the same world generation.</summary>
    public const int OffsetItemTemplateSpawnTickets = 0x5100;

    /// <summary>Version 75. WorldLoadSerial sampled at the exact native create-item hook.
    /// Kept separately from ItemTemplateSpawnGeneration because announced module transitions
    /// advance generation without advancing WorldLoadSerial.</summary>
    public const int OffsetItemTemplateSpawnWorldSerials = 0x5200;

    /// <summary>
    /// Plugin -&gt; agent. A count of entries EVER written, never wrapped or reset — the slot
    /// for the Nth write (1-indexed) is <c>(N - 1) % ItemTemplateSpawnRingCapacity</c>.
    /// Written LAST, strictly after that write's own id+resref payload
    /// (<see cref="SharedBlockView.PublishItemTemplateSpawn"/>), so a reader that checks this
    /// value first and then only trusts entries the value it read actually covers can never
    /// observe a half-written slot — the same "counter/serial written last" discipline every
    /// other ring in this block (barks, peer effects, damage ledger) already follows.
    ///
    /// The agent's own drain remembers the cursor value it last consumed. If this value has
    /// advanced by more than <see cref="ItemTemplateSpawnRingCapacity"/> since then, some
    /// entries in that gap were overwritten before the agent ever read them and are reported
    /// LOST rather than silently skipped — see <c>KotorGameBridge.ItemTemplateSpawns.cs</c>'s
    /// own drain method.
    /// </summary>
    public const int OffsetItemTemplateSpawnCursor =
        OffsetItemTemplateSpawnRing + (ItemTemplateSpawnRingCapacity * ItemTemplateSpawnEntryStride);
}
