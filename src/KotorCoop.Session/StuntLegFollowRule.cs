namespace KotorCoop.Session;

/// <summary>What a follower does with a leader's announced transition INTO a stunt module
/// this tick (docs/research/cinematics.md, 2026-09-18 section).</summary>
public enum StuntLegFollowPath
{
    /// <summary>Run the same scripted leg here: a <c>StartNewModule</c> to the same stunt module
    /// and entry waypoint through the plugin's ordinary travel request, so this engine plays the
    /// vision/cutscene itself and its own scripts carry it to wherever the leader lands.</summary>
    Replay,

    /// <summary>Keep the announce and look again next tick — this instance is busy in a way
    /// that clears by itself (mid its own load, in dialogue, in combat).</summary>
    Wait,

    /// <summary>Forget it — this engine is already running the leg (its own click, its own
    /// replay, or a later hop of a chain it is already inside), or the announce is too old to
    /// still be the same moment.</summary>
    Drop,

    /// <summary>The pre-2026-09-18 answer, kept for the one case it is still right for: the
    /// feature is off, or nobody eligible announced STARTING the leg (the slow path only ever
    /// sees the leader's published module read <c>STUNT_*</c>, which says nothing about who
    /// initiated it or where its scripts will exit) — following would trap this player in
    /// someone else's cinematic.</summary>
    Refuse,
}

/// <summary>
/// The follower's decision for a scripted stunt-module leg (docs/research/cinematics.md,
/// 2026-09-18 "Replaying the stunt leg" section), pure so it is testable without an engine.
/// The galaxy-map departure relay proved the shape: the leader's scripted leg is relayed and the
/// follower REPLAYS it on its own engine rather than being warped into the middle of it. This
/// generalises that to every script-driven <c>StartNewModule</c> into a <c>STUNT_*</c> module
/// — the Taris vision (<c>STUNT_00</c>), the Bastila/Revan visions, the romance payoffs
/// (<c>STUNT_42</c>/<c>44</c>) and the credits chain (<c>STUNT_50a</c>→<c>57</c>).
///
/// Eligibility — WHOSE announce counts as a lead — is <see cref="LeadFollowRule.IsEligibleLeader"/>'s
/// job and is decided before an announce is ever stored; <see cref="StuntModuleRule"/> decides
/// whether the destination is a stunt module at all. This decides only whether a stored,
/// eligible stunt announce is replayed on THIS instance right now. The rule is "the leader
/// started it, we are not already in it, and we are not busy":
/// <list type="bullet">
/// <item>Leader-initiated: only an announce (the leader's own <c>move_to_module_pending</c>
/// edge, relayed) proves the leader's engine began the leg. The slow path's "the leader's
/// published module reads <c>STUNT_*</c>" does not — it is also what a leader who is itself
/// following someone else's leg looks like — so that path keeps refusing, unchanged.</item>
/// <item>Not already in it: a stunt chain is several hops, each one announced by the leader's
/// engine as its scripts run (<c>STUNT_50a</c>→<c>51a</c>→<c>54a</c>→<c>56a</c>→<c>57</c>).
/// The follower replays the FIRST hop only; its own engine's scripts then run the rest natively,
/// so every later hop the leader announces is the same chain seen from the other seat. The same
/// gate stops the echo loop: the follower's own replay warp is itself announced (the plugin's
/// <c>TravelController</c> publishes every co-op warp), and the leader must not replay it back
/// — the leader already went through that module itself.</item>
/// <item>Not busy: the three interrupt guards <see cref="LeadFollowRule.HostMaySafelyFollow"/>
/// applies to a lead-swap, applied to BOTH roles — a <c>StartNewModule</c> fired mid-load,
/// mid-line or mid-fight is the transition-window poison docs/research/transition-window-races.md
/// exists to keep out.</item>
/// </list>
/// </summary>
public static class StuntLegFollowRule
{
    /// <summary>How long a stored stunt announce stays actionable. The Taris vision runs about
    /// a minute on the leader; a follower that was mid-conversation or mid-fight when it
    /// arrived gets that long to free up before the leg is old news (the leader has landed and
    /// the ordinary follow-the-host warp is the right tool again).</summary>
    public const int ExpiryMs = 60_000;

    /// <summary>
    /// The longest a single stunt hop is trusted to run on this engine before the ordinary
    /// follow rules take over again — the safety valve for a replayed leg whose scripts never
    /// exit here (a branch keyed on a global this engine has not synced, or a cutscene waiting
    /// on a click nobody makes). Per HOP, not per chain: entering a new stunt module locally
    /// restarts it, so a six-hop credits chain is bounded hop by hop rather than as a whole.
    /// Five minutes is longer than any single base-game stunt scene plus its movies.
    /// </summary>
    public const int HopWindowMs = 300_000;

    public static StuntLegFollowPath Decide(
        bool followStuntEnabled,
        bool leaderInitiated,
        bool alreadyInThisLeg,
        bool inDialogue,
        bool inCombat,
        bool localMidTransition,
        long ageMs)
    {
        if (!followStuntEnabled || !leaderInitiated)
        {
            return StuntLegFollowPath.Refuse;
        }

        if (alreadyInThisLeg || ageMs > ExpiryMs)
        {
            return StuntLegFollowPath.Drop;
        }

        return inDialogue || inCombat || localMidTransition
            ? StuntLegFollowPath.Wait
            : StuntLegFollowPath.Replay;
    }

    /// <summary>
    /// Whether this instance should hold EVERY follow decision this tick because its own engine
    /// is inside a stunt leg it entered on purpose (a replay, or its own scripted departure) —
    /// the exit is the leg's own scripts, and warping out early is exactly the "teleports into
    /// an apartment on another planet" outcome cinematics.md §4.3 describes. Bounded by
    /// <see cref="HopWindowMs"/> per hop so a leg that never exits here still ends.
    /// </summary>
    public static bool ShouldHoldFollowsInOwnLeg(bool legActive, bool localInStuntModule, long hopAgeMs) =>
        legActive && localInStuntModule && hopAgeMs <= HopWindowMs;
}
