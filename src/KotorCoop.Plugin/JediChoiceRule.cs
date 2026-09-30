namespace KotorCoop.Plugin;

/// <summary>What <see cref="JediChoicePromptController"/> does with a "someone's Jedi choice
/// happened" request, once the local class list has been read.</summary>
internal enum JediChoicePromptVerdict
{
    /// <summary>The class list could not be read this frame (no world, no stats block): ask
    /// again next frame rather than guess.</summary>
    NotReady,

    /// <summary>This character already carries a Jedi class (Guardian/Consular/Sentinel, in
    /// either slot) — never re-prompt (docs/research/class-and-power-grants.md 2026-09-25
    /// "requirement 3").</summary>
    AlreadyHasClass,

    /// <summary>This character has no Jedi class yet: show the popup once it is safe to.</summary>
    NeedsPrompt,
}

/// <summary>
/// The pure "do I need to be asked" rule for the Jedi-choice popup
/// (docs/research/class-and-power-grants.md's 2026-09-25 section): replaces the old
/// "copy the peer's exact class" apply rule. Each player picks their OWN class; this only
/// decides whether they need to be asked at all. Also names the popup's dlg resref and its
/// entry-index -&gt; classes.2da mapping, so the controller and the tests all agree.
///
/// The dlg itself is built by
/// <c>KotorCoop.Memory.GameFiles.JediClassDialogueBlueprint</c> — unreachable from here, since
/// the plugin is NativeAOT win-x86 and never references KotorCoop.Memory (the same project
/// split <c>CheatKindSyncTests.cs</c>'s header documents for
/// <c>CheatKinds</c>/<c>SharedBlock.CheatKind*</c>), so the resref and the three entry indices
/// below are duplicated by hand and cross-checked by
/// <c>JediChoiceRelayTests.TheDialogueConstantsMatchTheBlueprint</c>.
/// </summary>
internal static class JediChoiceRule
{
    public const int Guardian = 3;
    public const int Consular = 4;
    public const int Sentinel = 5;

    /// <summary>The popup's dlg resref. MUST match
    /// <c>KotorCoop.Memory.GameFiles.JediClassDialogueBlueprint.ResRef</c> byte for byte.</summary>
    public const string DialogResRef = "k_coop_jediclass";

    // Exact private-dialogue fingerprint, cross-checked against the generated blueprint.
    public static string EntryText(int entry) => entry switch
    {
        0 => "The Force is strong in you. Choose the training you will pursue.",
        1 => "You feel the Force flow through you as a Guardian.",
        2 => "You feel the Force flow through you as a Consular.",
        3 => "You feel the Force flow through you as a Sentinel.",
        _ => string.Empty,
    };

    public static bool IsPrivatePromptLayout(bool conversationActive, bool localPlayerOwnsDialog,
        int entries, int replies, int starts, int node) => conversationActive && localPlayerOwnsDialog
        && entries == 4 && replies == 3 && starts == 1 && node is >= 0 and < 4;

    /// <summary><c>EntryList</c> index of the popup's terminal Guardian line. MUST match
    /// <c>JediClassDialogueBlueprint.TerminalEntryGuardian</c>.</summary>
    public const int TerminalEntryGuardian = 1;

    /// <summary>MUST match <c>JediClassDialogueBlueprint.TerminalEntryConsular</c>.</summary>
    public const int TerminalEntryConsular = 2;

    /// <summary>MUST match <c>JediClassDialogueBlueprint.TerminalEntrySentinel</c>.</summary>
    public const int TerminalEntrySentinel = 3;

    public static bool IsJediClass(int classId) => classId is >= Guardian and <= Sentinel;

    /// <summary>The class a terminal <c>EntryList</c> index maps to, or null when
    /// <paramref name="entryIndex"/> is not one of the popup's three terminal lines (still on
    /// the prompt line itself, or no conversation at all: <c>Odyssey.GetDialogueNodeIndex</c>
    /// returns -1 for that).</summary>
    public static int? ClassForTerminalEntry(int entryIndex) => entryIndex switch
    {
        TerminalEntryGuardian => Guardian,
        TerminalEntryConsular => Consular,
        TerminalEntrySentinel => Sentinel,
        _ => null,
    };

    /// <summary>
    /// Reads like <see cref="ClassGrantApplyRule.Decide"/>'s "already has it" half, but the
    /// question is now "ANY Jedi class", not "this exact one" — nobody's own pick may ever be
    /// forced onto another player's character.
    /// </summary>
    public static JediChoicePromptVerdict Decide(bool listReadable, int classCount, int class0, int class1)
    {
        if (!listReadable)
        {
            return JediChoicePromptVerdict.NotReady;
        }

        bool hasJediClass = (classCount >= 1 && IsJediClass(class0)) || (classCount >= 2 && IsJediClass(class1));
        return hasJediClass ? JediChoicePromptVerdict.AlreadyHasClass : JediChoicePromptVerdict.NeedsPrompt;
    }
}
