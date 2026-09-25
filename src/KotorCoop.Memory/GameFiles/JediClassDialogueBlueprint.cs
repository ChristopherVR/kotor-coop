namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// Generates <c>k_coop_jediclass.dlg</c>, the private "choose your Jedi path" prompt every
/// OTHER connected player sees on their own screen once someone's own Dorak-style choice has
/// happened (docs/research/class-and-power-grants.md's 2026-09-25 "one player's class must
/// never be copied onto another" section). A pure data mod, same footing as
/// <see cref="PhantomBlueprint"/> and <see cref="SpawnWaypointBlueprint"/>: the field set is
/// copied field-for-field from a shipped single-NPC-line conversation decoded out of the game's
/// own resources (<c>danm13_s.rim:dan13_droid.dlg</c>, chosen for being the smallest dlg in that
/// module — one entry, one reply, both fully populated with every field the loader reads
/// unconditionally), so nothing here guesses at an Aurora dialog struct's shape.
///
/// <b>Deliberately carries NO scripts.</b> Every <c>Script</c> field is blank. Retail's own
/// Dorak choice (<c>k_pdan_makejedi</c>) is a compiled <c>.ncs</c> this repo has no compiler
/// for (see the research doc's "no NCS toolchain" note); rather than build or vendor one, the
/// three terminal entries below are plain, pre-authored dead ends whose ENTRY INDEX alone tells
/// <see cref="Plugin.JediChoicePromptController"/> which class the player picked — the same
/// "current entry index is world truth" fact docs/research/dialogue-sync.md's node-sync section
/// already verified live (<c>CSWSDialog+0x70</c>, read here through the already-shipped
/// <c>Odyssey.GetDialogueNodeIndex</c>). The controller then calls the SAME verified
/// <c>Odyssey.AddMultiClass</c> VM-handler call the peer-apply path already uses — never a raw
/// stats poke — so retail's own class-slot/XP side effects still happen.
///
/// <b>Structure</b> (indices below are GFF list positions, stable because this class builds
/// them, unlike a shipped .dlg's reply-slot compaction docs/research/dialogue-sync.md warns
/// about): <c>StartingList[0]</c> -&gt; <c>EntryList[0]</c>, the prompt line, offering
/// <c>ReplyList[0..2]</c> (Guardian/Consular/Sentinel); each reply links to its own terminal
/// entry (<c>EntryList[1..3]</c>) whose EMPTY <c>RepliesList</c> ends the conversation the same
/// way the shipped sample's own terminal reply does (an empty link list, not a script).
///
/// This conversation is exempted from <c>DialogueSpectateController</c>'s peer mirroring (see
/// that file's <c>JediChoicePromptController.IsPromptActive</c> check) — it is a private,
/// per-player prompt, never a shared world conversation.
/// </summary>
public static class JediClassDialogueBlueprint
{
    /// <summary>The blueprint's resref — what <c>ActionStartConversation</c> asks ResMan for.
    /// 16 characters, the engine's own resref cap.</summary>
    public const string ResRef = "k_coop_jediclass";

    /// <summary>File name as dropped into Override.</summary>
    public const string FileName = ResRef + ".dlg";

    /// <summary><c>EntryList</c> index of the terminal "you are now a Guardian" line.
    /// <see cref="Plugin.JediChoiceRule"/> maps this 1:1 to <c>classes.2da</c> row 3.</summary>
    public const int TerminalEntryGuardian = 1;

    /// <summary><c>EntryList</c> index of the terminal Consular line (classes.2da row 4).</summary>
    public const int TerminalEntryConsular = 2;

    /// <summary><c>EntryList</c> index of the terminal Sentinel line (classes.2da row 5).</summary>
    public const int TerminalEntrySentinel = 3;

    private const int LanguageEnglishMale = 0;

    /// <summary>Builds the .dlg bytes. Deterministic — same output every call.</summary>
    public static byte[] Build()
    {
        GffWriter writer = new("DLG ");
        GffWriterStruct root = writer.Root;

        // Root-level scalar fields, field order and defaults copied from the decoded
        // dan13_droid.dlg (the shipped file's own field set, so the loader finds every field
        // it reads unconditionally).
        root.SetDword("DelayEntry", 0);
        root.SetDword("DelayReply", 0);
        root.SetDword("NumWords", 0);
        root.SetResRef("EndConversation", string.Empty);
        root.SetResRef("EndConverAbort", string.Empty);
        root.SetByte("Skippable", 1);
        root.AddList("StuntList");
        root.SetResRef("CameraModel", string.Empty);
        root.SetString("VO_ID", string.Empty);
        root.SetInt("ConversationType", 0);
        root.SetByte("ComputerType", 0);
        root.SetByte("OldHitCheck", 0);
        root.SetResRef("AmbientTrack", string.Empty);
        root.SetByte("UnequipItems", 0);
        root.SetByte("AnimatedCut", 0);
        root.SetByte("UnequipHItem", 0);

        GffWriterList entries = root.AddList("EntryList");
        AddEntry(entries, "The Force is strong in you. Choose the training you will pursue.",
            replies: [0, 1, 2]);
        AddEntry(entries, "You feel the Force flow through you as a Guardian.", replies: []);
        AddEntry(entries, "You feel the Force flow through you as a Consular.", replies: []);
        AddEntry(entries, "You feel the Force flow through you as a Sentinel.", replies: []);

        GffWriterList replies = root.AddList("ReplyList");
        AddReply(replies, "The path of the Jedi Guardian.", TerminalEntryGuardian);
        AddReply(replies, "The path of the Jedi Consular.", TerminalEntryConsular);
        AddReply(replies, "The path of the Jedi Sentinel.", TerminalEntrySentinel);

        GffWriterList starting = root.AddList("StartingList");
        starting.Add(0).SetDword("Index", 0).SetResRef("Active", string.Empty);

        return writer.ToBytes();
    }

    /// <summary>One <c>EntryList</c> member — field set and order match the decoded sample's
    /// entry struct exactly (17 fields, struct id 0).</summary>
    private static void AddEntry(GffWriterList entries, string text, int[] replies)
    {
        GffWriterStruct entry = entries.Add(0);
        entry.SetString("Speaker", string.Empty);
        entry.AddList("AnimList");
        entry.SetLocString("Text", -1, (LanguageEnglishMale, text));
        entry.SetResRef("VO_ResRef", string.Empty);
        entry.SetResRef("Script", string.Empty); // no NCS — see the class remarks
        entry.SetDword("Delay", 0xFFFFFFFF);
        entry.SetString("Comment", string.Empty);
        entry.SetResRef("Sound", string.Empty);
        entry.SetString("Quest", string.Empty);
        entry.SetInt("PlotIndex", -1);
        entry.SetFloat("PlotXPPercentage", 1f);
        entry.SetString("Listener", string.Empty);
        entry.SetDword("WaitFlags", 0);
        entry.SetDword("CameraAngle", 0);
        entry.SetByte("FadeType", 0);

        GffWriterList repliesList = entry.AddList("RepliesList");
        foreach (int replyIndex in replies)
        {
            repliesList.Add(0)
                .SetDword("Index", (uint)replyIndex)
                .SetResRef("Active", string.Empty)
                .SetByte("IsChild", 0);
        }

        entry.SetByte("SoundExists", 0);
    }

    /// <summary>One <c>ReplyList</c> member — field set and order match the decoded sample's
    /// reply struct exactly (15 fields, struct id 0). Links to exactly one terminal entry.</summary>
    private static void AddReply(GffWriterList replies, string text, int targetEntryIndex)
    {
        GffWriterStruct reply = replies.Add(0);
        reply.AddList("AnimList");
        reply.SetLocString("Text", -1, (LanguageEnglishMale, text));
        reply.SetResRef("VO_ResRef", string.Empty);
        reply.SetResRef("Script", string.Empty); // no NCS — see the class remarks
        reply.SetDword("Delay", 0xFFFFFFFF);
        reply.SetString("Comment", string.Empty);
        reply.SetResRef("Sound", string.Empty);
        reply.SetString("Quest", string.Empty);
        reply.SetInt("PlotIndex", -1);
        reply.SetFloat("PlotXPPercentage", 1f);
        reply.SetString("Listener", string.Empty);
        reply.SetDword("WaitFlags", 0);
        reply.SetDword("CameraAngle", 0);
        reply.SetByte("FadeType", 0);

        GffWriterList entriesList = reply.AddList("EntriesList");
        entriesList.Add(0)
            .SetDword("Index", (uint)targetEntryIndex)
            .SetResRef("Active", string.Empty)
            .SetByte("IsChild", 0);
    }
}
