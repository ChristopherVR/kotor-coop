namespace KotorCoop.Plugin;

/// <summary>
/// Closes the container-loot race (docs/roadmap-seamless.md Tier 1 item 4): drives
/// <see cref="ContainerLockArbiter"/> — the pure click -&gt; pending -&gt; grant/deny/timeout ->
/// resume/refuse state machine — against <see cref="ContainerUseHook"/>'s deferral
/// capture and the shared-block request/response region
/// (<c>SharedBlock.OffsetLocalContainerLockRequestTargetId</c> onward,
/// docs/research/container-sync.md's <c>container_lock_arbitration</c> section).
///
/// WHY A SEPARATE FILE: "small partials by concern" (docs/CONTRIBUTING.md) — this is one
/// self-contained concern (a request lifecycle) layered on top of the existing container-lock
/// machinery in <c>InteractionLockController.cs</c>, not a growing branch inside it.
/// </summary>
internal sealed unsafe partial class InteractionLockController
{
    private readonly ContainerLockArbiter _containerArbiter = new();

    /// <summary>The last <see cref="ContainerUseHook"/> deferred-attempt serial this instance
    /// has already turned into a request, so a click that is still being arbitrated is not
    /// re-requested every frame while <see cref="ContainerUseHook.LatestDeferred"/> keeps
    /// reporting the same serial.</summary>
    private uint _seenDeferredSerial;

    /// <summary>This instance's own monotonic request-id counter — scoped to the plugin
    /// process, never persisted, never compared across instances (the host and every client
    /// each mint their own). Starts at 1 because 0 is reserved wire-wide for "no request".</summary>
    private uint _nextContainerRequestSerial;
    private long _containerRequestAtMs;
    private uint _pendingDeferredSerial;
    private Action<uint>? _resumeGrantedContainer;

    private void InvalidateContainerArbitration()
    {
        _containerArbiter.Reset();
        _seenDeferredSerial = 0;
        _containerRequestAtMs = 0;
        _pendingDeferredSerial = 0;
        block.LocalContainerLockRequestTargetId = 0;
        block.LocalContainerLockRequestSerial = 0;
        ContainerUseHook.WithdrawGrant();
    }

    /// <summary>
    /// One pass of the state machine: notices a fresh deferred click and starts a request;
    /// otherwise checks whether the in-flight request's answer (or timeout) has arrived and, if
    /// so, acts on it exactly once.
    /// </summary>
    private void PollContainerArbitration()
    {
        long now = Environment.TickCount64;

        if (_containerArbiter.IsIdle)
        {
            uint deferredId = ContainerUseHook.LatestDeferred(out uint deferredSerial);
            if (deferredSerial == 0 || deferredSerial == _seenDeferredSerial)
            {
                return;
            }

            _seenDeferredSerial = deferredSerial;

            uint requestSerial = ++_nextContainerRequestSerial;
            if (requestSerial == 0) // wrapped past uint.MaxValue — 0 is reserved
            {
                requestSerial = ++_nextContainerRequestSerial;
            }

            _containerArbiter.BeginRequest(deferredId, requestSerial, now);
            ContainerUseHook.LatestAttempt(out uint lastAllowedAttempt);
            _containerCloseRelease.BeginDeferred(deferredId, lastAllowedAttempt);
            // The helper can poll between frames. Withdraw the old shared-block release
            // before publishing the new request serial below, not on the next frame.
            if (!block.LocalInteractionActive && block.LocalInteractionKind == InteractionKindContainer
                && block.LocalInteractionTargetId == deferredId)
            {
                block.LocalInteractionKind = 0;
                block.LocalInteractionTargetId = 0;
            }
            _containerRequestAtMs = now;
            _pendingDeferredSerial = deferredSerial;
            PluginHost.Log($"container: arbitration requested 0x{deferredId:X} (serial {requestSerial})");

            // Published for the agent to forward (WorldStatePump.PumpContainerLockArbitration)
            // — target id FIRST, serial LAST, so a torn read on the agent side sees an old
            // serial paired with a not-yet-updated id rather than a new serial naming the wrong
            // container. See SharedBlock.Dialogue.cs's own remarks on this region.
            block.LocalContainerLockRequestTargetId = deferredId;
            block.LocalContainerLockRequestSerial = requestSerial;
            return;
        }

        uint pendingSerial = _containerArbiter.PendingSerial;
        uint pendingTargetId = _containerArbiter.PendingTargetId;

        bool responseArrived = block.ContainerLockResponseSerial == pendingSerial;
        bool granted = responseArrived && block.ContainerLockResponseGranted;

        switch (_containerArbiter.PollAndResume(now, responseArrived, granted,
            _resumeGrantedContainer ??= ResumeGrantedContainer))
        {
            case ContainerLockPollOutcome.Granted:
                break;

            case ContainerLockPollOutcome.Denied:
                // Somebody else won the race. The peer-held refresh (ApplyPeerLocks) will pick
                // up the now-live lock on its own next pass and hold/refuse it the ordinary way
                // from then on; this notice is only for the ONE click that got here first.
                _containerNoticeUntilMs = now + FarNoticeDurationMs;
                _containerNoticeLength = BuildContainerNoticeText(0); // holder unknown from a
                                                                       // bare deny — generic text
                PluginHost.Log($"container: arbitration denied 0x{pendingTargetId:X} after {now - _containerRequestAtMs}ms (a peer got there first)");
                break;

            case ContainerLockPollOutcome.TimedOut:
                // A lost denial AND a lost lock broadcast must not open a contested chest.
                // Without a positive grant, refuse this click and let a new click retry.
                ContainerUseHook.WithdrawGrant();
                _containerNoticeUntilMs = now + FarNoticeDurationMs;
                ReadOnlySpan<byte> notice = "Container request timed out - try again"u8;
                notice.CopyTo(_containerNoticeBuffer);
                _containerNoticeLength = notice.Length;
                PluginHost.Log(
                    $"container: arbitration timed out for 0x{pendingTargetId:X} "
                    + $"(no host answer within {ContainerLockArbiter.TimeoutMs}ms; elapsed {now - _containerRequestAtMs}ms); "
                    + "open refused - try again (an abandoned reservation expires with its lease)");
                break;

            case ContainerLockPollOutcome.Waiting:
            default:
                break;
        }
    }

    private void ResumeGrantedContainer(uint targetId)
    {
        bool resumed = ContainerUseHook.ResumeDeferredOpen(targetId, _pendingDeferredSerial);
        PluginHost.Log($"container: arbitration granted 0x{targetId:X} after {Environment.TickCount64 - _containerRequestAtMs}ms; "
            + (resumed ? "resumed captured open" : "captured open expired or target unavailable"));
    }
}
