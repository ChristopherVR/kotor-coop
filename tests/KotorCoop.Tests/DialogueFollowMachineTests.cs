using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The spectate controller's state machine.
///
/// These exist because the controller this replaces was audited and found to hold thirteen
/// state defects, SEVEN of them the same shape: a flag whose clear sat below an early return
/// that a later pass had added above it. Nothing about that shape is visible from thirty
/// seconds of live play — the symptom is a player who, some minutes later, cannot answer any
/// conversation at all — so the rules are pulled out here where a test can drive them through
/// the sequences that produced the reports.
/// </summary>
public sealed class DialogueFollowMachineTests
{
    private static readonly DialogueFollowTimings Timings = DialogueFollowTimings.Default;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivatePromptNeverClosesAnEmptyGuiOrSuppressesItsChoice(bool wasFollowing)
    {
        DialogueFollowMachine machine = new(Timings);
        machine.Step(wasFollowing ? SameConversation(0) : CompetingConversation(0));
        if (wasFollowing) machine.Step(SameConversation(1));
        // The queued start has no GUI; once open its participant IDs and shared text
        // remain empty. Both phases must override an outstanding peer's token.
        foreach (long now in new long[] { 2, 3002, 16002, 30002 })
        {
            var input = PeerTalking(now) with
            {
                PrivatePrompt = true,
                EngineConversationOpen = now >= 3002,
                EngineHasContent = false,
            };
            var decision = machine.Step(input);
            Assert.Equal(DialogueFollowAction.None, decision.Action);
            Assert.Equal(DialogueFollowState.Idle, decision.State);
            Assert.False(machine.SuppressReplies);
            Assert.False(DialogueSkipAuthorityRule.ShouldSwallowSkip(machine.State));
        }
        // Closing the private prompt must not revive a prior follow's close/timeout.
        Assert.Equal(DialogueFollowAction.None, machine.Step(Quiet(30003)).Action);
        Assert.Equal(DialogueFollowState.Standby, machine.Step(PeerTalking(30004)).State);
    }

    /// <summary>Nothing happening anywhere: no peer, no local conversation.</summary>
    private static DialogueFollowInputs Quiet(long nowMs) => new(
        NowMs: nowMs,
        PeerActive: false,
        PeerPartnerKnown: false,
        LocalConversation: false,
        LocalPartnerKnown: false,
        LocalPartnerIsPeers: false,
        EngineConversationOpen: false,
        EngineHasContent: false);

    /// <summary>A peer is talking to a named NPC and we are not in a conversation.</summary>
    private static DialogueFollowInputs PeerTalking(long nowMs) => Quiet(nowMs) with
    {
        PeerActive = true,
        PeerPartnerKnown = true,
    };

    /// <summary>A peer is talking and we are in the SAME conversation, on screen with a line.</summary>
    private static DialogueFollowInputs SameConversation(long nowMs) => PeerTalking(nowMs) with
    {
        LocalConversation = true,
        LocalPartnerKnown = true,
        LocalPartnerIsPeers = true,
        EngineConversationOpen = true,
        EngineHasContent = true,
    };

    /// <summary>A peer is talking and we are in a DIFFERENT one.</summary>
    private static DialogueFollowInputs CompetingConversation(long nowMs) => SameConversation(nowMs) with
    {
        LocalPartnerIsPeers = false,
    };

    // ── Idle ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StartsIdleAndSuppressesNothing()
    {
        DialogueFollowMachine machine = new(Timings);

        Assert.Equal(DialogueFollowState.Idle, machine.State);
        Assert.False(machine.SuppressReplies);
        Assert.False(machine.IsFollowing);
    }

    [Fact]
    public void APeerConversationAloneNeverSuppressesInIdle()
    {
        DialogueFollowMachine machine = new(Timings);
        machine.Step(Quiet(0));

        Assert.False(machine.SuppressReplies);
    }

    // ── The suppression rule ───────────────────────────────────────────────────────────

    /// <summary>
    /// ⚠ THE JOIN WINDOW. The old controller asserted suppression one early return too low, so
    /// the whole 15-second window in which a joiner's conversation is on its way ran
    /// UNSUPPRESSED and both players could answer — on two copies of the same conversation, of
    /// which only the owner's was visible to anyone else. Standby is that window.
    /// </summary>
    [Fact]
    public void RepliesAreSuppressedFromTheMomentAPeerHoldsTheToken()
    {
        DialogueFollowMachine machine = new(Timings);
        machine.Step(PeerTalking(0));

        Assert.Equal(DialogueFollowState.Standby, machine.State);
        Assert.True(machine.SuppressReplies);
        Assert.False(machine.IsFollowing);
    }

    /// <summary>A conversation we have already decided to close still must not take a reply:
    /// its plot flags race the owner's for the seconds it takes to go.</summary>
    [Fact]
    public void RepliesStaySuppressedWhileYieldingALosingConversation()
    {
        DialogueFollowMachine machine = new(Timings);
        machine.Step(CompetingConversation(0));

        Assert.Equal(DialogueFollowState.Yielding, machine.State);
        Assert.True(machine.SuppressReplies);
    }

    [Fact]
    public void RepliesComeBackTheFrameThePeerReleasesTheToken()
    {
        DialogueFollowMachine machine = new(Timings);
        machine.Step(SameConversation(0));
        machine.Step(SameConversation(16));
        Assert.True(machine.SuppressReplies);

        machine.Step(Quiet(32));

        Assert.Equal(DialogueFollowState.Idle, machine.State);
        Assert.False(machine.SuppressReplies);
    }

    // ── Adopting ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Both players standing in the same trigger get the SAME conversation from the engine,
    /// independently, and the loser's copy is exactly the copy a follow would have opened. It
    /// is adopted rather than closed and reopened: the teardown ran the end script and the
    /// reopen cost the seconds the player spent watching an empty box.
    /// </summary>
    [Fact]
    public void AMatchingLocalConversationIsAdoptedRatherThanClosed()
    {
        DialogueFollowMachine machine = new(Timings);
        DialogueFollowDecision decision = machine.Step(SameConversation(0));

        Assert.Equal(DialogueFollowState.Adopting, decision.State);
        Assert.Equal(DialogueFollowAction.None, decision.Action);
        Assert.Equal(DialogueFollowNote.Adopted, decision.Note);
        Assert.True(machine.IsFollowing);
    }

    [Fact]
    public void TheAdoptFrameDoesNothingAndTheNextFrameDrives()
    {
        DialogueFollowMachine machine = new(Timings);
        machine.Step(SameConversation(0));

        DialogueFollowDecision next = machine.Step(SameConversation(16));

        Assert.Equal(DialogueFollowState.Following, next.State);
        Assert.Equal(DialogueFollowAction.DriveToPeerNode, next.Action);
    }

    /// <summary>
    /// The engine's active flag drops for a few frames BETWEEN ENTRIES, and the debounced
    /// LocalConversation input rides through that — so an adopt can land on a frame where the
    /// conversation reads closed. Banking that as "open" started the 8 s content clock on a
    /// conversation that was not on screen yet; it gets the long open budget instead.
    /// </summary>
    [Fact]
    public void AnAdoptDuringTheBetweenEntriesFlickerWaitsForTheRealOpen()
    {
        DialogueFollowMachine machine = new(Timings);
        machine.Step(SameConversation(0) with { EngineConversationOpen = false, EngineHasContent = false });

        DialogueFollowDecision next = machine.Step(
            SameConversation(16) with { EngineConversationOpen = false, EngineHasContent = false });

        Assert.Equal(DialogueFollowState.Opening, next.State);
        Assert.Null(machine.OpenedAtMs);

        // It appears a second later, well inside the open budget, and the content clock starts
        // from THERE rather than from the adopt.
        machine.Step(SameConversation(1000));
        Assert.Equal(1000, machine.OpenedAtMs);
    }

    // ── The local-conversation grace period ────────────────────────────────────────────

    /// <summary>
    /// ⚠ THIS IS WHY CLICK-TO-JOIN WORKS AT ALL. gui+0xB4 goes up the instant the player
    /// clicks, seconds before any conversation exists, and for that whole window
    /// gui+0x170/0x174 are OBJECT_INVALID so the partner tag publishes EMPTY. Treating that as
    /// a competitor annulled the player's own join one frame after the click — three separate
    /// passes went looking for an engine guard to explain it, and nothing was refusing it.
    /// </summary>
    [Fact]
    public void AnUnidentifiedLocalConversationIsNotYetACompetitor()
    {
        DialogueFollowMachine machine = new(Timings);

        DialogueFollowDecision decision = machine.Step(PeerTalking(0) with
        {
            LocalConversation = true,
            LocalPartnerKnown = false,
            EngineConversationOpen = true,
        });

        Assert.Equal(DialogueFollowState.Standby, decision.State);
        Assert.Equal(DialogueFollowAction.None, decision.Action);
    }

    [Fact]
    public void AnUnidentifiedLocalConversationSurvivesTheWholeGracePeriod()
    {
        DialogueFollowMachine machine = new(Timings);
        DialogueFollowInputs joining = PeerTalking(0) with
        {
            LocalConversation = true,
            LocalPartnerKnown = false,
            EngineConversationOpen = true,
        };

        machine.Step(joining);

        DialogueFollowDecision atTheLimit = machine.Step(
            joining with { NowMs = Timings.LocalOpenGraceMs - 1 });

        Assert.Equal(DialogueFollowAction.None, atTheLimit.Action);
    }

    [Fact]
    public void AConversationThatNeverNamesItselfBecomesACompetitorOnceTheGraceIsSpent()
    {
        DialogueFollowMachine machine = new(Timings);
        DialogueFollowInputs joining = PeerTalking(0) with
        {
            LocalConversation = true,
            LocalPartnerKnown = false,
            EngineConversationOpen = true,
        };

        machine.Step(joining);
        DialogueFollowDecision expired = machine.Step(joining with { NowMs = Timings.LocalOpenGraceMs + 1 });

        Assert.Equal(DialogueFollowState.Yielding, expired.State);
        Assert.Equal(DialogueFollowAction.CloseConversation, expired.Action);
    }

    /// <summary>The tag resolving mid-grace is the normal path: it identifies as the peer's and
    /// is adopted, which is exactly what a click-to-join looks like.</summary>
    [Fact]
    public void AJoinThatResolvesDuringTheGraceIsAdopted()
    {
        DialogueFollowMachine machine = new(Timings);
        machine.Step(PeerTalking(0) with
        {
            LocalConversation = true,
            LocalPartnerKnown = false,
            EngineConversationOpen = true,
        });

        DialogueFollowDecision resolved = machine.Step(SameConversation(3000));

        Assert.Equal(DialogueFollowState.Adopting, resolved.State);
        Assert.Equal(DialogueFollowNote.Adopted, resolved.Note);
    }

    /// <summary>Once the grace is spent it stays spent for THAT conversation — a fresh stamp on
    /// every frame would restart the budget forever and nothing would ever be yielded.</summary>
    [Fact]
    public void TheGraceIsNotRestartedFrameByFrame()
    {
        DialogueFollowMachine machine = new(Timings);
        DialogueFollowInputs joining = PeerTalking(0) with
        {
            LocalConversation = true,
            LocalPartnerKnown = false,
            EngineConversationOpen = true,
        };

        for (long t = 0; t <= Timings.LocalOpenGraceMs; t += 1000)
        {
            machine.Step(joining with { NowMs = t });
        }

        DialogueFollowDecision expired = machine.Step(joining with { NowMs = Timings.LocalOpenGraceMs + 1 });
        Assert.Equal(DialogueFollowState.Yielding, expired.State);
    }

    // ── Yielding ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ⚠ CloseDialogue routes to CSWSObject::StopDialog, which runs the conversation's END
    /// SCRIPT. An NPC scripted to re-initiate (the tutorial deck's Trask) turns a per-frame
    /// close into a conversation that opens and closes forever — a measured, live defect.
    /// </summary>
    [Fact]
    public void TheYieldCloseIsRateLimited()
    {
        DialogueFollowMachine machine = new(Timings);

        Assert.Equal(DialogueFollowAction.CloseConversation, machine.Step(CompetingConversation(0)).Action);
        Assert.Equal(DialogueFollowAction.None, machine.Step(CompetingConversation(16)).Action);
        Assert.Equal(DialogueFollowAction.None, machine.Step(CompetingConversation(500)).Action);
        Assert.Equal(
            DialogueFollowAction.CloseConversation,
            machine.Step(CompetingConversation(Timings.YieldCloseRetryMs)).Action);
    }

    /// <summary>The server route has had its turns and the conversation is still here: there is
    /// nothing server-side left to end, so the UI is shut directly rather than this instance
    /// sitting out the whole conversation retrying forever.</summary>
    [Fact]
    public void AConversationThatWillNotCloseIsEventuallyForcedShut()
    {
        DialogueFollowMachine machine = new(Timings);
        long t = 0;

        for (int i = 0; i < Timings.YieldCloseServerAttempts; i++)
        {
            Assert.Equal(DialogueFollowAction.CloseConversation, machine.Step(CompetingConversation(t)).Action);
            t += Timings.YieldCloseRetryMs;
        }

        DialogueFollowDecision forced = machine.Step(CompetingConversation(t));
        Assert.Equal(DialogueFollowAction.ForceCloseUi, forced.Action);
        Assert.Equal(DialogueFollowNote.ForcingUiShut, forced.Note);
    }

    /// <summary>
    /// ⚠ LIVE-VERIFIED, not a hypothetical escalation. A joiner left farther than the engine's
    /// own conversation-start range never reaches StartDialog, and the still-queued dialogue
    /// action re-arms <c>gui+0xB4</c> every tick — which <c>ForceCloseUi</c> alone cannot touch:
    /// measured on the actual rig, the UI-only close repeated every
    /// <see cref="DialogueFollowTimings.YieldCloseRetryMs"/> for over two minutes straight with
    /// no effect. See docs/research/dialogue-join-latency.md "Far-join live pass".
    /// </summary>
    [Fact]
    public void AShellTheUiCloseCannotBudgeEscalatesToClearingTheStuckAction()
    {
        DialogueFollowMachine machine = new(Timings);
        long t = 0;

        for (int i = 0; i < Timings.YieldClearActionsAttempts; i++)
        {
            Assert.NotEqual(
                DialogueFollowAction.ForceCloseUiAndClearActions,
                machine.Step(CompetingConversation(t)).Action);
            t += Timings.YieldCloseRetryMs;
        }

        DialogueFollowDecision escalated = machine.Step(CompetingConversation(t));
        Assert.Equal(DialogueFollowAction.ForceCloseUiAndClearActions, escalated.Action);
        Assert.Equal(DialogueFollowNote.ClearingStuckAction, escalated.Note);
    }

    /// <summary>Once escalated, it keeps escalating rather than falling back to the UI-only
    /// close that had already failed — a shell that is still here after the first clear gets
    /// another one, not a step backwards to the action that measured as useless.</summary>
    [Fact]
    public void TheClearedActionEscalationDoesNotStepBackDown()
    {
        DialogueFollowMachine machine = new(Timings);
        long t = 0;

        for (int i = 0; i <= Timings.YieldClearActionsAttempts; i++)
        {
            machine.Step(CompetingConversation(t));
            t += Timings.YieldCloseRetryMs;
        }

        Assert.Equal(
            DialogueFollowAction.ForceCloseUiAndClearActions,
            machine.Step(CompetingConversation(t)).Action);
    }

    /// <summary>The yield ledger belongs to one conversation. A new one must not inherit an
    /// exhausted attempt count and be forced shut on its first frame.</summary>
    [Fact]
    public void TheYieldLedgerResetsWhenOurConversationHasGone()
    {
        DialogueFollowMachine machine = new(Timings);
        long t = 0;

        for (int i = 0; i < Timings.YieldCloseServerAttempts + 2; i++)
        {
            machine.Step(CompetingConversation(t));
            t += Timings.YieldCloseRetryMs;
        }

        machine.Step(PeerTalking(t)); // ours finally went
        t += 16;

        DialogueFollowDecision fresh = machine.Step(CompetingConversation(t));
        Assert.Equal(DialogueFollowAction.CloseConversation, fresh.Action);
    }

    /// <summary>
    /// ⚠ The mirror image of the local grace period. The tag comparison refuses an empty tag by
    /// design, so a peer whose participants have not resolved yet reads as "not our partner" and
    /// EVERY local conversation looks like a competitor — including the copy of the peer's own
    /// conversation that the engine hands both players standing in the same trigger, which is
    /// exactly the copy the adopt path exists to keep.
    /// </summary>
    [Fact]
    public void NothingIsYieldedUntilThePeerHasNamedItsPartner()
    {
        DialogueFollowMachine machine = new(Timings);

        DialogueFollowDecision decision = machine.Step(CompetingConversation(0) with
        {
            PeerPartnerKnown = false,
        });

        Assert.Equal(DialogueFollowState.Standby, decision.State);
        Assert.Equal(DialogueFollowAction.None, decision.Action);
        Assert.True(machine.SuppressReplies);
    }

    [Fact]
    public void AConversationThatTurnsOutToBeThePeersIsAdoptedNotYielded()
    {
        DialogueFollowMachine machine = new(Timings);

        // The peer's token is here; its partner tag is not yet. Our own copy of the same
        // conversation is already on screen.
        machine.Step(SameConversation(0) with { PeerPartnerKnown = false, LocalPartnerIsPeers = false });
        machine.Step(SameConversation(500) with { PeerPartnerKnown = false, LocalPartnerIsPeers = false });

        DialogueFollowDecision resolved = machine.Step(SameConversation(900));

        Assert.Equal(DialogueFollowState.Adopting, resolved.State);
    }

    [Fact]
    public void ACompetitorIsStillYieldedOnceThePeerNamesItself()
    {
        DialogueFollowMachine machine = new(Timings);
        machine.Step(CompetingConversation(0) with { PeerPartnerKnown = false });

        DialogueFollowDecision yielded = machine.Step(CompetingConversation(600));

        Assert.Equal(DialogueFollowState.Yielding, yielded.State);
        Assert.Equal(DialogueFollowAction.CloseConversation, yielded.Action);
    }

    [Fact]
    public void AYieldInProgressSimplyStopsWhenThePeerReleasesTheToken()
    {
        DialogueFollowMachine machine = new(Timings);
        machine.Step(CompetingConversation(0));

        DialogueFollowDecision released = machine.Step(Quiet(16) with
        {
            LocalConversation = true,
            LocalPartnerKnown = true,
            EngineConversationOpen = true,
            EngineHasContent = true,
        });

        Assert.Equal(DialogueFollowState.Idle, released.State);
        Assert.Equal(DialogueFollowAction.None, released.Action);
    }

    // ── Following, and the two deadlines ───────────────────────────────────────────────

    [Fact]
    public void AFollowedConversationDrivesEveryFrameItHasContent()
    {
        DialogueFollowMachine machine = new(Timings);
        machine.Step(SameConversation(0));

        for (long t = 16; t < 5000; t += 16)
        {
            Assert.Equal(DialogueFollowAction.DriveToPeerNode, machine.Step(SameConversation(t)).Action);
        }
    }

    /// <summary>The between-entries gap is not an empty shell — there is nothing on screen to
    /// escape from — and treating it as one is what made conversations close mid-line.</summary>
    [Fact]
    public void AGapBetweenEntriesDoesNotEndAFollow()
    {
        DialogueFollowMachine machine = new(Timings);
        machine.Step(SameConversation(0));
        machine.Step(SameConversation(16));

        DialogueFollowDecision gap = machine.Step(SameConversation(30000) with
        {
            EngineConversationOpen = false,
            EngineHasContent = false,
        });

        Assert.Equal(DialogueFollowState.Following, gap.State);
        Assert.Equal(DialogueFollowAction.None, gap.Action);
        Assert.True(machine.IsFollowing);
    }

    [Fact]
    public void AnOpenShellThatNeverDeliversALineIsBackedOutOf()
    {
        DialogueFollowMachine machine = new(Timings);
        DialogueFollowInputs shell = SameConversation(0) with { EngineHasContent = false };

        machine.Step(shell);
        machine.Step(shell with { NowMs = 16 });

        DialogueFollowDecision gaveUp = machine.Step(shell with { NowMs = Timings.ContentTimeoutMs + 1 });

        Assert.Equal(DialogueFollowState.BackingOff, gaveUp.State);
        Assert.Equal(DialogueFollowAction.CloseConversation, gaveUp.Action);
        Assert.Equal(DialogueFollowNote.GaveUpNoContent, gaveUp.Note);
    }

    // ── Backing off ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ⚠ THE LOOP THIS PREVENTS. Give-up closes the conversation; without a stand-off the adopt
    /// path finds the very same conversation still on screen with the very same matching tag on
    /// the next frame and takes it straight back — an open/close cycle whose period is the
    /// content timeout and whose every lap runs the conversation's END SCRIPT.
    /// </summary>
    [Fact]
    public void AGiveUpIsNotAdoptedStraightBack()
    {
        DialogueFollowMachine machine = new(Timings);
        DialogueFollowInputs shell = SameConversation(0) with { EngineHasContent = false };

        machine.Step(shell);
        machine.Step(shell with { NowMs = 16 });
        long gaveUpAt = Timings.ContentTimeoutMs + 1;
        machine.Step(shell with { NowMs = gaveUpAt });

        // The conversation is STILL on screen and still matches — the close takes frames.
        DialogueFollowDecision next = machine.Step(SameConversation(gaveUpAt + 16));

        Assert.Equal(DialogueFollowState.BackingOff, next.State);
        Assert.Equal(DialogueFollowAction.None, next.Action);
        Assert.False(machine.IsFollowing);
    }

    [Fact]
    public void TheStandOffEndsAndTheConversationMayBeAdoptedAgain()
    {
        DialogueFollowMachine machine = new(Timings);
        DialogueFollowInputs shell = SameConversation(0) with { EngineHasContent = false };

        machine.Step(shell);
        machine.Step(shell with { NowMs = 16 });
        long gaveUpAt = Timings.ContentTimeoutMs + 1;
        machine.Step(shell with { NowMs = gaveUpAt });

        DialogueFollowDecision after = machine.Step(
            SameConversation(gaveUpAt + Timings.ReopenCooldownMs));

        Assert.Equal(DialogueFollowState.Adopting, after.State);
    }

    /// <summary>A stand-off is not a poison. The peer ending their conversation clears it
    /// outright — the old controller's give-up set an attempt counter that was only cleared
    /// when the peer's conversation ended, so a single timeout made the spectator ignore that
    /// conversation for as long as it ran while the gate kept refusing the player's clicks.</summary>
    [Fact]
    public void AStandOffIsClearedWhenThePeerConversationEnds()
    {
        DialogueFollowMachine machine = new(Timings);
        DialogueFollowInputs shell = SameConversation(0) with { EngineHasContent = false };

        machine.Step(shell);
        machine.Step(shell with { NowMs = 16 });
        long gaveUpAt = Timings.ContentTimeoutMs + 1;
        machine.Step(shell with { NowMs = gaveUpAt });

        machine.Step(Quiet(gaveUpAt + 16));
        Assert.Equal(DialogueFollowState.Idle, machine.State);

        DialogueFollowDecision fresh = machine.Step(SameConversation(gaveUpAt + 32));
        Assert.Equal(DialogueFollowState.Adopting, fresh.State);
    }

    // ── The owner ending it ────────────────────────────────────────────────────────────

    [Fact]
    public void TheOwnerEndingTheConversationClosesOursExactlyOnce()
    {
        DialogueFollowMachine machine = new(Timings);
        machine.Step(SameConversation(0));
        machine.Step(SameConversation(16));

        DialogueFollowDecision ended = machine.Step(Quiet(32));
        Assert.Equal(DialogueFollowAction.CloseConversation, ended.Action);
        Assert.Equal(DialogueFollowNote.OwnerEnded, ended.Note);

        Assert.Equal(DialogueFollowAction.None, machine.Step(Quiet(48)).Action);
        Assert.Equal(DialogueFollowAction.None, machine.Step(Quiet(64)).Action);
    }

    [Fact]
    public void NothingIsClosedWhenAPeerConversationEndsAndWeWereNotFollowing()
    {
        DialogueFollowMachine machine = new(Timings);
        machine.Step(PeerTalking(0));

        Assert.Equal(DialogueFollowAction.None, machine.Step(Quiet(16)).Action);
    }

    // ── Unsticking a stale shell ───────────────────────────────────────────────────────

    /// <summary>
    /// Verified in the binary: SendPlayerToServerInput_Dialog 0x00677EA0 reads [gui+0xB4] at
    /// 0x00677EB3 and, if it is set, jumps straight to its exit. While that flag is up this
    /// instance cannot start a conversation with anybody, so a client-only shell has to be
    /// ended or the player can never join anything.
    /// </summary>
    [Fact]
    public void AnEmptyShellIsEndedSoAClickCanJoin()
    {
        DialogueFollowMachine machine = new(Timings);

        DialogueFollowDecision decision = machine.Step(PeerTalking(0) with
        {
            EngineConversationOpen = true,
            EngineHasContent = false,
        });

        Assert.Equal(DialogueFollowAction.CloseConversation, decision.Action);
        Assert.Equal(DialogueFollowNote.UnstickingShell, decision.Note);
    }

    [Fact]
    public void TheUnstickIsRateLimitedBecauseItRunsTheEndScript()
    {
        DialogueFollowMachine machine = new(Timings);
        DialogueFollowInputs shell = PeerTalking(0) with
        {
            EngineConversationOpen = true,
            EngineHasContent = false,
        };

        Assert.Equal(DialogueFollowAction.CloseConversation, machine.Step(shell).Action);
        Assert.Equal(DialogueFollowAction.None, machine.Step(shell with { NowMs = 16 }).Action);
        Assert.Equal(
            DialogueFollowAction.None,
            machine.Step(shell with { NowMs = Timings.UnstickRetryMs - 1 }).Action);
        Assert.Equal(
            DialogueFollowAction.CloseConversation,
            machine.Step(shell with { NowMs = Timings.UnstickRetryMs }).Action);
    }

    /// <summary>A conversation carrying a line is not a shell, whoever it belongs to.</summary>
    [Fact]
    public void AConversationWithContentIsNeverUnstuck()
    {
        DialogueFollowMachine machine = new(Timings);

        DialogueFollowDecision decision = machine.Step(PeerTalking(0) with
        {
            EngineConversationOpen = true,
            EngineHasContent = true,
        });

        Assert.Equal(DialogueFollowAction.None, decision.Action);
    }

    // ── Reset ──────────────────────────────────────────────────────────────────────────

    /// <summary>A module change destroys every creature and every conversation, so nothing
    /// survives it — in particular not a live follow, which would otherwise keep suppressing
    /// replies in a world where the peer's conversation no longer exists.</summary>
    [Fact]
    public void ResetDropsALiveFollow()
    {
        DialogueFollowMachine machine = new(Timings);
        machine.Step(SameConversation(0));
        machine.Step(SameConversation(16));
        Assert.True(machine.IsFollowing);

        machine.Reset();

        Assert.Equal(DialogueFollowState.Idle, machine.State);
        Assert.False(machine.IsFollowing);
        Assert.False(machine.SuppressReplies);
        Assert.Null(machine.OpenedAtMs);
    }

    // ── The whole click-to-join sequence ───────────────────────────────────────────────

    /// <summary>
    /// The measured live sequence, start to finish: the peer starts talking, the player clicks
    /// the same NPC, the request spends seconds unidentified while the server walks them into
    /// range, the tag resolves, the conversation is adopted and driven, and the owner ends it.
    /// At no point between the peer taking the token and releasing it may a reply get through.
    /// </summary>
    [Fact]
    public void TheFullClickToJoinSequence()
    {
        DialogueFollowMachine machine = new(Timings);

        machine.Step(Quiet(0));
        Assert.False(machine.SuppressReplies);

        // The peer's token arrives before their partner tag resolves.
        machine.Step(PeerTalking(100) with { PeerPartnerKnown = false });
        Assert.True(machine.SuppressReplies);

        machine.Step(PeerTalking(400));
        Assert.Equal(DialogueFollowState.Standby, machine.State);

        // The player clicks. gui+0xB4 goes up immediately; the participants do not resolve
        // for another few seconds while the server walks them over.
        DialogueFollowInputs walking = PeerTalking(1000) with
        {
            LocalConversation = true,
            LocalPartnerKnown = false,
        };

        for (long t = 1000; t < 4000; t += 500)
        {
            DialogueFollowDecision step = machine.Step(walking with { NowMs = t });
            Assert.Equal(DialogueFollowAction.None, step.Action);
            Assert.True(machine.SuppressReplies);
        }

        // It resolves, and it is the peer's.
        Assert.Equal(DialogueFollowNote.Adopted, machine.Step(SameConversation(4000)).Note);
        Assert.Equal(DialogueFollowAction.DriveToPeerNode, machine.Step(SameConversation(4016)).Action);
        Assert.True(machine.SuppressReplies);

        // The owner ends it.
        DialogueFollowDecision ended = machine.Step(Quiet(20000));
        Assert.Equal(DialogueFollowAction.CloseConversation, ended.Action);
        Assert.False(machine.SuppressReplies);
    }
}
