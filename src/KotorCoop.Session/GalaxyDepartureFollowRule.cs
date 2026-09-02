using KotorCoop.Session.Pumps;

namespace KotorCoop.Session;

/// <summary>What a follower does with a peer's galaxy-map departure this tick.</summary>
public enum GalaxyDepartureFollowPath
{
    /// <summary>Ask the plugin to run the same leg here, now.</summary>
    Replay,

    /// <summary>Keep the departure and look again next tick — this instance is busy in a way
    /// that clears by itself (mid its own load, in dialogue, in combat).</summary>
    Wait,

    /// <summary>Forget it — it can never apply here (a different world, decoded to nothing,
    /// or too old to still be the same moment).</summary>
    Drop,
}

/// <summary>
/// The follower's decision for the galaxy-map departure relay (docs/research/
/// galaxy-map-and-party-select.md, 2026-09-03 relay addendum), pure so it is testable without
/// an engine. Eligibility — WHOSE click counts as a lead — is <see cref="LeadFollowRule.IsEligibleLeader"/>'s
/// job and is decided before a departure is ever stored; this decides only whether a stored,
/// eligible departure can be replayed on THIS instance right now.
///
/// The rule is "same world, same moment, and not busy":
/// <list type="bullet">
/// <item>Same world: the leader clicked standing in <c>ebo_m12aa</c>; the follower replays the
/// same map from the same module only. Anywhere else the ordinary follow-the-host warp is the
/// right tool once the leader lands (and replaying here would strand this player in a leg the
/// leader never took from here).</item>
/// <item>Same moment: <see cref="ExpiryMs"/> bounds how long a departure stays actionable —
/// past it the leader has long since landed and the follower's own follow logic has taken
/// over, so a late replay would only chase a world that has moved on.</item>
/// <item>Not busy: the same three interrupt guards <see cref="LeadFollowRule.HostMaySafelyFollow"/>
/// applies to a lead-swap, applied to BOTH roles here — the follower's engine runs a whole
/// scripted leg (map, movie, cutscene module, fresh ship), and a departure that fires mid-load,
/// mid-line or mid-fight is the transition-window poison docs/research/transition-crashes.md
/// exists to keep out.</item>
/// </list>
/// </summary>
public static class GalaxyDepartureFollowRule
{
    /// <summary>How long a stored departure stays actionable. The leader's own leg runs
    /// 17–45 s (main thread frozen throughout); 90 s leaves room for a follower to finish a
    /// load/conversation of its own first without ever reaching a departure that is genuinely
    /// old news.</summary>
    public const int ExpiryMs = 90_000;

    /// <summary>Spacing between replay attempts after the plugin reported the engine refused
    /// (the map would not open, the HUD was still coming up) — the refusals clear on their own
    /// within a second or two, and the leader's leg leaves tens of seconds of slack.</summary>
    public const int RetryMs = 2_000;

    /// <summary>Replay attempts before giving up on one departure — 10 × <see cref="RetryMs"/>
    /// = 20 s, comfortably inside <see cref="ExpiryMs"/> and past any transient refusal.</summary>
    public const int MaxAttempts = 10;

    /// <summary>How long to wait for the plugin to ack a request before treating silence as a
    /// refusal (the plugin's controller drops a request across its own module change
    /// without acking — see <c>GalaxyDepartureController.Invalidate</c>).</summary>
    public const int AckTimeoutMs = 10_000;

    public static GalaxyDepartureFollowPath Decide(
        string localModule,
        string announcedModule,
        bool inDialogue,
        bool inCombat,
        bool localMidTransition,
        long ageMs)
    {
        if (announcedModule.Length == 0
            || ModuleNameGuard.IsFallbackModuleName(announcedModule)
            || ageMs > ExpiryMs)
        {
            return GalaxyDepartureFollowPath.Drop;
        }

        // Where we are is not known yet (a load of our own in flight): the answer to "same
        // world?" is simply not in yet.
        if (localModule.Length == 0 || ModuleNameGuard.IsFallbackModuleName(localModule) || localMidTransition)
        {
            return GalaxyDepartureFollowPath.Wait;
        }

        if (!string.Equals(localModule, announcedModule, StringComparison.OrdinalIgnoreCase))
        {
            return GalaxyDepartureFollowPath.Drop;
        }

        return inDialogue || inCombat ? GalaxyDepartureFollowPath.Wait : GalaxyDepartureFollowPath.Replay;
    }
}
