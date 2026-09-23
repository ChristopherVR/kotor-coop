using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pure ring-math tests for <see cref="ItemTemplateSpawnRing"/> — the drain-range/slot
/// arithmetic <c>KotorGameBridge.ItemTemplateSpawns.cs</c> uses to read
/// <c>KotorCoop.Plugin.SharedBlock.OffsetItemTemplateSpawnRing</c> without ever touching a
/// game process. See docs/research/item-identity.md §13.
///
/// The contract under test: the plugin's write cursor counts every entry EVER written
/// (never wrapped); a reader remembers the highest cursor value it has already consumed and,
/// each pass, works out which (0-indexed) cursor values are new, which physical ring slot
/// each one lives in, and how many were overwritten before this reader ever got to them.
/// </summary>
public class ItemTemplateSpawnRingTests
{
    private const int Capacity = 64;

    [Fact]
    public void FreshAgentRejectsBufferedRowFromPreviousModuleGeneration()
    {
        // Cursor may still point at a row from the previous module when a new agent starts.
        // Matching IDs/resrefs are immaterial: only producer-captured generation is trusted.
        Assert.False(ItemTemplateSpawnRing.IsStableCurrentEntry(
            expectedCursor: 4,
            currentGenerationBefore: 9,
            entryGenerationBefore: 8,
            ticketBefore: 4,
            worldSerialBefore: 12,
            entryGenerationAfter: 8,
            ticketAfter: 4,
            worldSerialAfter: 12,
            currentGenerationAfter: 9));
    }

    [Fact]
    public void NewWorldRowIsAcceptedWithItsCapturedLoadSerial()
    {
        Assert.True(ItemTemplateSpawnRing.IsStableCurrentEntry(
            expectedCursor: 0,
            currentGenerationBefore: 10,
            entryGenerationBefore: 10,
            ticketBefore: 0,
            worldSerialBefore: 13,
            entryGenerationAfter: 10,
            ticketAfter: 0,
            worldSerialAfter: 13,
            currentGenerationAfter: 10));
    }

    [Fact]
    public void OverwriteInProgressOrGenerationChangeDuringReadIsRejected()
    {
        // The writer invalidates generation and changes the ticket before touching payload.
        Assert.False(ItemTemplateSpawnRing.IsStableCurrentEntry(
            64, 10, 0, ~64u, 13, 10, 64, 13, 10));

        Assert.False(ItemTemplateSpawnRing.IsStableCurrentEntry(
            64, 10, 10, 64, 13, 10, 64, 13, 11));
    }

    [Fact]
    public void CapturedLoadSerialMustNotTearDuringEntryRead()
    {
        Assert.False(ItemTemplateSpawnRing.IsStableCurrentEntry(
            3, 2, 2, 3, 5, 2, 3, 6, 2));
    }

    [Fact]
    public void NoNewWritesMeansNothingToRead()
    {
        ItemTemplateSpawnDrainRange range = ItemTemplateSpawnRing.ComputeDrainRange(
            lastSeenCursor: 5, currentCursor: 5, Capacity);

        Assert.Equal(0, range.Count);
        Assert.Equal(0u, range.Lost);
        Assert.Equal(5u, range.NextLastSeenCursor);
    }

    [Fact]
    public void TheFirstDrainReadsEverythingFromZero()
    {
        ItemTemplateSpawnDrainRange range = ItemTemplateSpawnRing.ComputeDrainRange(
            lastSeenCursor: 0, currentCursor: 3, Capacity);

        Assert.Equal(0u, range.StartCursor);
        Assert.Equal(3, range.Count);
        Assert.Equal(0u, range.Lost);
        Assert.Equal(3u, range.NextLastSeenCursor);
    }

    [Fact]
    public void APartialAdvanceReadsExactlyTheNewCursorValues()
    {
        ItemTemplateSpawnDrainRange range = ItemTemplateSpawnRing.ComputeDrainRange(
            lastSeenCursor: 10, currentCursor: 13, Capacity);

        Assert.Equal(10u, range.StartCursor);
        Assert.Equal(3, range.Count);
        Assert.Equal(0u, range.Lost);
        Assert.Equal(13u, range.NextLastSeenCursor);
    }

    /// <summary>Advancing by EXACTLY the ring's capacity is the boundary: every one of those
    /// entries is still physically present (nothing has been overwritten YET), so nothing is
    /// lost.</summary>
    [Fact]
    public void AdvancingByExactlyTheCapacityLosesNothing()
    {
        ItemTemplateSpawnDrainRange range = ItemTemplateSpawnRing.ComputeDrainRange(
            lastSeenCursor: 0, currentCursor: Capacity, Capacity);

        Assert.Equal(0u, range.StartCursor);
        Assert.Equal(Capacity, range.Count);
        Assert.Equal(0u, range.Lost);
        Assert.Equal((uint)Capacity, range.NextLastSeenCursor);
    }

    /// <summary>One write past the capacity is the first case that actually loses one: entry 0
    /// has been overwritten by entry <c>Capacity</c> (both land in slot 0), so the drain can
    /// only recover the newest <c>Capacity</c> entries.</summary>
    [Fact]
    public void OneWriteBeyondCapacityLosesExactlyOne()
    {
        ItemTemplateSpawnDrainRange range = ItemTemplateSpawnRing.ComputeDrainRange(
            lastSeenCursor: 0, currentCursor: Capacity + 1, Capacity);

        Assert.Equal(1u, range.StartCursor);
        Assert.Equal(Capacity, range.Count);
        Assert.Equal(1u, range.Lost);
        Assert.Equal((uint)Capacity + 1, range.NextLastSeenCursor);
    }

    /// <summary>A reader that fell far behind (many entries written between two drains) only
    /// ever recovers the newest <c>Capacity</c> of them, and reports the rest as lost rather
    /// than silently reading garbage or double-counting.</summary>
    [Fact]
    public void FallingFarBehindRecoversOnlyTheNewestCapacityEntries()
    {
        ItemTemplateSpawnDrainRange range = ItemTemplateSpawnRing.ComputeDrainRange(
            lastSeenCursor: 0, currentCursor: 1000, Capacity);

        Assert.Equal(1000u - Capacity, range.StartCursor);
        Assert.Equal(Capacity, range.Count);
        Assert.Equal(1000u - Capacity, range.Lost);
        Assert.Equal(1000u, range.NextLastSeenCursor);
    }

    /// <summary>
    /// The property that makes frequent draining safe rather than merely convenient: two
    /// SMALL passes (draining once mid-stream, again after more writes land, neither gap
    /// exceeding the capacity) lose NOTHING between them, even though the SAME total span
    /// drained in a single late pass loses some — 90 writes is 26 past this ring's 64-entry
    /// capacity. A drain cadence that keeps each individual gap under the capacity can never
    /// lose an entry no matter how long the SESSION runs; only a gap between two drains wider
    /// than the capacity can.
    /// </summary>
    [Fact]
    public void TwoSmallDrainsLoseNothingWhereOneLateDrainOverTheSameSpanWould()
    {
        ItemTemplateSpawnDrainRange first = ItemTemplateSpawnRing.ComputeDrainRange(
            lastSeenCursor: 0, currentCursor: 40, Capacity);
        ItemTemplateSpawnDrainRange second = ItemTemplateSpawnRing.ComputeDrainRange(
            first.NextLastSeenCursor, currentCursor: 90, Capacity);

        Assert.Equal(0u, first.Lost);
        Assert.Equal(0u, second.Lost);
        Assert.Equal(40, first.Count);
        Assert.Equal(50, second.Count);
        Assert.Equal(90u, second.NextLastSeenCursor);

        ItemTemplateSpawnDrainRange combined = ItemTemplateSpawnRing.ComputeDrainRange(
            lastSeenCursor: 0, currentCursor: 90, Capacity);
        Assert.Equal(90u - Capacity, combined.Lost);
        Assert.True(combined.Lost > first.Lost + second.Lost);
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(63u, 63)]
    [InlineData(64u, 0)]
    [InlineData(65u, 1)]
    [InlineData(128u, 0)]
    [InlineData(1000u, 1000 % Capacity)]
    public void SlotForWrapsAtCapacity(uint cursorValue, int expectedSlot)
    {
        Assert.Equal(expectedSlot, ItemTemplateSpawnRing.SlotFor(cursorValue, Capacity));
    }

    [Fact]
    public void EveryEntryInAFullDrainMapsToADistinctSlotWhenNothingWasLost()
    {
        ItemTemplateSpawnDrainRange range = ItemTemplateSpawnRing.ComputeDrainRange(
            lastSeenCursor: 0, currentCursor: Capacity, Capacity);

        HashSet<int> slots = [];
        for (int i = 0; i < range.Count; i++)
        {
            int slot = ItemTemplateSpawnRing.SlotFor(range.StartCursor + (uint)i, Capacity);
            Assert.True(slots.Add(slot), $"slot {slot} was visited twice");
        }

        Assert.Equal(Capacity, slots.Count);
    }

    [Fact]
    public void ZeroCapacityReadsNothingRatherThanDividingByZero()
    {
        ItemTemplateSpawnDrainRange range = ItemTemplateSpawnRing.ComputeDrainRange(
            lastSeenCursor: 0, currentCursor: 10, capacity: 0);

        Assert.Equal(0, range.Count);
        Assert.Equal(0, ItemTemplateSpawnRing.SlotFor(cursorValue: 5, capacity: 0));
    }
}
