namespace KotorCoop.Plugin;

/// <summary>
/// The plugin-side deferral state machine for one in-flight container-lock arbitration request
/// — click -&gt; pending -&gt; grant/deny/timeout -&gt; re-issue/refuse. Pure arithmetic over facts the
/// caller has already gathered, no engine calls and no shared-block access, so it compiles into
/// the 64-bit test project the same way <c>DoorUse</c>/<c>CheatAuthority</c> do (see
/// docs/CONTRIBUTING.md's "plugin compile-include trick") and the state machine can be asserted
/// directly rather than only through a live rig.
///
/// WHY THIS EXISTS (docs/roadmap-seamless.md Tier 1 item 4, docs/research/container-sync.md's
/// <c>container_lock_arbitration</c> section). Without it, <c>ContainerUseHook</c> only ever
/// consulted a snapshot of who ALREADY holds an object — refreshed once a frame from the
/// session's last broadcast — so two clicks on the same unclaimed container inside one RTT both
/// read "free" locally and both let the engine's own <c>OpenInventory</c> run before either
/// claim had crossed the wire. This class is what turns a click into a SUSPENDED decision: the
/// caller (<see cref="InteractionLockController"/>) swallows the engine's open exactly once,
/// asks the host to arbitrate, and only re-issues the click once an answer (or a timeout) says
/// it may.
///
/// ONE REQUEST AT A TIME, deliberately: a single local player can only be clicking one container
/// at once, so there is nothing to gain from tracking more, and a second click arriving while
/// one is already pending is refused by the caller before it ever reaches
/// <see cref="BeginRequest"/> (the ordinary "someone else has it" gate still applies to it,
/// exactly as before this feature existed).
/// </summary>
internal enum ContainerLockArbiterState
{
    /// <summary>No request outstanding. The default and resting state.</summary>
    Idle,

    /// <summary>A request has been sent (or, for the host's own in-process request, already
    /// answered) and <see cref="ContainerLockArbiter.Poll"/> is waiting to hear about it.</summary>
    Pending,
}

/// <summary>What a poll found. <see cref="Waiting"/> means keep swallowing the click; the other
/// three are terminal for THIS request — the caller acts once and the arbiter returns to
/// <see cref="ContainerLockArbiterState.Idle"/>.</summary>
internal enum ContainerLockPollOutcome
{
    /// <summary>Still nothing back; keep waiting (unless the caller's own frame budget for the
    /// re-issue window has separately run out — that is the caller's concern, not this one's).</summary>
    Waiting,

    /// <summary>Granted — the caller may re-issue the click, exactly once.</summary>
    Granted,

    /// <summary>Denied — someone else already holds it. The caller shows the refusal notice and
    /// does NOT re-issue.</summary>
    Denied,

    /// <summary>No answer arrived before the deadline. The caller falls back to "proceed as
    /// before this feature existed" — see <see cref="ContainerLockArbiter.TimeoutMs"/>'s own
    /// remarks for why that is the safe default rather than a refusal.</summary>
    TimedOut,
}

internal sealed class ContainerLockArbiter
{
    /// <summary>
    /// How long a request waits for a host answer before giving up and falling back to the
    /// pre-arbitration behaviour (proceed as if nothing were held — <c>ContainerUseHook</c>'s
    /// own peer-held check still runs on the re-issued click either way, so a fallback can never
    /// steal an object a peer genuinely holds).
    ///
    /// 300ms is comfortably more than a LAN round trip (the existing container-close latency
    /// work, docs/research/container-sync.md's round 78/round 10 sections, measured a full
    /// claim+broadcast round trip at 250-440ms even before this feature's synchronous reply
    /// exists) while still being short enough that a lost request or response reads as "briefly
    /// slower to open," not "container appears not to work."
    /// </summary>
    public const long TimeoutMs = 300;

    private ContainerLockArbiterState _state = ContainerLockArbiterState.Idle;
    private uint _targetId;
    private uint _requestSerial;
    private long _deadlineMs;

    public bool IsIdle => _state == ContainerLockArbiterState.Idle;

    /// <summary>The container id this request names. Meaningless while <see cref="IsIdle"/>.</summary>
    public uint PendingTargetId => _targetId;

    /// <summary>The serial the caller should ask the session about. Meaningless while
    /// <see cref="IsIdle"/>. Never 0 — see <see cref="BeginRequest"/>.</summary>
    public uint PendingSerial => _requestSerial;

    /// <summary>
    /// Starts tracking a fresh request. The CALLER is responsible for actually sending it
    /// (<c>session.RequestContainerLock</c>, reached through the shared block on the agent
    /// side) — this only tracks the local wait, so a caller that fails to send still times out
    /// safely rather than waiting forever.
    /// </summary>
    /// <param name="targetId">The container being requested. Must be nonzero.</param>
    /// <param name="requestSerial">The caller's own monotonic request id. Must be nonzero — 0 is
    /// reserved, wire-wide, for "no request" (see <see cref="KotorCoop.Protocol.InteractionClaimPacket.RequestSerial"/>).</param>
    /// <param name="nowMs">The current tick clock.</param>
    public void BeginRequest(uint targetId, uint requestSerial, long nowMs)
    {
        if (targetId == 0 || requestSerial == 0)
        {
            return;
        }

        _state = ContainerLockArbiterState.Pending;
        _targetId = targetId;
        _requestSerial = requestSerial;
        _deadlineMs = nowMs + TimeoutMs;
    }

    /// <summary>
    /// Call every frame while <see cref="IsIdle"/> is false. <paramref name="responseArrived"/>
    /// and <paramref name="granted"/> are exactly what the caller's
    /// <c>session.TryGetLockResponse(PendingSerial, out granted)</c> reported THIS frame — this
    /// class never talks to a session itself, so it stays pure and callable from the 64-bit
    /// test project.
    /// </summary>
    public ContainerLockPollOutcome Poll(long nowMs, bool responseArrived, bool granted)
    {
        if (_state != ContainerLockArbiterState.Pending)
        {
            return ContainerLockPollOutcome.Waiting;
        }

        if (responseArrived)
        {
            _state = ContainerLockArbiterState.Idle;
            return granted ? ContainerLockPollOutcome.Granted : ContainerLockPollOutcome.Denied;
        }

        if (nowMs >= _deadlineMs)
        {
            _state = ContainerLockArbiterState.Idle;
            return ContainerLockPollOutcome.TimedOut;
        }

        return ContainerLockPollOutcome.Waiting;
    }

    /// <summary>Drops any in-flight request without acting on it — a module change reassigns
    /// every object id, so a pending request from the old one names nothing in the new one, the
    /// same discipline every other per-module tracker in this plugin (<c>ContainerUseHook</c>,
    /// <c>ContainerCloseHook</c>, <see cref="InteractionLockController"/> itself) already
    /// follows on its own <c>Invalidate()</c>.</summary>
    public void Reset()
    {
        _state = ContainerLockArbiterState.Idle;
        _targetId = 0;
        _requestSerial = 0;
        _deadlineMs = 0;
    }
}
