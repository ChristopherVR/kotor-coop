namespace KotorCoop.Session;

/// <summary>Debounce state for <see cref="PersistentSaveMismatchRule"/> — whether the mismatch
/// was already present last tick (so a re-observation is a level, not a fresh edge), when
/// this rule last actually started a rejoin (so a stuck condition retries on a cooldown instead
/// of either spamming the network or going silent forever), how many attempts this host world
/// has had (the backoff exponent and the cap), and which host world those attempts were
/// against (<see cref="KotorCoop.Protocol.WorldDigest.SaveIdentity"/>; a new world starts the
/// count over).</summary>
public readonly record struct MismatchRejoinWatchState(
    bool WasMismatched, long? LastAttemptAtMs, int Attempts = 0, uint HostWorldKey = 0,
    long? RecentLoadEdgeAtMs = null, bool LoadAuthorizedEpisode = false)
{
    public static MismatchRejoinWatchState Initial => new(false, null);

    /// <summary>True once this world has used up every attempt and the mismatch persists — the
    /// caller says so once and leaves the player to <c>reconnect</c> by hand.</summary>
    public bool GaveUp => WasMismatched && Attempts >= PersistentSaveMismatchRule.MaxAttemptsPerWorld;
}

/// <summary>
/// Retries a save-identity mismatch only when a raw local or host world-load serial edge
/// correlates with it. Ordinary saves can make the compared identities differ while both
/// peers remain in the same loaded world; automatically adopting the host's newest disk save
/// in that case can replace a valid live world with unrelated progress. A load edge is retained
/// for <see cref="LoadCorrelationWindowMs"/> to allow the digest cache to catch up, then
/// authorizes retries for that mismatch episode. Existing compatibility checks still decide
/// whether the resulting worlds agree.
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

    /// <summary>A load edge may precede the save digest update while the cache catches up.
    /// Only a mismatch observed in this correlation window can authorize automatic adoption.</summary>
    public const long LoadCorrelationWindowMs = SaveOrLoadClassifier.DefaultCorrelationWindowMs;

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
        MismatchRejoinWatchState current, bool mismatchedNow, bool rejoinInFlight, long nowMs,
        uint hostWorldKey = 0, bool loadEdge = false)
    {
        long? recentLoadEdgeAtMs = loadEdge ? nowMs : current.RecentLoadEdgeAtMs;
        bool recentLoadEdge = recentLoadEdgeAtMs is { } edgeAt
            && nowMs >= edgeAt && nowMs - edgeAt <= LoadCorrelationWindowMs;

        if (!mismatchedNow)
        {
            // Clean now — whether it just healed or was never wrong, there is nothing to
            // retry, and the next mismatch (whenever it starts) must read as a fresh edge
            // rather than reusing a stale cooldown from an unrelated earlier episode. This is
            // also the "reset on success" of the attempt cap.
            // A pending edge observed before any mismatch may still correlate with a digest
            // that is catching up. Once an authorized mismatch episode heals, consume that
            // edge so a later ordinary save cannot reuse it as authority.
            bool healedEpisode = current.WasMismatched || current.LoadAuthorizedEpisode;
            long? pendingEdge = loadEdge
                ? recentLoadEdgeAtMs
                : !healedEpisode && recentLoadEdge ? recentLoadEdgeAtMs : null;
            return (false, MismatchRejoinWatchState.Initial with { RecentLoadEdgeAtMs = pendingEdge });
        }

        bool loadAuthorizedEpisode = current.LoadAuthorizedEpisode || recentLoadEdge;
        current = current with
        {
            RecentLoadEdgeAtMs = recentLoadEdge ? recentLoadEdgeAtMs : null,
            LoadAuthorizedEpisode = loadAuthorizedEpisode,
        };

        if (rejoinInFlight)
        {
            // Something is already fetching the host's world for us — note that the
            // condition persists (so the tick after it finishes is not treated as a brand
            // new edge) but do not start a second, redundant attempt.
            return (false, current with { WasMismatched = true });
        }

        if (hostWorldKey != 0 && current.HostWorldKey != 0 && hostWorldKey != current.HostWorldKey)
        {
            // A different host world starts a new authorization scope as well as a new retry
            // budget. A still-unconsumed raw load edge may follow a delayed digest update to
            // this key; an edge already used to start an attempt for the old key may not.
            bool edgeCanAuthorizeNewWorld = recentLoadEdge
                && current.RecentLoadEdgeAtMs is not null;
            loadAuthorizedEpisode = edgeCanAuthorizeNewWorld;
            current = new MismatchRejoinWatchState(false, null, 0, hostWorldKey,
                edgeCanAuthorizeNewWorld ? current.RecentLoadEdgeAtMs : null,
                LoadAuthorizedEpisode: edgeCanAuthorizeNewWorld);
        }
        else if (hostWorldKey != 0 && current.HostWorldKey == 0)
        {
            // Establishing the first known key is not evidence of a world replacement. Keep
            // an unconsumed edge while a digest delayed within its correlation window catches
            // up, and bind this watch to the now-known key for subsequent changes.
            current = current with { HostWorldKey = hostWorldKey };
        }

        // A save-identity mismatch by itself is not evidence that either loaded world changed.
        // In particular, two ordinary saves can diverge while both players remain in the
        // same in-memory world. Keep observing the level, but only a nearby raw load edge may
        // make this mismatch episode eligible for automatic adoption.
        if (!loadAuthorizedEpisode)
        {
            return (false, current with { WasMismatched = true });
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
            uint effectiveHostWorldKey = hostWorldKey != 0 ? hostWorldKey : current.HostWorldKey;
            return (true, new MismatchRejoinWatchState(
                true, nowMs, current.Attempts + 1, effectiveHostWorldKey,
                RecentLoadEdgeAtMs: null, LoadAuthorizedEpisode: true));
        }

        return (false, current with { WasMismatched = true });
    }
}
