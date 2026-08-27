using KotorCoop.Protocol;

namespace KotorCoop.Agent;

/// <summary>
/// Turns a behaviour-flag comparison (docs/research/mod-compatibility.md's "Behaviour-flag
/// negotiation", docs/roadmap-seamless.md Tier 1 item 7) into lines a player actually sees —
/// the same job <see cref="CompatibilityAnnouncer"/> does for a content mismatch, through the
/// same trio (stdout, the <c>~</c> console reply line, the in-game overlay).
///
/// Unlike a content mismatch, a behaviour-flag difference never refuses a session and never
/// drops a capability bit — the worst it does is get silently adopted (a client running a
/// different <c>effect_sync</c> than its host, say). "Silently" is the part this fixes: the
/// line is <c>co-op [W_CFG] &lt;key&gt; host=&lt;v&gt; you=&lt;v&gt;</c>, once per key, for the life of
/// the connection — coop.ini/CLI settings do not change mid-session the way in-world facts do,
/// so unlike <see cref="CompatibilityAnnouncer"/>'s <c>ModuleGit</c> case there is nothing here
/// that legitimately clears and needs to be said twice.
///
/// For a <see cref="BehaviourDifference.HostAuthoritative"/> key this ALSO posts a second line
/// — "adopted host's &lt;key&gt; (...)" — through the identical <paramref name="announce"/> trio
/// (stdout, the console reply, the chat overlay). That line used to be written straight to
/// <c>Console.Error</c> from <c>CoopClient.ApplyBehaviourComparison</c> (session layer, no
/// access to the agent's <c>Reply</c>/overlay/log path), which meant it reached a console
/// window that may not even exist and never reached <c>--log</c> or the in-game chat. The FACTS
/// it needs (which key, the host's value, the value it is replacing) were already sitting on
/// this same <see cref="BehaviourComparisonResult"/> — <c>HostAuthoritative</c> is exactly "was
/// this one adopted" — so no new session-side plumbing was needed, only moving where the line
/// gets written. Fixed 2026-08-27.
/// </summary>
public sealed class BehaviourAnnouncer
{
    private readonly HashSet<string> _announced = [];

    /// <summary>
    /// Diffs <paramref name="result"/> against what has already been said and announces
    /// whatever is new. Null (no session, or a session that has not compared yet) says
    /// nothing.
    /// </summary>
    public void Observe(BehaviourComparisonResult? result, Action<string> announce)
    {
        ArgumentNullException.ThrowIfNull(announce);

        if (result is null)
        {
            return;
        }

        foreach (BehaviourDifference diff in result.Differences)
        {
            if (!_announced.Add(diff.Key))
            {
                continue;
            }

            announce($"co-op [{BehaviourDifference.Tag}] {diff.Text}");

            if (diff.HostAuthoritative)
            {
                announce(
                    $"co-op: adopted host's {diff.Key} ({BoolWord(diff.HostValue)}; local setting "
                    + $"was {BoolWord(diff.YourValue)})");
            }
        }
    }

    /// <summary>Every <see cref="BehaviourDifference.HostAuthoritative"/> key compares through
    /// <c>BehaviourCompatibility.CompareFlag</c>, whose values are always the literal "1"/"0"
    /// strings <c>BehaviourCompatibility.Bool</c> produces — never <c>interp_ms</c>/
    /// <c>follow_lead</c>, both declared <c>HostAuthoritative: false</c> — so this is safe to
    /// apply only behind the same gate <see cref="Observe"/> already checks.</summary>
    private static string BoolWord(string value) => value == "1" ? "on" : "off";

    /// <summary>The session went away: forget everything, so a reconnect (possibly to a
    /// different host, with different settings) re-announces its own comparison.</summary>
    public void Reset() => _announced.Clear();
}
