namespace KotorCoop.Session;

/// <summary>What produced a rejoin trigger this tick, purely so the caller can pick the right
/// on-screen line (design doc: "you loaded a save - rejoining &lt;host&gt;'s world" versus
/// "host loaded a save - everyone re-joins") — the decision itself (below) treats both alike.</summary>
public enum RejoinTrigger
{
    None,

    /// <summary>This CLIENT's own <see cref="SaveOrLoadClassifier"/> returned
    /// <see cref="SaveOrLoadVerdict.Load"/> for its own save-identity digest (coop.ini
    /// <c>client_load_rejoin</c>).</summary>
    OwnLoad,

    /// <summary>A <see cref="SaveOrLoadClassifier"/> watching the HOST's reported
    /// <see cref="KotorCoop.Protocol.WorldDigest"/> (received over the wire — see that type's
    /// <c>LoadSerial</c> field) returned <see cref="SaveOrLoadVerdict.Load"/> (coop.ini
    /// <c>host_load_readopt</c>). Design doc §3.2: "have every client run the same
    /// auto-rejoin" — this is that reuse, from the other trigger.</summary>
    HostReload,
}

/// <summary>Where a rejoin attempt is: <see cref="Idle"/> (nothing running, a new trigger is
/// free to start one) or <see cref="InFlight"/> (an attempt is already under way).</summary>
public enum RejoinPhase
{
    Idle,
    InFlight,
}

public readonly record struct RejoinDecisionState(RejoinPhase Phase)
{
    public static RejoinDecisionState Idle => new(RejoinPhase.Idle);
}

/// <summary>
/// The rejoin decision state machine roadmap item 14 phase 2 needed: pure, so the edges,
/// debounce and idempotence properties are exactly what a test pins rather than something
/// buried inside <c>CoopCommandDispatcher</c>'s async orchestration.
///
/// "IGNORE THE LOAD THAT IS THE ADOPTION" is deliberately NOT this class's job — it is handled
/// one layer down, for free, by the SAME mechanism <see cref="SaveIdentityWatch"/> already uses
/// for the digest half: <see cref="SerialEdgeWatchState.Initial"/>'s first real reading becomes
/// the baseline, not a change, so the very first world-load edge a fresh connection ever
/// observes (which IS the adoption that just carried this client into the session) produces no
/// <see cref="RejoinTrigger"/> at all — there is nothing for this state machine to see. This
/// class only has to handle what happens AFTER that baseline is established: a genuine
/// mid-session load edge, however many times it fires, and never more than one rejoin running
/// at once.
/// </summary>
public static class ClientLoadRejoinRule
{
    /// <returns>Whether to start a rejoin THIS call, and the new state. A trigger while a
    /// rejoin is already <see cref="RejoinPhase.InFlight"/> is swallowed rather than queued or
    /// restarted: the in-flight attempt fetches the host's CURRENT world at the moment it
    /// actually runs (not a stale snapshot from when it started), so a second signal arriving
    /// before it finishes has nothing to add — restarting would only throw away progress for
    /// no benefit. <paramref name="gateEnabled"/> folded in here (rather than left to the
    /// caller) so "the gate is off" and "nothing happened" are the same no-op path, not two
    /// call sites that both have to remember to check it.</returns>
    public static (bool ShouldStart, RejoinDecisionState Next) Decide(
        RejoinDecisionState current, RejoinTrigger trigger, bool gateEnabled)
    {
        if (trigger == RejoinTrigger.None || !gateEnabled)
        {
            return (false, current);
        }

        if (current.Phase == RejoinPhase.InFlight)
        {
            return (false, current);
        }

        return (true, new RejoinDecisionState(RejoinPhase.InFlight));
    }

    /// <summary>The in-flight attempt finished, success or failure alike — either way there is
    /// nothing left running, so the next genuine trigger must be free to start a fresh one.</summary>
    public static RejoinDecisionState Complete(RejoinDecisionState current) => RejoinDecisionState.Idle;
}
