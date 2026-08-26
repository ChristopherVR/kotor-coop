namespace KotorCoop.Session;

/// <summary>Debounce state for <see cref="PersistentSaveMismatchRule"/> — whether the mismatch
/// was already present last tick (so a re-observation is a level, not a fresh edge), and when
/// this rule last actually started a rejoin (so a stuck condition retries on a cooldown instead
/// of either spamming the network or going silent forever).</summary>
public readonly record struct MismatchRejoinWatchState(bool WasMismatched, long? LastAttemptAtMs)
{
    public static MismatchRejoinWatchState Initial => new(false, null);
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

    /// <returns>Whether to start a rejoin THIS call, and the new state.</returns>
    public static (bool ShouldTrigger, MismatchRejoinWatchState Next) Observe(
        MismatchRejoinWatchState current, bool mismatchedNow, bool rejoinInFlight, long nowMs)
    {
        if (!mismatchedNow)
        {
            // Clean now — whether it just healed or was never wrong, there is nothing to
            // retry, and the next mismatch (whenever it starts) must read as a fresh edge
            // rather than reusing a stale cooldown from an unrelated earlier episode.
            return (false, MismatchRejoinWatchState.Initial);
        }

        if (rejoinInFlight)
        {
            // Something is already fetching the host's world for us — note that the
            // condition persists (so the tick after it finishes is not treated as a brand
            // new edge) but do not start a second, redundant attempt.
            return (false, current with { WasMismatched = true });
        }

        bool isNewEdge = !current.WasMismatched;
        bool cooldownElapsed = current.LastAttemptAtMs is not { } last || nowMs - last >= RetryIntervalMs;

        if (isNewEdge || cooldownElapsed)
        {
            return (true, new MismatchRejoinWatchState(true, nowMs));
        }

        return (false, current with { WasMismatched = true });
    }
}
