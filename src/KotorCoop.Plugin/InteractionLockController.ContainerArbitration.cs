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

        switch (_containerArbiter.Poll(now, responseArrived, granted))
        {
            case ContainerLockPollOutcome.Granted:
                bool queued = ContainerUseHook.ResumeDeferredOpen(pendingTargetId, _pendingDeferredSerial);
                PluginHost.Log($"container: arbitration granted 0x{pendingTargetId:X} after {now - _containerRequestAtMs}ms; "
                    + (queued ? "resumed captured open" : "captured open expired or target unavailable"));
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
                // Fails SAFE: proceed exactly as this feature's predecessor always did, rather
                // than refusing a container nobody may actually hold. ContainerUseHook's own
                // peer-held check still runs on the resumed open regardless, so a genuinely
                // contested container is still caught there even if arbitration itself dropped
                // the ball.
                bool fallbackQueued = ContainerUseHook.ResumeDeferredOpen(pendingTargetId, _pendingDeferredSerial);
                PluginHost.Log(
                    $"container: arbitration timed out for 0x{pendingTargetId:X} "
                    + $"(no host answer within {ContainerLockArbiter.TimeoutMs}ms; elapsed {now - _containerRequestAtMs}ms); "
                    + (fallbackQueued ? "resumed captured open" : "captured open expired or target unavailable"));
                break;

            case ContainerLockPollOutcome.Waiting:
            default:
                break;
        }
    }
}
