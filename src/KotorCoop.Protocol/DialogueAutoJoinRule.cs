namespace KotorCoop.Protocol;

/// <summary>
/// <c>coop.ini dialogue_auto_join</c>'s three settings. Default <see cref="Near"/>.
/// </summary>
public enum DialogueAutoJoinMode
{
    /// <summary>The pre-existing behaviour: never pulled in automatically, only by clicking the
    /// same NPC (docs/research/shared-state-audit.md's "force_dialogue_party, and why it is
    /// OFF").</summary>
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
    /// <summary>
    /// True when this peer should send the same join request a click on the partner NPC would
    /// send (the existing adopt path already carries it from there — see
    /// <c>DialogueSpectateController</c>'s "adopt" state).
    /// </summary>
    /// <param name="mode">The configured <see cref="DialogueAutoJoinMode"/>.</param>
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
    /// game units. Ignored under <see cref="DialogueAutoJoinMode.Always"/>.</param>
    /// <param name="rangeLimit"><c>dialogue_auto_join_range</c> (default 20). Ignored under
    /// <see cref="DialogueAutoJoinMode.Always"/>.</param>
    public static bool ShouldAutoJoin(
        DialogueAutoJoinMode mode,
        bool localAlreadyInConversation,
        bool localInCombat,
        bool localInMinigame,
        float distanceToPartner,
        float rangeLimit)
    {
        if (mode == DialogueAutoJoinMode.Off)
        {
            return false;
        }

        if (localAlreadyInConversation || localInCombat || localInMinigame)
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
    /// or missing — the default) falls to <see cref="DialogueAutoJoinMode.Near"/>, the least
    /// surprising of the three: closer to today's click-to-join feel than <c>always</c>, and
    /// still an improvement over <c>off</c>'s pre-existing behaviour.</summary>
    public static DialogueAutoJoinMode ParseMode(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "off" => DialogueAutoJoinMode.Off,
        "always" => DialogueAutoJoinMode.Always,
        _ => DialogueAutoJoinMode.Near,
    };
}
