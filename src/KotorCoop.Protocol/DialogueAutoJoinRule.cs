namespace KotorCoop.Protocol;

/// <summary>
/// <c>coop.ini dialogue_auto_join</c>'s three settings. Default <see cref="Off"/> (2026-08-23:
/// changed from <see cref="Near"/> — docs/research/dialogue-sync.md's Item 1. The user does not
/// want ordinary player-clicked conversations to auto-pull the other player, ever; only a
/// FORCED conversation should, and that path bypasses this mode entirely — see
/// <see cref="ShouldAutoJoin"/>'s <c>isForced</c> parameter).
/// </summary>
public enum DialogueAutoJoinMode
{
    /// <summary>The default: never pulled in automatically for an ORDINARY conversation, only
    /// by clicking the same NPC (docs/research/shared-state-audit.md's "force_dialogue_party,
    /// and why it is OFF") — spectate and click-to-join/walk-over instead. A FORCED
    /// conversation (see <see cref="DialogueAutoJoinRule.ShouldAutoJoin"/>'s <c>isForced</c>)
    /// still auto-joins regardless of this setting; that is not this mode's concern.</summary>
    Off = 0,

    /// <summary>Auto-join when close enough to the conversation's NPC not to feel like being
    /// yanked across the module — the default.</summary>
    Near = 1,

    /// <summary>Auto-join regardless of distance (<c>dialogue_join_teleport</c> covers the
    /// gap, same as a click from far away already does).</summary>
    Always = 2,
}

/// <summary>
/// The pure decision this codebase's own "everything is symmetric" rule needs for dialogue:
/// when one player starts a conversation, does the OTHER player auto-join it, or do they see
/// the "walk over to join" notice instead?
///
/// Deliberately free of every engine read this decision depends on (distance, combat state,
/// minigame state, whether the local player is already in their own conversation) — those live
/// in <c>InteractionLockController</c> (plugin), which is the one place with a live world to
/// read them from. This class only ever answers "given these facts, should I auto-join", which
/// is what makes it testable outside the game at all (the plugin assembly is x86 NativeAOT and
/// nothing references it — see this project's own README).
///
/// Symmetric by construction, not by convention: the SAME rule runs on every peer, deciding
/// FOR ITSELF whether to auto-join a conversation SOMEONE ELSE started, using only that peer's
/// OWN local facts (its own distance to the partner, its own combat/minigame state). Nothing
/// here reads "am I the host" — there is no such input to read — so a client-started
/// conversation and a host-started one are evaluated by the identical code path on whichever
/// peer would be joining, with no branch that could favour one direction over the other. See
/// <c>DialogueAutoJoinRuleTests</c>'s own symmetry test for the check this claim rests on.
/// </summary>
public static class DialogueAutoJoinRule
{
    /// <summary>Conversations whose node scripts move ONE player into a solo module: the
    /// Manaan airlock (strips the party), and the swoop-race announcers/entry talks, whose
    /// start node loads the single-seat track. A watcher that auto-joins runs those scripts on
    /// its own engine and follows (live 2026-10-01: the Manaan joiner left the station with the
    /// host; 2026-10-02 the Taris swoop joiner, and the host in a joiner-led race, were pulled
    /// onto the track). Matched case-insensitively against the dialogue resref.</summary>
    private static readonly string[] SoloEntryConversations =
    [
        "man28_airlock",
        "tar03_announcer",
        "man26_swoop1", "man26_swoop1a", "man26_swoop1q", "man26_swoop1r", "man26_swoop2", "man26_swoopgen01",
        "tat17_xracer_01", "tat17_04race_01", "tat17_04swoop_01",
    ];

    /// <summary>The same conversations by the speaker's TAG, for the case where no resref is
    /// known: a default-dialogue start (BeginConversation with an empty name) publishes an empty
    /// resref, so the host never saw it for a joiner-led race (live 2026-10-02, tag
    /// <c>raceannoun031</c>). Exact for the Taris announcer, prefix for the other tracks'
    /// race NPCs (their placed tags follow the template names).</summary>
    public static bool NeverAutoJoinTag(string tag) =>
        string.Equals(tag, "raceannoun031", StringComparison.OrdinalIgnoreCase)
        || tag.StartsWith("man26_swoop", StringComparison.OrdinalIgnoreCase)
        || tag.StartsWith("tat17_04race", StringComparison.OrdinalIgnoreCase)
        || tag.StartsWith("tat17_04swoop", StringComparison.OrdinalIgnoreCase)
        || tag.StartsWith("tat17_xracer", StringComparison.OrdinalIgnoreCase);

    public static bool NeverAutoJoin(string resref)
    {
        foreach (string known in SoloEntryConversations)
        {
            if (string.Equals(resref, known, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// True when this peer should send the same join request a click on the partner NPC would
    /// send (the existing adopt path already carries it from there — see
    /// <c>DialogueSpectateController</c>'s "adopt" state).
    /// </summary>
    /// <param name="mode">The configured <see cref="DialogueAutoJoinMode"/>. Irrelevant when
    /// <paramref name="isForced"/> is true — see that parameter.</param>
    /// <param name="isForced">
    /// True when the conversation is one the vanilla ENGINE ITSELF would force onto a
    /// single-player PC regardless of position — an ambush or scripted cutscene conversation
    /// (NWScript <c>ActionStartConversation</c> with <c>bIgnoreStartRange=TRUE</c> — see
    /// docs/research/dialogue-sync.md's 2026-08-23 Item 1 and
    /// <c>KotorCoop.Plugin.DialogueForceFlagHook</c> for how the plugin reads it). When true,
    /// this rule joins UNCONDITIONALLY — ignoring <paramref name="mode"/> (including
    /// <see cref="DialogueAutoJoinMode.Off"/>) and <paramref name="distanceToPartner"/> — because
    /// this is matching behaviour vanilla already forces, not inventing a new one; the
    /// mode/range knobs exist only to govern whether an ORDINARY, player-clicked conversation
    /// pulls the other player in. The three runtime-safety gates below (already-in-conversation,
    /// combat, minigame) still apply even when forced: none of them is a "does the user want
    /// this" policy choice, they are "can the engine correctly do this right now" guards, and a
    /// forced conversation the local engine cannot currently enter is not this rule's problem to
    /// force through.</param>
    /// <param name="localAlreadyInConversation">This player is already in a conversation of
    /// their own (their own, not the one being offered — a peer already holding a DIFFERENT
    /// token is handled by the existing yield/adopt machinery, not this rule; this flag exists
    /// so auto-join never fights a conversation the player is already having).</param>
    /// <param name="localInCombat">This player's own engine says it is fighting right now
    /// (<c>Odyssey.IsPlayerInCombat</c>) — being yanked into a conversation mid-fight is
    /// exactly the "felt wrong" complaint the feature was removed for once already.</param>
    /// <param name="localInMinigame">This player is in a swoop race, turret sequence or other
    /// minigame (<c>MinigameController.LocalInMinigame</c>) — the shared world is paused for
    /// this player in every other sense already; a dialogue window would not make sense either.
    /// </param>
    /// <param name="distanceToPartner">This player's own distance to the conversation's NPC, in
    /// game units. Ignored under <see cref="DialogueAutoJoinMode.Always"/> and whenever
    /// <paramref name="isForced"/> is true.</param>
    /// <param name="rangeLimit"><c>dialogue_auto_join_range</c> (default 20). Ignored under
    /// <see cref="DialogueAutoJoinMode.Always"/> and whenever <paramref name="isForced"/> is
    /// true.</param>
    public static bool ShouldAutoJoin(
        DialogueAutoJoinMode mode,
        bool isForced,
        bool localAlreadyInConversation,
        bool localInCombat,
        bool localInMinigame,
        float distanceToPartner,
        float rangeLimit)
    {
        if (localAlreadyInConversation || localInCombat || localInMinigame)
        {
            return false;
        }

        if (isForced)
        {
            return true;
        }

        if (mode == DialogueAutoJoinMode.Off)
        {
            return false;
        }

        if (mode == DialogueAutoJoinMode.Always)
        {
            return true;
        }

        // Near: NaN (an unresolved position — a body not streamed in yet) never compares true
        // to anything, so a NaN distance correctly refuses rather than accidentally passing an
        // unbounded range check.
        return distanceToPartner <= rangeLimit;
    }

    /// <summary>Parses <c>coop.ini dialogue_auto_join</c>. Unrecognised text (including empty
    /// or missing — the default) falls to <see cref="DialogueAutoJoinMode.Off"/> (2026-08-23:
    /// changed from <see cref="DialogueAutoJoinMode.Near"/> — the user's own stated preference
    /// is no forced joins for ordinary chat; a genuinely forced/critical conversation still
    /// auto-joins regardless of this setting, via <see cref="ShouldAutoJoin"/>'s
    /// <c>isForced</c>).</summary>
    public static DialogueAutoJoinMode ParseMode(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "near" => DialogueAutoJoinMode.Near,
        "always" => DialogueAutoJoinMode.Always,
        _ => DialogueAutoJoinMode.Off,
    };
}
