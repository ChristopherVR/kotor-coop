namespace KotorCoop.Memory;

/// <summary>
/// One drain pass's worth of work against KotorCoop.Plugin's item-template-spawn ring:
/// which cursor value to start reading from, how many entries to read, and how many were
/// lost to wraparound before this drain ever got to them.
/// </summary>
/// <param name="StartCursor">The (0-indexed) cursor value of the OLDEST entry this pass
/// should read. Combine with <see cref="ItemTemplateSpawnRing.SlotFor"/> to find its physical
/// ring slot.</param>
/// <param name="Count">How many consecutive cursor values, starting at
/// <paramref name="StartCursor"/>, this pass should read.</param>
/// <param name="Lost">How many entries older than <paramref name="StartCursor"/> were
/// overwritten by the plugin before this drain ever consumed them — nonzero only when more
/// than the ring's capacity was written between two drains.</param>
public readonly record struct ItemTemplateSpawnDrainRange(uint StartCursor, int Count, uint Lost)
{
    /// <summary>What the reader should remember as its own "last seen" cursor once this range
    /// has actually been read — always equal to the block's cursor value at the moment this
    /// range was computed, whether or not anything was lost.</summary>
    public uint NextLastSeenCursor => StartCursor + (uint)Count;
}

/// <summary>
/// Pure arithmetic for draining <c>KotorCoop.Plugin.SharedBlock.OffsetItemTemplateSpawnRing</c>:
/// no engine dependency, no I/O — the two facts a drain needs (which slots hold entries worth
/// reading, and which physical slot a given entry lives in) are both plain integer math over
/// the plugin's own monotonically-increasing write cursor.
///
/// The plugin's cursor (<c>SharedBlock.OffsetItemTemplateSpawnCursor</c>) counts every entry
/// EVER written, never wrapped — the Nth write (1-indexed) reads the cursor's value BEFORE
/// incrementing it (N-1, 0-indexed) as its own slot number, then writes the cursor back as N.
/// So after the plugin has written N entries, the ring physically holds entries for cursor
/// values <c>[max(0, N - capacity), N)</c> — anything older has been overwritten. A reader
/// that remembers the highest cursor value it has already consumed
/// (<paramref name="lastSeenCursor"/> in <see cref="ComputeDrainRange"/>) can therefore always
/// tell, from the block's current cursor value alone, exactly which entries are new AND
/// whether any of them were overwritten before this drain got around to them.
///
/// See docs/research/item-identity.md §13 and <c>ItemTemplateSpawnHook</c>/
/// <c>SharedBlockView.PublishItemTemplateSpawn</c> on the plugin side for the write half this
/// mirrors, and <c>KotorGameBridge.ItemTemplateSpawns.cs</c> for the engine-reading caller.
/// </summary>
public static class ItemTemplateSpawnRing
{
    /// <summary>
    /// What to read this pass, given the cursor value remembered from the previous drain and
    /// the value the block reports right now.
    /// </summary>
    public static ItemTemplateSpawnDrainRange ComputeDrainRange(
        uint lastSeenCursor, uint currentCursor, int capacity)
    {
        if (capacity <= 0)
        {
            return new ItemTemplateSpawnDrainRange(currentCursor, 0, 0);
        }

        // Unsigned subtraction: correct even across the (practically unreachable) point the
        // counter itself wraps past uint.MaxValue — the same "advance by delta" idiom every
        // other free-running counter in this codebase (Heartbeat, bark/effect serials) relies
        // on, rather than a signed comparison that would misbehave right at that wrap.
        uint delta = currentCursor - lastSeenCursor;

        if (delta == 0)
        {
            return new ItemTemplateSpawnDrainRange(currentCursor, 0, 0);
        }

        if (delta > (uint)capacity)
        {
            // Everything strictly older than (currentCursor - capacity) has already been
            // overwritten by the plugin's own next writes — the ring only ever remembers the
            // newest `capacity` entries. Read exactly those, and say how many were skipped.
            uint lost = delta - (uint)capacity;
            return new ItemTemplateSpawnDrainRange(currentCursor - (uint)capacity, capacity, lost);
        }

        return new ItemTemplateSpawnDrainRange(lastSeenCursor, (int)delta, 0);
    }

    /// <summary>The physical ring slot holding the entry written at (0-indexed) cursor value
    /// <paramref name="cursorValue"/>.</summary>
    public static int SlotFor(uint cursorValue, int capacity) =>
        capacity <= 0 ? 0 : (int)(cursorValue % (uint)capacity);
}
