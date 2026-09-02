namespace KotorCoop.Memory;

/// <summary>
/// An item sweep taken while the world is between lives is PROVISIONAL: it is served until the
/// world settles, and the first lookup in the settled world rebuilds the caches before answering.
/// Pure state behind <c>KotorGameBridge.ServiceItemTagCache</c>, kept separate so the rule can
/// be pinned without an engine.
///
/// Live 2026-09-02 (docs/research/death-handling.md §6.7, shared-inventory-v2.md §17): a client's
/// rejoin after a wipe resolved its new player 3.5 s before the plugin acked the world on screen,
/// and a lookup in that window ran the one-time inline sweep of the new world. That sweep found
/// the carried character's clothes but NOT its short sword — the heap was still being built —
/// and because a sweep is trusted for a full rebuild interval, the script-grant baseline taken
/// once the world had settled was read through it. The next background sweep, fifteen seconds
/// later, found the sword, and the diff published the client's own weapon as a script grant:
/// the host pooled a new short sword and both pools carried one more than anyone owned.
///
/// The sweep during the load is not wrong to run — callers between lives would otherwise see an
/// empty inventory, which is worse — it is wrong to KEEP. Without a plugin the world is never
/// between lives and nothing here ever fires.
///
/// ⚠ THAT FIX WAS NOT ENOUGH. Live 2026-09-02, 23:31 (a game-over reload of END_M01AA via
/// `KotorCoop.Agent.exe load --load-save 1`): the host's own player resolved (`adopted the
/// engine's own creature`) at 23:31:00.8, the plugin acked the world on screen a moment later
/// (hp read 8/8, then 12/12 — the PC was still being finished), and the FIRST settled lookup ran
/// its inline rebuild against a heap that had not finished being built EITHER: the equipped
/// clothes and short sword the save carries are created by the engine AFTER the player object
/// resolves, so the "settled" rebuild missed them just as thoroughly as a between-lives sweep
/// would have — except this sweep is not provisional, because `betweenLives` was already false
/// by the time it ran. Nothing rebuilt the caches again until the ordinary 15 s rebuild interval
/// elapsed, at which point the two items were diffed in as a fresh script grant and published to
/// the peer (`script item grant: … g_a_clothes01, g_w_shortswrd01`), and the party pool grew from
/// three keys to five reconciling them in.
///
/// So a sweep can be UNPROVISIONAL (the world is not between lives) and still be wrong, for a
/// short window right after the edge, because "the plugin says the world is up" and "the engine
/// has finished creating everything the save describes" are two different moments. <see
/// cref="Settling"/> covers that window: it opens at every world edge (<see cref="Reset"/>,
/// alongside every other cache this bumps <c>WorldEpoch</c> for) and does not close until two
/// consecutive sweeps, spaced apart, name the exact same set of item ids — the cheapest signal
/// <c>ApplyItemScanHits</c> already produces, and the same "hold the first, compare against a
/// second a full interval later" shape <c>PartyPoolSettleRule</c> uses for its own channel.
/// While settling, nothing here forces callers to stall (the inline resweep this class already
/// grants at the provisional→settled edge still runs, unconditionally, so a caller never sees an
/// artificially empty inventory) — it is <c>ScriptItemGrantPump</c>'s job to notice
/// <see cref="Settling"/> and rebaseline silently instead of publishing a diff through it. A cap
/// (<see cref="SettleCapMs"/>) keeps a genuinely churning inventory (a script dropping loot on
/// its own timer right after a load) from pinning every caller to that rebaseline-only path
/// forever.
/// </summary>
internal sealed class ItemSweepProvenanceRule
{
    /// <summary>Minimum gap between two settle-comparison sweeps. Two reads a few milliseconds
    /// apart would "agree" on the same still-being-built heap and close the window before the
    /// engine had a chance to finish it — the exact failure this class exists to close. Matches
    /// the order of magnitude <c>PartyPoolSettleRule</c> uses for the same reason on the pool
    /// channel (one full scan interval).</summary>
    private const long MinSweepSpacingMs = 1_500;

    /// <summary>How long <see cref="Settling"/> is allowed to stay true after a world edge before
    /// giving up and trusting whatever the last sweep found. Without a cap, an inventory that
    /// never stops changing (an OnEnter script that keeps dropping loot) would keep every sweep
    /// disagreeing with the last one forever, and <c>ScriptItemGrantPump</c> would rebaseline on
    /// every sample for the rest of the session instead of ever diffing again.</summary>
    private const long SettleCapMs = 20_000;

    private long _settleStartedAtMs;
    private HashSet<uint>? _pendingIds;
    private long _pendingAtMs;

    /// <summary>True while the caches describe a sweep taken between lives.</summary>
    public bool Provisional { get; private set; }

    /// <summary>
    /// True from a world edge (<see cref="Reset"/>) until the item view has stood still: two
    /// consecutive sweeps, at least <see cref="MinSweepSpacingMs"/> apart, named the exact same
    /// set of item ids, or <see cref="SettleCapMs"/> has passed since the edge and this gives up
    /// waiting. Starts false on a fresh rule (a bridge that never sees a world edge — the mock
    /// harness, or before the first attach — never settles anything, so nothing should ever hold
    /// a caller back).
    /// </summary>
    public bool Settling { get; private set; }

    /// <summary>
    /// A sweep has just been applied to the caches, taken while the world was (or was not)
    /// between lives. <paramref name="itemIds"/> is the id set the sweep found — read only while
    /// <see cref="Settling"/>, so a caller not tracking the fix this half exists for can keep
    /// calling the old one-argument shape and get exactly the old behaviour. <paramref
    /// name="nowMs"/> is the caller's own clock (<c>Environment.TickCount64</c> in
    /// <c>KotorGameBridge</c>), passed in rather than read here so this stays pure and pinnable
    /// without an engine.
    /// </summary>
    public void Swept(bool betweenLives, IReadOnlyCollection<uint>? itemIds = null, long nowMs = 0)
    {
        Provisional = betweenLives;

        if (!Settling)
        {
            return;
        }

        if (nowMs - _settleStartedAtMs >= SettleCapMs)
        {
            Settling = false;
            _pendingIds = null;
            return;
        }

        if (betweenLives)
        {
            // A between-lives sweep is EXPECTED to be incomplete and expected to change again
            // before the world settles — it is not a candidate for the "two sweeps agree"
            // comparison, and letting it stand as the pending baseline would let the load's own
            // churn "confirm" itself the instant the world settles.
            return;
        }

        HashSet<uint> ids = itemIds is null ? [] : [.. itemIds];

        if (_pendingIds is null)
        {
            _pendingIds = ids;
            _pendingAtMs = nowMs;
            return;
        }

        if (nowMs - _pendingAtMs < MinSweepSpacingMs)
        {
            // Too soon to count as a confirming read — wait for one spaced out properly rather
            // than let two ticks close together agree on a heap that has not had time to move.
            return;
        }

        if (_pendingIds.SetEquals(ids))
        {
            Settling = false;
            _pendingIds = null;
            return;
        }

        // Differs from the held candidate: it becomes the new candidate and the clock restarts.
        _pendingIds = ids;
        _pendingAtMs = nowMs;
    }

    /// <summary>Whether a lookup about to be served from the caches must rebuild them first:
    /// the held sweep is provisional and the world has now settled. Stays false while still
    /// between lives — rebuilding then would only produce another provisional sweep.</summary>
    public bool MustRebuildNow(bool betweenLives) => Provisional && !betweenLives;

    /// <summary>The caches were dropped (world replaced, or never built); nothing is held, and
    /// the item view starts <see cref="Settling"/> again from <paramref name="nowMs"/> — the new
    /// world's view is not trusted for publishing until it stands still. <paramref name="nowMs"/>
    /// defaults to 0 so the pre-existing (provenance-only) callers and tests need not change.</summary>
    public void Reset(long nowMs = 0)
    {
        Provisional = false;
        Settling = true;
        _settleStartedAtMs = nowMs;
        _pendingIds = null;
        _pendingAtMs = 0;
    }
}
