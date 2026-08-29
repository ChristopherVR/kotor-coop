namespace KotorCoop.Protocol;

/// <summary>
/// Whether this player may SKIP or ADVANCE the conversation currently on screen.
///
/// THE BUG THIS EXISTS FOR, observed live: a client started a conversation, the host joined it
/// as a spectator, and the host pressed the skip key — and the line advanced. Only the player
/// whose conversation it is may drive it. That rule was already enforced for REPLIES
/// (<c>DialogueReplyHook</c> swallows a spectator's reply commit) and it was never enforced for
/// SKIP, because skip is a different engine function reached from a different branch of the
/// same switch:
///
/// <code>
/// CSWGuiDialog::HandleInputEvent 0x006A7230, event 0x27/0x2D:
///   [panel+0x1DF8] bit0 == 0  -> SELECT the highlighted reply   (gated since dialogue-sync)
///   bit0 == 1, bit1 == 1      -> already mid-skip: no-op
///   bit0 == 1, bit1 == 0      -> CServerExoApp::SkipPastDialog   &lt;- UNGATED until now
/// </code>
///
/// One branch apart, and the whole of docs/research/dialogue-skip-desync.md — 695 lines that
/// reverse-engineered this exact path — treats skip as something only the owner would ever do.
/// Nothing checked.
///
/// WHY A SPECTATOR'S SKIP IS WORSE THAN A COSMETIC ANNOYANCE. Every peer runs its own real
/// local copy of the conversation; the wire carries a POSITION, not a queue
/// (dialogue-skip-desync.md §"the mechanism"). A spectator's skip therefore advances its own
/// engine's copy, which the node-publish/drive machinery then treats as a real position — so a
/// spectator can push the conversation forward for everyone, or desynchronise the two copies
/// into a fight the follower loses. The owner's own skip stays untouched: skipping your own
/// line is exactly what the key is for.
///
/// Pure, and in <c>KotorCoop.Protocol</c> rather than the plugin, for the reason
/// <c>DialogueSpectateController</c> states about itself: the plugin assembly is x86 NativeAOT
/// and nothing references it, so anything that must be covered by a test lives here and is
/// shared by source.
/// </summary>
public static class DialogueSkipAuthorityRule
{
    /// <summary>
    /// True when a skip/advance keypress must be swallowed rather than passed to the engine.
    ///
    /// ⚠ THE SAME PREDICATE THE REPLY GAG USES, DELIBERATELY —
    /// <see cref="DialogueFollowMachine.SuppressReplies"/>'s "anything but
    /// <see cref="DialogueFollowState.Idle"/>", where Idle means exactly "no peer holds the
    /// session's conversation token". The reply hook arrived at that width by measurement, not
    /// by taste: <see cref="DialogueFollowMachine"/>'s own doc comment records that
    /// <see cref="DialogueFollowState.Standby"/> (the 15-second join window),
    /// <see cref="DialogueFollowState.Yielding"/> and
    /// <see cref="DialogueFollowState.BackingOff"/> are each a window in which the player is
    /// visibly in somebody else's conversation while not yet, or no longer, "following" it. A
    /// narrower rule reopens exactly those three holes for skip.
    /// </summary>
    /// <param name="state">This peer's follow state — what the session says about who owns the
    /// conversation.</param>
    public static bool ShouldSwallowSkip(DialogueFollowState state) =>
        state != DialogueFollowState.Idle;

    /// <summary>
    /// The same answer stated positively, for a caller that reads better that way.
    ///
    /// Note what is NOT an input: whether this peer is the host. There is no such input here
    /// on purpose — the authority is "whose conversation is it", and a host spectating a
    /// client's conversation has exactly as little claim on it as the other way round. That
    /// symmetry is the same one <see cref="DialogueAutoJoinRule"/> makes explicit for joining.
    /// </summary>
    public static bool MaySkip(DialogueFollowState state) => !ShouldSwallowSkip(state);
}
