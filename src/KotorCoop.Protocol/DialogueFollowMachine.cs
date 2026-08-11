namespace KotorCoop.Protocol;

/// <summary>
/// Where this instance stands with respect to the session's single conversation.
///
/// ⚠ THE POINT OF NAMING THESE IS THAT A FLAG CAN NO LONGER BE SKIPPED BY AN EARLY RETURN.
///
/// The controller this replaces kept seven independent booleans and timestamps and decided
/// them at seven different depths of one long method. Seven of its thirteen audited defects
/// were the same shape: a flag whose CLEAR sat below an early return that a later pass had
/// added above it. The worst of them left <c>DialogueReplyHook.Suppressed</c> latched, and
/// that hook swallows <c>CSWGuiDialog::SelectReply</c> outright — so the player could never
/// answer ANY conversation again, including their own. Reported from play as "the dialogue
/// doesn't show its questions and then takes seconds to do anything".
///
/// One state is current at a time, exactly one place decides the next one, and every level
/// the plugin asserts on the engine (reply suppression, the start gate) is a pure function of
/// the state rather than something a code path has to remember to clear.
/// </summary>
public enum DialogueFollowState
{
    /// <summary>Nobody else is in a conversation. Nothing is suppressed and nothing is
    /// gated — this instance is an ordinary single-player client.</summary>
    Idle,

    /// <summary>A peer owns the session's conversation and we are not in one. We wait for
    /// the player's own click to join it (nothing is opened for them — see the controller),
    /// and we end a stale client-only shell if one is blocking new starts here.</summary>
    Standby,

    /// <summary>A peer owns the conversation and we are in a DIFFERENT one: we lost the race
    /// (both sides clicked inside the same token round-trip, which the engine-side gate cannot
    /// see). One conversation per session means ours goes.</summary>
    Yielding,

    /// <summary>One frame: the conversation the engine started here IS the peer's, so we take
    /// it over as a follow rather than closing and reopening it.</summary>
    Adopting,

    /// <summary>Following, but the conversation has not been seen on screen yet. The long
    /// deadline applies here — the server walks the player into range first.</summary>
    Opening,

    /// <summary>Following, on screen. This is where the owner's node is replayed.</summary>
    Following,

    /// <summary>A follow gave up and closed its conversation. Stand off before adopting the
    /// same one again, or the give-up and the adopt chase each other in a loop whose period is
    /// the content timeout — and every lap runs the conversation's END SCRIPT.</summary>
    BackingOff,
}

/// <summary>The one engine call, if any, that a frame's decision asks for. At most one per
/// frame: every one of these is expensive or destructive, and the controller it replaced could
/// issue a close from three different depths of the same pass.</summary>
public enum DialogueFollowAction
{
    None,

    /// <summary>The polite close: <c>Odyssey.CloseDialogue</c>, which routes to
    /// <c>CSWSObject::StopDialog</c> and RUNS THE CONVERSATION'S END SCRIPT. Never per frame.</summary>
    CloseConversation,

    /// <summary>Last resort: shut the UI directly, for a client-only shell whose server half
    /// has nothing left to stop and which therefore ignores the polite close forever.</summary>
    ForceCloseUi,

    /// <summary>Ask the engine to move to the owner's node.</summary>
    DriveToPeerNode,
}

/// <summary>What to say about a decision, if anything. The machine is pure and cannot log, so
/// it names the event and the plugin writes the sentence.</summary>
public enum DialogueFollowNote
{
    None,
    OwnerEnded,
    Adopted,
    Yielding,
    ForcingUiShut,
    UnstickingShell,
    GaveUpNeverOpened,
    GaveUpNoContent,
}

/// <summary>One frame's answer: where we now are, what to do about it, and what to say.</summary>
public readonly record struct DialogueFollowDecision(
    DialogueFollowState State,
    DialogueFollowAction Action,
    DialogueFollowNote Note);

/// <summary>
/// Everything the machine can observe in a frame. All of it is cheap: the two engine reads
/// (<paramref name="EngineConversationOpen"/>, <paramref name="EngineHasContent"/>) are taken
/// by the caller before the step so the decision itself stays pure and testable.
/// </summary>
/// <param name="NowMs">A monotonic millisecond clock. Passed in so tests own it.</param>
/// <param name="PeerActive">A REMOTE peer holds the session's conversation token. The agent
/// writes this only for peers other than this instance, so an owner never sees its own.</param>
/// <param name="PeerPartnerKnown">The peer has published WHO it is talking to. Empty for a real
/// window at the start of every conversation, because <c>gui+0x170/0x174</c> stay
/// OBJECT_INVALID until the server has resolved the participants.</param>
/// <param name="LocalConversation">This instance is in a conversation, as read from the
/// dialogue ring — and DEBOUNCED across the engine's between-entries flicker by the caller.
/// The raw flag drops for a few frames between entries.</param>
/// <param name="LocalPartnerKnown">Our own conversation has named its partner.</param>
/// <param name="LocalPartnerIsPeers">Our partner tag and the peer's are the same NPC.</param>
/// <param name="EngineConversationOpen">The engine's conversation-active flag, UNDEBOUNCED.</param>
/// <param name="EngineHasContent">The panel is carrying a line.</param>
public readonly record struct DialogueFollowInputs(
    long NowMs,
    bool PeerActive,
    bool PeerPartnerKnown,
    bool LocalConversation,
    bool LocalPartnerKnown,
    bool LocalPartnerIsPeers,
    bool EngineConversationOpen,
    bool EngineHasContent);

/// <summary>
/// The deadlines and rate limits, every one of them a past defect's fix. Grouped so a test can
/// shrink them without the machine caring, and so the numbers sit next to the reason.
/// </summary>
/// <param name="OpenTimeoutMs">How long a follow has to become a conversation on screen.
/// Generous: the server walks the player the whole way into range first, exactly as it does
/// for a click.</param>
/// <param name="ContentTimeoutMs">How long an OPEN conversation may stay empty, counted from
/// when it opened and never from when it was asked for — see <see cref="DialogueFollowClock"/>,
/// where the wrong clock is the bug the type exists to prevent.</param>
/// <param name="LocalOpenGraceMs">How long a local conversation may stay unidentified before it
/// counts as a competitor. ⚠ THIS IS WHY CLICK-TO-JOIN WORKS AT ALL: the player's own join is
/// unidentified for seconds after the click, and tearing it down one frame later is what three
/// separate passes went hunting for an engine guard to explain.</param>
/// <param name="YieldCloseRetryMs">Pause between closes of our own losing conversation. ⚠ The
/// close runs the END SCRIPT, and an NPC whose end script re-initiates (the tutorial deck's
/// Trask) turns a per-frame close into a conversation that opens and closes forever.</param>
/// <param name="YieldCloseServerAttempts">Polite closes before forcing the UI shut.</param>
/// <param name="UnstickRetryMs">Pause between attempts to end an empty conversation shell.</param>
/// <param name="ReopenCooldownMs">Stand-off after a follow gives up, before the same
/// conversation may be adopted again.</param>
public readonly record struct DialogueFollowTimings(
    long OpenTimeoutMs,
    long ContentTimeoutMs,
    long LocalOpenGraceMs,
    long YieldCloseRetryMs,
    int YieldCloseServerAttempts,
    long UnstickRetryMs,
    long ReopenCooldownMs)
{
    /// <summary>The shipped numbers. <see cref="LocalOpenGraceMs"/> deliberately equals
    /// <see cref="OpenTimeoutMs"/>: both are waiting on the same server-side walk.</summary>
    public static DialogueFollowTimings Default { get; } = new(
        OpenTimeoutMs: 15000,
        ContentTimeoutMs: 8000,
        LocalOpenGraceMs: 15000,
        YieldCloseRetryMs: 2000,
        YieldCloseServerAttempts: 2,
        UnstickRetryMs: 3000,
        ReopenCooldownMs: 4000);
}

/// <summary>
/// The spectate controller's decision logic, with every engine call lifted out.
///
/// <see cref="DialogueFollowClock"/> is the precedent and this is the same argument one level
/// up: the rules that decide whether a followed conversation is healthy, whether our own is a
/// competitor, and whether a close may be issued this frame are arithmetic over timestamps —
/// and arithmetic over timestamps is exactly what cannot be checked by looking at a live game
/// for thirty seconds. The plugin half is now reads, writes and calls; everything that decides
/// anything is here, under test.
/// </summary>
public sealed class DialogueFollowMachine(DialogueFollowTimings timings)
{
    private readonly DialogueFollowTimings _t = timings;

    public DialogueFollowMachine() : this(DialogueFollowTimings.Default)
    {
    }

    public DialogueFollowState State { get; private set; } = DialogueFollowState.Idle;

    /// <summary>
    /// Whether this instance is committed to a peer's conversation. Used for the one thing the
    /// old <c>_following</c> boolean still has to say: a follower records no picks of its own,
    /// because everything it "picks" is a replay.
    /// </summary>
    public bool IsFollowing => State
        is DialogueFollowState.Adopting
        or DialogueFollowState.Opening
        or DialogueFollowState.Following;

    /// <summary>
    /// ⚠ WHETHER THE PLAYER MAY COMMIT A REPLY — A PURE FUNCTION OF THE STATE, NEVER A LATCH.
    ///
    /// Suppressed in every state except <see cref="DialogueFollowState.Idle"/>, and Idle means
    /// exactly "no peer holds the session's conversation token". That is the whole BG3 rule
    /// stated once: while somebody else owns the conversation, nobody else answers it.
    ///
    /// It is deliberately wider than the follow itself, and each extra state is a hole that
    /// was measured:
    ///   * <see cref="DialogueFollowState.Standby"/> and
    ///     <see cref="DialogueFollowState.Opening"/> cover the JOIN WINDOW. A join takes up to
    ///     <c>OpenTimeoutMs</c> (15 s) because the server walks the player into range, and the
    ///     old assertion sat one early return too low, so for every one of those seconds both
    ///     players could answer — on two copies of the same conversation, of which only the
    ///     owner's was visible to anyone else. That is the "both players can still click the
    ///     options" report, and it was never a hole in the reply hook.
    ///   * <see cref="DialogueFollowState.Yielding"/> covers a competing conversation we have
    ///     already decided to close: its plot flags race the owner's, so its replies must not
    ///     land in the seconds it takes to go.
    ///   * <see cref="DialogueFollowState.BackingOff"/> covers the stand-off after a failed
    ///     follow, where a matching local conversation may still be on screen.
    /// </summary>
    public bool SuppressReplies => State != DialogueFollowState.Idle;

    /// <summary>When the current follow was committed to — the clock the OPEN deadline runs on.</summary>
    private long _requestedAtMs;

    /// <summary>When it first appeared on screen, or null. A DIFFERENT clock, and the content
    /// deadline runs on this one. See <see cref="DialogueFollowClock"/>.</summary>
    private long? _openedAtMs;

    /// <summary>When our own conversation first read live without naming a partner.
    ///
    /// ⚠ Nullable rather than a zero sentinel. The old controller used 0 for "not started",
    /// which is a value the clock genuinely takes — <c>Environment.TickCount64</c> is zero at
    /// the instant the machine boots, and it is every test's natural first timestamp — and a
    /// sentinel the clock can produce re-stamps itself on every frame, so the grace period
    /// never expires and a genuine competitor is never yielded.</summary>
    private long? _localUnidentifiedSinceMs;

    private long _nextYieldCloseMs;
    private int _yieldCloseAttempts;
    private long _nextUnstickMs;
    private long _backoffUntilMs;

    /// <summary>
    /// Forget everything. A module change destroys every creature and every conversation, so a
    /// partner tag now resolves to a different body and a node index names an entry in a
    /// different .dlg entirely.
    /// </summary>
    public void Reset()
    {
        State = DialogueFollowState.Idle;
        _requestedAtMs = 0;
        _openedAtMs = null;
        _localUnidentifiedSinceMs = null;
        _nextYieldCloseMs = 0;
        _yieldCloseAttempts = 0;
        _nextUnstickMs = 0;
        _backoffUntilMs = 0;
    }

    /// <summary>One frame. The single place a transition is decided.</summary>
    public DialogueFollowDecision Step(in DialogueFollowInputs input)
    {
        // ── Idle ──────────────────────────────────────────────────────────────────────
        // No peer conversation, so there is nothing to follow, nothing to yield to and
        // nothing to suppress. A follow that was live ends here, with ONE close: Reset puts
        // us in Idle, so the next frame's IsFollowing is already false and the close cannot
        // repeat. (A yield in progress simply stops — its conversation is ours to keep once
        // the peer's has gone.)
        if (!input.PeerActive)
        {
            bool wasFollowing = IsFollowing;
            Reset();
            return new DialogueFollowDecision(
                State,
                wasFollowing ? DialogueFollowAction.CloseConversation : DialogueFollowAction.None,
                wasFollowing ? DialogueFollowNote.OwnerEnded : DialogueFollowNote.None);
        }

        if (IsFollowing)
        {
            return StepFollow(input);
        }

        if (State == DialogueFollowState.BackingOff)
        {
            if (input.NowMs < _backoffUntilMs)
            {
                // Do nothing at all — in particular do not close again. The give-up already
                // issued one close, and this stand-off exists precisely so the adopt path
                // cannot pick the same conversation straight back up.
                return new DialogueFollowDecision(
                    DialogueFollowState.BackingOff, DialogueFollowAction.None, DialogueFollowNote.None);
            }

            State = DialogueFollowState.Standby; // the stand-off is over; arbitrate afresh
        }

        return StepUnjoined(input);
    }

    /// <summary>Standby / Yielding / Adopting: a peer owns the conversation and we are deciding
    /// what our own instance is doing about it.</summary>
    private DialogueFollowDecision StepUnjoined(in DialogueFollowInputs input)
    {
        if (!input.LocalConversation)
        {
            // Nothing of ours to arbitrate. The yield ledger and the identification clock both
            // belong to one specific local conversation, and that conversation has gone — so
            // they are cleared HERE, on the transition, rather than by whichever branch
            // happens to run next.
            _yieldCloseAttempts = 0;
            _nextYieldCloseMs = 0;
            _localUnidentifiedSinceMs = null;
            State = DialogueFollowState.Standby;

            // ⚠ UNSTICK A STALE CONVERSATION SHELL, or the player can never join anything.
            //
            // Verified in the binary: CSWCMessage::SendPlayerToServerInput_Dialog 0x00677EA0
            // reads [gui+0xB4] at 0x00677EB3 and, if it is set, JUMPS STRAIGHT TO ITS EXIT.
            // That call is the tail of the ordinary click path too, so while that flag is up
            // this instance cannot start a conversation with anybody — the request dies before
            // the engine reaches StartDialog, which is why the dialogue gate's attempt counter
            // never moved and the join looked refused when nothing had refused it.
            //
            // Rate-limited, because ending it runs the conversation's end script.
            if (input.EngineConversationOpen && !input.EngineHasContent
                && input.NowMs >= _nextUnstickMs)
            {
                _nextUnstickMs = input.NowMs + _t.UnstickRetryMs;
                return new DialogueFollowDecision(
                    DialogueFollowState.Standby,
                    DialogueFollowAction.CloseConversation,
                    DialogueFollowNote.UnstickingShell);
            }

            return new DialogueFollowDecision(
                DialogueFollowState.Standby, DialogueFollowAction.None, DialogueFollowNote.None);
        }

        // ⚠ AN UNIDENTIFIED LOCAL CONVERSATION IS NOT A COMPETITOR — IT IS USUALLY THIS
        // PLAYER'S OWN CLICK-TO-JOIN, STILL IN FLIGHT.
        //
        // gui+0xB4 goes up the INSTANT the player clicks, seconds before any conversation
        // exists: CSWCCreature::ActionInitiateDialog 0x0060F620 calls SetGlobalDialogState(1)
        // at 0x0060F76D straight after SendPlayerToServerInput_Dialog, and
        // CSWSObject::AIActionDialogObject 0x0057A470 sets it again at 0x0057A70E — BEFORE its
        // range test (0x0057A7AC / 0x0057A923) — re-queueing itself for every tick the server
        // spends walking the player into range. For that whole window gui+0x170/0x174 are
        // still OBJECT_INVALID, so the published partner tag is EMPTY.
        //
        // Yielding on that emptiness annulled the player's own join one frame after the click.
        // Three passes went looking for an engine guard refusing the join. Nothing refused it.
        if (!input.LocalPartnerKnown)
        {
            _localUnidentifiedSinceMs ??= input.NowMs;

            if (input.NowMs - _localUnidentifiedSinceMs.Value < _t.LocalOpenGraceMs)
            {
                // Still arriving. Standby rather than Yielding, so nothing tears it down —
                // and note that Standby suppresses replies, which is what covers the join
                // window on THIS side of the adopt.
                State = DialogueFollowState.Standby;
                return new DialogueFollowDecision(
                    DialogueFollowState.Standby, DialogueFollowAction.None, DialogueFollowNote.None);
            }

            // The grace is spent and it still will not say who it is. Treat it as a competitor
            // — and leave the stamp alone, so this conversation stays expired rather than
            // earning a fresh grace period on the next frame.
        }
        else
        {
            _localUnidentifiedSinceMs = null;
        }

        if (input.LocalPartnerIsPeers)
        {
            // Both players standing in the same trigger get the SAME conversation from the
            // engine, independently — and the loser's copy is exactly the copy a follow would
            // have opened. Adopt it instead of closing and reopening: the teardown ran the end
            // script, and the reopen cost the seconds the player spent staring at an empty
            // dialogue box. Measured at ~880 ms to adopt, with content immediately.
            _yieldCloseAttempts = 0;
            _nextYieldCloseMs = 0;
            _localUnidentifiedSinceMs = null;
            _requestedAtMs = input.NowMs;

            // ⚠ The two clocks are started HERE and only here. If the engine's active flag is
            // down at the instant we adopt (the between-entries flicker, which the debounced
            // LocalConversation input rides through), the conversation is NOT banked as open:
            // it gets the long open budget until it actually appears. Banking an open that had
            // not happened started the 8 s content clock on a conversation that was not on
            // screen yet.
            _openedAtMs = input.EngineConversationOpen ? input.NowMs : null;

            State = DialogueFollowState.Adopting;
            return new DialogueFollowDecision(
                DialogueFollowState.Adopting, DialogueFollowAction.None, DialogueFollowNote.Adopted);
        }

        // ⚠ AND NOT UNTIL THE PEER HAS NAMED ITS PARTNER EITHER — the same rule as the grace
        // above, pointed the other way, and the same rule the start gate already follows.
        //
        // "Different partner" is decided by a tag comparison that refuses an EMPTY tag by
        // design, so a peer whose participants have not resolved yet reads as "not ours" and
        // every local conversation looked like a competitor. The peer's tag is empty for a real
        // window at the start of EVERY conversation (gui+0x170/0x174 stay OBJECT_INVALID until
        // the server resolves them), and the conversation most likely to be running here during
        // that window is the SAME ONE — both players in the same trigger get it independently,
        // which is the case the adopt path exists for and which was measured at ~880 ms against
        // seconds for a teardown and reopen. Closing it on the strength of a tag that had not
        // arrived threw that away.
        //
        // Waiting costs a few hundred milliseconds of a possibly-competing conversation, which
        // is the identical cost the gate already accepts for the identical reason — and its
        // replies are suppressed throughout, because Standby suppresses.
        if (!input.PeerPartnerKnown)
        {
            State = DialogueFollowState.Standby;
            return new DialogueFollowDecision(
                DialogueFollowState.Standby, DialogueFollowAction.None, DialogueFollowNote.None);
        }

        // A genuinely competing conversation. Ours yields — before this, the loser kept a
        // private conversation whose plot flags raced the owner's.
        State = DialogueFollowState.Yielding;

        // ⚠ ONCE PER YieldCloseRetryMs, not every frame. The close runs the END SCRIPT, and an
        // NPC scripted to re-initiate turns a per-frame close into an open/close loop. The
        // local conversation also takes a few frames to actually go, and the LocalConversation
        // input is debounced on top of that, so it still reads live long after one close has
        // landed.
        if (input.NowMs < _nextYieldCloseMs)
        {
            return new DialogueFollowDecision(
                DialogueFollowState.Yielding, DialogueFollowAction.None, DialogueFollowNote.None);
        }

        _nextYieldCloseMs = input.NowMs + _t.YieldCloseRetryMs;
        _yieldCloseAttempts++;

        // The server route has had its turns and the conversation is still here, which means
        // there is nothing server-side left to end — a client-only shell. Shut the UI directly,
        // or this instance sits out the whole conversation retrying forever.
        return _yieldCloseAttempts <= _t.YieldCloseServerAttempts
            ? new DialogueFollowDecision(
                DialogueFollowState.Yielding,
                DialogueFollowAction.CloseConversation,
                DialogueFollowNote.Yielding)
            : new DialogueFollowDecision(
                DialogueFollowState.Yielding,
                DialogueFollowAction.ForceCloseUi,
                DialogueFollowNote.ForcingUiShut);
    }

    /// <summary>Adopting / Opening / Following: we are committed, and the two deadlines run.</summary>
    private DialogueFollowDecision StepFollow(in DialogueFollowInputs input)
    {
        // Adopting is a single announcing frame. It resolves here rather than in the frame
        // that entered it, so the plugin's "next frame drives it through the follow path"
        // ordering is kept: the adopt frame says what it did and touches nothing.
        if (State == DialogueFollowState.Adopting)
        {
            State = _openedAtMs is null
                ? DialogueFollowState.Opening
                : DialogueFollowState.Following;
        }

        // The open has landed. This is the only place _openedAtMs is written after the adopt,
        // and it is a one-way edge: the flicker that drops EngineConversationOpen between
        // entries must not re-arm the open clock, or the content deadline restarts on every
        // line change.
        if (_openedAtMs is null && input.EngineConversationOpen)
        {
            _openedAtMs = input.NowMs;
            State = DialogueFollowState.Following;
        }

        DialogueFollowVerdict verdict = DialogueFollowClock.Evaluate(
            input.NowMs,
            _requestedAtMs,
            _openedAtMs,
            input.EngineConversationOpen,
            input.EngineHasContent,
            _t.OpenTimeoutMs,
            _t.ContentTimeoutMs);

        switch (verdict)
        {
            case DialogueFollowVerdict.Drive:
                return new DialogueFollowDecision(
                    DialogueFollowState.Following,
                    DialogueFollowAction.DriveToPeerNode,
                    DialogueFollowNote.None);

            case DialogueFollowVerdict.Wait:
                return new DialogueFollowDecision(State, DialogueFollowAction.None, DialogueFollowNote.None);

            default:
                // ⚠ BACK OFF RATHER THAN SIMPLY STOPPING. Give-up closes the conversation, and
                // the adopt path would find the very same conversation still on screen with the
                // very same matching tag on the next frame and take it straight back — an
                // open/close loop whose period is the content timeout and whose every lap runs
                // the end script. The stand-off is what makes a give-up a give-up.
                //
                // ⚠ AND NO SetCanSendDialog, IN EITHER DIRECTION. It is not a restore, it is a
                // fresh one-second gag on the voice-over: CClientExoAppInternal::SetCanSendDialog
                // 0x005F3030 stamps +0x22C with the world time at 0x005F305F, and
                // GetCanSendDialog refuses while `now - [+0x22C] <= 1000`, which makes
                // CSWSObject::SendDialogEntry DEFER — and that is the only producer of the
                // voice-over and the lipsync arming behind it.
                State = DialogueFollowState.BackingOff;
                _backoffUntilMs = input.NowMs + _t.ReopenCooldownMs;
                _requestedAtMs = 0;
                _openedAtMs = null;
                return new DialogueFollowDecision(
                    DialogueFollowState.BackingOff,
                    DialogueFollowAction.CloseConversation,
                    verdict == DialogueFollowVerdict.GiveUpNoContent
                        ? DialogueFollowNote.GaveUpNoContent
                        : DialogueFollowNote.GaveUpNeverOpened);
        }
    }

    /// <summary>When the current follow first appeared on screen, for the latency report the
    /// content timeout is supposed to be set from. Null until it has.</summary>
    public long? OpenedAtMs => _openedAtMs;

    /// <summary>When the current follow was committed to.</summary>
    public long RequestedAtMs => _requestedAtMs;
}
