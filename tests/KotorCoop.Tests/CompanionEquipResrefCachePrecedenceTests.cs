using System.Runtime.InteropServices;
using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Root cause 2 of docs/research/companion-state-sync.md's 2026-08-28 "mid-session re-equip
/// never propagated" gap: <see cref="KotorGameBridge.ReadEquippedResref"/> — the read every
/// companion-gear and PC-equipment publish goes through — used to consult ONLY the vtable/Tag
/// scan cache (<c>_itemTags</c>, rate-limited to one rebuild per 15 seconds regardless of how
/// many lookups miss) and never <c>_spawnedItemResrefs</c>, the cache
/// <see cref="KotorGameBridge.RememberSpawnedItemResref"/> populates and
/// <see cref="KotorGameBridge.DrainItemTemplateSpawns"/> feeds the instant the plugin's
/// <c>ItemTemplateSpawnHook</c> ring reports a brand-new item (docs/research/item-identity.md
/// §13) — exactly what a companion re-equip is. <see cref="KotorGameBridge.TryGetItemResref"/>
/// already had the right precedence (see <c>ItemTemplateSpawnCachePrecedenceTests</c>);
/// <c>ReadEquippedResref</c> now delegates to it instead of duplicating half the logic.
///
/// Self-attached, no game process needed: a "creature" and its "inventory" are fabricated in
/// the TEST HOST's own unmanaged memory (a handful of targeted reads, not a committed-memory
/// SWEEP — <c>ItemTemplateSpawnCachePrecedenceTests</c>' own header explains why a sweep against
/// the test host's GC heap is the thing to avoid, not a few pointer-chase reads).
/// </summary>
public sealed class CompanionEquipResrefCachePrecedenceTests : IDisposable
{
    // Must match KotorGameBridge.Attach.cs's own (private) constants.
    private const int OffInventory = 0xA2C;
    private const int InvSlotRightWeapon = 0x14;

    private readonly ProcessMemory _memory = ProcessMemory.Attach(Environment.ProcessId);
    private readonly KotorGameBridge _bridge;
    private readonly nint _creature;
    private readonly nint _inventory;

    public CompanionEquipResrefCachePrecedenceTests()
    {
        _bridge = new KotorGameBridge(_memory, ownsMemory: false);

        // A creature is a much bigger structure in the real engine; this test only ever reads
        // OffInventory out of it, so a small zeroed block is enough.
        _creature = Marshal.AllocHGlobal(0x1000);
        _inventory = Marshal.AllocHGlobal(0x40);
        NativeMemset(_creature, 0, 0x1000);
        NativeMemset(_inventory, 0, 0x40);

        Marshal.WriteIntPtr(_creature + OffInventory, _inventory);
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _memory.Dispose();
        Marshal.FreeHGlobal(_creature);
        Marshal.FreeHGlobal(_inventory);
    }

    private static unsafe void NativeMemset(nint address, byte value, int length)
    {
        new Span<byte>((void*)address, length).Fill(value);
    }

    [Fact]
    public void AFreshlyEquippedItemResolvesImmediatelyFromTheSpawnCache()
    {
        const uint itemId = 0x9001;
        Marshal.WriteInt32(_inventory + InvSlotRightWeapon, unchecked((int)itemId));

        // The fact the ItemTemplateSpawnHook ring would have fed this session the instant
        // EquipItemFromTemplateDirect's own CreateItemOnObject call produced the item —
        // BEFORE any vtable/Tag rescan (rate-limited to once per 15s) ever ran.
        _bridge.RememberSpawnedItemResref(itemId, "g_w_vbrdblswd01");

        string resref = _bridge.ReadEquippedResref(_creature, InvSlotRightWeapon);

        Assert.Equal("g_w_vbrdblswd01", resref);
    }

    [Fact]
    public void AnEmptyInventorySlotReadsEmpty()
    {
        // 0 = nothing equipped; must never be looked up as an id.
        Assert.Equal(string.Empty, _bridge.ReadEquippedResref(_creature, InvSlotRightWeapon));
    }

    [Fact]
    public void ObjectInvalidInTheSlotReadsEmpty()
    {
        Marshal.WriteInt32(_inventory + InvSlotRightWeapon, unchecked((int)0x7F000000u));

        Assert.Equal(string.Empty, _bridge.ReadEquippedResref(_creature, InvSlotRightWeapon));
    }

    // ⚠ DELIBERATELY NOT TESTED HERE: an item id neither cache has ever seen. That falls
    // through TryGetItemResref into EnsureItemKnown's rebuild gate, which — on a freshly
    // constructed bridge (_itemTagsBuiltAtMs still 0) — is immediately open and would trigger a
    // REAL RebuildItemTagCache sweep of committed memory. Against THIS (self-attached, test
    // host) process that is exactly the "scan the test host's own GC heap" hazard
    // ItemTemplateSpawnCachePrecedenceTests' own header warns can kill the runtime; that class
    // avoids it the same way, by only ever exercising a cache HIT — the miss/rebuild fallback
    // path is TryGetItemResref's own concern and is already covered by
    // ItemTemplateSpawnCachePrecedenceTests and the wider KotorGameBridge test suite.
}
