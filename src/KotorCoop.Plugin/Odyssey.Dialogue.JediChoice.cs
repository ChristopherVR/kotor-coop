namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>The private self-dialogue has invalid GUI participant ids live, even while
    /// its own PC owns a valid server dialog. Resolve the fresh own PC directly, and require
    /// the generated dialogue's complete layout and four exact inline lines. Ordinary NPC
    /// conversations continue using the participant-based node-sync lookup.</summary>
    public static int GetPrivateJediChoiceNodeIndex()
    {
        nint gui = GetInGameGui();
        if (gui == 0 || !ConversationLive(gui)) return -1;
        nint player = GetPlayerCreaturePointer();
        if (player == 0 || !NativeMethods.IsReadable(player, 0x58)) return -1;
        nint candidate = *(nint*)(player + ObjActiveDialog);
        if (candidate == 0 || !NativeMethods.IsReadable(candidate, 0x88)
            || !IsDialogueOwner(player, out nint dialog)) return -1;

        int node = *(int*)(dialog + DialogCurrentEntryIndex);
        if (!JediChoiceRule.IsPrivatePromptLayout(true, true, *(int*)(dialog + 4),
            *(int*)(dialog + 12), *(int*)(dialog + 20), node)) return -1;
        nint entries = *(nint*)(dialog + DialogEntries);
        for (int entry = 0; entry < 4; entry++)
        {
            nint text = entries + entry * DialogEntryStride + NodeText;
            if (!PrivateChoiceTextMatches(text, JediChoiceRule.EntryText(entry))) return -1;
        }
        return node;
    }

    private static bool PrivateChoiceTextMatches(nint text, string expected)
    {
        // CExoLocString->{CExoLocStringInternal: linked-list*,count}; list internal
        // {head,tail,count}; head {prev,next,data}; data {language,CExoString}.
        // Verified against native GetString_2 0x5EC9C0 and the live private prompt.
        if (!NativeMethods.IsReadable(text, 8) || *(uint*)(text + 4) != uint.MaxValue) return false;
        nint loc = *(nint*)text;
        if (loc == 0 || !NativeMethods.IsReadable(loc, 8) || *(int*)(loc + 4) != 1) return false;
        nint list = *(nint*)loc;
        if (list == 0 || !NativeMethods.IsReadable(list, 12) || *(int*)(list + 8) != 1) return false;
        nint head = *(nint*)list;
        if (head == 0 || !NativeMethods.IsReadable(head, 12)) return false;
        nint value = *(nint*)(head + 8);
        if (value == 0 || !NativeMethods.IsReadable(value, 12) || *(uint*)value != 0) return false;
        int length = *(int*)(value + 8);
        if (length != expected.Length && length != expected.Length + 1) return false;
        nint chars = *(nint*)(value + 4);
        if (chars == 0 || !NativeMethods.IsReadable(chars, (nuint)length)) return false;
        for (int i = 0; i < expected.Length; i++) if (((byte*)chars)[i] != expected[i]) return false;
        return length == expected.Length || ((byte*)chars)[expected.Length] == 0;
    }
}
