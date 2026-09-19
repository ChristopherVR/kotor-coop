namespace KotorCoop.Plugin;

/// <summary>Where the save-time flush is, frame to frame.</summary>
internal enum SaveFlushPhase
{
    /// <summary>Nothing held. Every save request passes straight through to the engine.</summary>
    Idle,

    /// <summary>A save request was swallowed and is waiting for the area to be clean: one
    /// spawned body is destroyed per frame, and the frame returns early so nothing is
    /// re-spawned behind the flush.</summary>
    Holding,

    /// <summary>The held save has been re-issued. The frame still returns early for a few
    /// frames, because the engine stores the module on its NEXT main-loop pass (the stall
    /// event <c>SaveGame</c> arms), and a stand-in re-spawned in between would be baked in
    /// after all.</summary>
    Quiet,
}

/// <summary>What the hook on <c>CServerExoAppInternal::SaveGame</c> should do with a request.</summary>
internal enum SaveFlushRequest
{
    /// <summary>Let the engine save now.</summary>
    PassThrough,

    /// <summary>Swallow it (answer the caller as if it were accepted) and re-issue it once the
    /// flush has run.</summary>
    Hold,
}

/// <summary>What the frame should do after this frame's flush attempt.</summary>
internal enum SaveFlushStep
{
    /// <summary>Nothing is held; the frame runs normally.</summary>
    Idle,

    /// <summary>Still flushing (a body was destroyed, or a refused destroy is being retried):
    /// return early.</summary>
    KeepHolding,

    /// <summary>The area has been clean for the settle frames: re-issue the held save now,
    /// then stay quiet.</summary>
    ReissueClean,

    /// <summary>The flush budget ran out with bodies still standing: re-issue the held save
    /// ANYWAY. A save with a ghost in it is healed by the load-time sweep; a save that never
    /// happens is not.</summary>
    ReissueUnclean,

    /// <summary>Re-issued; keep the controllers parked until the engine has stored.</summary>
    KeepQuiet,

    /// <summary>The quiet window is over: let the controllers rebuild what the flush
    /// destroyed.</summary>
    Release,
}

/// <summary>
/// The state machine behind the save-time flush (docs/research/save-ghosts.md §4): hold a
/// manual save while the plugin's own spawns are destroyed, re-issue it once the area is
/// clean, and never let the hold become a lost save.
///
/// Separated from <c>PluginHost</c> and <see cref="SaveFlushHook"/> so it can be unit-tested
/// — the hook is a byte splice on the engine and the flush is engine destroys, neither of
/// which exists in the test runner (the same split <see cref="SpawnDrainRule"/> and
/// <c>GhostSweepRule</c> use).
///
/// THE INVARIANTS THE TESTS PIN:
/// <list type="bullet">
/// <item>A request with nothing standing is never held. The common case (a solo player, or a
/// partner who has not joined yet) must be bit-for-bit the retail save.</item>
/// <item>A held save is re-issued EXACTLY once, whatever happens: clean (the normal case),
/// unclean after the frame budget (a destroy the engine keeps refusing), and never twice
/// (a second request that lands while the first is held is coalesced into it — the player
/// pressed F4 twice — and the request that lands in the quiet window passes through,
/// because the area is verifiably clean then).</item>
/// <item>The re-issue itself passes through: it arrives at the same hook while the rule is
/// in <see cref="SaveFlushPhase.Quiet"/>, and the rule moves to Quiet BEFORE the host makes
/// the call, so the hook cannot hold its own re-issue.</item>
/// <item>"Clean" means clean for <c>settleFrames + 1</c> consecutive frames. The engine
/// destroys a body on its next main-loop pass, not inside the destroy call, so one extra
/// frame is what guarantees the last body is really gone before the store is armed.</item>
/// </list>
/// </summary>
internal sealed class SaveFlushRule
{
    /// <summary>The door-transition flush window is ~150 frames for at most 16 bodies; this
    /// is the same order, and the number the log's "budget exhausted" line quotes.</summary>
    public const int DefaultBudgetFrames = 120;

    /// <summary>Frames the controllers stay parked after the re-issue. The store runs on the
    /// very next main-loop pass; four of our own frames is generous and still invisible.</summary>
    public const int DefaultQuietFrames = 4;

    /// <summary>Extra clean frames required before the re-issue — see the class remarks.</summary>
    public const int DefaultSettleFrames = 1;

    private readonly int _budgetFrames;
    private readonly int _quietFrames;
    private readonly int _settleFrames;

    private int _cleanStreak;
    private int _quietLeft;

    public SaveFlushRule(
        int budgetFrames = DefaultBudgetFrames,
        int quietFrames = DefaultQuietFrames,
        int settleFrames = DefaultSettleFrames)
    {
        _budgetFrames = budgetFrames;
        _quietFrames = quietFrames;
        _settleFrames = settleFrames;
    }

    public SaveFlushPhase Phase { get; private set; }

    /// <summary>Frames the current (or last) hold has lasted.</summary>
    public int HeldFrames { get; private set; }

    /// <summary>How many bodies were standing when the hold began.</summary>
    public int StandingAtHold { get; private set; }

    /// <summary>Requests coalesced into the current hold (1 for a single F4).</summary>
    public int Requests { get; private set; }

    /// <summary>The hook's question: a save request just arrived with this many spawned
    /// bodies standing. The host captures the request's arguments only on
    /// <see cref="SaveFlushRequest.Hold"/>; a coalesced request replaces the capture, so the
    /// latest slot and name win.</summary>
    public SaveFlushRequest OnSaveRequested(int standing)
    {
        switch (Phase)
        {
            case SaveFlushPhase.Idle:
                if (standing <= 0)
                {
                    return SaveFlushRequest.PassThrough;
                }

                Phase = SaveFlushPhase.Holding;
                HeldFrames = 0;
                StandingAtHold = standing;
                Requests = 1;
                _cleanStreak = 0;
                return SaveFlushRequest.Hold;

            case SaveFlushPhase.Holding:
                Requests++;
                return SaveFlushRequest.Hold;

            case SaveFlushPhase.Quiet:
            default:
                // Our own re-issue, or a request that landed after it: the area is clean.
                return SaveFlushRequest.PassThrough;
        }
    }

    /// <summary>
    /// The frame's question, asked once per frame while <see cref="Phase"/> is not
    /// <see cref="SaveFlushPhase.Idle"/>, AFTER the host attempted one flush.
    /// <paramref name="flushedOne"/> is what that attempt returned: true while something was
    /// still there to destroy (or retry), false once every registry reported empty. Ignored
    /// in the quiet phase.
    /// </summary>
    public SaveFlushStep OnFrame(bool flushedOne)
    {
        switch (Phase)
        {
            case SaveFlushPhase.Holding:
                HeldFrames++;

                if (flushedOne)
                {
                    _cleanStreak = 0;
                    if (HeldFrames >= _budgetFrames)
                    {
                        EnterQuiet();
                        return SaveFlushStep.ReissueUnclean;
                    }

                    return SaveFlushStep.KeepHolding;
                }

                if (++_cleanStreak <= _settleFrames)
                {
                    return SaveFlushStep.KeepHolding;
                }

                EnterQuiet();
                return SaveFlushStep.ReissueClean;

            case SaveFlushPhase.Quiet:
                if (--_quietLeft > 0)
                {
                    return SaveFlushStep.KeepQuiet;
                }

                Phase = SaveFlushPhase.Idle;
                return SaveFlushStep.Release;

            default:
                return SaveFlushStep.Idle;
        }
    }

    /// <summary>Forgets the hold without re-issuing anything — the world it belonged to is
    /// gone, or the re-issue itself failed. The host logs why.</summary>
    public void Reset()
    {
        Phase = SaveFlushPhase.Idle;
        _cleanStreak = 0;
        _quietLeft = 0;
    }

    private void EnterQuiet()
    {
        Phase = SaveFlushPhase.Quiet;
        _quietLeft = _quietFrames;
    }
}
