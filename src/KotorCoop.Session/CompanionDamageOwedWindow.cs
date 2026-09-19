using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Fits the host's owed table into the plugin's fixed row count (<c>SharedBlock.CompanionDamageOwedCapacity</c>
/// = <see cref="ProtocolConstants.PartyRosterSlots"/>, 9). Before heals (2026-09-18) the table
/// could never exceed it — one row per roster slot — but a heal row per slot doubles the
/// possible keys, and <c>TryPublishCompanionDamageOwed</c> truncates silently. A truncated
/// LEVEL table is safe (the plugin diffs each row against what it already applied, and a row
/// absent this pass is simply pending until it reappears), so the fix is a ROTATING window:
/// every row is published within <c>ceil(n / capacity)</c> passes, ~150 ms apart.
/// </summary>
public static class CompanionDamageOwedWindow
{
    /// <summary>
    /// The rows to publish this pass. <paramref name="start"/> is the caller's rotation cursor;
    /// it advances by <paramref name="capacity"/> per call once the table is larger than the
    /// window, and is left alone (reset to 0) while everything fits.
    /// </summary>
    public static IReadOnlyList<CompanionDamageEntry> Slice(
        IReadOnlyList<CompanionDamageEntry> table, int capacity, ref int start)
    {
        if (capacity <= 0 || table.Count == 0)
        {
            start = 0;
            return [];
        }

        if (table.Count <= capacity)
        {
            start = 0;
            return table;
        }

        if (start >= table.Count || start < 0)
        {
            start = 0;
        }

        CompanionDamageEntry[] window = new CompanionDamageEntry[capacity];
        for (int i = 0; i < capacity; i++)
        {
            window[i] = table[(start + i) % table.Count];
        }

        start = (start + capacity) % table.Count;
        return window;
    }
}
