namespace KotorCoop.Session;

/// <summary>Debounce state for <see cref="PersistentSaveMismatchRule"/> — whether the mismatch
/// was already present last tick (so a re-observation is a level, not a fresh edge), when
/// this rule last actually started a rejoin (so a stuck condition retries on a cooldown instead
/// of either spamming the network or going silent forever), how many attempts this host world
/// has had (the backoff exponent and the cap), and which host world those attempts were
/// against (<see cref="KotorCoop.Protocol.WorldDigest.SaveIdentity"/>; a new world starts the
/// count over).</summary>
public readonly record struct MismatchRejoinWatchState(
    bool WasMismatched, long? LastAttemptAtMs, int Attempts = 0, uint HostWorldKey = 0)
{
    public static MismatchRejoinWatchState Initial => new(false, null);

    /// <summary>True once this world has used up every attempt and the mismatch persists — the
    /// caller says so once and leaves the player to <c>reconnect</c> by hand.</summary>
    public bool GaveUp => WasMismatched && Attempts >= PersistentSaveMismatchRule.MaxAttemptsPerWorld;
}

/// <summary>
/// Closes the gap <c>client_load_rejoin</c> (<see cref="ClientLoadRejoinRule"/>) left open,
/// found live (docs/research/party-reconciliation.md's "Attempted... two simultaneous
/// releases" entry): a debug-cheat cross-module warp (<c>tools/blockpoke.ps1 travel</c>, and
/// anything else that moves the engine to a new module without going through a real Save/Load)
/// bumps the LIVE world-load serial (<c>WorldLoadRule.IsUnannouncedTeardown</c> sees no
/// <c>trans_pending</c> precursor, exactly like a real Load Game) but writes NOTHING to disk —
/// <c>SaveDigest.TryComputeSaveIdentity</c> hashes the STAGED SAVE FILE
/// (<c>SaveDigestCache.RefreshSaveIfStale</c> only re-stats and recomputes when that file's
/// length/write-time actually changes), and a debug warp never touches it. The result: the two
/// peers' <see cref="KotorCoop.Protocol.WorldDigest.SaveIdentity"/> values can go on disagreeing
/// indefinitely (<c>ContentCompatibility.ApplyWorldMismatch</c> correctly keeps reporting
/// <see cref="KotorCoop.Protocol.CompatibilityCode.Save"/>, dropping party/enemies/doors/items —
/// this half is not a bug) while NEITHER of <see cref="RejoinTrigger.OwnLoad"/> (needs the
/// LOCAL digest to actually CHANGE — it never does) nor <see cref="RejoinTrigger.HostReload"/>
/// (needs the HOST's own reported digest to change — same problem on that side) ever produces
/// the EDGE <see cref="SaveOrLoadClassifier"/> requires to fire. The player-facing line
/// ("...rejoin to adopt the host's world and get them back") describes a corrective action that,
/// for exactly this class of mismatch, nothing was ever wired to actually take.
///
/// THE FIX IS NOT A NEW DIGEST SOURCE. <c>SessionManager.RejoinAfterLoadAsync</c> already fetches
/// the host's world and overwrites the client's own staged save with it — a REAL disk write,
/// which (once it lands) gives <see cref="KotorCoop.Memory.SaveDigestCache"/> a changed file stamp
/// and a freshly matching digest, closing the mismatch for real, not just retrying the same
/// stale comparison. The only missing piece was a trigger that watches the COMPARISON'S OWN
/// verdict — a LEVEL, the same "say it every tick, not just once" reasoning
/// <c>PartyRosterController</c>'s own header already argues for a structurally identical problem
/// — instead of requiring an edge on a signal a debug warp never produces.
///
/// Deliberately NOT edge-only: <see cref="RetryIntervalMs"/> re-triggers a still-mismatched
/// condition even after one attempt already ran and finished (success or failure), because a
/// single failed rejoin (a dropped packet, a momentarily busy host) must not mean this gate goes
/// back to being permanently stuck exactly the way the bug it fixes did. Deliberately NOT
/// hammering either: a rejoin already <see cref="RejoinPhase.InFlight"/> is never restarted (the
/// caller checks that independently, same as every other <see cref="RejoinTrigger"/>), and a
/// freshly-started attempt is not retried again until the cooldown elapses even if it is still
/// running past that point — <see cref="ClientLoadRejoinRule.Decide"/>'s own InFlight gate
/// already swallows that call for free.
/// </summary>
public static class PersistentSaveMismatchRule
{
    /// <summary>How long a still-mismatched condition waits before this rule asks for another
    /// rejoin attempt. Long enough that a single 60s-deadline <c>RejoinAfterLoadAsync</c> attempt
    /// (<c>CoopCommandDispatcher.TriggerRejoin</c>'s own poll deadline) has a real chance to
    /// either land or fail cleanly before a second one is even considered; short enough that a
    /// player is not left sitting in a degraded session for minutes over one bad attempt.</summary>
    public const long RetryIntervalMs = 20_000;

    /// <summary>
    /// Attempts per host world before this rule stops on its own (docs/review-seamless-2026-09-18.md
    /// §2 "long-session growth": there was no cap, so a mismatch that no fetch could ever heal —
    /// a host whose world transfer is blocked, a save the client cannot apply — re-ran the whole
    /// 180 s rejoin cycle every 20 s for the rest of the session). With the backoff below the
    /// five attempts span 20 + 40 + 80 + 160 s of waiting on top of the attempts themselves:
    /// long enough for a transient (a busy host, a dropped fetch) to clear several times over,
    /// short enough that a genuinely stuck client is told to act within a few minutes rather
    /// than being churned all evening. Reset when the mismatch heals (success) or the host's
    /// world changes (a different save has a different chance).
    /// </summary>
    public const int MaxAttemptsPerWorld = 5;

    /// <summary>The wait before attempt number <paramref name="attemptsSoFar"/> + 1:
    /// <see cref="RetryIntervalMs"/> doubled per attempt already made (20 s, 40 s, 80 s, 160 s),
    /// never less than the base interval.</summary>
    public static long RetryDelayMs(int attemptsSoFar) =>
        RetryIntervalMs << Math.Clamp(attemptsSoFar - 1, 0, MaxAttemptsPerWorld);

    /// <returns>Whether to start a rejoin THIS call, and the new state.</returns>
    /// <param name="hostWorldKey">The host's current <c>WorldDigest.SaveIdentity</c> (0 when
    /// unknown): the per-world scope of the attempt cap. A change resets the count, since the
    /// attempts made against the old world say nothing about the new one.</param>
    public static (bool ShouldTrigger, MismatchRejoinWatchState Next) Observe(
        MismatchRejoinWatchState current, bool mismatchedNow, bool rejoinInFlight, long nowMs, uint hostWorldKey = 0)
    {
        if (!mismatchedNow)
        {
            // Clean now — whether it just healed or was never wrong, there is nothing to
            // retry, and the next mismatch (whenever it starts) must read as a fresh edge
            // rather than reusing a stale cooldown from an unrelated earlier episode. This is
            // also the "reset on success" of the attempt cap.
            return (false, MismatchRejoinWatchState.Initial);
        }

        if (rejoinInFlight)
        {
            // Something is already fetching the host's world for us — note that the
            // condition persists (so the tick after it finishes is not treated as a brand
            // new edge) but do not start a second, redundant attempt.
            return (false, current with { WasMismatched = true });
        }

        if (hostWorldKey != current.HostWorldKey)
        {
            // A different host world: whatever was tried against the old one does not count,
            // and a mismatch against the new one is a fresh edge.
            current = new MismatchRejoinWatchState(false, null, 0, hostWorldKey);
        }

        if (current.Attempts >= MaxAttemptsPerWorld)
        {
            return (false, current with { WasMismatched = true });
        }

        bool isNewEdge = !current.WasMismatched;
        bool cooldownElapsed = current.LastAttemptAtMs is not { } last
            || nowMs - last >= RetryDelayMs(current.Attempts);

        if (isNewEdge || cooldownElapsed)
        {
            return (true, new MismatchRejoinWatchState(true, nowMs, current.Attempts + 1, hostWorldKey));
        }

        return (false, current with { WasMismatched = true });
    }
}
