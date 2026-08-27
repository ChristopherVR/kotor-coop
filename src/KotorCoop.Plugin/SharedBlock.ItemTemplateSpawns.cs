namespace KotorCoop.Plugin;

// Vanilla-script item-template-spawn ring (version 58, region 0x4100..0x4604, TotalSize
// grown to 0x4700 to hold it).
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
    /// Plugin -&gt; agent. A bounded ring of (object id, 16-byte resref) pairs, one per
    /// <c>CreateItemOnObject</c> call this instance's own engine actually executed and
    /// produced a real item for (a failed create, or one whose id never resolved past
    /// <c>OBJECT_INVALID</c>, is never entered — see <c>ItemTemplateSpawnHook</c>).
    ///
    /// Entry stride <see cref="ItemTemplateSpawnEntryStride"/> (0x14): object id (u32) at
    /// <see cref="ItemTemplateSpawnIdOffset"/>, then 16 raw bytes at
    /// <see cref="ItemTemplateSpawnResrefOffset"/> — a CResRef is <c>char[16]</c>, not a C
    /// string and not necessarily NUL-terminated, the same convention
    /// <see cref="BarkResRefOffset"/> already uses for the identical reason. Occupies
    /// <see cref="ItemTemplateSpawnRingCapacity"/> (64) entries, 0x4100..0x4600.
    /// </summary>
    public const int OffsetItemTemplateSpawnRing = 0x4100;

    /// <summary>How many (id, resref) pairs the ring remembers before an unread one is
    /// overwritten. Generous past anything one script/cheat burst produces in the time
    /// between two agent drains — the same "past the failure, not a limit that bites"
    /// reasoning <see cref="OffsetItemTemplateSpawnCursor"/>'s own overflow note relies on.</summary>
    public const int ItemTemplateSpawnRingCapacity = 64;

    /// <summary>4 (id) + 16 (resref) bytes per entry.</summary>
    public const int ItemTemplateSpawnEntryStride = 0x14;

    public const int ItemTemplateSpawnIdOffset = 0x00;

    public const int ItemTemplateSpawnResrefOffset = 0x04;

    /// <summary>A CResRef is a fixed <c>char[16]</c>.</summary>
    public const int ItemTemplateSpawnResrefLength = 16;

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
