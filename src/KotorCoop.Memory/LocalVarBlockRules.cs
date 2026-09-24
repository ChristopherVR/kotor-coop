using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>One capture-ring row as the agent read it out of the plugin block.</summary>
public readonly record struct LocalVarCaptureRow(uint ModuleHash, uint EntryWord, uint Generation, uint Ticket);

/// <summary>
/// The pure decisions behind <c>KotorGameBridge.LocalVars.cs</c>, split out so they are unit
/// tested without a game: which capture-ring rows are trustworthy local writes, and which of the
/// session's flags go into the plugin's bounded apply list.
/// </summary>
public static class LocalVarBlockRules
{
    /// <summary>Mirrors SharedBlock.LocalVarCaptureCapacity in the plugin.</summary>
    public const int CaptureCapacity = 16;

    /// <summary>Mirrors SharedBlock.LocalVarApplyCapacity in the plugin.</summary>
    public const int ApplyCapacity = 28;

    /// <summary>
    /// Turns the rows published since <paramref name="drained"/> into flag changes. A row is
    /// kept only when its ticket is the position the cursor says it holds (not overwritten or
    /// mid-rewrite), it was captured under <paramref name="generation"/> (the plugin's current
    /// world) and in <paramref name="moduleHash"/> (the module the agent reads now), and it
    /// decodes to a valid GIT id and slot. <paramref name="lost"/> counts rows the ring
    /// overwrote before this drain could read them.
    /// </summary>
    public static List<KeyValuePair<ulong, sbyte>> Drain(
        uint cursor, uint drained, uint generation, uint moduleHash,
        Func<uint, LocalVarCaptureRow> readRowForTicket, out uint lost)
    {
        List<KeyValuePair<ulong, sbyte>> changes = [];
        lost = 0;

        uint pending = unchecked(cursor - drained);
        if (pending == 0 || pending > int.MaxValue)
        {
            return changes;
        }

        uint first = drained;
        if (pending > CaptureCapacity)
        {
            lost = pending - CaptureCapacity;
            first = unchecked(cursor - CaptureCapacity);
        }

        if (generation == 0)
        {
            return changes;
        }

        for (uint ticket = unchecked(first + 1); ticket != unchecked(cursor + 1); ticket = unchecked(ticket + 1))
        {
            LocalVarCaptureRow row = readRowForTicket(ticket);
            if (row.Ticket != ticket || row.Generation != generation || row.ModuleHash != moduleHash)
            {
                continue;
            }

            LocalVarSyncRule.UnpackEntry(row.EntryWord, out ushort objectId, out LocalVarKind kind, out byte slot, out sbyte value);
            if (!LocalVarSyncRule.IsGitObjectId(objectId) || !LocalVarSyncRule.IsValidSlot(kind, slot))
            {
                continue;
            }

            changes.Add(new KeyValuePair<ulong, sbyte>(
                LocalVarSyncRule.MakeKey(moduleHash, objectId, kind, slot),
                LocalVarSyncRule.NormaliseValue(kind, value)));
        }

        return changes;
    }

    /// <summary>
    /// The session's local-variable flags for <paramref name="moduleHash"/>, packed as plugin
    /// entry words, ordered by key so the list (and its change signature) is stable. At most
    /// <see cref="ApplyCapacity"/>; a larger set rotates through <paramref name="cursor"/> so
    /// every key is eventually offered (an audited allowlist keeps a module far below this).
    /// </summary>
    public static List<uint> SelectApplyEntries(
        IReadOnlyList<KeyValuePair<ulong, sbyte>> flags, uint moduleHash, ref int cursor)
    {
        List<KeyValuePair<ulong, sbyte>> mine = [];
        foreach (KeyValuePair<ulong, sbyte> flag in flags)
        {
            if (LocalVarSyncRule.TryParseKey(flag.Key, out uint module, out _, out _, out _) && module == moduleHash)
            {
                mine.Add(flag);
            }
        }

        mine.Sort((a, b) => a.Key.CompareTo(b.Key));
        if (mine.Count == 0)
        {
            cursor = 0;
            return [];
        }

        int start = mine.Count <= ApplyCapacity ? 0 : Math.Clamp(cursor, 0, mine.Count - 1);
        int count = Math.Min(ApplyCapacity, mine.Count);
        List<uint> words = new(count);
        for (int i = 0; i < count; i++)
        {
            KeyValuePair<ulong, sbyte> flag = mine[(start + i) % mine.Count];
            LocalVarSyncRule.TryParseKey(flag.Key, out _, out ushort objectId, out LocalVarKind kind, out byte slot);
            words.Add(LocalVarSyncRule.PackEntry(objectId, kind, slot, LocalVarSyncRule.NormaliseValue(kind, flag.Value)));
        }

        cursor = mine.Count <= ApplyCapacity ? 0 : (start + count) % mine.Count;
        return words;
    }
}
